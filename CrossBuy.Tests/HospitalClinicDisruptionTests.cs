using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H161: clinic disruptions. What must never happen:
	//   · a patient booked in the window left off the list, or one outside it put on;
	//   · anything moved or anyone messaged before the desk approves;
	//   · two patients proposed the same free slot;
	//   · a patient counted as reached because a message was queued (only a call confirms it);
	//   · a late doctor shown on the board as the patients' own lateness.
	// ==========================================================================================
	public class HospitalClinicDisruptionTests
	{
		// Sunday 15 November 2026, 08:00: tomorrow (Monday) both doctors work 09:00-12:00 in 15-minute slots.
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime NowLocal = new(2026, 11, 15, 8, 0, 0);
		private static readonly DateTime NowUtc = new(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Tomorrow = Today.AddDays(1);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed record World(PlatformTestHost Host, HospitalOutpatientService Op, HospitalClinicDisruptionService Svc, int DoctorA, int DoctorB, int Omar, int Sara, int Late);

		private static async Task<World> Make()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var op = new HospitalOutpatientService(host.Db, access);
			var work = new HospitalFrontDeskWorkService(host.Db, access, op, new HospitalPortalBookingService(host.Db, access));
			var dep = new HospitalDepartment { CompanyId = 1, Code = "CARD", Name = "القلب", NameEn = "Cardiology" };
			host.Db.Add(dep); await host.Db.SaveChangesAsync();
			async Task<int> Doc(string en) => (await op.SavePractitionerAsync(Ctx(), new HospitalPractitionerInput { Name = "د. " + en, NameEn = "Dr " + en, DepartmentId = dep.ID, WorkDays = new() { 0, 1 }, SessionStartMinute = 540, SessionEndMinute = 720, SlotMinutes = 15, Privileges = new() { "opd" } }, 7)).Id!.Value;
			var patients = new HospitalPatientService(host.Db, access);
			async Task<int> Pat(string en) => (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م " + en, NameEn = en, Sex = "M" }, 7, NowUtc)).Id!.Value;
			int a = await Doc("Ali"), b = await Doc("Badr"), omar = await Pat("Omar"), sara = await Pat("Sara"), late = await Pat("Late");
			async Task Book(int p, int d, int m) => Assert.True((await op.BookAsync(Ctx(), new HospitalBookingInput { PatientId = p, PractitionerId = d, Date = Tomorrow, StartMinute = m, VisitType = HospitalVisitTypes.FollowUp }, 7, Today)).Ok);
			await Book(omar, a, 540); await Book(sara, a, 555); await Book(late, a, 660);   // 11:00 is outside a 09:00-10:00 window
			return new World(host, op, new HospitalClinicDisruptionService(host.Db, access, work, op), a, b, omar, sara, late);
		}

		private static HospitalDisruptionInput Declare(int doctor, string kind, int from = 540, int to = 600) => new()
		{ PractitionerId = doctor, Day = Tomorrow, Kind = kind, FromMinute = from, ToMinute = to, DelayMinutes = kind == HospitalDisruptionKind.Late ? 30 : null, Room = kind == HospitalDisruptionKind.Room ? "B-12" : null };

		[Theory]
		[InlineData("Flood", 540, 600, 30, null, "Choose what happened")]
		[InlineData(HospitalDisruptionKind.Late, 600, 540, 30, null, "Choose the hours it covers")]
		[InlineData(HospitalDisruptionKind.Late, 540, 600, null, null, "Write the delay in minutes (up to 8 hours)")]
		[InlineData(HospitalDisruptionKind.Room, 540, 600, null, " ", "Write the new room")]
		[InlineData(HospitalDisruptionKind.Absent, 540, 600, null, null, null)]
		public void A_disruption_says_what_happened_and_when(string kind, int from, int to, int? delay, string? room, string? refusal)
			=> Assert.Equal(refusal, HospitalDisruptionRules.Refuse(new HospitalDisruptionInput { PractitionerId = 1, Day = Tomorrow, Kind = kind, FromMinute = from, ToMinute = to, DelayMinutes = delay, Room = room }, Today));

		[Fact]
		public async Task An_absent_doctor_proposes_distinct_slots_and_nothing_moves_before_approval()
		{
			var w = await Make(); using var _h = w.Host;
			var r = await w.Svc.DeclareAsync(Ctx(), Declare(w.DoctorA, HospitalDisruptionKind.Absent), 7, NowLocal, NowUtc);
			Assert.True(r.Ok, r.Error);
			var v = (await w.Svc.ViewAsync(Ctx(), r.Id!.Value))!;
			Assert.Equal(new[] { w.Omar, w.Sara }, v.Rows.Select(x => x.R.PatientId).ToArray());   // the 11:00 patient is outside the window
			Assert.All(v.Rows, x => Assert.Equal((HospitalDisruptionPlan.Move, w.DoctorB, Tomorrow), (x.R.Plan, x.R.NewPractitionerId!.Value, x.R.NewDay!.Value)));
			Assert.Equal(2, v.Rows.Select(x => x.R.NewMinute).Distinct().Count());   // never one slot twice
			Assert.Equal(3, await w.Host.Db.HospitalAppointments.CountAsync(a => a.Status == HospitalAppointmentStatus.Booked && a.PractitionerId == w.DoctorA));   // nothing moved yet
			Assert.Equal(0, await w.Host.Db.Set<HospitalSmsMessage>().CountAsync(m => m.SourceType == "ClinicDisruption"));   // the bookings' own messages aside

			var cancel = v.Rows.Single(x => x.R.PatientId == w.Sara).R.ID;
			Assert.True((await w.Svc.SetPlanAsync(Ctx(), cancel, HospitalDisruptionPlan.Cancel)).Ok);
			var (ar, applied, failed) = await w.Svc.ApplyAsync(Ctx(), r.Id!.Value, 7, NowLocal, NowUtc);
			Assert.True(ar.Ok, ar.Error);
			Assert.Equal((2, 0), (applied, failed));
			Assert.Equal("The plans are applied already", (await w.Svc.SetPlanAsync(Ctx(), cancel, HospitalDisruptionPlan.Keep)).Error);
			Assert.Equal(1, await w.Host.Db.HospitalAppointments.CountAsync(a => a.Status == HospitalAppointmentStatus.Booked && a.PractitionerId == w.DoctorB && a.PatientId == w.Omar));
			Assert.Equal(HospitalAppointmentStatus.Cancelled, (await w.Host.Db.HospitalAppointments.SingleAsync(a => a.PatientId == w.Sara)).Status);
			var sms = await w.Host.Db.Set<HospitalSmsMessage>().Where(m => m.SourceType == "ClinicDisruption").OrderBy(m => m.ID).ToListAsync();
			Assert.Equal(2, sms.Count);
			Assert.All(sms, m => Assert.Equal(HospitalSmsStatus.Skipped, m.Status));   // no mobile on file: skipped, with the reason
			Assert.Contains("د. Badr", sms[0].Body);   // the moved patient is told the new doctor
			Assert.Contains("إلغاء", sms[1].Body);
		}

		[Fact]
		public async Task Only_a_call_reaches_a_patient_and_a_failed_call_needs_a_word()
		{
			var w = await Make(); using var _h = w.Host;
			var id = (await w.Svc.DeclareAsync(Ctx(), Declare(w.DoctorA, HospitalDisruptionKind.Room), 7, NowLocal, NowUtc)).Id!.Value;
			var first = (await w.Svc.ViewAsync(Ctx(), id))!.Rows[0].R.ID;
			Assert.Equal("Apply the plan before calling the patient", (await w.Svc.CallAsync(Ctx(), first, true, null, 7, NowUtc)).Error);
			Assert.True((await w.Svc.ApplyAsync(Ctx(), id, 7, NowLocal, NowUtc)).Result.Ok);
			Assert.Equal(2, (await w.Svc.ToCallAsync(Ctx())).Count);   // queued is not reached
			Assert.Equal("Write what happened on the call", (await w.Svc.CallAsync(Ctx(), first, false, " ", 7, NowUtc)).Error);
			Assert.True((await w.Svc.CallAsync(Ctx(), first, false, "no answer", 7, NowUtc)).Ok);
			Assert.Equal(2, (await w.Svc.ToCallAsync(Ctx())).Count);
			Assert.True((await w.Svc.CallAsync(Ctx(), first, true, null, 7, NowUtc)).Ok);
			Assert.Single(await w.Svc.ToCallAsync(Ctx()));
			Assert.Equal(2, (await w.Host.Db.Set<HospitalDisruptionPatient>().FindAsync(first))!.Calls);
			Assert.Empty(await w.Svc.ToCallAsync(Ctx(2)));
			Assert.True((await w.Svc.CloseAsync(Ctx(), id, 7, NowUtc)).Ok);
			Assert.Empty(await w.Svc.ToCallAsync(Ctx()));
		}

		[Fact]
		public async Task A_late_doctor_is_the_clinics_delay_on_the_board_and_the_patients_keep_their_slot()
		{
			var w = await Make(); using var _h = w.Host;
			var id = (await w.Svc.DeclareAsync(Ctx(), Declare(w.DoctorA, HospitalDisruptionKind.Late), 7, NowLocal, NowUtc)).Id!.Value;
			var delays = await w.Svc.ClinicDelaysAsync(Ctx(), Tomorrow);
			Assert.Equal(2, delays.Count);
			Assert.All(delays.Values, v => Assert.Equal(30, v));
			Assert.Empty(await w.Svc.ClinicDelaysAsync(Ctx(), Today));
			Assert.True((await w.Svc.ApplyAsync(Ctx(), id, 7, NowLocal, NowUtc)).Result.Ok);
			Assert.Equal(3, await w.Host.Db.HospitalAppointments.CountAsync(a => a.Status == HospitalAppointmentStatus.Booked && a.PractitionerId == w.DoctorA));   // kept
			Assert.Contains("09:30", (await w.Host.Db.Set<HospitalSmsMessage>().Where(m => m.SourceType == "ClinicDisruption").OrderBy(m => m.ID).FirstAsync()).Body);   // 09:00 + 30
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Theory]
		[InlineData(typeof(HospitalClinicDisruption), "HospitalClinicDisruptions")]
		[InlineData(typeof(HospitalDisruptionPatient), "HospitalDisruptionPatients")]
		public void Each_table_is_read_in_the_shape_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h161_clinic_disruptions.sql"), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h161_clinic_disruptions.sql"), entity, table);
		}
	}
}
