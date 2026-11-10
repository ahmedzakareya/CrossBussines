using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H82 — payer recoupment, with no database: when a line can be recouped, what the payer finally paid, the
	// doctor's share given back (percent and per-unit), the ledger direction of a recoupment, and the slice.
	// ==========================================================================================
	public class HospitalRecoupTests
	{
		[Fact]
		public void Only_an_accepted_line_is_recouped_once_within_what_was_accepted_and_with_a_reason()
		{
			Assert.Null(HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Accepted, 800m, null, 300m, "audit: no indication"));
			Assert.Null(HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Accepted, 800m, null, 800m, "x"));   // all of it
			Assert.Equal("Only an accepted line can be recouped", HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Rejected, 800m, null, 100m, "x"));
			Assert.Equal("This line was already recouped", HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Accepted, 800m, 100m, 100m, "x"));
			Assert.Equal("Write the amount the payer takes back", HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Accepted, 800m, null, 0m, "x"));
			Assert.Equal("The payer cannot take back more than it accepted", HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Accepted, 800m, null, 800.01m, "x"));
			Assert.Equal("Write the payer's reason", HospitalRecoupRules.RecoupRefusal(HospitalClaimLineStatus.Accepted, 800m, null, 100m, " "));
		}

		[Fact]
		public void The_payer_finally_paid_what_it_accepted_less_what_it_took_back()
		{
			Assert.Equal(500m, HospitalRecoupRules.NetAccepted(800m, 300m));
			Assert.Equal(800m, HospitalRecoupRules.NetAccepted(800m, null));
			Assert.Equal(0m, HospitalRecoupRules.NetAccepted(800m, 800m));
		}

		[Fact]
		public void The_doctor_gives_back_his_percent_of_what_was_taken_and_a_unit_fee_only_for_the_whole_line()
		{
			var pct = new HospitalFeeRule { Basis = HospitalFeeBasis.Percent, Value = 30m };
			var unit = new HospitalFeeRule { Basis = HospitalFeeBasis.PerUnit, Value = 50m };
			Assert.Equal(-90m, HospitalRecoupRules.RecoupShare(pct, 300m, 800m, 1, false, 2));
			Assert.Equal(0m, HospitalRecoupRules.RecoupShare(unit, 300m, 800m, 1, false, 2));     // part of the line: the visit stands
			Assert.Equal(-100m, HospitalRecoupRules.RecoupShare(unit, 800m, 800m, 2, false, 2));  // the whole line, two units
			Assert.Equal(0m, HospitalRecoupRules.RecoupShare(unit, 800m, 800m, 1, true, 2));      // the unit fee was earned on the patient's part
		}

		[Fact]
		public void A_recoupment_debits_the_rejections_and_credits_the_payer_receivable()
		{
			var lines = HospitalClaimAccountingRules.Lines(HospitalClaimEvent.Recoupment, 300m, payerReceivable: 11, counter: 22, memo: "m");
			Assert.Equal(2, lines.Count);
			Assert.Contains(lines, l => l.AccountId == 22 && l.Debit == 300m && l.Credit == 0);
			Assert.Contains(lines, l => l.AccountId == 11 && l.Credit == 300m && l.Debit == 0);
			// it lowers what the payer still owes on its batch
			Assert.Equal(500m, HospitalClaimAccountingRules.Outstanding(800m, new[] { (HospitalClaimEvent.Recoupment, 300m) }));
		}

		[Fact]
		public void The_slice_adds_the_columns_and_widens_the_fee_part()
		{
			var s = HospitalSliceParity.Slice("hospital_h82_recoupment.sql");
			foreach (var c in new[] { "RecoupedAmount decimal(18,4) NULL", "RecoupedAt datetime2 NULL", "RecoupReason nvarchar(300) NULL", "RecoupedByEmployeeId int NULL" })
				Assert.Contains(c, s);
			Assert.Contains("Part IN ('Patient','Payer','Recoup')", s);
			Assert.True(HospitalFeePart.Recoup.Length <= 8);   // the column is nvarchar(8)
		}
	}
}
