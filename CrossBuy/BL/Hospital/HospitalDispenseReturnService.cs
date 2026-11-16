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
	//   2. the bill: the line's pharmacy bill lines (newest fill first) lose the returned quantity; a line at nothing is
	//      removed. Only an OPEN bill — a closed one posted its revenue and needs a credit note;
	//   3. the money: if the patient had paid more than the bill now asks, the excess moves to the patient's deposit
	//      balance — a negative payment of method "Deposit" on the bill and a deposit of method "Credit", posted
	//      receivables against patient deposits — through the returning person's open cashier shift.
	// Refused, with nothing changed: more than the line can still return, no reason, a closed bill, an excess with no open
	// shift or no patient-deposits account.
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
			if (bill.Status != HospitalBillStatus.Open) return HospitalResult.Fail("The bill is closed: a return after closing needs a credit note");

			// the bill lines this line was charged on, newest first (a dispense before H145 has its one line)
			var fills = await _db.Set<HospitalDispenseFill>().AsNoTracking().Where(f => f.CompanyId == ctx.CompanyId && f.LineId == l.ID && f.BillLineId != null).Select(f => f.BillLineId!.Value).ToListAsync(ct);
			if (fills.Count == 0 && l.BillLineId is int legacy) fills.Add(legacy);
			var billLines = await _db.HospitalBillLines.Where(b => b.CompanyId == ctx.CompanyId && b.BillId == billId && fills.Contains(b.ID)).ToListAsync(ct);
			var credit = HospitalReturnRules.Newest(billLines.Select(b => (b.ID, b.Quantity)), quantity);
			if (credit == null) return HospitalResult.Fail("The bill no longer carries what was dispensed: settle it at billing");

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
				decimal credited = 0;
				foreach (var (bl, take) in credit)
				{
					var line = billLines.First(x => x.ID == bl);
					credited += Math.Round(take * line.UnitPrice, 4);
					line.Quantity -= take;
					if (line.Quantity <= 0) _db.HospitalBillLines.Remove(line);
				}
				var row = new HospitalDispenseReturn { CompanyId = ctx.CompanyId, DispenseId = d.ID, LineId = l.ID, Quantity = quantity, Reason = reason!.Trim()[..Math.Min(300, reason.Trim().Length)], Credited = credited, At = nowUtc, ByEmployeeId = employeeId };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);

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
					string Next(IEnumerable<string> issued, string prefix) { int max = 0; foreach (var n in issued) if (n.StartsWith(prefix) && int.TryParse(n.AsSpan(prefix.Length), out var v) && v > max) max = v; return prefix + (max + 1).ToString("000000"); }
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
