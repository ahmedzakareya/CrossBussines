using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H5 — the consultation.
	//
	// What must never happen: a consultation for a patient who has not checked in, two encounters
	// for one appointment, a signed visit without a complaint or a primary diagnosis, a diagnosis
	// code the catalogue does not know, a vital sign no human has, a signed visit edited, another
	// company's visit reached by id, or desk staff writing a clinical note.
	// ==========================================================================================
	public class HospitalConsultationTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Today = new(2026, 11, 15);   // Sunday
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required HospitalOutpatientService Op;
			public required HospitalPatientService Patients;
			public required HospitalConsultationService Consult;
		}

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			return new World { Host = host, Op = new(host.Db, access), Patients = new(host.Db, access), Consult = new(host.Db, access) };
		}

		/// A doctor, a patient, the code catalogue, and a booked appointment at 09:00. Returns the appointment.
		private static async Task<(int Appt, int Doctor, int Patient)> VisitAsync(World w, int companyId = CompanyOne, bool arrive = true)
		{
			var ctx = Ctx(7, companyId);
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د", NameEn = "Dr A", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600 }, 7)).Id!.Value;
			var pat = (await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P " + companyId, Sex = "M" }, 7, Now)).Id!.Value;
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = pat, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			if (arrive) Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			return (appt, doc, pat);
		}

		private static HospitalEncounterInput Note(params (string Code, bool Primary)[] dx) => new()
		{
			ChiefComplaint = "Fever for 3 days", History = "No cough", Examination = "Throat red",
			BpSystolic = 120, BpDiastolic = 80, HeartRate = 88, TemperatureC = 38.4m, SpO2 = 98, WeightKg = 70m, HeightCm = 175m,
			Diagnoses = dx.Select(d => new HospitalDiagnosisInput { Code = d.Code, Primary = d.Primary }).ToList(),
			Prescription = new() { new HospitalPrescriptionInput { Drug = "Paracetamol 500 mg", Instructions = "1 tab every 8 h · 3 days", Quantity = 9 } },
		};

		[Fact]
		public async Task Opening_a_consultation_takes_the_waiting_patient_in_and_creates_one_encounter_only()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (appt, _, _) = await VisitAsync(w);
			var first = await w.Consult.OpenAsync(Ctx(7), appt, 7, Now);
			var again = await w.Consult.OpenAsync(Ctx(7), appt, 7, Now);
			Assert.True(first.Ok, first.Error);
			Assert.Equal(first.Id, again.Id);
			Assert.Equal(1, await host.Db.HospitalEncounters.CountAsync());
			Assert.Equal(HospitalAppointmentStatus.InRoom, (await host.Db.HospitalAppointments.AsNoTracking().SingleAsync()).Status);
		}

		[Fact]
		public async Task A_patient_who_has_not_checked_in_cannot_be_seen()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (appt, _, _) = await VisitAsync(w, arrive: false);
			Assert.Equal("The patient must check in before the consultation starts", (await w.Consult.OpenAsync(Ctx(7), appt, 7, Now)).Error);
			Assert.Equal(0, await host.Db.HospitalEncounters.CountAsync());
		}

		[Fact]
		public async Task Signing_needs_a_complaint_and_exactly_one_primary_diagnosis_then_locks_the_visit_and_finishes_the_appointment()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (appt, _, _) = await VisitAsync(w);
			var enc = (await w.Consult.OpenAsync(Ctx(7), appt, 7, Now)).Id!.Value;

			var noComplaint = Note(("J02.9", true)); noComplaint.ChiefComplaint = " ";
			Assert.Equal("Enter the chief complaint before signing", (await w.Consult.SignAsync(Ctx(7), enc, noComplaint, 7, Now)).Error);
			Assert.Equal("Add at least one diagnosis before signing", (await w.Consult.SignAsync(Ctx(7), enc, Note(), 7, Now)).Error);
			Assert.Equal("Mark exactly one diagnosis as primary", (await w.Consult.SignAsync(Ctx(7), enc, Note(("J02.9", false), ("R50.9", false)), 7, Now)).Error);
			Assert.Equal("Mark exactly one diagnosis as primary", (await w.Consult.SignAsync(Ctx(7), enc, Note(("J02.9", true), ("R50.9", true)), 7, Now)).Error);

			Assert.True((await w.Consult.SignAsync(Ctx(7), enc, Note(("J02.9", true), ("R50.9", false)), 7, Now.AddMinutes(12))).Ok);
			var view = (await w.Consult.GetAsync(Ctx(7), enc))!;
			Assert.Equal(HospitalEncounterStatus.Signed, view.Encounter.Status);
			Assert.Equal(new[] { ("J02.9", true), ("R50.9", false) }, view.Diagnoses.Select(d => (d.Code, d.IsPrimary)).ToArray());
			Assert.Equal("Acute pharyngitis, unspecified", view.Diagnoses[0].NameEn);   // copied from the catalogue
			Assert.Single(view.Prescription);
			Assert.Equal(22.9m, view.Encounter.Bmi);
			Assert.Equal(HospitalAppointmentStatus.Done, (await host.Db.HospitalAppointments.AsNoTracking().SingleAsync()).Status);

			// Locked: neither a draft nor a second signature changes it.
			Assert.Equal("This visit is signed and can no longer be changed", (await w.Consult.SaveDraftAsync(Ctx(7), enc, Note(("I10", true)), 7)).Error);
			Assert.Equal("J02.9", (await w.Consult.GetAsync(Ctx(7), enc))!.Diagnoses.Single(d => d.IsPrimary).Code);
		}

		[Fact]
		public async Task An_unknown_code_and_impossible_vitals_are_refused_and_nothing_is_written()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (appt, _, _) = await VisitAsync(w);
			var enc = (await w.Consult.OpenAsync(Ctx(7), appt, 7, Now)).Id!.Value;

			Assert.Equal("Unknown diagnosis code: {0}", (await w.Consult.SaveDraftAsync(Ctx(7), enc, Note(("X99.9", true)), 7)).Error);
			var hot = Note(("J02.9", true)); hot.TemperatureC = 47m;
			Assert.Equal("Temperature is 30 to 44 °C", (await w.Consult.SaveDraftAsync(Ctx(7), enc, hot, 7)).Error);
			var bp = Note(("J02.9", true)); bp.BpSystolic = 80; bp.BpDiastolic = 90;
			Assert.Equal("Diastolic pressure must be below systolic", (await w.Consult.SaveDraftAsync(Ctx(7), enc, bp, 7)).Error);
			Assert.Equal("A diagnosis is listed twice", (await w.Consult.SaveDraftAsync(Ctx(7), enc, Note(("J02.9", true), ("J02.9", false)), 7)).Error);
			Assert.Equal(0, await host.Db.HospitalEncounterDiagnoses.CountAsync());
			Assert.Null((await host.Db.HospitalEncounters.AsNoTracking().SingleAsync()).ChiefComplaint);
		}

		[Fact]
		public async Task A_draft_can_be_saved_again_and_again_and_replaces_its_lines()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (appt, _, _) = await VisitAsync(w);
			var enc = (await w.Consult.OpenAsync(Ctx(7), appt, 7, Now)).Id!.Value;
			Assert.True((await w.Consult.SaveDraftAsync(Ctx(7), enc, Note(("J02.9", true), ("R50.9", false)), 7)).Ok);
			var second = Note(("I10", true)); second.Prescription.Clear();
			Assert.True((await w.Consult.SaveDraftAsync(Ctx(7), enc, second, 7)).Ok);
			var view = (await w.Consult.GetAsync(Ctx(7), enc))!;
			Assert.Equal(new[] { "I10" }, view.Diagnoses.Select(d => d.Code).ToArray());
			Assert.Empty(view.Prescription);
			Assert.Equal(HospitalEncounterStatus.Draft, view.Encounter.Status);
		}

		[Fact]
		public async Task Another_companys_visit_is_out_of_reach_and_desk_staff_write_no_clinical_note()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (theirAppt, _, _) = await VisitAsync(w, CompanyTwo);
			var theirEnc = (await w.Consult.OpenAsync(Ctx(9, CompanyTwo), theirAppt, 9, Now)).Id!.Value;
			Assert.Null(await w.Consult.GetAsync(Ctx(7), theirEnc));
			Assert.Equal("not_found", (await w.Consult.OpenAsync(Ctx(7), theirAppt, 7, Now)).Error);
			Assert.Equal("not_found", (await w.Consult.SaveDraftAsync(Ctx(7), theirEnc, Note(), 7)).Error);

			var (mine, _, _) = await VisitAsync(w);
			foreach (var (emp, role) in new[] { (1, HospitalRoles.HospitalAdministrator), (2, HospitalRoles.HospitalStaff), (3, HospitalRoles.HospitalDoctor) })
				host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
			Assert.Equal("forbidden", (await w.Consult.OpenAsync(Ctx(2), mine, 2, Now)).Error);   // desk staff
			Assert.True((await w.Consult.OpenAsync(Ctx(3), mine, 3, Now)).Ok);                    // the doctor
		}

		[Fact]
		public async Task The_worklist_shows_the_doctors_day_with_its_encounters()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (appt, doc, _) = await VisitAsync(w);
			var enc = (await w.Consult.OpenAsync(Ctx(7), appt, 7, Now)).Id!.Value;
			Assert.True((await w.Consult.SaveDraftAsync(Ctx(7), enc, Note(("J02.9", true)), 7)).Ok);
			var row = Assert.Single(await w.Consult.WorklistAsync(Ctx(7), w.Op, doc, Today));
			Assert.Equal((enc, HospitalEncounterStatus.Draft, true), (row.EncounterId!.Value, row.EncounterStatus!, row.HasVitals));
		}
	}
}
