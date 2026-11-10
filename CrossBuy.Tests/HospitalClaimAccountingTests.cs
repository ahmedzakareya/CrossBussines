using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H20 — insurance accounting, DATABASE-FREE: what each payer answer posts (the pure plan and its
	// lines), the payer receivable's resolution, the remittance refusals, and the model against its slice.
	//
	// What must never happen: a payer's share posted to the patients' receivable, an answer posted twice, a
	// rejection left sitting in the receivable, or a remittance clearing more than the payer owes.
	// The service paths that read the database (refusing to close an unmapped insured bill, refusing an answer
	// with an unmapped account, the duplicate index) are NOT exercised here: the owner's rule allows no database
	// of any kind for tests, so they are asserted by the plan and the slice, and verified on the app once the slice
	// is authorized.
	// ==========================================================================================
	public class HospitalClaimAccountingTests
	{
		// ---------------- what an answer posts ----------------

		[Fact]
		public void An_original_line_accepted_in_full_posts_nothing() =>
			Assert.Null(HospitalClaimAccountingRules.Plan(true, 38.475m, null, false));

		[Fact]
		public void An_original_line_accepted_short_posts_the_difference_as_an_adjustment()
		{
			var p = HospitalClaimAccountingRules.Plan(true, 38.475m, 30m, false)!;
			Assert.Equal(HospitalClaimEvent.Adjustment, p.Event);
			Assert.Equal(8.475m, p.Amount);
		}

		[Fact]
		public void An_original_line_rejected_writes_off_the_whole_payer_share()
		{
			var p = HospitalClaimAccountingRules.Plan(false, 38.475m, null, false)!;
			Assert.Equal(HospitalClaimEvent.Rejection, p.Event);
			Assert.Equal(38.475m, p.Amount);
		}

		[Fact]
		public void A_rejected_line_with_no_payer_share_posts_nothing() =>
			Assert.Null(HospitalClaimAccountingRules.Plan(false, 0m, null, false));

		[Fact]
		public void A_resubmission_accepted_recovers_what_was_accepted()
		{
			var p = HospitalClaimAccountingRules.Plan(true, 38.475m, 20m, true)!;
			Assert.Equal(HospitalClaimEvent.Recovery, p.Event);
			Assert.Equal(20m, p.Amount);
		}

		[Fact]
		public void A_resubmission_rejected_again_posts_nothing_because_it_is_already_written_off() =>
			Assert.Null(HospitalClaimAccountingRules.Plan(false, 38.475m, null, true));

		[Theory]
		[InlineData(HospitalClaimEvent.Adjustment)]
		[InlineData(HospitalClaimEvent.Rejection)]
		public void Adjustments_and_rejections_credit_the_payer_receivable(string ev)
		{
			var l = HospitalClaimAccountingRules.Lines(ev, 8.475m, payerReceivable: 110210, counter: 530120, "BILL-1 · CLM-1 · S1");
			Assert.Equal(2, l.Count);
			Assert.Equal((530120, 8.475m, 0m), (l[0].AccountId, l[0].Debit, l[0].Credit));
			Assert.Equal((110210, 0m, 8.475m), (l[1].AccountId, l[1].Debit, l[1].Credit));
		}

		[Fact]
		public void A_recovery_debits_the_payer_receivable_back()
		{
			var l = HospitalClaimAccountingRules.Lines(HospitalClaimEvent.Recovery, 20m, 110210, 530120, "m");
			Assert.Equal((110210, 20m, 0m), (l[0].AccountId, l[0].Debit, l[0].Credit));
			Assert.Equal((530120, 0m, 20m), (l[1].AccountId, l[1].Debit, l[1].Credit));
		}

		[Theory]
		[InlineData(HospitalClaimEvent.Adjustment)]
		[InlineData(HospitalClaimEvent.Rejection)]
		[InlineData(HospitalClaimEvent.Recovery)]
		public void Every_posting_balances(string ev)
		{
			var l = HospitalClaimAccountingRules.Lines(ev, 12.345m, 1, 2, "m");
			Assert.Equal(l.Sum(x => x.Debit), l.Sum(x => x.Credit));
		}

		// ---------------- the payer receivable (D2) ----------------

		[Fact]
		public void A_payer_override_wins_over_the_company_payer_receivable() => Assert.Equal(7, HospitalClaimAccountingRules.PayerReceivable(7, 5));

		[Fact]
		public void Without_an_override_the_company_payer_receivable_applies() => Assert.Equal(5, HospitalClaimAccountingRules.PayerReceivable(null, 5));

		[Fact]
		public void With_neither_the_payer_receivable_is_unmapped_and_never_guessed() => Assert.Null(HospitalClaimAccountingRules.PayerReceivable(null, null));

		[Fact]
		public void The_payer_receivable_is_never_the_patients_receivable() =>
			Assert.NotNull(HospitalClaimAccountingRules.MappingRefusal(110205, 530120, 530121, patientReceivable: 110205));

		[Theory]
		[InlineData(110210, 110210, 530121)]
		[InlineData(110210, 530120, 530120)]
		[InlineData(110210, 530120, 110210)]
		public void One_account_cannot_play_two_roles(int ar, int rejection, int adjustment) =>
			Assert.NotNull(HospitalClaimAccountingRules.MappingRefusal(ar, rejection, adjustment, 110205));

		[Fact]
		public void Three_distinct_accounts_apart_from_the_patients_are_accepted()
		{
			Assert.Null(HospitalClaimAccountingRules.MappingRefusal(110210, 530120, 530121, 110205));
			Assert.Null(HospitalClaimAccountingRules.MappingRefusal(null, null, null, 110205));   // unmapped is saved; closing then refuses
		}

		[Fact]
		public void The_one_resolver_refuses_the_patients_receivable_for_every_path()
		{
			// F1: the check lives in HospitalClaimAccounts.PayerReceivableAsync, which close, answer and remit all call
			var src = Source("BL", "Hospital", "HospitalClaimAccountingService.cs");
			int at = src.IndexOf("public static async Task<(int? Account, string? Refusal)> PayerReceivableAsync", StringComparison.Ordinal);
			int end = src.IndexOf("public const string Asset", at, StringComparison.Ordinal);
			Assert.Contains("PatientReceivableAsync(db, ctx, ct)", src[at..end]);
		}

		[Fact]
		public void A_remittance_rechecks_what_is_owed_after_locking_the_batch_inside_its_transaction()
		{
			// F3, in order inside RemitAsync: the transaction opens, the batch row is locked, the outstanding is re-read
			// and re-checked, and only then does the posting happen. (Code order only: the lock itself needs SQL Server.)
			var src = Source("BL", "Hospital", "HospitalClaimAccountingService.cs");
			int body = src.IndexOf("public async Task<HospitalResult> RemitAsync", StringComparison.Ordinal);
			int end = src.IndexOf("private async Task<string?> OriginRefusalAsync", body, StringComparison.Ordinal);
			string m = src[body..end];
			int tx = m.IndexOf("ScopedTx.BeginOrJoinAsync", StringComparison.Ordinal);
			int lockAt = m.IndexOf("SetProperty(b => b.Status, b => b.Status)", StringComparison.Ordinal);
			int reread = m.IndexOf("var locked = await BatchLedgerAsync", StringComparison.Ordinal);
			int recheck = m.IndexOf("RemittanceRefusal(amount, locked.Value.Outstanding", StringComparison.Ordinal);
			int post = m.IndexOf("await PostAsync(", StringComparison.Ordinal);
			Assert.True(tx >= 0 && tx < lockAt && lockAt < reread && reread < recheck && recheck < post, $"order tx={tx} lock={lockAt} reread={reread} recheck={recheck} post={post}");
		}

		[Fact]
		public void The_bill_preview_never_debits_the_payer_share_to_the_patients_receivable()
		{
			// the H18 line that put the payer's share on s.ReceivableAccountId must stay gone (mutation-checked:
			// restoring it turns this red)
			var src = Source("BL", "Hospital", "HospitalBillingService.cs");
			Assert.DoesNotContain("new HospitalLedgerLine(s.ReceivableAccountId.Value, v.Totals.Insurer", src);
			Assert.Contains("new HospitalLedgerLine(pa, v.Totals.Insurer", src);
		}

		// ---------------- remittances ----------------

		private static decimal Owed(decimal original, params (string, decimal)[] posted) => HospitalClaimAccountingRules.Outstanding(original, posted);

		[Fact]
		public void What_is_owed_is_net_of_adjustments_rejections_and_payments_plus_recoveries() =>
			Assert.Equal(60m, Owed(100m, (HospitalClaimEvent.Adjustment, 10m), (HospitalClaimEvent.Rejection, 20m), (HospitalClaimEvent.Remittance, 30m), (HospitalClaimEvent.Recovery, 20m)));

		[Fact]
		public void A_partial_remittance_is_accepted() => Assert.Null(HospitalClaimAccountingRules.RemittanceRefusal(25m, 60m, "TRX-1"));

		[Fact]
		public void A_remittance_of_exactly_what_is_owed_is_accepted() => Assert.Null(HospitalClaimAccountingRules.RemittanceRefusal(60m, 60m, "TRX-1"));

		[Fact]
		public void A_remittance_above_what_is_owed_is_refused() => Assert.NotNull(HospitalClaimAccountingRules.RemittanceRefusal(60.001m, 60m, "TRX-1"));

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void A_remittance_needs_the_payer_reference(string? reference) => Assert.NotNull(HospitalClaimAccountingRules.RemittanceRefusal(10m, 60m, reference));

		[Theory]
		[InlineData(0)]
		[InlineData(-5)]
		public void A_remittance_is_more_than_zero(int amount) => Assert.NotNull(HospitalClaimAccountingRules.RemittanceRefusal(amount, 60m, "TRX-1"));

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h20_claim_accounting.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalClaimAccountingModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalInsuranceAccounts), "HospitalInsuranceAccounts" }, new object[] { typeof(HospitalPayerAccount), "HospitalPayerAccounts" },
			new object[] { typeof(HospitalClaimPosting), "HospitalClaimPostings" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Theory]
		[InlineData("UX_HospitalClaimPostings_LineEvent", "[ClaimLineId] IS NOT NULL", "(ClaimLineId, Event)")]
		[InlineData("UX_HospitalClaimPostings_Remittance", "[Reference] IS NOT NULL", "(BatchId, Reference)")]
		public void Each_answer_posts_once_in_model_and_slice(string name, string filter, string keys)
		{
			var ix = Model().FindEntityType(typeof(HospitalClaimPosting))!.GetIndexes().Single(i => i.GetDatabaseName() == name);
			Assert.True(ix.IsUnique);
			Assert.Equal(filter, ix.GetFilter());
			Assert.Contains($"CREATE UNIQUE INDEX {name} ON dbo.HospitalClaimPostings {keys} WHERE {filter};", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void One_mapping_per_company_and_one_override_per_payer()
		{
			Assert.True(Model().FindEntityType(typeof(HospitalInsuranceAccounts))!.GetIndexes().Single().IsUnique);
			Assert.True(Model().FindEntityType(typeof(HospitalPayerAccount))!.GetIndexes().Single().IsUnique);
		}

		[Fact]
		public void Event_and_reference_are_bounded_so_they_can_be_index_keys()
		{
			var et = Model().FindEntityType(typeof(HospitalClaimPosting))!;
			Assert.Equal(20, et.FindProperty(nameof(HospitalClaimPosting.Event))!.GetMaxLength());
			Assert.Equal(60, et.FindProperty(nameof(HospitalClaimPosting.Reference))!.GetMaxLength());
			Assert.Contains("nvarchar(20)", Slice());
			Assert.DoesNotContain("nvarchar(max)", Slice());
		}

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(6, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // accounts, payers, batches, claim lines, bills, journals

		[Fact]
		public void The_slice_alters_no_existing_table() =>
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);

		private static string Source(params string[] parts)
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(new[] { d.FullName, "CrossBuy" }.Concat(parts).ToArray());
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException(string.Join("/", parts));
		}
	}
}
