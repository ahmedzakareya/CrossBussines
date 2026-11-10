using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H34 — English added to a SIGNED imaging report, with no database: when it may be added, and the slice
	// that keeps who added it and when (nullable columns only, each guarded, each read by the model).
	// ==========================================================================================
	public class HospitalRadTranslationTests
	{
		[Theory]
		[InlineData(HospitalRadStatus.Scheduled)]
		[InlineData(HospitalRadStatus.Reporting)]
		public void A_report_not_yet_signed_is_edited_not_translated(string status) =>
			Assert.Equal("A translation is added to a signed report; before signing, write the English in the report",
				HospitalRadTranslationRules.Refusal(status, "Normal chest", "No acute finding"));

		[Fact]
		public void A_signed_report_takes_the_findings_or_the_impression_in_English()
		{
			Assert.Null(HospitalRadTranslationRules.Refusal(HospitalRadStatus.Signed, "Normal chest", null));
			Assert.Null(HospitalRadTranslationRules.Refusal(HospitalRadStatus.Signed, null, "No acute finding"));
			Assert.Equal("Write the English findings or impression", HospitalRadTranslationRules.Refusal(HospitalRadStatus.Signed, " ", null));
		}

		[Fact]
		public void The_slice_only_adds_who_translated_and_when_and_the_model_reads_both()
		{
			var sql = HospitalSliceParity.Slice("hospital_h34_rad_translation.sql");
			var adds = System.Text.RegularExpressions.Regex.Matches(sql, @"IF COL_LENGTH\('dbo\.HospitalRadOrders', '(\w+)'\) IS NULL ALTER TABLE dbo\.HospitalRadOrders ADD (\w+) \w+ NULL;");
			Assert.Equal(new[] { "TranslatedAt", "TranslatedByEmployeeId" }, adds.Select(m => m.Groups[1].Value));
			foreach (System.Text.RegularExpressions.Match m in adds) Assert.Equal(m.Groups[1].Value, m.Groups[2].Value);
			Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, "ALTER TABLE").Count);
			Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
			// the table and H33's English column are checked before anything is added
			var guard = sql[..sql.IndexOf("THROW", StringComparison.Ordinal)];
			Assert.Contains("OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL", guard);
			Assert.Contains("COL_LENGTH('dbo.HospitalRadOrders', 'FindingsEn') IS NULL", guard);
			Assert.Equal(typeof(DateTime?), typeof(HospitalRadOrder).GetProperty("TranslatedAt")!.PropertyType);
			Assert.Equal(typeof(int?), typeof(HospitalRadOrder).GetProperty("TranslatedByEmployeeId")!.PropertyType);
		}

		// a patient photograph is health PII: the hospital uploads folder was missing from the gate and served anonymously
		[Theory]
		[InlineData("/uploads/hospital/patients/1/52b85706bddd48fcbadeca57f99cf10b.jpg")]
		[InlineData("/uploads/hospital/x.png")]
		public void Hospital_uploads_are_never_served_to_an_anonymous_caller(string path) =>
			Assert.NotEqual(CrossBuy.BL.Platform.PrivateFilePolicy.Public, CrossBuy.BL.Platform.PrivateFileGate.PolicyFor(path));
	}
}
