using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H11 — the negative cases, DATABASE-FREE.
	//
	//   · The claim rules (HospitalInsuranceRules.ClaimRefusal / LineAmount / ResponseRefusal / ResubmitRefusal /
	//     PreauthDecisionRefusal) are pure: what BuildBatchAsync, RecordResponseAsync, ResubmitAsync and
	//     DecidePreauthAsync decide, they decide through these calls.
	//   · Concurrency cannot be raced without a database. What makes it safe is the unique indexes (one
	//     draft per payer and month, one claim per bill per batch, one waiting request per patient and
	//     service, one resubmission per line) together with the guarded "WHERE Status = …" updates. The EF
	//     model is BUILT here with the SQL Server conventions and no connection at all, and the same
	//     indexes are read back from CrossBuy/deploy/sql/hospital_h11_insurance.sql, so the model and the slice
	//     cannot drift apart silently.
	//
	// No context is constructed, no connection string exists, nothing is opened or created.
	// ==========================================================================================
	public class HospitalInsuranceGuardTests
	{
		private const int Co = 7, OtherCo = 8;
		private static readonly DateTime Closed = new(2026, 10, 14, 9, 30, 0);
		private const string Oct = "2026-10";
		private static readonly IReadOnlySet<int> NoneClaimed = new HashSet<int>();

		private static HospitalPayer Payer(int company = Co, DateTime? to = null) =>
			new() { ID = 3, CompanyId = company, Code = "GIG", IsActive = true, ValidFrom = new(2026, 1, 1), ValidTo = to };

		private static HospitalPatientPolicy Policy(int company = Co, int patient = 11, int payer = 3, DateTime? to = null) =>
			new() { ID = 5, CompanyId = company, PatientId = patient, PayerId = payer, PolicyNo = "KW-1", IsActive = true, ValidFrom = new(2026, 1, 1), ValidTo = to };

		private static HospitalClaimCandidate Bill(int company = Co, int patient = 11, string status = HospitalBillStatus.Closed, DateTime? closed = null, int? currency = null) =>
			new(21, company, patient, status, closed ?? Closed, null, currency);

		private static string? Refusal(HospitalClaimCandidate? bill = null, HospitalPayer? payer = null, HospitalPatientPolicy? policy = null,
			IReadOnlySet<int>? claimed = null, int? contractCurrency = null, string period = Oct)
			=> HospitalInsuranceRules.ClaimRefusal(bill ?? Bill(), Co, period, contractCurrency, claimed ?? NoneClaimed, policy ?? Policy(), payer ?? Payer());

		// ------------------------------------------------------------------ the happy path, so the negatives mean something

		[Fact]
		public void A_closed_covered_unclaimed_bill_of_the_month_is_claimed()
			=> Assert.Null(Refusal());

		// ------------------------------------------------------------------ cross-company

		[Fact]
		public void A_bill_of_another_company_is_never_claimed()
			=> Assert.Equal(HospitalInsuranceRules.OtherCompany, Refusal(bill: Bill(company: OtherCo)));

		[Fact]
		public void A_payer_of_another_company_never_claims()
			=> Assert.Equal(HospitalInsuranceRules.OtherCompany, Refusal(payer: Payer(company: OtherCo)));

		[Fact]
		public void A_policy_of_another_company_never_covers()
			=> Assert.Equal(HospitalInsuranceRules.OtherCompany, Refusal(policy: Policy(company: OtherCo)));

		[Fact]
		public void Another_patients_policy_never_covers_this_bill()
			=> Assert.Equal(HospitalInsuranceRules.OtherCompany, Refusal(policy: Policy(patient: 12)));

		[Fact]
		public void A_policy_with_another_payer_never_covers_this_payers_claim()
			=> Assert.Equal(HospitalInsuranceRules.OtherCompany, Refusal(policy: Policy(payer: 4)));

		// ------------------------------------------------------------------ duplicate claims, state, month, currency

		[Fact]
		public void A_bill_already_claimed_is_not_claimed_again()
			=> Assert.Equal(HospitalInsuranceRules.AlreadyClaimed, Refusal(claimed: new HashSet<int> { 21 }));

		[Fact]
		public void An_open_bill_is_not_claimed()
		{
			Assert.Equal(HospitalInsuranceRules.NotClosed, Refusal(bill: Bill(status: HospitalBillStatus.Open)));
			Assert.Equal(HospitalInsuranceRules.NotClosed, Refusal(bill: new HospitalClaimCandidate(21, Co, 11, HospitalBillStatus.Closed, null, null, null)));
		}

		[Fact]
		public void A_bill_closed_in_another_month_is_not_in_this_months_batch()
		{
			Assert.Equal(HospitalInsuranceRules.OtherPeriod, Refusal(bill: Bill(closed: new DateTime(2026, 11, 1))));
			Assert.Equal(HospitalInsuranceRules.OtherPeriod, Refusal(period: "2026-09"));
		}

		[Fact]
		public void A_bill_in_another_currency_than_the_contract_is_not_claimed()
		{
			Assert.Equal(HospitalInsuranceRules.OtherCurrency, Refusal(bill: Bill(currency: 2)));
			Assert.Equal(HospitalInsuranceRules.OtherCurrency, Refusal(contractCurrency: 2));
			Assert.Null(Refusal(bill: Bill(currency: 2), contractCurrency: 2));
		}

		// ------------------------------------------------------------------ expired coverage, judged on the day the bill closed

		[Fact]
		public void A_policy_expired_before_the_bill_closed_does_not_cover_it()
			=> Assert.Equal(HospitalInsuranceRules.PolicyExpired, Refusal(policy: Policy(to: Closed.Date.AddDays(-1))));

		[Fact]
		public void A_contract_expired_before_the_bill_closed_does_not_cover_it()
			=> Assert.Equal(HospitalInsuranceRules.ContractExpired, Refusal(payer: Payer(to: Closed.Date.AddDays(-1))));

		[Fact]
		public void Coverage_ending_on_the_closing_day_still_covers()
			=> Assert.Null(Refusal(policy: Policy(to: Closed.Date), payer: Payer(to: Closed.Date)));

		[Fact]
		public void No_policy_with_this_payer_is_not_covered()
			=> Assert.Equal(HospitalInsuranceRules.NoPolicy, HospitalInsuranceRules.ClaimRefusal(Bill(), Co, Oct, null, NoneClaimed, null, Payer()));

		// ------------------------------------------------------------------ the line amount

		[Fact]
		public void The_claim_never_includes_VAT_and_the_discount_applies_on_the_main_list_only()
		{
			Assert.Equal(90.000m, HospitalInsuranceRules.LineAmount(2, 50m, null, 10, 3));     // main list: 100 less 10%
			Assert.Equal(100.000m, HospitalInsuranceRules.LineAmount(2, 50m, 9, 10, 3));       // the payer's own list: built in
			Assert.Equal(0m, HospitalInsuranceRules.LineAmount(1, 50m, null, 150, 3));          // a discount is clamped to 100%
			Assert.Equal(33.33m, HospitalInsuranceRules.LineAmount(1, 33.333m, 9, 0, 2));      // the contract currency's decimals
		}

		// ------------------------------------------------------------------ the payer's answer and resubmission

		[Fact]
		public void No_answer_is_recorded_on_a_draft_or_twice()
		{
			Assert.NotNull(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Draft, HospitalClaimLineStatus.Pending, true, null, 10, null, null));
			foreach (var done in new[] { HospitalClaimLineStatus.Accepted, HospitalClaimLineStatus.Rejected, HospitalClaimLineStatus.Resubmitted })
				Assert.NotNull(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, done, true, null, 10, null, null));
			Assert.Null(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, HospitalClaimLineStatus.Pending, true, null, 10, null, null));
		}

		[Fact]
		public void An_accepted_amount_stays_within_what_was_claimed()
		{
			Assert.NotNull(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, HospitalClaimLineStatus.Pending, true, 10.001m, 10, null, null));
			Assert.NotNull(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, HospitalClaimLineStatus.Pending, true, -1, 10, null, null));
			Assert.Null(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Partial, HospitalClaimLineStatus.Pending, true, 4, 10, null, null));
		}

		[Fact]
		public void A_rejection_needs_both_the_code_and_the_reason()
		{
			Assert.NotNull(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, HospitalClaimLineStatus.Pending, false, null, 10, "R01", " "));
			Assert.NotNull(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, HospitalClaimLineStatus.Pending, false, null, 10, null, "no approval"));
			Assert.Null(HospitalInsuranceRules.ResponseRefusal(HospitalClaimBatchStatus.Submitted, HospitalClaimLineStatus.Pending, false, null, 10, "R01", "no approval"));
		}

		[Fact]
		public void Only_a_rejected_line_is_resubmitted_and_only_with_a_justification()
		{
			Assert.Null(HospitalInsuranceRules.ResubmitRefusal(HospitalClaimLineStatus.Rejected, "attached the report"));
			Assert.NotNull(HospitalInsuranceRules.ResubmitRefusal(HospitalClaimLineStatus.Rejected, "  "));
			foreach (var s in new[] { HospitalClaimLineStatus.Pending, HospitalClaimLineStatus.Accepted, HospitalClaimLineStatus.Resubmitted })
				Assert.NotNull(HospitalInsuranceRules.ResubmitRefusal(s, "again"));   // Resubmitted: never twice
		}

		[Fact]
		public void An_approval_request_is_answered_once_with_its_reference_or_reason()
		{
			Assert.NotNull(HospitalInsuranceRules.PreauthDecisionRefusal(HospitalPreauthStatus.Approved, true, "A1", null));
			Assert.NotNull(HospitalInsuranceRules.PreauthDecisionRefusal(HospitalPreauthStatus.Pending, true, " ", null));
			Assert.NotNull(HospitalInsuranceRules.PreauthDecisionRefusal(HospitalPreauthStatus.Pending, false, null, ""));
			Assert.Null(HospitalInsuranceRules.PreauthDecisionRefusal(HospitalPreauthStatus.Pending, true, "A1", null));
			Assert.Null(HospitalInsuranceRules.PreauthDecisionRefusal(HospitalPreauthStatus.Pending, false, null, "not covered"));
		}

		// ------------------------------------------------------------------ concurrency guards: the model, built without a database

		private static IModel Model()
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			HospitalInsuranceModel.Configure(mb);
			return mb.FinalizeModel();
		}

		[Theory]
		[InlineData(typeof(HospitalClaimBatch), "UX_HospitalClaimBatches_Draft", "CompanyId,PayerId,Period", "[Status] = 'Draft'")]
		[InlineData(typeof(HospitalPreauth), "UX_HospitalPreauths_Pending", "CompanyId,PatientId,ServiceId", "[Status] = 'Pending'")]
		[InlineData(typeof(HospitalClaimLine), "UX_HospitalClaimLines_Resubmits", "ResubmitsLineId", "[ResubmitsLineId] IS NOT NULL")]
		public void Concurrent_twins_meet_a_filtered_unique_index_in_the_model(Type entity, string name, string columns, string filter)
		{
			var ix = Model().FindEntityType(entity)!.GetIndexes().Single(i => i.GetDatabaseName() == name);
			Assert.True(ix.IsUnique);
			Assert.Equal(columns, string.Join(",", ix.Properties.Select(p => p.Name)));
			Assert.Equal(filter, ix.GetFilter());
		}

		[Fact]
		public void A_bill_is_claimed_once_per_batch_in_the_model()
		{
			var ix = Model().FindEntityType(typeof(HospitalClaim))!.GetIndexes().Single(i => i.IsUnique);
			Assert.Equal("CompanyId,BatchId,BillId", string.Join(",", ix.Properties.Select(p => p.Name)));
		}

		private static string Slice()
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", "hospital_h11_insurance.sql");
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException("CrossBuy/deploy/sql/hospital_h11_insurance.sql not found above " + AppContext.BaseDirectory);
		}

		// ------------------------------------------------------------------ table names: these entities have no DbSet

		private static string Slice(string unit, string file)
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", file);   // the registered slice, not a copy
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException($"CrossBuy/deploy/sql/{file} not found above " + AppContext.BaseDirectory);
		}

		/// Without a DbSet, EF names a table after its CLR type (dbo.HospitalPayer) while the slice creates the
		/// plural (dbo.HospitalPayers): the screens would fail even with the SQL applied. Every entity the
		/// Configure maps must name a table its slice creates.
		[Theory]
		[InlineData("h11", "hospital_h11_insurance.sql", 6)]
		[InlineData("h12", "hospital_h12_estimates.sql", 3)]
		public void Every_mapped_entity_names_a_table_its_slice_creates(string unit, string file, int expected)
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			if (unit == "h11") HospitalInsuranceModel.Configure(mb); else { HospitalInsuranceModel.Configure(mb); HospitalEstimatesModel.Configure(mb); }
			var model = mb.FinalizeModel();
			var mine = unit == "h11"
				? new[] { typeof(HospitalPayer), typeof(HospitalPatientPolicy), typeof(HospitalPreauth), typeof(HospitalClaimBatch), typeof(HospitalClaim), typeof(HospitalClaimLine) }
				: new[] { typeof(HospitalPackage), typeof(HospitalEstimate), typeof(HospitalEstimateLine) };
			Assert.Equal(expected, mine.Length);
			var sql = Slice(unit, file).Split('\n').Select(l => l.Trim()).ToList();
			foreach (var t in mine)
			{
				var table = model.FindEntityType(t)!.GetTableName();
				Assert.NotEqual(t.Name, table);
				Assert.Contains($"CREATE TABLE dbo.{table} (", sql);
			}
		}

		/// A slice that references a table it does not create must refuse to run without it (its THROW guard),
		/// instead of failing halfway on a foreign key.
		[Theory]
		[InlineData("h11", "hospital_h11_insurance.sql")]
		[InlineData("h12", "hospital_h12_estimates.sql")]
		public void Every_table_a_slice_references_but_does_not_create_is_a_checked_prerequisite(string unit, string file)
		{
			var sql = Slice(unit, file);
			var created = System.Text.RegularExpressions.Regex.Matches(sql, @"CREATE TABLE dbo\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
			var referenced = System.Text.RegularExpressions.Regex.Matches(sql, @"REFERENCES dbo\.(\w+)\(").Select(m => m.Groups[1].Value).ToHashSet();
			var guard = sql[..sql.IndexOf("THROW", StringComparison.Ordinal)];
			Assert.NotEmpty(referenced.Except(created));
			foreach (var t in referenced.Except(created)) Assert.Contains($"OBJECT_ID('dbo.{t}','U') IS NULL", guard);
		}

		[Theory]
		[InlineData("CREATE UNIQUE INDEX UX_HospitalClaimBatches_Draft ON dbo.HospitalClaimBatches (CompanyId, PayerId, Period) WHERE Status = N'Draft';")]
		[InlineData("CREATE UNIQUE INDEX UX_HospitalPreauths_Pending ON dbo.HospitalPreauths (CompanyId, PatientId, ServiceId) WHERE Status = N'Pending';")]
		[InlineData("CREATE UNIQUE INDEX UX_HospitalClaimLines_Resubmits ON dbo.HospitalClaimLines (ResubmitsLineId) WHERE ResubmitsLineId IS NOT NULL;")]
		[InlineData("CREATE UNIQUE INDEX UX_HospitalClaims_BatchBill ON dbo.HospitalClaims (CompanyId, BatchId, BillId);")]
		public void The_slice_declares_the_same_guard(string statement)
			=> Assert.Contains(statement, Slice().Split('\n').Select(l => l.Trim()));
	}
}
