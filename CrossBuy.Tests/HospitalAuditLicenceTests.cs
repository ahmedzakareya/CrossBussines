using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H264–H265 — the audit viewer's range and the licence reminders. What must never happen: an unbounded
	// search over the whole log; a reversed range; a lapsed licence shown as valid; a missing licence shown as valid; a
	// reminder sent twice for one stage, or a renewed licence silenced by the old one's reminder.
	// ==========================================================================================
	public class HospitalAuditLicenceTests
	{
		private static readonly DateTime Today = new(2026, 11, 23);

		[Fact]
		public void The_search_range_is_bounded_and_ordered()
		{
			Assert.Equal((Today.AddDays(-6), Today), HospitalAccessAuditRules.Range(null, null, Today));
			Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 10)), HospitalAccessAuditRules.Range(new DateTime(2026, 11, 10), new DateTime(2026, 11, 1), Today));
			var (f, t) = HospitalAccessAuditRules.Range(new DateTime(2025, 1, 1), Today, Today);
			Assert.Equal(Today, t); Assert.Equal(91, (t - f).TotalDays);
		}

		[Fact]
		public void Licence_states()
		{
			Assert.Equal("Expired", HospitalLicenceRules.State("KW-1", Today.AddDays(-1), Today));
			Assert.Equal("Expiring", HospitalLicenceRules.State("KW-1", Today, Today));
			Assert.Equal("Expiring", HospitalLicenceRules.State("KW-1", Today.AddDays(60), Today));
			Assert.Equal("Valid", HospitalLicenceRules.State("KW-1", Today.AddDays(61), Today));
			Assert.Equal("Missing", HospitalLicenceRules.State(null, Today.AddDays(100), Today));
			Assert.Equal("Missing", HospitalLicenceRules.State("KW-1", null, Today));
		}

		[Fact]
		public void Reminder_stages_and_their_keys()
		{
			Assert.Null(HospitalLicenceRules.Stage(Today.AddDays(61), Today));
			Assert.Equal(60, HospitalLicenceRules.Stage(Today.AddDays(45), Today));
			Assert.Equal(30, HospitalLicenceRules.Stage(Today.AddDays(30), Today));
			Assert.Equal(7, HospitalLicenceRules.Stage(Today.AddDays(1), Today));
			Assert.Equal(0, HospitalLicenceRules.Stage(Today.AddDays(-3), Today));
			Assert.Null(HospitalLicenceRules.Stage(null, Today));
			// one key per licence, per expiry date and per stage: a renewal (new date) starts the reminders again
			Assert.NotEqual(HospitalLicenceRules.DedupKey(5, Today, 30), HospitalLicenceRules.DedupKey(5, Today, 7));
			Assert.NotEqual(HospitalLicenceRules.DedupKey(5, Today, 30), HospitalLicenceRules.DedupKey(5, Today.AddYears(1), 30));
			Assert.Equal(HospitalLicenceRules.DedupKey(5, Today, 30), HospitalLicenceRules.DedupKey(5, Today, 30));
		}
	}
}
