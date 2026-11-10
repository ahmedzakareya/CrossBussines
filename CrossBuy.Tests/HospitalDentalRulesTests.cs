using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H15 — the dental rules, DATABASE-FREE: pure calls on HospitalDentalRules, and the EF model built
	// with the SQL Server conventions and no connection, read against its slice's text.
	//
	// What must never happen: a tooth number outside FDI, an occlusal surface on a front tooth, a two-surface code
	// billed for three surfaces, an extracted tooth still drawn with its filling, a declined phase in the quote,
	// the payer paying past the yearly limit or for a filling repeated within two years, a perio value out of
	// range, or a model reading a column in a type its slice does not create (the H13 InvalidCastException).
	// ==========================================================================================
	public class HospitalDentalRulesTests
	{
		private static HospitalDentalFinding F(int id, int tooth, string? surf, string? work, string status, int day, int? line = null) => new()
		{ ID = id, Tooth = tooth, Surfaces = surf, Work = work, Status = status, RecordedAt = new DateTime(2026, 1, 1).AddDays(day), PlanLineId = line };

		[Theory]
		[InlineData(11, true)] [InlineData(18, true)] [InlineData(48, true)] [InlineData(51, true)] [InlineData(85, true)]
		[InlineData(19, false)] [InlineData(10, false)] [InlineData(56, false)] [InlineData(90, false)] [InlineData(9, false)]
		public void Tooth_numbers_are_FDI(int n, bool ok) => Assert.Equal(ok, HospitalDentalRules.IsTooth(n));

		[Fact]
		public void Arches_sides_and_roots_follow_the_anatomy()
		{
			Assert.True(HospitalDentalRules.IsUpper(16)); Assert.True(HospitalDentalRules.IsUpper(65)); Assert.False(HospitalDentalRules.IsUpper(36));
			Assert.True(HospitalDentalRules.PatientRight(46)); Assert.False(HospitalDentalRules.PatientRight(36));
			Assert.Equal(3, HospitalDentalRules.Roots(16)); Assert.Equal(2, HospitalDentalRules.Roots(36));
			Assert.Equal(2, HospitalDentalRules.Roots(14)); Assert.Equal(1, HospitalDentalRules.Roots(15)); Assert.Equal(1, HospitalDentalRules.Roots(44));
			Assert.Equal("can", HospitalDentalRules.Kind(13)); Assert.Equal("mol", HospitalDentalRules.Kind(55)); Assert.Equal("inc", HospitalDentalRules.Kind(52));
		}

		[Fact]
		public void The_chart_opens_in_the_dentition_of_the_age()
		{
			var today = new DateTime(2026, 11, 3);
			Assert.Equal(HospitalDentition.Mixed, HospitalDentalRules.DentitionFor(new DateTime(2019, 5, 1), today));
			Assert.Equal(HospitalDentition.Primary, HospitalDentalRules.DentitionFor(new DateTime(2022, 5, 1), today));
			Assert.Equal(HospitalDentition.Permanent, HospitalDentalRules.DentitionFor(new DateTime(1990, 5, 1), today));
			Assert.Equal(HospitalDentition.Permanent, HospitalDentalRules.DentitionFor(null, today));
		}

		[Fact]
		public void Surfaces_are_written_in_chart_order_and_only_where_the_tooth_has_them()
		{
			Assert.Equal("MOD", HospitalDentalRules.NormalizeSurfaces(36, "dom"));
			Assert.Equal("MIB", HospitalDentalRules.NormalizeSurfaces(11, "bim"));
			Assert.Null(HospitalDentalRules.NormalizeSurfaces(11, "O"));   // a front tooth has an incisal edge, not an occlusal face
			Assert.Null(HospitalDentalRules.NormalizeSurfaces(36, "I"));
			Assert.Null(HospitalDentalRules.NormalizeSurfaces(36, "MM"));
			Assert.Null(HospitalDentalRules.NormalizeSurfaces(36, "X"));
			Assert.Null(HospitalDentalRules.NormalizeSurfaces(36, ""));
		}

		[Fact]
		public void A_multi_surface_code_bills_exactly_its_surfaces()
		{
			var c2 = new HospitalDentalProcedure { Surfaces = 2, NeedsTooth = true };
			Assert.Null(HospitalDentalRules.PlacementRefusal(c2, 25, "MO"));
			Assert.NotNull(HospitalDentalRules.PlacementRefusal(c2, 25, "MOD"));
			Assert.NotNull(HospitalDentalRules.PlacementRefusal(c2, 25, "O"));
			Assert.NotNull(HospitalDentalRules.PlacementRefusal(c2, null, "MO"));
			var scaling = new HospitalDentalProcedure();
			Assert.Null(HospitalDentalRules.PlacementRefusal(scaling, null, null));
			Assert.NotNull(HospitalDentalRules.PlacementRefusal(scaling, 16, null));
			var crown = new HospitalDentalProcedure { ToothWork = HospitalToothWork.Crown, NeedsTooth = true };
			Assert.Null(HospitalDentalRules.PlacementRefusal(crown, 36, null));
			Assert.NotNull(HospitalDentalRules.PlacementRefusal(crown, 36, "O"));
			var implant = new HospitalDentalProcedure { ToothWork = HospitalToothWork.Implant, NeedsTooth = true };
			Assert.Null(HospitalDentalRules.PlacementRefusal(implant, 46, null));
			Assert.NotNull(HospitalDentalRules.PlacementRefusal(implant, 84, null));
		}

		[Fact]
		public void The_latest_mark_of_a_surface_wins_and_voided_marks_are_gone()
		{
			var chart = HospitalDentalRules.Chart(new[]
			{
				F(1, 16, "O", null, HospitalDentalMark.Condition, 0),
				F(2, 16, "O", null, HospitalDentalMark.Completed, 5),
				F(3, 16, "M", null, HospitalDentalMark.Condition, 6),
				new HospitalDentalFinding { ID = 4, Tooth = 16, Surfaces = "D", Status = HospitalDentalMark.Condition, RecordedAt = new DateTime(2026, 1, 9), VoidedAt = new DateTime(2026, 1, 10) },
			}, Array.Empty<(int, string)>());
			Assert.Equal(HospitalDentalMark.Completed, chart[16].Surfaces["O"]);
			Assert.Equal(HospitalDentalMark.Condition, chart[16].Surfaces["M"]);
			Assert.False(chart[16].Surfaces.ContainsKey("D"));
		}

		[Fact]
		public void A_tooth_taken_out_is_missing_and_what_follows_it_stays()
		{
			var chart = HospitalDentalRules.Chart(new[]
			{
				F(1, 46, "MOD", null, HospitalDentalMark.Existing, 0),
				F(2, 46, null, HospitalToothWork.Crown, HospitalDentalMark.Existing, 1),
				F(3, 46, null, HospitalToothWork.Extraction, HospitalDentalMark.Completed, 2),
				F(4, 46, null, HospitalToothWork.Implant, HospitalDentalMark.Planned, 3, 77),
			}, new[] { (46, "IMP|77") });
			var t = chart[46];
			Assert.True(t.Missing);
			Assert.Empty(t.Surfaces);
			Assert.Equal(new[] { (HospitalToothWork.Implant, HospitalDentalMark.Planned) }, t.Work);
			Assert.Equal(new[] { "IMP|77" }, t.Planned);
		}

		[Fact]
		public void A_planned_extraction_keeps_the_tooth_and_draws_the_cross()
		{
			var t = HospitalDentalRules.Chart(new[] { F(1, 48, null, HospitalToothWork.Impacted, HospitalDentalMark.Existing, 0), F(2, 48, null, HospitalToothWork.Extraction, HospitalDentalMark.Planned, 1, 9) }, Array.Empty<(int, string)>())[48];
			Assert.False(t.Missing); Assert.True(t.Impacted);
			Assert.Contains((HospitalToothWork.Extraction, HospitalDentalMark.Planned), t.Work);
		}

		[Theory]
		[InlineData(new string[0], "Presented")]
		[InlineData(new[] { "Pending", "Pending" }, "Presented")]
		[InlineData(new[] { "Accepted", "Pending" }, "Partial")]
		[InlineData(new[] { "Accepted", "Declined" }, "Partial")]
		[InlineData(new[] { "Accepted", "Accepted" }, "Accepted")]
		[InlineData(new[] { "Declined", "Declined" }, "Declined")]
		[InlineData(new[] { "Declined", "Pending" }, "Presented")]
		public void A_plan_state_follows_its_phases(string[] decisions, string state) => Assert.Equal(state, HospitalDentalRules.PlanState(decisions));

		private static readonly HospitalDentalBenefit Cover = new() { YearlyLimit = 2000, DiagnosticPct = 100, PreventivePct = 100, RestorativePct = 80, EndodonticPct = 70, ProstheticPct = 50, SurgeryPct = 70 };

		[Fact]
		public void The_quote_covers_by_category_and_leaves_out_declined_work()
		{
			var q = HospitalDentalRules.Quote(new[]
			{
				new HospitalDentalRules.QuoteInput(1, 300, HospitalDentalCategory.Restorative, false, false, false),   // 80% → 240
				new HospitalDentalRules.QuoteInput(2, 1600, HospitalDentalCategory.Endodontic, false, false, false),   // 70% → 1120
				new HospitalDentalRules.QuoteInput(3, 4500, HospitalDentalCategory.Implant, true, false, false),       // never covered
				new HospitalDentalRules.QuoteInput(4, 1200, HospitalDentalCategory.Cosmetic, false, true, false),      // declined: not quoted
			}, Cover, 0, 3);
			Assert.Equal(6400m, q.Gross);
			Assert.Equal(1360m, q.PayerShare);
			Assert.Equal(5040m, q.PatientShare);
			Assert.False(q.Capped);
			Assert.Equal(3, q.Lines.Count);
		}

		[Fact]
		public void The_payer_never_pays_past_what_is_left_of_the_yearly_limit()
		{
			var lines = new[] { new HospitalDentalRules.QuoteInput(1, 2200, HospitalDentalCategory.Prosthetic, false, false, false), new HospitalDentalRules.QuoteInput(2, 1600, HospitalDentalCategory.Endodontic, false, false, false) };
			var q = HospitalDentalRules.Quote(lines, Cover, 1500, 3);   // 1100 + 1120 asked, 500 left
			Assert.Equal(500m, q.PayerShare);
			Assert.Equal(3300m, q.PatientShare);
			Assert.True(q.Capped);
			Assert.Equal(0m, HospitalDentalRules.Quote(lines, Cover, 2500, 3).PayerShare);
			Assert.Equal(0m, HospitalDentalRules.Quote(lines, null, 0, 3).PayerShare);   // self-pay
			var unlimited = new HospitalDentalBenefit { EndodonticPct = 70, ProstheticPct = 50 };
			Assert.Equal(2220m, HospitalDentalRules.Quote(lines, unlimited, 99999, 3).PayerShare);   // a limit of 0 means none
		}

		[Fact]
		public void A_filling_repeated_on_a_surface_within_two_years_is_the_patients()
		{
			var done = new[] { (16, "MO", new DateTime(2025, 6, 1)) };
			Assert.True(HospitalDentalRules.RepeatWithinTwoYears(16, "O", done, new DateTime(2026, 11, 3)));
			Assert.False(HospitalDentalRules.RepeatWithinTwoYears(16, "D", done, new DateTime(2026, 11, 3)));
			Assert.False(HospitalDentalRules.RepeatWithinTwoYears(17, "O", done, new DateTime(2026, 11, 3)));
			Assert.False(HospitalDentalRules.RepeatWithinTwoYears(16, "O", done, new DateTime(2027, 6, 2)));
			var q = HospitalDentalRules.Quote(new[] { new HospitalDentalRules.QuoteInput(1, 300, HospitalDentalCategory.Restorative, false, false, true) }, Cover, 0, 3);
			Assert.Equal(0m, q.PayerShare); Assert.True(q.Lines[0].Repeat);
		}

		[Fact]
		public void Perio_values_are_kept_only_in_range()
		{
			Assert.Null(HospitalDentalRules.PerioRefusal(16, "5,4,3,4,3,3", "1,0,0,1,0,0", "110100", 1, 1));
			Assert.Null(HospitalDentalRules.PerioRefusal(11, "2,1,2,2,2,2", "-1,0,0,0,0,0", "000000", 0, null));
			Assert.NotNull(HospitalDentalRules.PerioRefusal(16, "5,4,3,4,3", "1,0,0,1,0,0", "110100", null, null));
			Assert.NotNull(HospitalDentalRules.PerioRefusal(16, "16,4,3,4,3,3", "0,0,0,0,0,0", "000000", null, null));
			Assert.NotNull(HospitalDentalRules.PerioRefusal(16, "5,4,3,4,3,3", "0,0,0,0,0,0", "11010", null, null));
			Assert.NotNull(HospitalDentalRules.PerioRefusal(16, "5,4,3,4,3,3", "0,0,0,0,0,0", "000000", 4, null));
			Assert.NotNull(HospitalDentalRules.PerioRefusal(11, "2,2,2,2,2,2", "0,0,0,0,0,0", "000000", null, 1));   // a single root has no furcation
			Assert.NotNull(HospitalDentalRules.PerioRefusal(55, "2,2,2,2,2,2", "0,0,0,0,0,0", "000000", null, null));
		}

		[Fact]
		public void Perio_stats_count_sites_bleeding_deep_pockets_and_mobile_teeth()
		{
			var s = HospitalDentalRules.PerioStats(new[]
			{
				new HospitalPerioTooth { Tooth = 16, Depths = "5,4,3,4,3,3", Recession = "0,0,0,0,0,0", Bleeding = "110100", Mobility = 1 },
				new HospitalPerioTooth { Tooth = 26, Depths = "6,5,4,6,4,4", Recession = "0,0,0,0,0,0", Bleeding = "111110", Mobility = 0 },
			});
			Assert.Equal(new HospitalPerioStats(12, 8, 4, 1), s);
		}

		[Fact]
		public void Plan_numbers_run_per_year()
		{
			Assert.Equal("TP-2026-0215", HospitalDentalRules.NextPlanNo(new[] { "TP-2026-0214", "TP-2025-0999", "TP-2026-0002" }, 2026));
			Assert.Equal("TP-2027-0001", HospitalDentalRules.NextPlanNo(new[] { "TP-2026-0214" }, 2027));
		}

		// ---------------- model and slice ----------------

		// H33 adds the findings' English note (NoteEn)
		private static string Slice() => HospitalSliceParity.Slice("hospital_h15_dental.sql") + HospitalSliceParity.Slice("hospital_h33_bilingual_text.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h53_dental_line_billing.sql");   // + H53 BillLineId
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDentalModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalDentalProcedure), "HospitalDentalProcedures" }, new object[] { typeof(HospitalDentalBenefit), "HospitalDentalBenefits" },
			new object[] { typeof(HospitalDentalChart), "HospitalDentalCharts" }, new object[] { typeof(HospitalDentalFinding), "HospitalDentalFindings" },
			new object[] { typeof(HospitalDentalPlan), "HospitalDentalPlans" }, new object[] { typeof(HospitalDentalPlanPhase), "HospitalDentalPlanPhases" },
			new object[] { typeof(HospitalDentalPlanLine), "HospitalDentalPlanLines" }, new object[] { typeof(HospitalPerioExam), "HospitalPerioExams" },
			new object[] { typeof(HospitalPerioTooth), "HospitalPerioTeeth" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		/// Every non-text column is read in the type the slice creates it in: a tinyint read as int throws at runtime.
		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void The_one_index_filter_is_provider_neutral_and_in_the_slice()
		{
			var filters = Model().GetEntityTypes().SelectMany(t => t.GetIndexes()).Where(i => i.GetFilter() != null).ToList();
			var ix = Assert.Single(filters);
			Assert.Equal("[PlanLineId] IS NOT NULL", ix.GetFilter());
			Assert.True(ix.IsUnique);
			Assert.Contains("CREATE UNIQUE INDEX UX_HospitalDentalFindings_PlanLine ON dbo.HospitalDentalFindings (PlanLineId) WHERE PlanLineId IS NOT NULL;", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(5, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // patients, services, practitioners, payers, pre-authorisations
	}
}
