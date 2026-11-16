using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CrossBuy.Tests.HospitalPartialDispenseTests;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H146 (+ H148 credit notes): medicine the patient brings back. What must never happen:
	//   · more coming back than was dispensed (or returned twice);
	//   · stock coming back at a cost or batch other than the one that left;
	//   · the patient still charged for what came back; a closed bill changed instead of credited (H148), a bill line
	//     credited past its quantity, or a line the insurer paid part of credited outside its claim;
	//   · an overpaid excess lost — or credited with no ledger entry, or counted as money in the till;
	//   · a refused return leaving anything changed.
	// ==========================================================================================
	public class HospitalDispenseReturnTests
	{
		private const int Receivable = 1103, Deposits = 1108, Cash = 1101, Bank = 1102, Revenue = 1104, Vat = 1106, OverShort = 1107;

		private sealed class Recorder : IHospitalLedger
		{
			public readonly List<(string Source, IReadOnlyList<HospitalLedgerLine> Lines)> Posted = new();
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
			{
				Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
				Posted.Add((sourceType, lines));
				return Task.FromResult<(bool, string?, int?)>((true, null, Posted.Count));
			}
		}

		private static HospitalDispenseReturnService Svc(World w, IHospitalLedger ledger) => new(w.Host.Db, w.Access, w.StockService, w.Billing, ledger);

		private static async Task Books(World w)
		{
			foreach (var id in new[] { Cash, Bank, Receivable, Revenue, Vat, OverShort, Deposits })
				w.Host.Seed.Accounts.Add(new CrossBuy.Models.Context.Accounting.Account { ID = id, CompanyID = 1, Code = "A" + id, Name = "ح" + id, NameEn = "Acc " + id, IsPostable = true, IsActive = true, AccountTypeId = 1 });
			await w.Host.Seed.SaveChangesAsync();
			Assert.True((await w.Billing.SaveSettingsAsync(Ctx(), new HospitalBillingSettingsInput
			{ CashAccountId = Cash, BankAccountId = Bank, ReceivableAccountId = Receivable, RevenueAccountId = Revenue, VatAccountId = Vat, CashOverShortAccountId = OverShort, PatientDepositAccountId = Deposits }, 7)).Ok);
		}

		private static async Task<decimal> BilledParacetamol(World w, int billId)
			=> await w.Host.Db.HospitalBillLines.AsNoTracking().Where(l => l.BillId == billId && l.Code == "MED-PARA").SumAsync(l => l.Quantity);

		[Fact]
		public void The_rules_take_newest_first_and_never_more_than_was_given()
		{
			Assert.Equal(7m, HospitalReturnRules.Returnable(new HospitalDispenseLine { DispensedQty = 10 }, 3));
			Assert.Equal("More than was dispensed and not yet returned ({0})", HospitalReturnRules.Refuse(8, 7, "unopened"));
			Assert.Equal("Write why the medicine came back", HospitalReturnRules.Refuse(1, 7, " "));
			Assert.Equal(new[] { (9, 4m), (8, 1m) }, HospitalReturnRules.Newest(new[] { (8, 6m), (9, 4m) }, 5)!.ToArray());
			Assert.Null(HospitalReturnRules.Newest(new[] { (8, 1m) }, 2));
		}

		[Fact]
		public async Task A_return_puts_the_stock_back_as_it_left_and_takes_it_off_the_open_bill()
		{
			var ledger = new Recorder();
			var (w, rx, para, _) = await Prescribed(ledger);
			using var _h = w.Host;
			var lines = await Lines(w, rx.ID);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [lines[para].ID] = 6 })).Ok);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);   // the other 4: two fills, two bill lines
			var billId = (await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync()).BillId!.Value;
			var svc = Svc(w, ledger);
			int asked = w.Stock.Asked.Count;

			var r = await svc.ReturnAsync(Ctx(), rx.ID, lines[para].ID, 5, "Unopened, sealed box", 7, "u7", Now);
			Assert.True(r.Ok, r.Error);
			var ins = w.Stock.Asked.Skip(asked).ToList();
			Assert.Equal(new[] { (4m, (short)1), (1m, (short)1) }, ins.Select(m => (m.Qty, m.Direction)).ToArray());   // newest movement first
			Assert.All(ins, m => Assert.Equal((HospitalReturnRules.ReturnSource, 2m), (m.SourceType, m.UnitCostInBase!.Value)));   // at the cost it left
			Assert.Equal(5m, await BilledParacetamol(w, billId));   // 10 billed, 5 came back
			Assert.Equal(1, await w.Host.Db.HospitalBillLines.CountAsync(l => l.BillId == billId && l.Code == "MED-PARA"));   // the newest line went to nothing and was removed

			Assert.Equal("More than was dispensed and not yet returned ({0})", (await svc.ReturnAsync(Ctx(), rx.ID, lines[para].ID, 6, "again", 7, "u7", Now)).Error);
			Assert.True((await svc.ReturnAsync(Ctx(), rx.ID, lines[para].ID, 5, "the rest, sealed", 7, "u7", Now)).Ok);
			Assert.Equal(0m, await BilledParacetamol(w, billId));
			Assert.Equal(10m, w.Stock.Asked.Skip(asked).Sum(m => m.Qty));   // everything that left came back, no more
			Assert.Empty(ledger.Posted.Where(p => p.Source == "HospitalPatientDeposit"));   // nothing was paid: no credit
		}

		[Fact]
		public async Task A_paid_bill_turns_the_excess_into_a_credit_on_the_patients_deposit_through_the_cashiers_shift()
		{
			var ledger = new Recorder();
			var (w, rx, para, _) = await Prescribed(ledger);
			using var _h = w.Host;
			await Books(w);
			var lines = await Lines(w, rx.ID);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			var d = await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync();
			var svc = Svc(w, ledger);

			// paid in full at the counter
			var shift = (await w.Billing.OpenShiftAsync(Ctx(), "Desk", 0m, 7, Now)).Id!.Value;
			var due = (await w.Billing.BillAsync(Ctx(), d.BillId!.Value, Now))!.Totals.Due;
			Assert.True((await w.Billing.CollectAsync(Ctx(), d.BillId!.Value, due, HospitalPaymentMethods.Cash, 7, Now)).Ok);
			var till = (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!.Expected;

			// someone with no shift cannot hand back money; nothing changes
			int asked = w.Stock.Asked.Count;
			Assert.Equal("The bill is already paid: the cashier records this return, so the excess goes to the patient's deposit",
				(await svc.ReturnAsync(Ctx(9), rx.ID, lines[para].ID, 2, "sealed", 9, "u9", Now)).Error);
			Assert.Equal(10m, await BilledParacetamol(w, d.BillId!.Value));
			Assert.Empty(await w.Host.Db.Set<HospitalDispenseReturn>().ToListAsync());

			// the cashier (employee 7, with the open shift) records it: 2 × 3.000 = 6.000 to the patient's deposit
			var r = await svc.ReturnAsync(Ctx(), rx.ID, lines[para].ID, 2, "sealed", 7, "u7", Now);
			Assert.True(r.Ok, r.Error);
			var dep = await w.Host.Db.Set<HospitalPatientDeposit>().AsNoTracking().SingleAsync();
			Assert.Equal((6m, HospitalReturnRules.CreditMethod, d.PatientId), (dep.Amount, dep.Method, dep.PatientId));
			var (src, posted) = ledger.Posted[^1];
			Assert.Equal(new[] { (Receivable, 6m, 0m), (Deposits, 0m, 6m) }, posted.Select(l => (l.AccountId, l.Debit, l.Credit)).ToArray());
			Assert.Equal(0m, (await w.Billing.BillAsync(Ctx(), d.BillId!.Value, Now))!.Totals.Due);   // nothing owed either way
			Assert.Equal(till, (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!.Expected);   // no money moved in the drawer
			Assert.Equal(dep.ID, (await w.Host.Db.Set<HospitalDispenseReturn>().AsNoTracking().SingleAsync()).DepositId);
		}

		[Fact]
		public async Task Another_company_is_not_found()
		{
			var (w, rx, para, _) = await Prescribed();
			using var _h = w.Host;
			var lines = await Lines(w, rx.ID);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			Assert.Equal("not_found", (await Svc(w, new Recorder()).ReturnAsync(Ctx(7, 2), rx.ID, lines[para].ID, 1, "sealed", 7, "u7", Now)).Error);
		}

		// ---- H148: a CLOSED bill — a credit note, never a change to the posted bill ----

		/// Dispensed 10 paracetamol + 4 amoxicillin at 3.000, the visit's bill closed (posted). Returns the bill id.
		private static async Task<int> DispensedAndClosed(World w, Recorder ledger, HospitalDispense rx)
		{
			await Books(w);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			var billId = (await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync()).BillId!.Value;
			var closed = await w.Billing.CloseAsync(Ctx(), billId, 7, Now);
			Assert.True(closed.Ok, closed.Error);
			return billId;
		}

		[Fact]
		public async Task A_closed_bill_gets_a_credit_note_that_reverses_the_revenue_and_lowers_what_is_due()
		{
			var ledger = new Recorder();
			var (w, rx, para, _) = await Prescribed(ledger);
			using var _h = w.Host;
			var billId = await DispensedAndClosed(w, ledger, rx);
			var lines = await Lines(w, rx.ID);
			var due = (await w.Billing.BillAsync(Ctx(), billId, Now))!.Totals.Due;
			var posted = await w.Host.Db.HospitalBillLines.AsNoTracking().Where(l => l.BillId == billId).Select(l => new { l.ID, l.Quantity }).ToListAsync();
			int asked = w.Stock.Asked.Count;

			var r = await Svc(w, ledger).ReturnAsync(Ctx(), rx.ID, lines[para].ID, 3, "Unopened, sealed box", 7, "u7", Now);
			Assert.True(r.Ok, r.Error);
			Assert.Equal(3m, w.Stock.Asked.Skip(asked).Sum(m => m.Qty));   // the stock came back
			var note = await w.Host.Db.Set<HospitalCreditNote>().AsNoTracking().SingleAsync();
			Assert.Equal(("CN-000001", 9m, 0m, 9m, billId), (note.NoteNo, note.Net, note.Vat, note.Total, note.BillId));   // 3 × 3.000
			var (src, je) = ledger.Posted[^1];
			Assert.Equal("HospitalCreditNote", src);
			Assert.Equal(new[] { (Revenue, 9m, 0m), (Receivable, 0m, 9m) }, je.Select(l => (l.AccountId, l.Debit, l.Credit)).ToArray());
			var after = (await w.Billing.BillAsync(Ctx(), billId, Now))!;
			Assert.Equal((due - 9m, 9m), (after.Totals.Due, after.Totals.Credited));
			// the closed bill itself never changed
			Assert.Equal(posted, await w.Host.Db.HospitalBillLines.AsNoTracking().Where(l => l.BillId == billId).Select(l => new { l.ID, l.Quantity }).ToListAsync());
			Assert.Empty(await w.Host.Db.Set<HospitalPatientDeposit>().ToListAsync());   // nothing had been paid: no deposit credit
		}

		[Fact]
		public async Task A_paid_closed_bill_sends_the_excess_to_the_deposit_and_no_money_moves_in_the_drawer()
		{
			var ledger = new Recorder();
			var (w, rx, para, amox) = await Prescribed(ledger);
			using var _h = w.Host;
			var billId = await DispensedAndClosed(w, ledger, rx);
			var lines = await Lines(w, rx.ID);
			var shift = (await w.Billing.OpenShiftAsync(Ctx(), "Desk", 0m, 7, Now)).Id!.Value;
			var due = (await w.Billing.BillAsync(Ctx(), billId, Now))!.Totals.Due;
			Assert.True((await w.Billing.CollectAsync(Ctx(), billId, due, HospitalPaymentMethods.Cash, 7, Now)).Ok);
			var till = (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!.Expected;

			// someone with no shift cannot hand back money: nothing changes, not even the credit note
			Assert.Equal("The bill is already paid: the cashier records this return, so the excess goes to the patient's deposit",
				(await Svc(w, ledger).ReturnAsync(Ctx(9), rx.ID, lines[amox].ID, 2, "sealed", 9, "u9", Now)).Error);
			Assert.Empty(await w.Host.Db.Set<HospitalCreditNote>().ToListAsync());

			Assert.True((await Svc(w, ledger).ReturnAsync(Ctx(), rx.ID, lines[amox].ID, 2, "sealed", 7, "u7", Now)).Ok);
			var dep = await w.Host.Db.Set<HospitalPatientDeposit>().AsNoTracking().SingleAsync();
			Assert.Equal((6m, HospitalReturnRules.CreditMethod), (dep.Amount, dep.Method));
			Assert.Equal(new[] { "HospitalCreditNote", "HospitalPatientDeposit" }, ledger.Posted.TakeLast(2).Select(p => p.Source).ToArray());
			Assert.Equal(0m, (await w.Billing.BillAsync(Ctx(), billId, Now))!.Totals.Due);   // nothing owed either way
			Assert.Equal(till, (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!.Expected);   // no cash moved
		}

		[Fact]
		public async Task A_closed_bill_line_is_never_credited_past_its_quantity()
		{
			var ledger = new Recorder();
			var (w, rx, para, _) = await Prescribed(ledger);
			using var _h = w.Host;
			var billId = await DispensedAndClosed(w, ledger, rx);
			var lines = await Lines(w, rx.ID);
			Assert.True((await Svc(w, ledger).ReturnAsync(Ctx(), rx.ID, lines[para].ID, 6, "sealed", 7, "u7", Now)).Ok);
			Assert.True((await Svc(w, ledger).ReturnAsync(Ctx(), rx.ID, lines[para].ID, 4, "the rest, sealed", 7, "u7", Now)).Ok);
			Assert.Equal("More than was dispensed and not yet returned ({0})", (await Svc(w, ledger).ReturnAsync(Ctx(), rx.ID, lines[para].ID, 1, "again", 7, "u7", Now)).Error);
			var notes = await w.Host.Db.Set<HospitalCreditNoteLine>().AsNoTracking().ToListAsync();
			Assert.Equal(10m, notes.Sum(n => n.Quantity));
			Assert.Equal(30m, (await w.Billing.BillAsync(Ctx(), billId, Now))!.Totals.Credited);
			Assert.Equal(new[] { "CN-000001", "CN-000002" }, await w.Host.Db.Set<HospitalCreditNote>().OrderBy(n => n.ID).Select(n => n.NoteNo).ToArrayAsync());
		}

		[Fact]
		public async Task A_line_the_insurer_paid_part_of_goes_through_the_claim_and_nothing_changes()
		{
			var ledger = new Recorder();
			var (w, rx, para, _) = await Prescribed(ledger);
			using var _h = w.Host;
			var billId = await DispensedAndClosed(w, ledger, rx);
			var lines = await Lines(w, rx.ID);
			var billLine = await w.Host.Db.HospitalBillLines.AsNoTracking().FirstAsync(l => l.BillId == billId && l.Code == "MED-PARA");
			var payer = new HospitalPayer { CompanyId = 1, Code = "INS", Name = "تأمين", NameEn = "Insurer" };
			w.Host.Db.Add(payer); await w.Host.Db.SaveChangesAsync();
			var cover = new HospitalBillCoverage { CompanyId = 1, BillId = billId, PayerId = payer.ID, PolicyId = 1, Amount = 30m, Covered = 30m, InsurerShare = 24m, PatientShare = 6m, FrozenAt = Now };
			w.Host.Db.Add(cover); await w.Host.Db.SaveChangesAsync();
			w.Host.Db.Add(new HospitalBillCoverageLine { CompanyId = 1, CoverageId = cover.ID, BillLineId = billLine.ID, Amount = 30m, Covered = true, InsurerAmount = 24m });
			await w.Host.Db.SaveChangesAsync();
			int asked = w.Stock.Asked.Count;
			Assert.Equal("The insurer paid part of this medicine: correct it through the insurance claim",
				(await Svc(w, ledger).ReturnAsync(Ctx(), rx.ID, lines[para].ID, 1, "sealed", 7, "u7", Now)).Error);
			Assert.Equal(asked, w.Stock.Asked.Count);
			Assert.Empty(await w.Host.Db.Set<HospitalCreditNote>().ToListAsync());
			Assert.Empty(await w.Host.Db.Set<HospitalDispenseReturn>().ToListAsync());
		}

		[Fact]
		public void A_credit_note_line_takes_the_bill_lines_own_price_and_vat()
		{
			Assert.Equal((9m, 1.35m), HospitalReturnRules.Money(3, 3m, true, 15m));
			Assert.Equal((9m, 0m), HospitalReturnRules.Money(3, 3m, false, 15m));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h146_dispense_returns.sql"), typeof(HospitalDispenseReturn), "HospitalDispenseReturns");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h146_dispense_returns.sql"), typeof(HospitalDispenseReturn), "HospitalDispenseReturns");

		[Theory]
		[InlineData(typeof(HospitalCreditNote), "HospitalCreditNotes")]
		[InlineData(typeof(HospitalCreditNoteLine), "HospitalCreditNoteLines")]
		public void The_credit_note_tables_match_their_slice(Type entity, string table)
		{
			var slice = HospitalSliceParity.Slice("hospital_h148_credit_notes.sql");
			HospitalSliceParity.NamesItsTable(Model(), slice, entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), slice, entity, table);
		}
	}
}
