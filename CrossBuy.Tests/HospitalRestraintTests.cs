using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H213 — restraint and seclusion. What must never happen: a restraint without its reason or what was tried
	// first, seclusion checked on the 2-hour clock, a missed check or an expired order not shown, a circulation or skin
	// problem recorded without its description, or an ended episode checked, renewed or ended again.
	// ==========================================================================================
	public class HospitalRestraintTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
		private static HospitalRestraintInput Ok() => new() { AdmissionId = 8, Kind = HospitalRestraintKind.Physical, Reason = "Pulling at the central line", Alternatives = "Sitter, mittens, reorientation" };

		[Fact]
		public void An_order_states_its_kind_its_reason_and_what_was_tried_first()
		{
			Assert.Null(HospitalRestraintRules.StartRefusal(Ok()));
			var i = Ok(); i.Kind = "Belt"; Assert.Equal("Choose the kind of restraint", HospitalRestraintRules.StartRefusal(i));
			i = Ok(); i.Reason = " "; Assert.Equal("Write why the restraint is needed", HospitalRestraintRules.StartRefusal(i));
			i = Ok(); i.Alternatives = null; Assert.Equal("Write what was tried before the restraint", HospitalRestraintRules.StartRefusal(i));
		}

		[Fact]
		public void Seclusion_is_checked_every_15_minutes_and_a_restraint_every_2_hours()
		{
			Assert.Equal(15, HospitalRestraintRules.IntervalFor(HospitalRestraintKind.Seclusion));
			Assert.Equal(120, HospitalRestraintRules.IntervalFor(HospitalRestraintKind.Physical));
			Assert.Equal(120, HospitalRestraintRules.IntervalFor(HospitalRestraintKind.Chemical));
		}

		[Fact]
		public void A_missed_check_and_an_expired_order_show()
		{
			var r = new HospitalRestraint { StartedAt = Now, CheckEveryMinutes = 120, ExpiresAt = Now.AddHours(24) };
			Assert.Equal(Now.AddHours(2), HospitalRestraintRules.NextCheckAt(r, null));
			Assert.False(HospitalRestraintRules.CheckOverdue(r, null, Now.AddHours(2)));
			Assert.True(HospitalRestraintRules.CheckOverdue(r, null, Now.AddHours(2).AddMinutes(1)));
			Assert.False(HospitalRestraintRules.CheckOverdue(r, Now.AddHours(1.5), Now.AddHours(3)));   // the last check resets the clock
			Assert.True(HospitalRestraintRules.Expired(r, Now.AddHours(25)));
			r.EndedAt = Now.AddHours(4); r.EndReason = "Calm";
			Assert.False(HospitalRestraintRules.CheckOverdue(r, null, Now.AddHours(30)));
			Assert.False(HospitalRestraintRules.Expired(r, Now.AddHours(30)));
		}

		[Fact]
		public void A_problem_found_on_a_check_is_described_and_an_ended_episode_is_closed()
		{
			var r = new HospitalRestraint();
			Assert.Null(HospitalRestraintRules.CheckRefusal(r, new() { CirculationOk = true, SkinOk = true, StillNeeded = true }));
			Assert.Equal("Describe the circulation or skin problem in the note", HospitalRestraintRules.CheckRefusal(r, new() { CirculationOk = false, SkinOk = true }));
			Assert.Null(HospitalRestraintRules.CheckRefusal(r, new() { CirculationOk = true, SkinOk = false, Behaviour = "Redness at the left wrist, strap loosened" }));
			Assert.Equal("Write why the restraint ends", HospitalRestraintRules.EndRefusal(r, ""));
			r.EndedAt = Now; r.EndReason = "Calm, oriented";
			Assert.Equal("This restraint has ended", HospitalRestraintRules.CheckRefusal(r, new() { CirculationOk = true, SkinOk = true }));
			Assert.Equal("This restraint has ended", HospitalRestraintRules.RenewRefusal(r));
			Assert.Equal("This restraint has ended", HospitalRestraintRules.EndRefusal(r, "again"));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalRestraintModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h213_restraints.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalRestraint), "HospitalRestraints");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalRestraint), "HospitalRestraints");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalRestraintCheck), "HospitalRestraintChecks");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalRestraintCheck), "HospitalRestraintChecks");
			foreach (var k in HospitalRestraintKind.All) Assert.Contains("'" + k + "'", s);
		}
	}
}
