using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H240 — the denial work-queue. What must never happen: a line rejected for a missing pre-authorisation
	// classed as anything else; a payer's Arabic reason left unclassified; a closed item worked again; "not pursued"
	// without why; an overturn counted that was never paid; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalDenialTests
	{
		[Theory]
		[InlineData(true, "X1", "any", HospitalDenialCause.Authorization)]
		[InlineData(false, "MN-1", "Service not medically necessary", HospitalDenialCause.Necessity)]
		[InlineData(false, "E02", "Member policy inactive on service date", HospitalDenialCause.Eligibility)]
		[InlineData(false, "C7", "ICD-10 code does not match the procedure", HospitalDenialCause.Coding)]
		[InlineData(false, "D1", "Duplicate of a line already paid", HospitalDenialCause.Duplicate)]
		[InlineData(false, "T", "Submitted after the time limit", HospitalDenialCause.Timely)]
		[InlineData(false, "P9", "Price exceeds the agreed tariff", HospitalDenialCause.Pricing)]
		[InlineData(false, "R", "Missing operative report", HospitalDenialCause.Documentation)]
		[InlineData(false, "", "الخدمة تحتاج موافقة مسبقة", HospitalDenialCause.Authorization)]
		[InlineData(false, "", "السعر يتجاوز التعرفة المتفق عليها", HospitalDenialCause.Pricing)]
		[InlineData(false, "Z", "see letter", HospitalDenialCause.Other)]
		public void The_root_cause_is_suggested_from_the_line_and_the_payers_words(bool missingPreauth, string code, string reason, string cause) =>
			Assert.Equal(cause, HospitalDenialRules.SuggestCause(missingPreauth, code, reason));

		[Fact]
		public void The_deadline_is_the_answer_day_plus_the_window_and_only_open_items_are_overdue()
		{
			var day = new DateTime(2026, 11, 1, 15, 0, 0);
			Assert.Equal(new DateTime(2026, 12, 1), HospitalDenialRules.DueDate(day, null));
			Assert.Equal(new DateTime(2026, 11, 15), HospitalDenialRules.DueDate(day, 14));
			Assert.True(HospitalDenialRules.Overdue(HospitalDenialStatus.Working, new DateTime(2026, 12, 1), new DateTime(2026, 12, 2)));
			Assert.False(HospitalDenialRules.Overdue(HospitalDenialStatus.Open, new DateTime(2026, 12, 1), new DateTime(2026, 12, 1)));
			Assert.False(HospitalDenialRules.Overdue(HospitalDenialStatus.Resubmitted, new DateTime(2026, 12, 1), new DateTime(2027, 1, 1)));
		}

		[Fact]
		public void A_closed_item_stays_closed_and_not_pursued_says_why()
		{
			Assert.Equal("This denial is already closed", HospitalDenialRules.UpdateRefusal(HospitalDenialStatus.NotPursued, null, HospitalDenialStatus.Working, "x"));
			Assert.Equal("Write why it is not pursued", HospitalDenialRules.UpdateRefusal(HospitalDenialStatus.Working, null, HospitalDenialStatus.NotPursued, " "));
			Assert.Null(HospitalDenialRules.UpdateRefusal(HospitalDenialStatus.Working, null, HospitalDenialStatus.NotPursued, "Below the cost of appeal"));
			Assert.Equal("not_found", HospitalDenialRules.UpdateRefusal(HospitalDenialStatus.Open, null, HospitalDenialStatus.Resubmitted, null));   // only the batch resubmits
			Assert.NotNull(HospitalDenialRules.UpdateRefusal(HospitalDenialStatus.Open, "Weather", null, null));
		}

		[Fact]
		public void Causes_rank_by_amount_and_an_overturn_is_resubmitted_and_paid()
		{
			var s = HospitalDenialRules.ByCause(new[]
			{
				(HospitalDenialCause.Coding, 10m, HospitalDenialStatus.Resubmitted, true),
				(HospitalDenialCause.Coding, 15m, HospitalDenialStatus.Resubmitted, false),
				(HospitalDenialCause.Coding, 5m, HospitalDenialStatus.Open, false),
				(HospitalDenialCause.Authorization, 50m, HospitalDenialStatus.NotPursued, false),
			});
			Assert.Equal(new[] { HospitalDenialCause.Authorization, HospitalDenialCause.Coding }, s.Select(x => x.Cause));
			var coding = s[1];
			Assert.Equal((3, 30m, 1, 2), (coding.Count, coding.Amount, coding.Overturned, coding.Closed));
			Assert.Equal(50m, coding.OverturnPct);
			Assert.Equal(0m, s[0].OverturnPct);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDenialModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h240_denials.sql");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalDenialWork), "HospitalDenialWork");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalDenialWork), "HospitalDenialWork");
		}
	}
}
