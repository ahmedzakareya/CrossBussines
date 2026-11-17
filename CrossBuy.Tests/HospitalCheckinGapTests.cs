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
	// HOSPITAL H159: check-in gaps. What must never happen:
	//   · a follow-up with no reason or no due date, or due in the past;
	//   · a follow-up for a step the file already shows done, or two open for the same step;
	//   · a step done on the file left open on the list (it must close by itself);
	//   · a waiver with no reason, or a closed follow-up reopened by "finished" or "take";
	//   · "mine" showing a colleague's, or another company's gaps showing at all.
	// ==========================================================================================
	public class HospitalCheckinGapTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId = 7, int companyId = 1) => new() { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task<(PlatformTestHost Host, HospitalCheckinGapService Svc, int Patient)> World()
		{
			var host = new PlatformTestHost();
			var p = (await new HospitalPatientService(host.Db, Access(host)).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, Now)).Id!.Value;
			return (host, new HospitalCheckinGapService(host.Db, Access(host)), p);
		}

		private static HospitalCheckinGapInput Gap(int patient, string item, int? owner = null, int days = 1, string reason = "brings the ID tomorrow")
			=> new() { PatientId = patient, Item = item, Reason = reason, OwnerEmployeeId = owner, DueDate = Today.AddDays(days) };

		[Theory]
		[InlineData("passport", "x", 1, "Choose the missing step")]
		[InlineData(HospitalCheckinItem.Identity, " ", 1, "Write why it could not be finished at the counter")]
		[InlineData(HospitalCheckinItem.Identity, "x", -1, "The due date cannot be in the past")]
		[InlineData(HospitalCheckinItem.Identity, "x", 91, "The due date must be within 90 days")]
		[InlineData(HospitalCheckinItem.Payer, "x", 0, null)]
		public void A_follow_up_names_the_step_why_and_by_when(string item, string reason, int days, string? refusal)
			=> Assert.Equal(refusal, HospitalCheckinGapRules.Refuse(new HospitalCheckinGapInput { PatientId = 1, Item = item, Reason = reason, DueDate = Today.AddDays(days) }, Today));

		[Fact]
		public void Only_an_open_gap_past_its_date_is_overdue()
		{
			var g = new HospitalCheckinGap { Status = HospitalCheckinGapStatus.Open, DueDate = Today };
			Assert.False(HospitalCheckinGapRules.Overdue(g, Today));   // due today is not late yet
			Assert.True(HospitalCheckinGapRules.Overdue(g, Today.AddDays(1)));
			g.Status = HospitalCheckinGapStatus.Done;
			Assert.False(HospitalCheckinGapRules.Overdue(g, Today.AddDays(5)));
		}

		[Fact]
		public async Task One_open_follow_up_per_step_and_none_for_a_step_already_done()
		{
			var (host, svc, p) = await World();
			using var _h = host;
			Assert.True((await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Identity), 7, Now, Today)).Ok);
			Assert.Equal("This step is already being followed for the patient", (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Identity), 7, Now, Today)).Error);
			Assert.True((await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Payer), 7, Now, Today)).Ok);   // another step is its own follow-up
			var row = await host.Db.HospitalPatients.FindAsync(p); row!.Phone = "+96550000000"; await host.Db.SaveChangesAsync();
			Assert.Equal("This step is already done on the file", (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Phone), 7, Now, Today)).Error);
			Assert.Equal("not_found", (await svc.OpenAsync(Ctx(companyId: 2), Gap(p, HospitalCheckinItem.Photo), 7, Now, Today)).Error);   // another company's patient
		}

		[Fact]
		public async Task A_step_done_on_the_file_closes_its_follow_up_by_itself_and_only_that_one()
		{
			var (host, svc, p) = await World();
			using var _h = host;
			var id = (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Identity), 7, Now, Today)).Id!.Value;
			var pay = (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Payer), 7, Now, Today)).Id!.Value;
			Assert.Equal(0, await svc.SettleAsync(Ctx(), null, Now));
			var row = await host.Db.HospitalPatients.FindAsync(p); row!.NationalId = "283073012345"; await host.Db.SaveChangesAsync();
			Assert.Equal(1, await svc.SettleAsync(Ctx(), new[] { p }, Now.AddHours(1)));
			var g = await host.Db.Set<HospitalCheckinGap>().FindAsync(id);
			Assert.Equal((HospitalCheckinGapStatus.Done, "auto"), (g!.Status, g.CloseNote));
			Assert.Equal(HospitalCheckinGapStatus.Open, (await host.Db.Set<HospitalCheckinGap>().FindAsync(pay))!.Status);   // the payer is closed by hand only
		}

		[Fact]
		public async Task Finished_waived_taken_and_the_lists_by_owner()
		{
			var (host, svc, p) = await World();
			using var _h = host;
			var mine = (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Payer, owner: null, days: 0), 7, Now, Today)).Id!.Value;
			var hers = (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Balance, owner: 9), 7, Now, Today));
			Assert.Equal("Choose someone from this company", hers.Error);   // employee 9 is not on file in this host
			var nobody = (await svc.OpenAsync(Ctx(), Gap(p, HospitalCheckinItem.Document), 7, Now, Today)).Id!.Value;

			Assert.Equal(new HospitalCheckinGapCounts(2, 0, 0, 2), await svc.CountsAsync(Ctx(), 7, Today));
			Assert.True((await svc.TakeAsync(Ctx(), mine, 7)).Ok);
			Assert.Equal(new HospitalCheckinGapCounts(2, 1, 1, 1), await svc.CountsAsync(Ctx(), 7, Today.AddDays(1)));   // the one due today is late tomorrow
			Assert.Single((await svc.ListAsync(Ctx(), null, "mine", null, "open", 7, 1, 10, Today)).Rows);
			Assert.Empty((await svc.ListAsync(Ctx(), null, "mine", null, "open", 8, 1, 10, Today)).Rows);   // a colleague's "mine" is empty
			Assert.Equal(2, (await svc.ListAsync(Ctx(), null, "desk", null, "open", 7, 1, 10, Today)).Total);
			Assert.Equal(0, (await svc.ListAsync(Ctx(companyId: 2), null, null, null, "all", 7, 1, 10, Today)).Total);

			Assert.Equal("Write why the step is waived", (await svc.WaiveAsync(Ctx(), nobody, "  ", 7, Now)).Error);
			Assert.True((await svc.WaiveAsync(Ctx(), nobody, "the patient has no document of this kind", 7, Now)).Ok);
			Assert.True((await svc.DoneAsync(Ctx(), mine, null, 7, Now)).Ok);
			Assert.Equal("This follow-up is closed already", (await svc.DoneAsync(Ctx(), mine, null, 7, Now)).Error);
			Assert.Equal("This follow-up is closed already", (await svc.TakeAsync(Ctx(), nobody, 7)).Error);
			Assert.Equal(new HospitalCheckinGapCounts(0, 0, 0, 0), await svc.CountsAsync(Ctx(), 7, Today));
			Assert.Equal(2, (await svc.ListAsync(Ctx(), null, null, null, "closed", 7, 1, 10, Today)).Total);
		}
	}
}
