using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H154: the reception desk's "where is the patient?" and "what is their history?". What must never happen:
	//   · a confidential file's place or history given at the desk;
	//   · a diagnosis, result or note in the desk's history (it is administrative: with whom, where, when, how it ended);
	//   · a past booking nobody came to counted as a visit;
	//   · another company's events in the history.
	// ==========================================================================================
	public class HospitalDeskHistoryTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static async Task<(PlatformTestHost Host, int Patient, int Doctor)> World()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var op = new HospitalOutpatientService(host.Db, access);
			var patients = new HospitalPatientService(host.Db, access);
			var dr = (await op.SavePractitionerAsync(Ctx(), new HospitalPractitionerInput { Name = "د. سامي", NameEn = "Dr Sami", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 660, SlotMinutes = 15, Privileges = new() { "opd" } }, 7)).Id!.Value;
			var p = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, Now)).Id!.Value;
			return (host, p, dr);
		}

		private static HospitalAppointment Appt(int patient, int doctor, DateTime day, string status) => new()
		{ CompanyId = 1, PatientId = patient, PractitionerId = doctor, AppointmentDate = day, StartMinute = 540, DurationMinutes = 15, VisitType = "New", Status = status, CreatedAt = Now };

		[Fact]
		public async Task The_history_counts_what_happened_and_says_nothing_clinical()
		{
			var (host, p, dr) = await World();
			using var _h = host;
			host.Db.AddRange(Appt(p, dr, Today.AddDays(-30), HospitalAppointmentStatus.Done), Appt(p, dr, Today.AddDays(-10), HospitalAppointmentStatus.Booked),   // never came
				Appt(p, dr, Today.AddDays(-5), HospitalAppointmentStatus.Cancelled), Appt(p, dr, Today.AddDays(3), HospitalAppointmentStatus.Booked));              // future: not history
			host.Db.Add(new HospitalAdmission { CompanyId = 1, PatientId = p, Status = HospitalAdmissionStatus.Discharged, AdmittedAt = Now.AddDays(-20), DischargedAt = Now.AddDays(-17), Diagnosis = "Pneumonia", CreatedAt = Now });
			await host.Db.SaveChangesAsync();

			var h = await new HospitalDeskHistoryService(host.Db).HistoryAsync(Ctx(), p, 10, Today);
			Assert.False(h.Restricted);
			Assert.Equal((1, 1, 1), (h.Visits, h.NoShows, h.Admissions));
			Assert.Equal(4, h.Recent.Count);   // the future booking is not history
			Assert.Equal(HospitalDeskEventKind.Visit, h.Recent[0].Kind);   // newest first: the cancelled one
			Assert.Equal(HospitalAppointmentStatus.NoShow, h.Recent.Single(e => e.At.Date == Today.AddDays(-10)).State);
			Assert.Equal(("Dr Sami", HospitalAdmissionStatus.Discharged), (h.Recent[0].TitleEn, h.Recent.Single(e => e.Kind == HospitalDeskEventKind.Admission).State));
			Assert.DoesNotContain(h.Recent, e => (e.Title + e.TitleEn + e.Detail + e.DetailEn).Contains("Pneumonia"));   // the diagnosis stays in the file
			Assert.Equal(Today.AddDays(-30), h.FirstSeen!.Value.Date);
			Assert.Empty((await new HospitalDeskHistoryService(host.Db).HistoryAsync(Ctx(2), p, 10, Today)).Recent);   // another company
		}

		[Fact]
		public async Task A_confidential_file_gives_the_desk_neither_its_place_nor_its_history()
		{
			var (host, p, dr) = await World();
			using var _h = host;
			host.Db.Add(Appt(p, dr, Today.AddDays(-30), HospitalAppointmentStatus.Done));
			host.Db.Add(new HospitalAdmission { CompanyId = 1, PatientId = p, Status = HospitalAdmissionStatus.Active, AdmittedAt = Now.AddDays(-1), CreatedAt = Now });
			host.Db.Add(new HospitalRestrictedFile { CompanyId = 1, PatientId = p, Reason = "VIP", RestrictedAt = Now, RestrictedByEmployeeId = 1 });
			await host.Db.SaveChangesAsync();

			var h = await new HospitalDeskHistoryService(host.Db).HistoryAsync(Ctx(), p, 10, Today);
			Assert.True(h.Restricted);
			Assert.Empty(h.Recent);
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var (where, restricted) = await new HospitalWhereaboutsService(host.Db, access).ForDeskAsync(Ctx(), new[] { p }, Now);
			Assert.Contains(p, restricted);
			Assert.False(where.ContainsKey(p));   // its place is never even read
		}
	}
}
