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
	// HOSPITAL H160: checkout at the desk. What must never happen:
	//   · a visit whose consultation is not finished offered for checkout, or one already closed still listed;
	//   · an advised follow-up, or tests / medicines still waiting, closed with no word from the desk;
	//   · a follow-up counted as missing when a later appointment is already booked;
	//   · the same visit closed twice.
	// And (H158-H160) each new table is read in the shape its slice creates.
	// ==========================================================================================
	public class HospitalDeskCheckoutTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		private static async Task<(PlatformTestHost Host, HospitalDeskCheckoutService Svc, int Patient, int Doctor)> World()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var dr = (await new HospitalOutpatientService(host.Db, access).SavePractitionerAsync(Ctx(), new HospitalPractitionerInput { Name = "د. سامي", NameEn = "Dr Sami", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 660, SlotMinutes = 15, Privileges = new() { "opd" } }, 7)).Id!.Value;
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, Now)).Id!.Value;
			return (host, new HospitalDeskCheckoutService(host.Db, access, new HospitalBillingService(host.Db, access, new NoLedger())), p, dr);
		}

		private static HospitalAppointment Appt(int patient, int doctor, DateTime day, string status, int minute = 540) => new()
		{ CompanyId = 1, PatientId = patient, PractitionerId = doctor, AppointmentDate = day, StartMinute = minute, DurationMinutes = 15, VisitType = "New", Status = status, CreatedAt = Now, FinishedAt = status == HospitalAppointmentStatus.Done ? Now : null };

		[Fact]
		public async Task Only_finished_consultations_await_the_desk_and_a_closed_one_leaves_the_list()
		{
			var (host, svc, p, dr) = await World();
			using var _h = host;
			var done = Appt(p, dr, Today, HospitalAppointmentStatus.Done); var waiting = Appt(p, dr, Today, HospitalAppointmentStatus.Arrived, 600); var yesterday = Appt(p, dr, Today.AddDays(-1), HospitalAppointmentStatus.Done);
			host.Db.AddRange(done, waiting, yesterday); await host.Db.SaveChangesAsync();
			var row = Assert.Single(await svc.AwaitingAsync(Ctx(), Today));
			Assert.Equal(done.ID, row.AppointmentId);
			Assert.Equal("The consultation is not finished yet", (await svc.CloseAsync(Ctx(), waiting.ID, null, 7, Today, Now)).Error);
			Assert.True((await svc.CloseAsync(Ctx(), done.ID, null, 7, Today, Now)).Ok);   // nothing open: no word needed
			Assert.Empty(await svc.AwaitingAsync(Ctx(), Today));
			Assert.Equal("This visit is checked out already", (await svc.CloseAsync(Ctx(), done.ID, null, 7, Today, Now)).Error);
			Assert.Empty(await svc.AwaitingAsync(Ctx(2), Today));   // another company
		}

		[Fact]
		public async Task An_advised_follow_up_and_waiting_orders_need_the_desks_word_and_are_kept()
		{
			var (host, svc, p, dr) = await World();
			using var _h = host;
			var a = Appt(p, dr, Today, HospitalAppointmentStatus.Done); host.Db.Add(a); await host.Db.SaveChangesAsync();
			var enc = new HospitalEncounter { CompanyId = 1, AppointmentId = a.ID, PatientId = p, PractitionerId = dr, Status = HospitalEncounterStatus.Signed, FollowUpDays = 14, StartedAt = Now, CreatedAt = Now };
			host.Db.Add(enc); await host.Db.SaveChangesAsync();
			host.Db.Add(new HospitalLabOrder { CompanyId = 1, PatientId = p, EncounterId = enc.ID, AccessionNo = "L-CO1", Code = "CBC", Name = "صورة دم", NameEn = "Blood count", Status = HospitalLabStatus.Ordered, OrderedAt = Now });
			await host.Db.SaveChangesAsync();

			var s = (await svc.SheetAsync(Ctx(), a.ID, Today, Now))!;
			Assert.Equal((14, Today.AddDays(14), (DateTime?)null, 1, true), (s.FollowUpDays!.Value, s.FollowUpBy!.Value, s.NextBooked, s.Tests, s.Unresolved));
			Assert.Equal("Something is still open: write why the visit is closed anyway", (await svc.CloseAsync(Ctx(), a.ID, " ", 7, Today, Now)).Error);
			Assert.True((await svc.CloseAsync(Ctx(), a.ID, "the patient will call to book; the test is tomorrow", 7, Today, Now)).Ok);
			var row = host.Db.Set<HospitalDeskCheckout>().Single();
			Assert.Equal((14, false, 1, enc.ID), (row.FollowUpDays!.Value, row.FollowUpBooked, row.OpenOrders, row.EncounterId!.Value));
		}

		[Fact]
		public async Task A_later_appointment_already_booked_settles_the_follow_up()
		{
			var (host, svc, p, dr) = await World();
			using var _h = host;
			var a = Appt(p, dr, Today, HospitalAppointmentStatus.Done); host.Db.AddRange(a, Appt(p, dr, Today.AddDays(13), HospitalAppointmentStatus.Booked)); await host.Db.SaveChangesAsync();
			host.Db.Add(new HospitalEncounter { CompanyId = 1, AppointmentId = a.ID, PatientId = p, PractitionerId = dr, Status = HospitalEncounterStatus.Signed, FollowUpDays = 14, StartedAt = Now, CreatedAt = Now });
			await host.Db.SaveChangesAsync();
			var s = (await svc.SheetAsync(Ctx(), a.ID, Today, Now))!;
			Assert.Equal((Today.AddDays(13), false, false), (s.NextBooked!.Value, s.FollowUpOpen, s.Unresolved));
			Assert.True((await svc.CloseAsync(Ctx(), a.ID, null, 7, Today, Now)).Ok);
			Assert.True(host.Db.Set<HospitalDeskCheckout>().Single().FollowUpBooked);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Theory]
		[InlineData("hospital_h158_desk_handoffs.sql", typeof(HospitalDeskHandoff), "HospitalDeskHandoffs")]
		[InlineData("hospital_h159_checkin_gaps.sql", typeof(HospitalCheckinGap), "HospitalCheckinGaps")]
		[InlineData("hospital_h160_desk_checkouts.sql", typeof(HospitalDeskCheckout), "HospitalDeskCheckouts")]
		public void Each_desk_table_is_read_in_the_shape_its_slice_creates(string slice, Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice(slice), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice(slice), entity, table);
		}
	}
}
