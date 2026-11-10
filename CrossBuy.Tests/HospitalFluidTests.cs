using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H78 — fluid balance, with no database: what an entry needs, the 07:00 fluid day, the day's totals without
	// voided entries, the low-urine flag (weight, six hours, a chart in use), voiding, and the table against its slice.
	// ==========================================================================================
	public class HospitalFluidTests
	{
		private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void An_entry_has_a_known_kind_a_sane_volume_and_a_time_within_the_stay()
		{
			var adm = Now.AddDays(-2);
			Assert.Null(HospitalFluidRules.EntryRefusal(HospitalFluidKind.Urine, 350, Now.AddHours(-1), adm, Now));
			Assert.Equal("Choose what went in or came out", HospitalFluidRules.EntryRefusal("Sweat", 100, Now, adm, Now));
			Assert.Equal("The volume is between 1 and 5,000 ml", HospitalFluidRules.EntryRefusal(HospitalFluidKind.Oral, 0, Now, adm, Now));
			Assert.Equal("The volume is between 1 and 5,000 ml", HospitalFluidRules.EntryRefusal(HospitalFluidKind.Iv, 5001, Now, adm, Now));
			Assert.Equal("The time is in the future", HospitalFluidRules.EntryRefusal(HospitalFluidKind.Oral, 100, Now.AddHours(1), adm, Now));
			Assert.Equal("The time is before the admission", HospitalFluidRules.EntryRefusal(HospitalFluidKind.Oral, 100, adm.AddHours(-1), adm, Now));
			Assert.Equal("An entry older than three days cannot be added", HospitalFluidRules.EntryRefusal(HospitalFluidKind.Oral, 100, Now.AddDays(-4), Now.AddDays(-10), Now));
			Assert.Equal(HospitalFluidDirection.In, HospitalFluidKind.DirectionOf(HospitalFluidKind.Blood));
			Assert.Equal(HospitalFluidDirection.Out, HospitalFluidKind.DirectionOf(HospitalFluidKind.BloodLoss));
		}

		[Fact]
		public void The_fluid_day_runs_from_seven_to_seven_and_adds_up_its_own_entries()
		{
			Assert.Equal(new DateTime(2026, 11, 10, 7, 0, 0), HospitalFluidRules.DayStartOf(new DateTime(2026, 11, 10, 7, 0, 0)));
			Assert.Equal(new DateTime(2026, 11, 9, 7, 0, 0), HospitalFluidRules.DayStartOf(new DateTime(2026, 11, 10, 6, 59, 0)));
			Assert.Equal(new DateTime(2026, 11, 10, 7, 0, 0), HospitalFluidRules.DayStartOf(new DateTime(2026, 11, 10, 23, 30, 0)));
			var d = new DateTime(2026, 11, 10, 7, 0, 0);
			var t = HospitalFluidRules.Total(d, new[]
			{
				("In", 1000, d.AddHours(1)), ("In", 500, d.AddHours(23).AddMinutes(59)), ("Out", 400, d.AddHours(5)),
				("In", 900, d.AddMinutes(-1)),            // the day before
				("Out", 300, d.AddDays(1)),               // the next day
			});
			Assert.Equal(1500, t.In);
			Assert.Equal(400, t.Out);
			Assert.Equal(1100, t.Balance);
		}

		[Fact]
		public void Low_urine_needs_a_weight_six_hours_and_a_chart_in_use()
		{
			var adm = Now.AddDays(-1);
			// 70 kg → 0.5 × 70 × 6 = 210 ml in six hours
			Assert.True(HospitalFluidRules.LowUrine(70m, 150, true, adm, Now));
			Assert.False(HospitalFluidRules.LowUrine(70m, 210, true, adm, Now));
			Assert.False(HospitalFluidRules.LowUrine(null, 0, true, adm, Now));          // no weight: no flag
			Assert.False(HospitalFluidRules.LowUrine(70m, 0, false, adm, Now));          // chart not in use: no flag
			Assert.False(HospitalFluidRules.LowUrine(70m, 0, true, Now.AddHours(-3), Now));   // admitted three hours ago
		}

		[Fact]
		public void A_wrong_entry_is_voided_once_with_a_reason()
		{
			Assert.Null(HospitalFluidRules.VoidRefusal(new HospitalFluidEntry(), "written on the wrong patient"));
			Assert.Equal("Write why it is voided", HospitalFluidRules.VoidRefusal(new HospitalFluidEntry(), " "));
			Assert.Equal("This entry is already voided", HospitalFluidRules.VoidRefusal(new HospitalFluidEntry { VoidedAt = Now }, "x"));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h78_fluid_balance.sql");
			var m = HospitalSliceParity.Model(HospitalFluidModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalFluidEntry), "HospitalFluidEntries");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalFluidEntry), "HospitalFluidEntries");
			foreach (var x in HospitalFluidKind.All) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalFluidEntry));
		}
	}
}
