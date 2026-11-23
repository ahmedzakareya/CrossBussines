using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H262 — disease registries. What must never happen: a diabetic left out because the diagnosis lives in
	// a lab value or the chronic note; a gap shown when the care was done; a wrong eGFR; outreach without a note; a model
	// naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalRegistryTests
	{
		private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
		private static HospitalRegistryFacts F(string[]? codes = null, string? text = null, decimal? a1c = null, int a1cDays = 30, decimal? maxA1c = null,
			int? creaDays = 30, decimal? egfr = 90, int? sys = null, int? dia = null, int? bpDays = null) =>
			new(codes ?? Array.Empty<string>(), text, a1c, a1c == null ? null : Now.AddDays(-a1cDays), maxA1c ?? a1c, creaDays == null ? null : Now.AddDays(-creaDays.Value), egfr,
				sys, dia, bpDays == null ? null : Now.AddDays(-bpDays.Value));

		[Fact]
		public void Membership_comes_from_codes_the_chronic_note_or_the_numbers()
		{
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Diabetes, F(codes: new[] { "E11.9" })));
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Diabetes, F(text: "سكري النوع الثاني")));
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Diabetes, F(a1c: 6.0m, maxA1c: 7.1m)));
			Assert.False(HospitalRegistryRules.Member(HospitalRegistryKind.Diabetes, F(a1c: 5.6m, codes: new[] { "I10" })));
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Hypertension, F(codes: new[] { "I10" })));
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Hypertension, F(text: "ارتفاع ضغط الدم")));
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Ckd, F(codes: new[] { "N18.3" })));
			Assert.True(HospitalRegistryRules.Member(HospitalRegistryKind.Ckd, F(egfr: 45m)));
			Assert.False(HospitalRegistryRules.Member(HospitalRegistryKind.Ckd, F(egfr: 75m)));
		}

		[Fact]
		public void Gaps_show_only_what_is_missing_or_off_target()
		{
			Assert.Empty(HospitalRegistryRules.Gaps(HospitalRegistryKind.Diabetes, F(a1c: 7.0m), Now));
			Assert.Equal(new[] { "hba1c-due", "kidney-due" }, HospitalRegistryRules.Gaps(HospitalRegistryKind.Diabetes, F(creaDays: null), Now).Select(g => g.Code));
			Assert.Equal(new[] { "hba1c-high" }, HospitalRegistryRules.Gaps(HospitalRegistryKind.Diabetes, F(a1c: 9.2m), Now).Select(g => g.Code));
			Assert.Equal(new[] { "hba1c-due" }, HospitalRegistryRules.Gaps(HospitalRegistryKind.Diabetes, F(a1c: 7.0m, a1cDays: 200), Now).Select(g => g.Code));
			Assert.Equal(new[] { "bp-due" }, HospitalRegistryRules.Gaps(HospitalRegistryKind.Hypertension, F(), Now).Select(g => g.Code));
			Assert.Equal(new[] { "bp-high" }, HospitalRegistryRules.Gaps(HospitalRegistryKind.Hypertension, F(sys: 150, dia: 85, bpDays: 10), Now).Select(g => g.Code));
			Assert.Empty(HospitalRegistryRules.Gaps(HospitalRegistryKind.Hypertension, F(sys: 128, dia: 82, bpDays: 10), Now));
			Assert.Equal(new[] { "kidney-due", "egfr-low" }, HospitalRegistryRules.Gaps(HospitalRegistryKind.Ckd, F(creaDays: 200, egfr: 25m), Now).Select(g => g.Code));
		}

		[Fact]
		public void The_egfr_is_ckd_epi_2021()
		{
			// published worked values: a 50-year-old man with creatinine 1.0 mg/dL ≈ 92; a 50-year-old woman ≈ 69
			Assert.InRange(HospitalRegistryRules.Egfr(1.0m, 50, "M")!.Value, 91m, 93m);
			Assert.InRange(HospitalRegistryRules.Egfr(1.0m, 50, "F")!.Value, 68m, 70m);
			Assert.True(HospitalRegistryRules.Egfr(3.0m, 70, "M") < 30m);
			Assert.Null(HospitalRegistryRules.Egfr(1.0m, 15, "M"));
			Assert.Null(HospitalRegistryRules.Egfr(null, 50, "M"));
			Assert.Null(HospitalRegistryRules.Egfr(1.0m, 50, null));
		}

		[Fact]
		public void Outreach_needs_a_registry_and_a_note()
		{
			Assert.Null(HospitalRegistryRules.OutreachRefusal(HospitalRegistryKind.Diabetes, "Booked HbA1c for Sunday"));
			Assert.NotNull(HospitalRegistryRules.OutreachRefusal("Asthma", "x"));
			Assert.NotNull(HospitalRegistryRules.OutreachRefusal(HospitalRegistryKind.Ckd, " "));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalRegistryModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h262_registries.sql");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalRegistryOutreach), "HospitalRegistryOutreach");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalRegistryOutreach), "HospitalRegistryOutreach");
		}
	}
}
