using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H203 — referrals. What must never happen: a referral with no question, an internal one with no department,
	// a doctor referring to themself, an answer before the department accepted, a decline or cancel without a reason, a
	// closed referral moved again, or an open one past its time not shown as overdue.
	// ==========================================================================================
	public class HospitalReferralTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void A_referral_states_its_question_and_where_it_goes()
		{
			var ok = new HospitalReferralInput { Kind = "Internal", Urgency = "Urgent", ToDepartmentId = 2, Reason = "Chest pain, ECG changes: please assess" };
			Assert.Null(HospitalReferralRules.CreateRefusal(ok, 11));
			Assert.Equal("Write the question for the doctor you refer to", HospitalReferralRules.CreateRefusal(new() { Kind = "Internal", Urgency = "Routine", ToDepartmentId = 2, Reason = " " }, null));
			Assert.Equal("Choose the department you refer to", HospitalReferralRules.CreateRefusal(new() { Kind = "Internal", Urgency = "Routine", Reason = "x" }, null));
			Assert.Equal("A doctor cannot refer a patient to themself", HospitalReferralRules.CreateRefusal(new() { Kind = "Internal", Urgency = "Routine", Reason = "x", ToDepartmentId = 2, ToPractitionerId = 11 }, 11));
			Assert.Equal("Name the facility you refer to", HospitalReferralRules.CreateRefusal(new() { Kind = "External", Urgency = "Routine", Reason = "x" }, null));
			Assert.Equal("Choose the urgency", HospitalReferralRules.CreateRefusal(new() { Kind = "External", Urgency = "Soon", Reason = "x", ToFacility = "F" }, null));
			Assert.Equal("Choose an internal or an external referral", HospitalReferralRules.CreateRefusal(new() { Kind = "Both", Urgency = "Routine", Reason = "x" }, null));
		}

		[Fact]
		public void Urgency_sets_the_due_time_and_overdue_follows()
		{
			Assert.Equal(Now.AddHours(2), HospitalReferralRules.DueAt("Emergency", Now));
			Assert.Equal(Now.AddHours(24), HospitalReferralRules.DueAt("Urgent", Now));
			Assert.Equal(Now.AddDays(14), HospitalReferralRules.DueAt("Routine", Now));
			var r = new HospitalReferral { Status = "Sent", DueAt = Now };
			Assert.True(HospitalReferralRules.Overdue(r, Now.AddMinutes(1)));
			Assert.False(HospitalReferralRules.Overdue(r, Now));
			r.Status = "Completed";
			Assert.False(HospitalReferralRules.Overdue(r, Now.AddDays(1)));
		}

		[Fact]
		public void An_internal_referral_is_accepted_then_answered()
		{
			var r = new HospitalReferral { Kind = "Internal", Status = "Sent" };
			Assert.Equal("Accept the referral before answering it", HospitalReferralRules.MoveRefusal(r, "Completed", "Seen"));
			Assert.Null(HospitalReferralRules.MoveRefusal(r, "Accepted", null));
			Assert.Equal("Write why the referral is declined", HospitalReferralRules.MoveRefusal(r, "Declined", ""));
			r.Status = "Accepted";
			Assert.Equal("This referral is no longer waiting", HospitalReferralRules.MoveRefusal(r, "Declined", "busy"));
			Assert.Equal("Write the reply to the referring doctor", HospitalReferralRules.MoveRefusal(r, "Completed", " "));
			Assert.Null(HospitalReferralRules.MoveRefusal(r, "Completed", "Seen: stable angina, start aspirin"));
			r.Status = "Completed";
			Assert.Equal("This referral is closed", HospitalReferralRules.MoveRefusal(r, "Cancelled", "no longer needed"));
		}

		[Fact]
		public void An_external_referral_closes_on_the_facility_reply()
		{
			var r = new HospitalReferral { Kind = "External", Status = "Sent" };
			Assert.Equal("Only a referral within the hospital is accepted here", HospitalReferralRules.MoveRefusal(r, "Accepted", null));
			Assert.Equal("Record what the facility wrote back", HospitalReferralRules.MoveRefusal(r, "Completed", null));
			Assert.Null(HospitalReferralRules.MoveRefusal(r, "Completed", "Seen at the cardiac centre; angiography booked"));
			Assert.Equal("Write why the referral is cancelled", HospitalReferralRules.MoveRefusal(r, "Cancelled", null));
			Assert.Equal("not_found", HospitalReferralRules.MoveRefusal(r, "Lost", "x"));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalReferralModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h203_referrals.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalReferral), "HospitalReferrals");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalReferral), "HospitalReferrals");
			foreach (var x in HospitalReferralStatus.All.Concat(HospitalReferralKind.All).Concat(HospitalReferralUrgency.All)) Assert.Contains("'" + x + "'", s);
		}
	}
}
