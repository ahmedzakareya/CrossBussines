using System.Globalization;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H74 — order sets, with no database: what a set and its lines need (a medicine exactly as the order screen
	// takes it, a task in one language at least and due within three days), the board's two-language task title shown in
	// the reader's language, and the tables against their slice.
	// ==========================================================================================
	public class HospitalOrderSetTests
	{
		[Fact]
		public void A_set_needs_a_code_and_both_names()
		{
			Assert.Null(HospitalOrderSetRules.SetRefusal(new HospitalOrderSetInput { Code = "CHEST-PAIN", Name = "ألم الصدر", NameEn = "Chest pain" }));
			Assert.Equal("The code is 2 to 20 letters, digits or dashes", HospitalOrderSetRules.SetRefusal(new HospitalOrderSetInput { Code = "chest pain", Name = "x", NameEn = "x" }));
			Assert.Equal("The code is 2 to 20 letters, digits or dashes", HospitalOrderSetRules.SetRefusal(new HospitalOrderSetInput { Code = "X", Name = "x", NameEn = "x" }));
			Assert.Equal("Write the name in Arabic and in English", HospitalOrderSetRules.SetRefusal(new HospitalOrderSetInput { Code = "PREOP", Name = "قبل العملية" }));
		}

		[Fact]
		public void A_medicine_line_is_held_to_the_order_screens_rules()
		{
			Assert.Null(HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Medication, Drug = "Aspirin", Dose = "300 mg", Route = "PO", Hours = "08" }));
			Assert.Null(HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Medication, Drug = "Morphine", Dose = "2 mg", Route = "IV", AsNeeded = true }));
			// the same refusal the nursing order gives
			var noHours = new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Medication, Drug = "Aspirin", Dose = "300 mg", Route = "PO" };
			Assert.Equal(HospitalNursingRules.OrderRefusal("Aspirin", "300 mg", "PO", null, false), HospitalOrderSetRules.ItemRefusal(noHours));
			Assert.NotNull(HospitalOrderSetRules.ItemRefusal(noHours));
			Assert.Equal("Choose medicine or nursing task", HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = "Exam" }));
		}

		[Fact]
		public void A_task_line_is_said_in_a_language_at_least_and_due_within_three_days()
		{
			Assert.Null(HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Task, TitleAr = "تخطيط قلب", TitleEn = "ECG", DueInHours = 0 }));
			Assert.Null(HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Task, TitleEn = "Repeat troponin", DueInHours = 6 }));
			Assert.Equal("Write the task", HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Task, DueInHours = 1 }));
			Assert.Equal("A task is due 0 to 72 hours after the set is placed", HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Task, TitleEn = "x", DueInHours = 96 }));
			Assert.Equal("A task cannot contain the | sign", HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Task, TitleEn = "a|b", DueInHours = 1 }));
			Assert.Equal("A task is at most 90 characters", HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Task, TitleEn = new string('x', 91), DueInHours = 1 }));
		}

		[Fact]
		public void The_boards_task_shows_in_the_readers_language_and_falls_back_to_the_other()
		{
			var code = HospitalOrderSetRules.TaskCode("تخطيط قلب", "ECG");
			Assert.Equal("OS|تخطيط قلب|ECG", code);
			var was = CultureInfo.CurrentUICulture;
			try
			{
				CultureInfo.CurrentUICulture = new CultureInfo("ar");
				Assert.Equal("تخطيط قلب", HospitalNews2Text.TaskText(code, k => k));
				Assert.Equal("Repeat troponin", HospitalNews2Text.TaskText(HospitalOrderSetRules.TaskCode(null, "Repeat troponin"), k => k));
				CultureInfo.CurrentUICulture = new CultureInfo("en");
				Assert.Equal("ECG", HospitalNews2Text.TaskText(code, k => k));
				Assert.Equal("Turn the patient", HospitalNews2Text.TaskText("Turn the patient", k => k));
			}
			finally { CultureInfo.CurrentUICulture = was; }
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			// H75 adds a column (ServiceId) and two kinds of line: the model reads both slices
			var s74 = HospitalSliceParity.Slice("hospital_h74_order_sets.sql");
			var s = s74 + Environment.NewLine + HospitalSliceParity.Slice("hospital_h75_inpatient_orders.sql");
			var m = HospitalSliceParity.Model(HospitalOrderSetModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalOrderSet), "HospitalOrderSets"), (typeof(HospitalOrderSetItem), "HospitalOrderSetItems"), (typeof(HospitalOrderSetUse), "HospitalOrderSetUses") })
			{ HospitalSliceParity.NamesItsTable(m, s, t, n); HospitalSliceParity.ColumnsMatch(m, s, t, n); }
			foreach (var x in HospitalOrderSetItemKind.All) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s74).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalOrderSetUse));
		}
	}
}
