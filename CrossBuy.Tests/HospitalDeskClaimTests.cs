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
	// HOSPITAL H164: ownership on the desk's queues. What must never happen:
	//   · "mine" showing a colleague's item, or "unassigned" one somebody holds;
	//   · an item given back by someone who does not hold it;
	//   · lateness judged by one clock for every queue (each queue has its own), or a duplicate pair turning overdue.
	// ==========================================================================================
	public class HospitalDeskClaimTests
	{
		private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId = 7, int companyId = 1) => new() { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static HospitalDeskWorkItem Item(string kind, string key, DateTime? at = null, DateTime? due = null, bool urgent = false)
			=> new(kind, null, "x", null, null, null, null, null, null, at, due, urgent, null, null, key);

		[Theory]
		[InlineData("portal", 5 * 60, false, true)]
		[InlineData("portal", 3 * 60, false, false)]
		[InlineData("intake", 20, true, true)]    // urgent: 10 minutes
		[InlineData("intake", 20, false, false)]  // not urgent: 30 minutes
		[InlineData("preauth", 25 * 60, false, true)]
		[InlineData("dup", 99 * 60, false, false)] // backlog, never overdue
		public void Each_queue_keeps_its_own_clock(string kind, int minutesOld, bool urgent, bool overdue)
			=> Assert.Equal(overdue, HospitalDeskQueueRules.Overdue(Item(kind, "1", Now.AddMinutes(-minutesOld), urgent: urgent), Now));

		[Fact]
		public void A_waitlist_offer_is_late_when_it_is_about_to_expire()
		{
			Assert.True(HospitalDeskQueueRules.Overdue(Item("waitlist", "1", due: Now.AddMinutes(30)), Now));
			Assert.False(HospitalDeskQueueRules.Overdue(Item("waitlist", "1", due: Now.AddHours(3)), Now));
		}

		[Fact]
		public async Task Mine_unassigned_and_all_and_only_the_holder_gives_back()
		{
			using var host = new PlatformTestHost();
			var svc = new HospitalDeskClaimService(host.Db, new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance));
			var all = new[] { Item("portal", "1", Now.AddHours(-5)), Item("portal", "2", Now), Item("portal", "3", Now) };
			Assert.True((await svc.TakeAsync(Ctx(7), "portal", "1", 7, Now)).Ok);
			Assert.Equal("This item is yours already", (await svc.TakeAsync(Ctx(7), "portal", "1", 7, Now)).Error);
			Assert.True((await svc.TakeAsync(Ctx(8), "portal", "2", 8, Now)).Ok);
			Assert.Equal("not_found", (await svc.TakeAsync(Ctx(7), "bills", "2", 7, Now)).Error);   // not a desk queue

			var (mine, counts) = await svc.QueueAsync(Ctx(7), all, "mine", 7, Now, 8);
			Assert.Equal(new HospitalQueueCounts(3, 1, 1, 1), counts);
			Assert.Equal("1", Assert.Single(mine).Item.Key);
			Assert.Equal("3", Assert.Single((await svc.QueueAsync(Ctx(7), all, "unassigned", 7, Now, 8)).Items).Item.Key);
			Assert.True((await svc.QueueAsync(Ctx(7), all, "all", 7, Now, 8)).Items[0].Overdue);   // the late one first

			Assert.Equal("Only the one who took an item releases it", (await svc.ReleaseAsync(Ctx(7), "portal", "2", 7, Now)).Error);
			Assert.True((await svc.TakeAsync(Ctx(7), "portal", "2", 7, Now)).Ok);   // handed over: taking from a colleague is allowed
			Assert.True((await svc.ReleaseAsync(Ctx(7), "portal", "2", 7, Now)).Ok);
			Assert.Equal(new HospitalQueueCounts(3, 1, 2, 1), (await svc.QueueAsync(Ctx(7), all, null, 7, Now, 8)).Counts);
			Assert.Equal(0, (await svc.QueueAsync(Ctx(7, 2), all, null, 7, Now, 8)).Counts.Mine);   // another company owns nothing here
		}

		[Fact]
		public void The_table_is_read_in_the_shape_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalPharmacyModel.Configure);
			HospitalSliceParity.NamesItsTable(model, HospitalSliceParity.Slice("hospital_h164_desk_claims.sql"), typeof(HospitalDeskClaim), "HospitalDeskClaims");
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h164_desk_claims.sql"), typeof(HospitalDeskClaim), "HospitalDeskClaims");
		}
	}
}
