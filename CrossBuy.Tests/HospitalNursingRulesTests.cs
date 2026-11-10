using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H14 — the nursing rules, DATABASE-FREE: pure calls on HospitalNursingRules, and the EF model built
	// with the SQL Server conventions and no connection, read against its slice's text.
	//
	// What must never happen: an order with neither a schedule nor "as needed" (or both), a dose given before
	// its window, a dose not given without a reason, a controlled drug given without a second signature (or with
	// the nurse as their own witness), a dose answered twice, or a model naming a table its slice lacks.
	// ==========================================================================================
	public class HospitalNursingRulesTests
	{
		private static readonly Func<DateTime, DateTime> Utc3 = t => t.AddHours(-3);   // a ward three hours ahead of UTC (Kuwait)

		[Fact]
		public void Hours_parse_sorted_and_distinct_and_bad_text_is_refused()
		{
			Assert.Equal(new[] { 6, 14, 22 }, HospitalNursingRules.ParseHours(" 22, 06,14,06 "));
			Assert.Empty(HospitalNursingRules.ParseHours(null)!);
			Assert.Null(HospitalNursingRules.ParseHours("6,25"));
			Assert.Null(HospitalNursingRules.ParseHours("morning"));
			Assert.Equal("06,14,22", HospitalNursingRules.FormatHours(new[] { 22, 6, 14, 6 }));
		}

		[Fact]
		public void An_order_has_a_schedule_or_is_as_needed_never_both_never_neither()
		{
			Assert.Null(HospitalNursingRules.OrderRefusal("Paracetamol", "1 g", "PO", "06,14,22", false));
			Assert.Null(HospitalNursingRules.OrderRefusal("Morphine", "2 mg", "IV", null, true));
			Assert.NotNull(HospitalNursingRules.OrderRefusal("Morphine", "2 mg", "IV", "06", true));
			Assert.NotNull(HospitalNursingRules.OrderRefusal("Paracetamol", "1 g", "PO", null, false));
			Assert.NotNull(HospitalNursingRules.OrderRefusal("", "1 g", "PO", "06", false));
			Assert.NotNull(HospitalNursingRules.OrderRefusal("Paracetamol", " ", "PO", "06", false));
			Assert.NotNull(HospitalNursingRules.OrderRefusal("Paracetamol", "1 g", "ORAL", "06", false));
			Assert.NotNull(HospitalNursingRules.OrderRefusal("Paracetamol", "1 g", "PO", "6,99", false));
		}

		[Fact]
		public void The_days_doses_are_the_ward_clock_hours_in_utc_between_start_and_stop()
		{
			var day = new DateTime(2026, 11, 3);
			var start = new DateTime(2026, 11, 3, 8, 0, 0);   // UTC 08:00 = ward 11:00
			var doses = HospitalNursingRules.DosesOn(day, new[] { 6, 14, 22 }, start, null, Utc3);
			// 06:00 ward = 03:00 UTC is before the order (less the window): not a dose; 14:00 and 22:00 are
			Assert.Equal(new[] { new DateTime(2026, 11, 3, 11, 0, 0), new DateTime(2026, 11, 3, 19, 0, 0) }, doses);
			var stopped = HospitalNursingRules.DosesOn(day, new[] { 6, 14, 22 }, start, new DateTime(2026, 11, 3, 12, 0, 0), Utc3);
			Assert.Equal(new[] { new DateTime(2026, 11, 3, 11, 0, 0) }, stopped);
		}

		[Fact]
		public void An_order_written_just_before_a_dose_still_gets_that_dose()
		{
			var day = new DateTime(2026, 11, 3);
			var start = new DateTime(2026, 11, 3, 10, 30, 0);   // 30 min before the 14:00 ward dose (11:00 UTC)
			Assert.Contains(new DateTime(2026, 11, 3, 11, 0, 0), HospitalNursingRules.DosesOn(day, new[] { 14 }, start, null, Utc3));
		}

		[Fact]
		public void A_dose_is_upcoming_due_within_its_window_then_overdue()
		{
			var due = new DateTime(2026, 11, 3, 11, 0, 0);
			Assert.Equal(HospitalNursingRules.Upcoming, HospitalNursingRules.DoseState(due, due.AddMinutes(-61)));
			Assert.Equal(HospitalNursingRules.Due, HospitalNursingRules.DoseState(due, due.AddMinutes(-60)));
			Assert.Equal(HospitalNursingRules.Due, HospitalNursingRules.DoseState(due, due.AddMinutes(60)));
			Assert.Equal(HospitalNursingRules.Overdue, HospitalNursingRules.DoseState(due, due.AddMinutes(61)));
		}

		[Fact]
		public void A_dose_not_given_needs_its_reason()
		{
			var now = new DateTime(2026, 11, 3, 11, 0, 0);
			Assert.NotNull(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Held, " ", false, 5, null, now, now));
			Assert.NotNull(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Missed, null, false, 5, null, now, now));
			Assert.Null(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Held, "BP 85/50, doctor informed", false, 5, null, now, now));
			Assert.NotNull(HospitalNursingRules.RecordRefusal("Skipped", "x", false, 5, null, now, now));
		}

		[Fact]
		public void A_controlled_drug_needs_a_witness_who_is_not_the_nurse()
		{
			var now = new DateTime(2026, 11, 3, 11, 0, 0);
			Assert.NotNull(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Given, null, true, 5, null, now, now));
			Assert.NotNull(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Given, null, true, 5, 5, now, now));
			Assert.Null(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Given, null, true, 5, 9, now, now));
			Assert.Null(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Held, "patient asleep, pain 0", true, 5, null, now, now));   // not given: no witness needed
		}

		[Fact]
		public void A_dose_is_not_given_before_its_window_opens()
		{
			var due = new DateTime(2026, 11, 3, 19, 0, 0);
			Assert.NotNull(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Given, null, false, 5, null, due, due.AddMinutes(-90)));
			Assert.Null(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Given, null, false, 5, null, due, due.AddMinutes(-30)));
			Assert.Null(HospitalNursingRules.RecordRefusal(HospitalMarStatus.Given, null, false, 5, null, null, due));   // as needed: any time
		}

		[Theory]
		[InlineData(7, 0, "Day")]
		[InlineData(14, 59, "Day")]
		[InlineData(15, 0, "Evening")]
		[InlineData(22, 59, "Evening")]
		[InlineData(23, 0, "Night")]
		[InlineData(6, 59, "Night")]
		public void Shifts_follow_the_ward_clock(int hour, int minute, string shift)
			=> Assert.Equal(shift, HospitalNursingRules.ShiftOf(new DateTime(2026, 11, 3, hour, minute, 0)));

		[Fact]
		public void The_night_shift_is_filed_under_the_day_it_started()
		{
			Assert.Equal(new DateTime(2026, 11, 2), HospitalNursingRules.ShiftDate(new DateTime(2026, 11, 3, 3, 0, 0)));
			Assert.Equal(new DateTime(2026, 11, 3), HospitalNursingRules.ShiftDate(new DateTime(2026, 11, 3, 23, 30, 0)));
		}

		// ------------------------------------------------------------------ the model against its slice, no database

		private static string Slice()
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", "hospital_h14_nursing.sql");
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException("CrossBuy/deploy/sql/hospital_h14_nursing.sql not found above " + AppContext.BaseDirectory);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model()
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			HospitalNursingModel.Configure(mb);
			return mb.FinalizeModel();
		}

		[Theory]
		[InlineData(typeof(HospitalMedOrder), "HospitalMedOrders")]
		[InlineData(typeof(HospitalMarEntry), "HospitalMarEntries")]
		[InlineData(typeof(HospitalNursingTask), "HospitalNursingTasks")]
		[InlineData(typeof(HospitalShiftHandover), "HospitalShiftHandovers")]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table)
		{
			Assert.Equal(table, Model().FindEntityType(entity)!.GetTableName());
			Assert.Contains($"CREATE TABLE dbo.{table} (", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void A_dose_is_answered_once_in_model_and_slice()
		{
			var ix = Model().FindEntityType(typeof(HospitalMarEntry))!.GetIndexes().Single(i => i.GetDatabaseName() == "UX_HospitalMarEntries_Dose");
			Assert.True(ix.IsUnique);
			Assert.Equal("OrderId,DueAt", string.Join(",", ix.Properties.Select(p => p.Name)));
			Assert.Equal("[DueAt] IS NOT NULL", ix.GetFilter());
			Assert.Contains("CREATE UNIQUE INDEX UX_HospitalMarEntries_Dose ON dbo.HospitalMarEntries (OrderId, DueAt) WHERE DueAt IS NOT NULL;", Slice().Split('\n').Select(l => l.Trim()));
		}

		/// The model is also built on SQLite by the test hosts: an index filter must be provider-neutral SQL.
		/// N'…' (SQL Server's Unicode literal) broke every SQLite schema creation once (H11's filters).
		[Fact]
		public void The_H11_to_H14_index_filters_are_provider_neutral()
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			HospitalInsuranceModel.Configure(mb); HospitalEstimatesModel.Configure(mb); HospitalEmergencyModel.Configure(mb); HospitalNursingModel.Configure(mb);
			var filters = mb.FinalizeModel().GetEntityTypes().SelectMany(t => t.GetIndexes()).Select(i => i.GetFilter()).Where(f => f != null).ToList();
			Assert.Equal(6, filters.Count);   // H11: draft, pending, resubmits · H13: open bay, open patient · H14: one answer per dose
			Assert.All(filters, f => Assert.DoesNotContain("N'", f));
		}

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite()
		{
			var sql = Slice();
			var created = System.Text.RegularExpressions.Regex.Matches(sql, @"CREATE TABLE dbo\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
			var referenced = System.Text.RegularExpressions.Regex.Matches(sql, @"REFERENCES dbo\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Except(created).ToList();
			var guard = sql[..sql.IndexOf("THROW", StringComparison.Ordinal)];
			Assert.Equal(3, referenced.Count);
			foreach (var t in referenced) Assert.Contains($"OBJECT_ID('dbo.{t}','U') IS NULL", guard);
		}
	}
}
