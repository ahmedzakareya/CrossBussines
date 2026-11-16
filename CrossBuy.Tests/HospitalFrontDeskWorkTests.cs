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
	// HOSPITAL H141: the desk's work. What must never happen:
	//   · a move that books the new time and leaves the old one standing (or cancels the old and books nothing);
	//   · a move into a slot someone took meanwhile, or into the past;
	//   · an arrived or cancelled appointment "confirmed" or moved from the desk;
	//   · a patient who asked to move left unconfirmed on the time they chose;
	//   · another company's appointment touched.
	// ==========================================================================================
	public class HospitalFrontDeskWorkTests
	{
		// Sunday 15 November 2026: the doctor works Sunday and Monday, 09:00-12:00 in 15-minute slots.
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime NowLocal = new(2026, 11, 15, 8, 0, 0);
		private static readonly DateTime NowUtc = new(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Tomorrow = Today.AddDays(1);

		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed record World(PlatformTestHost Host, HospitalOutpatientService Op, HospitalPatientService Patients, HospitalFrontDeskWorkService Work);

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var op = new HospitalOutpatientService(host.Db, access);
			return new World(host, op, new HospitalPatientService(host.Db, access), new HospitalFrontDeskWorkService(host.Db, access, op, new HospitalPortalBookingService(host.Db, access)));
		}

		private static async Task<int> Doctor(World w, string en, int companyId = 1)
		{
			var r = await w.Op.SavePractitionerAsync(Ctx(companyId), new HospitalPractitionerInput
			{ Name = "د. " + en, NameEn = "Dr " + en, WorkDays = new() { 0, 1 }, SessionStartMinute = 540, SessionEndMinute = 720, SlotMinutes = 15, Privileges = new() { "opd" } }, 7);
			Assert.True(r.Ok, r.Error); return r.Id!.Value;
		}

		private static async Task<int> Patient(World w, string en, int companyId = 1)
		{
			var r = await w.Patients.SaveAsync(Ctx(companyId), new HospitalPatientInput { Name = "م " + en, NameEn = en, Sex = "M" }, 7, NowUtc);
			Assert.True(r.Ok, r.Error); return r.Id!.Value;
		}

		private static async Task<int> Book(World w, int patient, int doctor, int minute, int companyId = 1)
		{
			var r = await w.Op.BookAsync(Ctx(companyId), new HospitalBookingInput { PatientId = patient, PractitionerId = doctor, Date = Tomorrow, StartMinute = minute, VisitType = HospitalVisitTypes.FollowUp }, 7, Today);
			Assert.True(r.Ok, r.Error); return r.Id!.Value;
		}

		private static Task<HospitalAppointment> Appt(World w, int id) => w.Host.Db.HospitalAppointments.AsNoTracking().SingleAsync(a => a.ID == id);

		[Fact]
		public void The_rules_refuse_what_the_desk_must_not_do()
		{
			Assert.Equal("Unknown answer", HospitalFrontDeskWorkRules.Refuse("Maybe", HospitalAppointmentStatus.Booked));
			Assert.Equal("Only a booked appointment is confirmed", HospitalFrontDeskWorkRules.Refuse(HospitalConfirmStatus.Confirmed, HospitalAppointmentStatus.Arrived));
			Assert.Null(HospitalFrontDeskWorkRules.Refuse(HospitalConfirmStatus.NoAnswer, HospitalAppointmentStatus.Booked));
			Assert.Equal("Only a booked appointment can be moved", HospitalFrontDeskWorkRules.RefuseMove(HospitalAppointmentStatus.Cancelled, Tomorrow, Today));
			Assert.Equal("An appointment cannot be booked in the past", HospitalFrontDeskWorkRules.RefuseMove(HospitalAppointmentStatus.Booked, Today.AddDays(-1), Today));
			Assert.False(HospitalFrontDeskWorkRules.SlotFree(new[] { 540, 555 }, 600));
		}

		[Fact]
		public async Task A_move_books_the_new_time_and_cancels_the_old_together()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			int dr = await Doctor(w, "Ali"), omar = await Patient(w, "Omar");
			int old = await Book(w, omar, dr, 540);

			var moved = await w.Work.RescheduleAsync(Ctx(), old, dr, Tomorrow, 600, 7, NowLocal, NowUtc);
			Assert.True(moved.Ok, moved.Error);
			Assert.Equal(HospitalAppointmentStatus.Cancelled, (await Appt(w, old)).Status);
			var now = await Appt(w, moved.Id!.Value);
			Assert.Equal((HospitalAppointmentStatus.Booked, 600, omar, HospitalVisitTypes.FollowUp), (now.Status, now.StartMinute, now.PatientId, now.VisitType));
		}

		[Fact]
		public async Task A_taken_slot_or_the_same_time_is_refused_and_nothing_changes()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			int dr = await Doctor(w, "Ali"), omar = await Patient(w, "Omar"), sara = await Patient(w, "Sara");
			int a = await Book(w, omar, dr, 540); await Book(w, sara, dr, 615);

			Assert.Equal("This slot is no longer free", (await w.Work.RescheduleAsync(Ctx(), a, dr, Tomorrow, 615, 7, NowLocal, NowUtc)).Error);
			Assert.Equal("Pick another time", (await w.Work.RescheduleAsync(Ctx(), a, dr, Tomorrow, 540, 7, NowLocal, NowUtc)).Error);
			Assert.Equal("This slot is no longer free", (await w.Work.RescheduleAsync(Ctx(), a, dr, Tomorrow, 545, 7, NowLocal, NowUtc)).Error);   // not a slot at all
			Assert.Equal(HospitalAppointmentStatus.Booked, (await Appt(w, a)).Status);
			Assert.Equal(2, await host.Db.HospitalAppointments.CountAsync());
		}

		[Fact]
		public async Task Answers_are_kept_per_appointment_and_a_patient_who_asked_to_move_is_confirmed_on_the_new_time()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			int dr = await Doctor(w, "Ali"), omar = await Patient(w, "Omar"), sara = await Patient(w, "Sara");
			int a = await Book(w, omar, dr, 540), b = await Book(w, sara, dr, 555);
			Assert.Equal(2, await w.Work.UnconfirmedAsync(Ctx(), Tomorrow));

			Assert.True((await w.Work.ConfirmAsync(Ctx(), b, HospitalConfirmStatus.NoAnswer, null, 7, NowUtc)).Ok);
			Assert.True((await w.Work.ConfirmAsync(Ctx(), b, HospitalConfirmStatus.Confirmed, null, 7, NowUtc)).Ok);
			var rowB = await host.Db.Set<HospitalDeskConfirmation>().AsNoTracking().SingleAsync(c => c.AppointmentId == b);
			Assert.Equal((HospitalConfirmStatus.Confirmed, 2), (rowB.Status, rowB.Calls));   // the last call wins; both calls counted
			Assert.Equal(1, await w.Work.UnconfirmedAsync(Ctx(), Tomorrow));

			Assert.True((await w.Work.ConfirmAsync(Ctx(), a, HospitalConfirmStatus.WantsToMove, "after 11", 7, NowUtc)).Ok);
			var moved = await w.Work.RescheduleAsync(Ctx(), a, dr, Tomorrow, 660, 7, NowLocal, NowUtc);
			Assert.True(moved.Ok, moved.Error);
			Assert.Equal(HospitalConfirmStatus.Confirmed, (await host.Db.Set<HospitalDeskConfirmation>().AsNoTracking().SingleAsync(c => c.AppointmentId == moved.Id)).Status);
			Assert.Equal(0, await w.Work.UnconfirmedAsync(Ctx(), Tomorrow));

			// the old appointment is cancelled now: nothing more is recorded or moved on it
			Assert.Equal("Only a booked appointment is confirmed", (await w.Work.ConfirmAsync(Ctx(), a, HospitalConfirmStatus.Confirmed, null, 7, NowUtc)).Error);
			Assert.Equal("Only a booked appointment can be moved", (await w.Work.RescheduleAsync(Ctx(), a, dr, Tomorrow, 675, 7, NowLocal, NowUtc)).Error);
		}

		[Fact]
		public async Task Another_companys_appointment_is_not_found()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			int dr2 = await Doctor(w, "Other", 2), p2 = await Patient(w, "Other", 2);
			int theirs = await Book(w, p2, dr2, 540, 2);
			Assert.Equal("not_found", (await w.Work.ConfirmAsync(Ctx(1), theirs, HospitalConfirmStatus.Confirmed, null, 7, NowUtc)).Error);
			Assert.Equal("not_found", (await w.Work.RescheduleAsync(Ctx(1), theirs, dr2, Tomorrow, 600, 7, NowLocal, NowUtc)).Error);
			Assert.Equal(HospitalAppointmentStatus.Booked, (await Appt(w, theirs)).Status);
		}

		[Fact]
		public async Task The_confirm_list_shows_tomorrows_booked_with_their_answers()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			int dr = await Doctor(w, "Ali"), omar = await Patient(w, "Omar"), sara = await Patient(w, "Sara");
			int a = await Book(w, omar, dr, 600), b = await Book(w, sara, dr, 540);
			await w.Work.ConfirmAsync(Ctx(), a, HospitalConfirmStatus.Confirmed, null, 7, NowUtc);
			var list = await w.Work.ConfirmListAsync(Ctx(), Tomorrow);
			Assert.Equal(new[] { b, a }, list.Select(r => r.A.Id).ToArray());   // by time
			Assert.Equal(new string?[] { null, HospitalConfirmStatus.Confirmed }, list.Select(r => r.Answer?.Status).ToArray());
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h141_desk_confirmations.sql"), typeof(HospitalDeskConfirmation), "HospitalDeskConfirmations");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h141_desk_confirmations.sql"), typeof(HospitalDeskConfirmation), "HospitalDeskConfirmations");
	}
}
