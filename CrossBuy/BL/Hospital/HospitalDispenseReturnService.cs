using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H146 — medicine the patient brings back (see HospitalDispenseReturns.cs for the model). In ONE transaction:
	//   1. the stock: each outgoing movement of the line (newest first) replayed IN for what is returned, with its own
	//      batch, expiry and unit cost and the cost-of-goods account as the counter (the H131 replay), never more than that
	//      movement took out less what earlier returns brought back;
	//   2. the bill: on an OPEN bill the line's pharmacy bill lines (newest fill first) lose the returned quantity; a line
	//      at nothing is removed. A CLOSED bill (H148) stays as posted: a credit note credits those lines (never past
	//      their quantity less earlier notes), posts their revenue and VAT back against the receivable, and lowers what is
	//      due. Refused there: a foreign-currency bill, or a line the insurer paid part of (that goes through the claim);
	//   3. the money: if the patient had paid more than the bill now asks, the excess moves to the patient's deposit
	//      balance — a negative payment of method "Deposit" on the bill and a deposit of method "Credit", posted
	//      receivables against patient deposits — through the returning person's open cashier shift.
	// Refused, with nothing changed: more than the line can still return, no reason, an excess with no open shift or no
	// patient-deposits account, a closed bill the credit note cannot take (see 2).
	// ==========================================================================================

	public static class HospitalReturnRules
	{
		public const string ReturnSource = "HospitalDispenseReturn";
		/// A deposit that came from a return's overpaid excess, not from money taken at the counter (outside the till).
		public const string CreditMethod = "Credit";

		public static decimal Returnable(HospitalDispenseLine l, decimal returnedSoFar) => Math.Max(0, (l.DispensedQty ?? 0) - returnedSoFar);

		public static string? Refuse(decimal quantity, decimal returnable, string? reason)
		{
			if (quantity <= 0) return "Enter an amount above zero";
			if (quantity > returnable) return "More than was dispensed and not yet returned ({0})";
			if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return "Write why the medicine came back";
			return null;
		}

		/// Takes `quantity` from sources newest first, each giving at most what it still holds. Null when they hold less.
		/// H148: one credited bill line's money — the bill line's own price and VAT rate (the patient's whole part: an
		/// insured line is never credited here).
		public static (decimal Net, decimal Vat) Money(decimal quantity, decimal unitPrice, bool taxable, decimal vatRatePercent)
		{
			var net = Math.Round(quantity * unitPrice, 4);
			return (net, taxable ? Math.Round(net * vatRatePercent / 100m, 4) : 0m);
		}

		public static IReadOnlyList<(int Id, decimal Take)>? Newest(IEnumerable<(int Id, decimal Holds)> sources, decimal quantity)
		{
			var plan = new List<(int, decimal)>(); decimal need = quantity;
			foreach (var s in sources.OrderByDescending(x => x.Id))
			{
				if (need <= 0) break;
				var take = Math.Min(need, s.Holds); if (take <= 0) continue;
				plan.Add((s.Id, take)); need -= take;
			}
			return need > 0 ? null : plan;
		}
	}

	public sealed record HospitalReturnView(HospitalDispenseReturn Return, string? By);

	public sealed class HospitalDispenseReturnService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly HospitalBillingService _billing;
		private readonly IHospitalLedger _ledger;

		public HospitalDispenseReturnService(CrossDbContext db, IHospitalAccessService access, IStockService stock, HospitalBillingService billing, IHospitalLedger ledger)
		{ _db = db; _access = access; _stock = stock; _billing = billing; _ledger = ledger; }

		private IQueryable<HospitalDispenseReturn> Returns(BusinessContext ctx) => _db.Set<HospitalDispenseReturn>().Where(r => r.CompanyId == ctx.CompanyId);

		public async Task<Dictionary<int, decimal>> ReturnedAsync(BusinessContext ctx, int dispenseId, CancellationToken ct = default)
		{
			try { return await Returns(ctx).Where(r => r.DispenseId == dispenseId).GroupBy(r => r.LineId).Select(g => new { g.Key, Q = g.Sum(r => r.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new(); }
		}

		public async Task<IReadOnlyList<HospitalReturnView>> ListAsync(BusinessContext ctx, int dispenseId, CancellationToken ct = default)
		{
			List<HospitalDispenseReturn> rows;
			try { rows = await Returns(ctx).AsNoTracking().Where(r => r.DispenseId == dispenseId).OrderBy(r => r.ID).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalReturnView>(); }
			var ids = rows.Where(r => r.ByEmployeeId != null).Select(r => r.ByEmployeeId!.Value).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			return rows.Select(r => new HospitalReturnView(r, r.ByEmployeeId is int b ? names.GetValueOrDefault(b) : null)).ToList();
		}

		public async Task<HospitalResult> ReturnAsync(BusinessContext ctx, int dispenseId, int lineId, decimal quantity, string? reason, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			quantity = Math.Round(quantity, 4);
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == dispenseId && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (d.Status is not (HospitalDispenseStatus.Dispensed or HospitalDispenseStatus.Partial)) return HospitalResult.Fail("Only dispensed medicine can come back");
			var l = await _db.HospitalDispenseLines.AsNoTracking().FirstOrDefaultAsync(x => x.ID == lineId && x.DispenseId == d.ID && x.CompanyId == ctx.CompanyId, ct);
			if (l?.ItemId is not int itemId) return HospitalResult.Fail("not_found");
			var returned = (await ReturnedAsync(ctx, d.ID, ct)).GetValueOrDefault(l.ID);
			var why = HospitalReturnRules.Refuse(quantity, HospitalReturnRules.Returnable(l, returned), reason);
			if (why != null) return HospitalResult.Fail(why, HospitalReturnRules.Returnable(l, returned).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			if (d.BillId is not int billId) return HospitalResult.Fail("not_found");
			var bill = await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId, ct);
			if (bill == null) return HospitalResult.Fail("not_found");
			bool closed = bill.Status != HospitalBillStatus.Open;   // H148: a closed bill gets a credit note instead

			// the bill lines this line was charged on, newest first (a dispense before H145 has its one line)
			var fills = await _db.Set<HospitalDispenseFill>().AsNoTracking().Where(f => f.CompanyId == ctx.CompanyId && f.LineId == l.ID && f.BillLineId != null).Select(f => f.BillLineId!.Value).ToListAsync(ct);
			if (fills.Count == 0 && l.BillLineId is int legacy) fills.Add(legacy);
			var billLines = await _db.HospitalBillLines.Where(b => b.CompanyId == ctx.CompanyId && b.BillId == billId && fills.Contains(b.ID)).ToListAsync(ct);
			// a closed bill's lines stay as posted: what each can still credit is its quantity less earlier credit notes
			var creditedQty = closed ? await _db.Set<HospitalCreditNoteLine>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && fills.Contains(c.BillLineId))
				.GroupBy(c => c.BillLineId).Select(g => new { g.Key, Q = g.Sum(c => c.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct) : new Dictionary<int, decimal>();
			var credit = HospitalReturnRules.Newest(billLines.Select(b => (b.ID, b.Quantity - creditedQty.GetValueOrDefault(b.ID))), quantity);
			if (credit == null) return HospitalResult.Fail("The bill no longer carries what was dispensed: settle it at billing");
			HospitalBillingSettings? books = null;
			if (closed)
			{
				var view = (await _billing.BillAsync(ctx, billId, nowUtc, ct))!;
				if (view.Foreign) return HospitalResult.Fail("The bill was closed in a foreign currency: settle this return at billing");
				var creditIds = credit.Select(c => c.Id).ToList();
				bool insured = await (from cl in _db.Set<HospitalBillCoverageLine>().AsNoTracking()
									  join cv in _db.Set<HospitalBillCoverage>().AsNoTracking() on cl.CoverageId equals cv.ID
									  where cv.CompanyId == ctx.CompanyId && cv.BillId == billId && creditIds.Contains(cl.BillLineId) && cl.InsurerAmount > 0
									  select cl.ID).AnyAsync(ct);
				if (insured) return HospitalResult.Fail("The insurer paid part of this medicine: correct it through the insurance claim");
				books = await _billing.SettingsAsync(ctx, ct);
				bool taxed = credit.Any(c => billLines.First(b => b.ID == c.Id).Taxable);
				if (books.ReceivableAccountId == null || books.RevenueAccountId == null) return HospitalResult.Fail("Map the receivable and revenue accounts in billing settings");
				if (taxed && books.VatAccountId == null) return HospitalResult.Fail("Map the output VAT account in billing settings");
			}

			// the stock: the line's outgoing movements, less what earlier returns brought back, newest first
			var outs = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == HospitalPharmacyService.MovementSource && m.SourceId == d.ID && m.SourceLineId == l.ID && m.Direction == -1).ToListAsync(ct);
			var outIds = outs.Select(m => m.ID).ToList();
			var back = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == HospitalReturnRules.ReturnSource && m.SourceId == d.ID && m.Direction == 1 && m.SourceLineId != null && outIds.Contains(m.SourceLineId.Value))
				.GroupBy(m => m.SourceLineId!.Value).Select(g => new { g.Key, Q = g.Sum(m => m.QtyBase) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
			var stock = HospitalReturnRules.Newest(outs.Select(m => (m.ID, m.QtyBase - back.GetValueOrDefault(m.ID))), quantity);
			if (stock == null) return HospitalResult.Fail("The store's record of this dispense is short: nothing returned");
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId, ct);
			var cogs = await _db.ItemCategories.AsNoTracking().Where(c => c.ID == item.ItemCategoryId).Select(c => c.CogsAccountId).FirstOrDefaultAsync(ct);
			var before = (await _billing.BillAsync(ctx, billId, nowUtc, ct))!;

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				foreach (var (moveId, take) in stock)
				{
					var m = outs.First(x => x.ID == moveId);
					var b = m.BatchId is int bid ? await _db.StockBatches.AsNoTracking().FirstOrDefaultAsync(x => x.ID == bid, ct) : null;
					var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
					{
						Date = nowUtc, ItemId = itemId, WarehouseId = m.WarehouseId, Direction = 1, Qty = take, UnitCostInBase = m.UnitCost,
						BatchNo = b?.BatchNo, Expiry = b?.ExpiryDate, AllowExpired = true,
						SourceType = HospitalReturnRules.ReturnSource, SourceId = d.ID, SourceLineId = m.ID, PostToGl = true, CounterAccountOverride = cogs,
						Notes = "Medicine returned " + d.RxNo,
					}, userId);
					if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("{0}: {1}", DisplayName.Of(item.Name, item.NameEn ?? item.Name), error ?? ""); }
				}
				string Next(IEnumerable<string> issued, string prefix) { int max = 0; foreach (var n in issued) if (n.StartsWith(prefix) && int.TryParse(n.AsSpan(prefix.Length), out var v) && v > max) max = v; return prefix + (max + 1).ToString("000000"); }
				var said = reason!.Trim()[..Math.Min(300, reason.Trim().Length)];
				decimal credited = 0;
				if (!closed)
					foreach (var (bl, take) in credit)
					{
						var line = billLines.First(x => x.ID == bl);
						credited += Math.Round(take * line.UnitPrice, 4);
						line.Quantity -= take;
						if (line.Quantity <= 0) _db.HospitalBillLines.Remove(line);
					}
				var row = new HospitalDispenseReturn { CompanyId = ctx.CompanyId, DispenseId = d.ID, LineId = l.ID, Quantity = quantity, Reason = said, Credited = credited, At = nowUtc, ByEmployeeId = employeeId };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);

				if (closed)
				{
					// H148: the closed bill stays as posted; a credit note reverses the returned lines' revenue and VAT
					var noteLines = credit.Select(c =>
					{
						var bl = billLines.First(x => x.ID == c.Id);
						var (net, vat) = HospitalReturnRules.Money(c.Take, bl.UnitPrice, bl.Taxable, bl.VatRatePercent);
						return new HospitalCreditNoteLine { CompanyId = ctx.CompanyId, BillLineId = bl.ID, Quantity = c.Take, UnitPrice = bl.UnitPrice, Net = net, Vat = vat, RevenueAccountId = bl.RevenueAccountId ?? books!.RevenueAccountId!.Value };
					}).ToList();
					var note = new HospitalCreditNote
					{
						CompanyId = ctx.CompanyId, BillId = billId, PatientId = d.PatientId, SourceType = HospitalCreditNoteSource.DispenseReturn, SourceId = row.ID,
						Net = noteLines.Sum(x => x.Net), Vat = noteLines.Sum(x => x.Vat), Reason = said, At = nowUtc, ByEmployeeId = employeeId,
						NoteNo = Next(await _db.Set<HospitalCreditNote>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.NoteNo).ToListAsync(ct), "CN-"),
					};
					note.Total = note.Net + note.Vat;
					if (note.Total <= 0) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The bill no longer carries what was dispensed: settle it at billing"); }
					_db.Add(note);
					await _db.SaveChangesAsync(ct);
					foreach (var nl in noteLines) { nl.CreditNoteId = note.ID; _db.Add(nl); }
					var je = noteLines.GroupBy(x => x.RevenueAccountId).Select(g => new HospitalLedgerLine(g.Key, g.Sum(x => x.Net), 0, note.NoteNo)).Where(x => x.Debit > 0).ToList();
					if (note.Vat > 0) je.Add(new HospitalLedgerLine(books!.VatAccountId!.Value, note.Vat, 0, note.NoteNo));
					je.Add(new HospitalLedgerLine(books!.ReceivableAccountId!.Value, 0, note.Total, note.NoteNo));
					var (jok, jerr, jid) = await _ledger.PostAsync(ctx, nowUtc, "إشعار دائن " + note.NoteNo + " — مرتجع " + d.RxNo + " على الفاتورة " + bill.BillNo,
						"Credit note " + note.NoteNo + " — return " + d.RxNo + " on bill " + bill.BillNo, "HospitalCreditNote", note.ID, je, ct);
					if (!jok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The ledger refused the posting: {0}", jerr ?? ""); }
					note.JournalEntryId = jid; row.Credited = note.Net;
					await _db.SaveChangesAsync(ct);
				}

				// the money: an overpaid excess moves to the patient's deposit balance
				var after = (await _billing.BillAsync(ctx, billId, nowUtc, ct))!;
				decimal excess = Math.Max(0, -after.Totals.Due);
				if (excess > 0)
				{
					var shift = employeeId is int me ? await _db.HospitalCashierShifts.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.CashierEmployeeId == me && s.Status == HospitalShiftStatus.Open, ct) : null;
					var s = await _billing.SettingsAsync(ctx, ct);
					string? stop = shift == null ? "The bill is already paid: the cashier records this return, so the excess goes to the patient's deposit"
						: s.PatientDepositAccountId == null ? "Map the patient deposits account in billing settings"
						: s.ReceivableAccountId == null ? "Map the cash, bank and receivable accounts in billing settings" : null;
					if (stop != null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(stop); }
					var dep = new HospitalPatientDeposit
					{
						CompanyId = ctx.CompanyId, PatientId = d.PatientId, Purpose = HospitalDepositPurpose.General, Note = "Return " + d.RxNo, Amount = excess, Method = HospitalReturnRules.CreditMethod,
						ShiftId = shift!.ID, CashierEmployeeId = employeeId!.Value, ReceivedAt = nowUtc,
						DepositNo = Next(await _db.Set<HospitalPatientDeposit>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.DepositNo).ToListAsync(ct), "DEP-"),
					};
					_db.Add(dep);
					var pay = new HospitalPayment
					{
						CompanyId = ctx.CompanyId, BillId = billId, ShiftId = shift.ID, Amount = -excess, Method = HospitalPaymentMethods.Deposit, ReceivedAt = nowUtc, CashierEmployeeId = employeeId.Value,
						ReceiptNo = Next(await _db.HospitalPayments.Where(p => p.CompanyId == ctx.CompanyId).Select(p => p.ReceiptNo).ToListAsync(ct), "RCPT-"),
					};
					_db.HospitalPayments.Add(pay);
					await _db.SaveChangesAsync(ct);
					var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "مرتجع " + d.RxNo + " — رصيد للمريض " + dep.DepositNo, "Return " + d.RxNo + " — credit to the patient " + dep.DepositNo, "HospitalPatientDeposit", dep.ID,
						new[] { new HospitalLedgerLine(s.ReceivableAccountId!.Value, excess, 0, dep.DepositNo), new HospitalLedgerLine(s.PatientDepositAccountId!.Value, 0, excess, dep.DepositNo) }, ct);
					if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? ""); }
					dep.JournalEntryId = entryId; pay.JournalEntryId = entryId; row.DepositId = dep.ID;
					await _db.SaveChangesAsync(ct);
				}
				await tx.CommitAsync();
				return HospitalResult.Success(row.ID);
			}
			catch
			{
				await tx.RollbackAsync(); _db.ChangeTracker.Clear();
				throw;
			}
		}
	}
}
