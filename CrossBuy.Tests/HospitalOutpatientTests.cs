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
	// HOSPITAL H4 — practitioners, the appointment book and the clinics board.
	//
	// What must never happen: two patients in one doctor's slot, one patient in two places at once,
	// a booking on a day the doctor is off, between their slots, in the past or on an expired licence,
	// a status jump (booked → done), two patients in one doctor's room, another company's doctor,
	// patient or appointment reached by id, or a caller without `book` writing anything.
	// ==========================================================================================
	public class HospitalOutpatientTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		// Sunday 15 November 2026 — the doctors below work Sunday and Monday.
		private static readonly DateTime Today = new(2026, 11, 15);
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
		}

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			return new World { Host = host, Op = new(host.Db, access), Patients = new(host.Db, access) };
		}

		private static async Task<int> DoctorAsync(World w, string en, int companyId = CompanyOne, DateTime? licence = null, int slot = 15)
		{
			var r = await w.Op.SavePractitionerAsync(Ctx(7, companyId), new HospitalPractitionerInput
			{
				Name = "د. " + en, NameEn = "Dr " + en, WorkDays = new() { 0, 1 }, SessionStartMinute = 9 * 60, SessionEndMinute = 12 * 60,
				SlotMinutes = slot, LicenseExpiry = licence, Privileges = new() { "opd" },
			}, 7);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		private static async Task<int> PatientAsync(World w, string en, int companyId = CompanyOne)
		{
			var r = await w.Patients.SaveAsync(Ctx(7, companyId), new HospitalPatientInput { Name = "م " + en, NameEn = en, Sex = "M" }, 7, Now);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		private static Task<HospitalResult> Book(World w, int patient, int doctor, int minute, DateTime? day = null, int companyId = CompanyOne)
			=> w.Op.BookAsync(Ctx(7, companyId), new HospitalBookingInput { PatientId = patient, PractitionerId = doctor, Date = day ?? Today, StartMinute = minute }, 7, Today);

		// ---- booking ------------------------------------------------------------------------------

		[Fact]
		public async Task A_slot_holds_one_patient_and_a_patient_is_never_in_two_places_at_once()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var drA = await DoctorAsync(w, "Ali"); var drB = await DoctorAsync(w, "Mona");
			var omar = await PatientAsync(w, "Omar"); var sara = await PatientAsync(w, "Sara");

			Assert.True((await Book(w, omar, drA, 9 * 60)).Ok);
			Assert.Equal("That slot is already booked", (await Book(w, sara, drA, 9 * 60)).Error);
			Assert.Equal("The patient already has an appointment at that time", (await Book(w, omar, drB, 9 * 60)).Error);
			Assert.True((await Book(w, omar, drB, 9 * 60 + 15)).Ok);   // right after: fine
			Assert.Equal(2, await host.Db.HospitalAppointments.CountAsync());
		}

		[Fact]
		public async Task Off_days_off_grid_minutes_and_the_past_are_refused()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = await DoctorAsync(w, "Ali"); var omar = await PatientAsync(w, "Omar");

			Assert.Equal("The doctor does not see patients on that day", (await Book(w, omar, dr, 9 * 60, Today.AddDays(2))).Error);   // Tuesday
			Assert.Equal("That time is not one of the doctor's slots", (await Book(w, omar, dr, 9 * 60 + 7)).Error);
			Assert.Equal("That time is not one of the doctor's slots", (await Book(w, omar, dr, 12 * 60)).Error);                  // session end
			Assert.Equal("An appointment cannot be booked in the past", (await Book(w, omar, dr, 9 * 60, Today.AddDays(-7))).Error);
			Assert.Equal(0, await host.Db.HospitalAppointments.CountAsync());
		}

		[Fact]
		public async Task Booking_stops_once_the_licence_has_expired_and_works_up_to_its_last_day()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = await DoctorAsync(w, "Ali", licence: Today.AddDays(1));   // expires Monday 16th
			var omar = await PatientAsync(w, "Omar");

			Assert.True((await Book(w, omar, dr, 9 * 60, Today.AddDays(1))).Ok);                 // the last valid day
			Assert.Equal("The doctor's licence has expired; booking is stopped until it is renewed", (await Book(w, omar, dr, 9 * 60, Today.AddDays(7))).Error);
		}

		[Fact]
		public async Task A_cancelled_appointment_frees_its_slot()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = await DoctorAsync(w, "Ali"); var omar = await PatientAsync(w, "Omar"); var sara = await PatientAsync(w, "Sara");
			var id = (await Book(w, omar, dr, 10 * 60)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(Ctx(7), id, HospitalAppointmentStatus.Cancelled, 7, Now)).Ok);
			Assert.True((await Book(w, sara, dr, 10 * 60)).Ok);
			Assert.Single(await w.Op.DayAsync(Ctx(7), Today));   // the cancelled one is not on the day
		}

		// ---- the visit's flow -----------------------------------------------------------------------

		[Fact]
		public async Task Arrival_numbers_the_queue_and_the_flow_cannot_skip_a_step()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = await DoctorAsync(w, "Ali"); var omar = await PatientAsync(w, "Omar"); var sara = await PatientAsync(w, "Sara");
			var a = (await Book(w, omar, dr, 9 * 60)).Id!.Value;
			var b = (await Book(w, sara, dr, 9 * 60 + 15)).Id!.Value;

			Assert.Equal("This appointment cannot make that move", (await w.Op.MoveAsync(Ctx(7), a, HospitalAppointmentStatus.Done, 7, Now)).Error);
			Assert.True((await w.Op.MoveAsync(Ctx(7), b, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			Assert.True((await w.Op.MoveAsync(Ctx(7), a, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var day = await w.Op.DayAsync(Ctx(7), Today);
			Assert.Equal(1, day.Single(x => x.Id == b).QueueNumber);   // first to arrive, first number
			Assert.Equal(2, day.Single(x => x.Id == a).QueueNumber);

			// Call next takes the lowest queue number, and the room holds one patient.
			Assert.True((await w.Op.CallNextAsync(Ctx(7), dr, Today, 7, Now)).Ok);
			Assert.Equal(HospitalAppointmentStatus.InRoom, (await host.Db.HospitalAppointments.AsNoTracking().SingleAsync(x => x.ID == b)).Status);
			Assert.Equal("The doctor already has a patient in the room", (await w.Op.MoveAsync(Ctx(7), a, HospitalAppointmentStatus.InRoom, 7, Now)).Error);
			Assert.True((await w.Op.MoveAsync(Ctx(7), b, HospitalAppointmentStatus.Done, 7, Now.AddMinutes(12))).Ok);
			Assert.NotNull((await host.Db.HospitalAppointments.AsNoTracking().SingleAsync(x => x.ID == b)).FinishedAt);
			Assert.True((await w.Op.CallNextAsync(Ctx(7), dr, Today, 7, Now.AddMinutes(13))).Ok);
			Assert.Equal("Nobody is waiting for this doctor", (await w.Op.CallNextAsync(Ctx(7), dr, Today, 7, Now.AddMinutes(14))).Error);
		}

		[Fact]
		public async Task The_board_shows_who_is_in_the_room_who_waits_who_is_next_and_the_delay()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = await DoctorAsync(w, "Ali");
			var p = new[] { await PatientAsync(w, "A"), await PatientAsync(w, "B"), await PatientAsync(w, "C"), await PatientAsync(w, "D") };
			var ids = new List<int>();
			for (int i = 0; i < 4; i++) ids.Add((await Book(w, p[i], dr, 9 * 60 + i * 15)).Id!.Value);
			foreach (var id in ids.Take(3)) Assert.True((await w.Op.MoveAsync(Ctx(7), id, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			Assert.True((await w.Op.CallNextAsync(Ctx(7), dr, Today, 7, Now)).Ok);   // A in the room
			Assert.True((await w.Op.MoveAsync(Ctx(7), ids[0], HospitalAppointmentStatus.Done, 7, Now)).Ok);
			Assert.True((await w.Op.CallNextAsync(Ctx(7), dr, Today, 7, Now)).Ok);   // B in the room

			// 09:50, C arrived at 09:00: kept 20 minutes past their 09:30 slot.
			var card = Assert.Single(await w.Op.BoardAsync(Ctx(7), Today, 9 * 60 + 50, Now.AddMinutes(50)));
			Assert.Equal(ids[1], card.Current!.Id);
			Assert.Equal(new[] { ids[2] }, card.Waiting.Select(x => x.Id).ToArray());
			Assert.Equal(1, card.Seen);
			Assert.Equal(ids[2], card.Next!.Id);              // an arrived patient comes before a booked one
			Assert.Equal(20, card.DelayMinutes);
			// A patient who only just arrived does not make the doctor look late, however old their slot.
			// At 14:00 C's slot is hours old, but C arrived (at Now) only five minutes before this moment.
			Assert.Equal(5, Assert.Single(await w.Op.BoardAsync(Ctx(7), Today, 14 * 60, Now.AddMinutes(5))).DelayMinutes);
		}

		// ---- practitioners ----------------------------------------------------------------------------

		[Fact]
		public async Task A_practitioner_needs_a_session_that_holds_a_slot_and_a_real_clinic_room()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var bad = await w.Op.SavePractitionerAsync(Ctx(7), new HospitalPractitionerInput { Name = "د", NameEn = "D", SessionStartMinute = 600, SessionEndMinute = 610, SlotMinutes = 15 }, 7);
			Assert.Equal("The session must end after it starts and hold at least one slot", bad.Error);
			var room = await w.Op.SavePractitionerAsync(Ctx(7), new HospitalPractitionerInput { Name = "د", NameEn = "D", ClinicRoomId = 999 }, 7);
			Assert.Equal("Choose a clinic room", room.Error);
			Assert.Equal("Unknown privilege", (await w.Op.SavePractitionerAsync(Ctx(7), new HospitalPractitionerInput { Name = "د", NameEn = "D", Privileges = new() { "magic" } }, 7)).Error);
			Assert.Equal(0, await host.Db.HospitalPractitioners.CountAsync());
			Assert.Equal(new[] { 540, 560, 580 }, HospitalOutpatientService.Slots(new HospitalPractitioner { SessionStartMinute = 540, SessionEndMinute = 600, SlotMinutes = 20 }).ToArray());
		}

		// ---- isolation and authority ------------------------------------------------------------------

		[Fact]
		public async Task Another_companys_doctor_patient_and_appointment_are_out_of_reach()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var mine = await DoctorAsync(w, "Ali"); var omar = await PatientAsync(w, "Omar");
			var theirs = await DoctorAsync(w, "Nour", CompanyTwo); var khaled = await PatientAsync(w, "Khaled", CompanyTwo);

			Assert.Equal("not_found", (await Book(w, omar, theirs, 9 * 60)).Error);
			Assert.Equal("not_found", (await Book(w, khaled, mine, 9 * 60)).Error);
			var theirAppt = (await Book(w, khaled, theirs, 9 * 60, companyId: CompanyTwo)).Id!.Value;
			Assert.Empty(await w.Op.DayAsync(Ctx(7), Today));
			Assert.Equal("not_found", (await w.Op.MoveAsync(Ctx(7), theirAppt, HospitalAppointmentStatus.Cancelled, 7, Now)).Error);
			Assert.Equal(HospitalAppointmentStatus.Booked, (await host.Db.HospitalAppointments.AsNoTracking().SingleAsync()).Status);
			Assert.DoesNotContain(await w.Op.PractitionersAsync(Ctx(7)), p => p.Row.ID == theirs);
			Assert.DoesNotContain(await w.Op.BoardAsync(Ctx(7), Today, 600, Now), c => c.Practitioner.Row.ID == theirs);
		}

		[Fact]
		public async Task A_caller_without_book_books_nothing_and_without_manage_adds_no_doctor()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = await DoctorAsync(w, "Ali"); var omar = await PatientAsync(w, "Omar");
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{
				CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee,
				PrincipalId = 2, Role = HospitalRoles.HospitalStaff, IsActive = true, CreatedAt = DateTime.UtcNow,
			});
			await host.Seed.SaveChangesAsync();

			Assert.Equal("forbidden", (await w.Op.BookAsync(Ctx(3), new HospitalBookingInput { PatientId = omar, PractitionerId = dr, Date = Today, StartMinute = 540 }, 3, Today)).Error);
			Assert.True((await w.Op.BookAsync(Ctx(2), new HospitalBookingInput { PatientId = omar, PractitionerId = dr, Date = Today, StartMinute = 540 }, 2, Today)).Ok);   // staff book
			Assert.Equal("forbidden", (await w.Op.SavePractitionerAsync(Ctx(2), new HospitalPractitionerInput { Name = "د", NameEn = "D" }, 2)).Error);       // but do not add doctors
			Assert.Equal(1, await host.Db.HospitalPractitioners.CountAsync());
		}
	}
}
