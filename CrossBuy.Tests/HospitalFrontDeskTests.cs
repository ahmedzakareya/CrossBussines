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
	// HOSPITAL H139: the front desk. What must never happen:
	//   · "احمد" missing the file of "أحمد", or Mohamed missing Muhammad;
	//   · a name outranking an exact file number or national ID;
	//   · a patient with no identity or an undecided payer checked in without the desk being told (never refused: care is not held for paperwork);
	//   · a walk-in put in a slot already past;
	//   · the wait counted from the appointment time rather than from arrival.
	// ==========================================================================================
	public class HospitalFrontDeskTests
	{
		private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Theory]
		[InlineData("Mohamed", "mhmd")]
		[InlineData("Muhammad", "mhmd")]
		[InlineData("Mohammed", "mhmd")]
		[InlineData("Ahmed", "ahmd")]
		[InlineData("Ahmad", "ahmd")]
		public void Latin_spellings_of_one_name_share_a_key(string name, string key) => Assert.Equal(key, HospitalFrontDeskRules.LatinKey(name));

		[Theory]
		[InlineData("احمد محمد", "أحمد محمد علي", null, 100)]             // hamza folded
		[InlineData("علي احمد", "أحمد محمد علي", null, 100)]              // any order
		[InlineData("محم", "محمد سعيد", null, 100)]                       // the start of a part
		[InlineData("فاطمة الزهراء", "فاطمه زهراء", null, 100)]          // taa marbuta and the article
		[InlineData("Muhammad Saeed", "محمد سعيد", "Mohamed Saeed", 100)] // the Latin spelling
		[InlineData("احمد خالد", "أحمد محمد علي", null, 50)]
		[InlineData("سارة", "أحمد محمد علي", null, 0)]
		public void A_typed_name_finds_the_file_however_it_is_spelled(string query, string name, string? nameEn, int score)
			=> Assert.Equal(score, HospitalFrontDeskRules.NameScore(query, name, nameEn));

		[Fact]
		public void The_board_reads_the_appointment_statuses_as_stages()
		{
			Assert.Equal(new[] { HospitalDeskStage.Expected, HospitalDeskStage.Waiting, HospitalDeskStage.WithDoctor, HospitalDeskStage.Done, HospitalDeskStage.NoShow, HospitalDeskStage.Cancelled },
				new[] { HospitalAppointmentStatus.Booked, HospitalAppointmentStatus.Arrived, HospitalAppointmentStatus.InRoom, HospitalAppointmentStatus.Done, HospitalAppointmentStatus.NoShow, HospitalAppointmentStatus.Cancelled }
					.Select(HospitalFrontDeskRules.Stage).ToArray());
		}

		[Fact]
		public void The_wait_runs_from_arrival_until_the_doctor_starts()
		{
			Assert.Null(HospitalFrontDeskRules.WaitMinutes(null, null, Now));
			Assert.Equal(25, HospitalFrontDeskRules.WaitMinutes(Now.AddMinutes(-25), null, Now));
			Assert.Equal(10, HospitalFrontDeskRules.WaitMinutes(Now.AddMinutes(-25), Now.AddMinutes(-15), Now));
			Assert.False(HospitalFrontDeskRules.Late(HospitalAppointmentStatus.Booked, 540, 555));
			Assert.True(HospitalFrontDeskRules.Late(HospitalAppointmentStatus.Booked, 540, 556));
			Assert.False(HospitalFrontDeskRules.Late(HospitalAppointmentStatus.Arrived, 540, 600));   // arrived: not late, waiting
		}

		[Fact]
		public void A_walk_in_takes_the_first_free_slot_from_now()
		{
			Assert.Equal(600, HospitalFrontDeskRules.WalkInSlot(new[] { 540, 600, 615 }, 595));
			Assert.Equal(540, HospitalFrontDeskRules.WalkInSlot(new[] { 540, 600 }, 548));   // began 8 minutes ago: still fine
			Assert.Null(HospitalFrontDeskRules.WalkInSlot(new[] { 540 }, 600));
		}

		[Fact]
		public void Identity_and_the_payer_need_attention_and_nothing_else_does()
		{
			var p = new HospitalPatient { Name = "x", NameEn = "x", Mrn = "MRN-1", Sex = "M" };
			var c = HospitalFrontDeskRules.Checklist(p, insured: false, ineligible: false, consents: 0, owed: 12.5m);
			Assert.Equal(new[] { "identity" }, c.Where(x => x.Blocks && !x.Done).Select(x => x.Key).ToArray());   // no ID, no photo
			Assert.Equal("self", c.Single(x => x.Key == "payer").Detail);
			Assert.False(c.Single(x => x.Key == "balance").Blocks);
			p.NationalId = "29001011234567";
			Assert.DoesNotContain(HospitalFrontDeskRules.Checklist(p, false, false, 0, 0), x => x.Blocks && !x.Done);
			Assert.Equal(new[] { "payer" }, HospitalFrontDeskRules.Checklist(p, true, true, 1, 0).Where(x => x.Blocks && !x.Done).Select(x => x.Key).ToArray());   // insured, not covered today
		}

		[Fact]
		public async Task The_one_search_ranks_an_exact_identifier_above_a_name()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var patients = new HospitalPatientService(host.Db, access);
			var a = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "أحمد محمد علي", NameEn = "Ahmed Mohamed Ali", Sex = "M", Phone = "01012345678", NationalId = "29001011234567" }, 7, Now)).Id!.Value;
			var b = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "أحمد سعيد", NameEn = "Ahmad Saeed", Sex = "M" }, 7, Now)).Id!.Value;
			await patients.SaveAsync(Ctx(2), new HospitalPatientInput { Name = "أحمد محمد علي", NameEn = "Ahmed Mohamed Ali", Sex = "M" }, 7, Now);   // another company's twin
			var svc = new HospitalFrontDeskService(host.Db, access, new HospitalOutpatientService(host.Db, access), new HospitalPortalBookingService(host.Db, access),
				new HospitalInsuranceService(host.Db, access), new HospitalBillingService(host.Db, access, new NoLedger()));

			var byName = await svc.SearchAsync(Ctx(), "احمد", Now.Date);
			Assert.Equal(new[] { a, b }.OrderBy(x => x), byName.Select(x => x.PatientId).OrderBy(x => x));   // both, and only this company's
			var latin = await svc.SearchAsync(Ctx(), "Muhammad Ali", Now.Date);
			Assert.Equal(a, Assert.Single(latin).PatientId);
			var byId = await svc.SearchAsync(Ctx(), "29001011234567", Now.Date);
			Assert.Equal((a, 100, "id"), (byId[0].PatientId, byId[0].Score, byId[0].Reason));
			var byPhone = await svc.SearchAsync(Ctx(), "012345678", Now.Date);
			Assert.Equal(a, byPhone[0].PatientId);
			Assert.Empty(await svc.SearchAsync(Ctx(), "ز", Now.Date));   // one letter: no search
		}

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}
	}
}
