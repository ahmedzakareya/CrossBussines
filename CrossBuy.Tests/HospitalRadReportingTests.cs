using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H259 — structured reports and peer review. What must never happen: a report signed with a required
	// section empty; a section counted as written because its heading is there; a radiologist reviewing their own
	// report; a discrepancy scored without saying what it was; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalRadReportingTests
	{
		private static readonly IReadOnlyList<HospitalRadSection> Cxr = HospitalRadReportingRules.Standard.First(s => s.Code == "CXR").Sections;

		[Fact]
		public void Sections_round_trip_and_a_template_is_refused_until_it_is_whole()
		{
			Assert.Equal(Cxr, HospitalRadReportingRules.Parse(HospitalRadReportingRules.Serialize(Cxr)));
			Assert.Null(HospitalRadReportingRules.Refusal("CXR", "أشعة الصدر", "Chest X-ray", HospitalRadModality.XRay, Cxr));
			Assert.NotNull(HospitalRadReportingRules.Refusal("", "a", "b", null, Cxr));
			Assert.NotNull(HospitalRadReportingRules.Refusal("X", "a", "b", "PET", Cxr));
			Assert.NotNull(HospitalRadReportingRules.Refusal("X", "a", "b", null, Array.Empty<HospitalRadSection>()));
			Assert.NotNull(HospitalRadReportingRules.Refusal("X", "a", "b", null, new[] { new HospitalRadSection("أ", "A", false) }));
			Assert.NotNull(HospitalRadReportingRules.Refusal("X", "a", "b", null, new[] { new HospitalRadSection("أ", "A", true), new HospitalRadSection("أ", "B", false) }));
			// a "|" typed in a heading cannot break the stored line
			Assert.Equal("أ/ب", HospitalRadReportingRules.Parse(HospitalRadReportingRules.Serialize(new[] { new HospitalRadSection("أ|ب", "A", true) }))[0].Heading);
		}

		[Fact]
		public void The_skeleton_alone_leaves_every_required_section_missing()
		{
			var ar = HospitalRadReportingRules.Skeleton(Cxr, false);
			var en = HospitalRadReportingRules.Skeleton(Cxr, true);
			Assert.StartsWith("دواعي الفحص:\n\nالتقنية:", ar);
			Assert.Equal(Cxr.Count(s => s.Required), HospitalRadReportingRules.Missing(Cxr, ar, en).Count);
		}

		[Fact]
		public void A_section_counts_when_written_in_either_language()
		{
			var ar = "الرئتان: صافيتان.\nالجنبة: لا انصباب.\nالقلب والمنصف:\nحجم القلب طبيعي.";
			var en = "Lungs:\n\nPleura:\n";
			Assert.Empty(HospitalRadReportingRules.Missing(Cxr, ar, en));
			var half = HospitalRadReportingRules.Missing(Cxr, "الرئتان: صافيتان.", "Pleura: no effusion.");
			Assert.Equal(new[] { "Heart and mediastinum" }, half.Select(s => s.HeadingEn));
			// an optional section left empty is fine; text before the first heading belongs to none
			Assert.Equal(3, HospitalRadReportingRules.Missing(Cxr, "تمهيد بلا عنوان", null).Count);
		}

		[Fact]
		public void Peer_review_needs_another_radiologist_and_a_reason_for_a_discrepancy()
		{
			Assert.Null(HospitalRadReportingRules.ReviewRefusal(1, false, null, 5, 7));
			Assert.Null(HospitalRadReportingRules.ReviewRefusal(3, true, "Missed a 9 mm nodule", 5, 7));
			Assert.NotNull(HospitalRadReportingRules.ReviewRefusal(1, false, null, 5, 5));
			Assert.NotNull(HospitalRadReportingRules.ReviewRefusal(1, false, null, null, 7));
			Assert.NotNull(HospitalRadReportingRules.ReviewRefusal(2, false, " ", 5, 7));
			Assert.NotNull(HospitalRadReportingRules.ReviewRefusal(1, true, null, 5, 7));
			Assert.NotNull(HospitalRadReportingRules.ReviewRefusal(4, false, "x", 5, 7));
		}

		[Fact]
		public void Rates_and_the_sample_order()
		{
			Assert.Equal((4, 50.0m, 25.0m), HospitalRadReportingRules.Rates(new[] { (1, false), (2, false), (3, true), (1, false) }));
			Assert.Equal((0, 0m, 0m), HospitalRadReportingRules.Rates(Array.Empty<(int, bool)>()));
			var day = new DateTime(2026, 11, 23);
			Assert.Equal(HospitalRadReportingRules.SampleKey(17, day), HospitalRadReportingRules.SampleKey(17, day));
			var order = Enumerable.Range(1, 20).OrderBy(i => HospitalRadReportingRules.SampleKey(i, day)).ToList();
			Assert.NotEqual(Enumerable.Range(1, 20), order);   // not simply newest or oldest first
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalRadReportingModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h259_rad_reporting.sql");

		[Theory]
		[InlineData(typeof(HospitalRadTemplate), "HospitalRadTemplates")]
		[InlineData(typeof(HospitalRadTemplateUse), "HospitalRadTemplateUses")]
		[InlineData(typeof(HospitalRadPeerReview), "HospitalRadPeerReviews")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
