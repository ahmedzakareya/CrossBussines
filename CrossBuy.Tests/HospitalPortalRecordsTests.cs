using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H27 — the portal's results, bills and medicines, with no database: when a result may be released,
	// when a medicine may be asked for again, how the clinic may answer, what counts as abnormal; and the model
	// against its slice.
	// ==========================================================================================
	public class HospitalPortalRecordsTests
	{
		private static readonly DateTime Now = new(2026, 11, 4, 10, 0, 0);

		// ---------------- releasing a result ----------------

		[Theory]
		[InlineData(HospitalLabStatus.Ordered, "A lab result is released once it is verified")]
		[InlineData(HospitalLabStatus.Resulted, "A lab result is released once it is verified")]
		[InlineData(HospitalLabStatus.Verified, null)]
		public void A_lab_result_is_released_only_once_verified(string status, string? why) =>
			Assert.Equal(why, HospitalPortalRecordsRules.ReleaseRefusal(HospitalPortalResultSource.Lab, status));

		[Theory]
		[InlineData(HospitalRadStatus.Scanning, "An imaging report is released once it is signed")]
		[InlineData(HospitalRadStatus.Reporting, "An imaging report is released once it is signed")]
		[InlineData(HospitalRadStatus.Signed, null)]
		public void An_imaging_report_is_released_only_once_signed(string status, string? why) =>
			Assert.Equal(why, HospitalPortalRecordsRules.ReleaseRefusal(HospitalPortalResultSource.Radiology, status));

		[Fact]
		public void An_unknown_source_is_not_found_whatever_its_status()
		{
			Assert.Equal("not_found", HospitalPortalRecordsRules.ReleaseRefusal("Pharmacy", HospitalLabStatus.Verified));
			// the lab's "Verified" does not release an imaging order, nor the report's "Signed" a lab order
			Assert.NotNull(HospitalPortalRecordsRules.ReleaseRefusal(HospitalPortalResultSource.Radiology, HospitalLabStatus.Verified));
			Assert.NotNull(HospitalPortalRecordsRules.ReleaseRefusal(HospitalPortalResultSource.Lab, HospitalRadStatus.Signed));
		}

		// ---------------- asking for a medicine again ----------------

		[Fact]
		public void A_refill_is_refused_while_one_is_open_and_after_a_year()
		{
			Assert.Null(HospitalPortalRecordsRules.RefillRefusal(Now.AddDays(-30), Now, open: false));
			Assert.Equal("A refill request for this medicine is already open", HospitalPortalRecordsRules.RefillRefusal(Now.AddDays(-30), Now, open: true));
			Assert.Null(HospitalPortalRecordsRules.RefillRefusal(Now.AddDays(-HospitalPortalRecordsRules.RefillWithinDays), Now, open: false));
			Assert.Equal("Prescribed more than a year ago: book a visit to renew it",
				HospitalPortalRecordsRules.RefillRefusal(Now.AddDays(-HospitalPortalRecordsRules.RefillWithinDays - 1), Now, open: false));
		}

		// ---------------- the clinic's answer ----------------

		[Fact]
		public void A_refill_is_answered_once_and_a_refusal_says_why()
		{
			Assert.Null(HospitalPortalRecordsRules.DecisionRefusal(HospitalPortalRefillStatus.Open, approve: true, answer: null));
			Assert.Null(HospitalPortalRecordsRules.DecisionRefusal(HospitalPortalRefillStatus.Open, approve: false, answer: "Needs a blood test first"));
			Assert.Equal("Write the reason the patient will read", HospitalPortalRecordsRules.DecisionRefusal(HospitalPortalRefillStatus.Open, approve: false, answer: "  "));
			foreach (var done in new[] { HospitalPortalRefillStatus.Approved, HospitalPortalRefillStatus.Declined, HospitalPortalRefillStatus.Withdrawn })
				Assert.Equal("This request is already handled", HospitalPortalRecordsRules.DecisionRefusal(done, approve: true, answer: "ok"));
		}

		// ---------------- abnormal ----------------

		[Fact]
		public void A_lab_result_is_abnormal_when_any_value_is_flagged()
		{
			HospitalLabResult V(string flag) => new() { Flag = flag };
			Assert.False(HospitalPortalRecordsRules.Abnormal(new[] { V(HospitalLabFlag.Normal), V(HospitalLabFlag.Normal) }));
			Assert.True(HospitalPortalRecordsRules.Abnormal(new[] { V(HospitalLabFlag.Normal), V(HospitalLabFlag.High) }));
			Assert.True(HospitalPortalRecordsRules.Abnormal(new[] { V(HospitalLabFlag.CriticalLow) }));
			Assert.False(HospitalPortalRecordsRules.Abnormal(Array.Empty<HospitalLabResult>()));
		}

		[Theory]
		[InlineData(HospitalRadImpression.Normal, false)]
		[InlineData(HospitalRadImpression.Abnormal, true)]
		[InlineData(HospitalRadImpression.Critical, true)]
		[InlineData(null, false)]
		public void An_imaging_report_is_abnormal_by_its_impression(string? category, bool abnormal) =>
			Assert.Equal(abnormal, HospitalPortalRecordsRules.Abnormal(category));

		// ---------------- the model against its slice ----------------

		// H33 adds the English twins (NoteEn, AnswerEn) to these tables; the refill-reason slice adds HospitalPortalRefills.Reason
		private static string Slice() => HospitalSliceParity.Slice("hospital_h27_portal_records.sql") + HospitalSliceParity.Slice("hospital_h33_bilingual_text.sql")
			+ HospitalSliceParity.Slice("hospital_portal_refill_reason.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPortalRecordsModel.Configure);

		public static TheoryData<Type, string> Tables => new()
		{
			{ typeof(HospitalPortalRelease), "HospitalPortalReleases" },
			{ typeof(HospitalPortalRefill), "HospitalPortalRefills" },
		};

		[Theory, MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory, MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void A_result_is_released_once_by_a_unique_index_the_model_declares_too()
		{
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalPortalReleases_CompanyId_Source_OrderId ON dbo.HospitalPortalReleases (CompanyId, Source, OrderId);", Slice().Split('\n').Select(l => l.Trim()));
			var ix = Model().FindEntityType(typeof(HospitalPortalRelease))!.GetIndexes().Single(i => i.IsUnique);
			Assert.Equal(new[] { "CompanyId", "Source", "OrderId" }, ix.Properties.Select(p => p.Name));
		}

		[Fact]
		public void The_slice_checks_its_prerequisites_and_alters_no_existing_table()
		{
			Assert.Equal(2, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // portal accounts, prescription lines
			Assert.DoesNotContain("ALTER TABLE", HospitalSliceParity.Slice("hospital_h27_portal_records.sql"), StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void Every_status_and_source_the_code_knows_the_slice_allows()
		{
			foreach (var s in HospitalPortalRefillStatus.All) Assert.Contains($"N'{s}'", Slice());
			foreach (var s in HospitalPortalResultSource.All) Assert.Contains($"N'{s}'", Slice());
		}
	}
}
