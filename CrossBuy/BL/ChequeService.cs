using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// <summary>
	/// Cheques under collection — "شيك" in the owner's mobile prototype, and the web register behind it.
	///
	/// RECEIVE: a cheque is a receipt voucher like any other (ReceivableService.CreateReceiptAsync, unchanged — it
	/// allocates to the oldest open invoices and posts the entry), whose debit account is 1106 «شيكات تحت التحصيل»
	/// instead of a cash or bank account, with the cheque's facts kept beside it (ReceiptCheques). Both land in ONE
	/// transaction.
	///
	/// CLEAR: the bank honoured it — Dr the bank account / Cr 1106, posted through JournalEntryService.
	///
	/// BOUNCE: it came back unpaid — the receipt's entry is REVERSED (JournalEntryService.ReverseAsync: Dr AR / Cr 1106,
	/// so the debt returns to the customer), the receipt is marked Reversed exactly as the POS void marks one, and its
	/// invoice allocations get equal and opposite rows so the invoices it had settled are open again. A cheque already
	/// cleared has its clearing entry reversed first. Nothing is deleted.
	///
	/// GL is only ever written through JournalEntryService (the receivables writer posts the receipt itself).
	/// </summary>
	public sealed class ChequeService
	{
		private readonly CrossDbContext _db;
		private readonly IReceivableService _ar;
		private readonly IJournalEntryService _journals;
		private readonly ICurrencyService _currency;

		public ChequeService(CrossDbContext db, IReceivableService ar, IJournalEntryService journals, ICurrencyService currency)
		{ _db = db; _ar = ar; _journals = journals; _currency = currency; }

		public const string HoldingAccountCode = "1106";

		public Task<int?> HoldingAccountIdAsync(int companyId, CancellationToken ct = default) =>
			_db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == HoldingAccountCode && a.IsPostable && a.IsActive)
				.Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);

		public async Task<(bool ok, string? error, Receipt? receipt)> ReceiveAsync(int companyId, int customerId, DateTime date, decimal amount,
			string chequeNo, DateTime dueDate, string? bank, string? notes, int? userId, string? user, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(chequeNo)) return (false, "Enter the cheque number", null);
			if (amount <= 0) return (false, "The amount must be greater than zero", null);
			var holding = await HoldingAccountIdAsync(companyId, ct);
			if (holding == null) return (false, "The cheques-under-collection account (1106) is missing from the chart of accounts", null);
			var no = chequeNo.Trim();
			if (await _db.ReceiptCheques.AsNoTracking().AnyAsync(c => c.CompanyID == companyId && c.ChequeNo == no && c.Bank == bank && c.Status != ReceiptCheque.Bounced, ct))
				return (false, "This cheque is already recorded", null);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var (ok, err) = await _ar.CreateReceiptAsync(companyId, customerId, date, amount, "Cheque", holding.Value,
				string.IsNullOrWhiteSpace(notes) ? $"Cheque {no}" : notes.Trim(), userId);
			if (!ok) return (false, err, null);
			// the receipt the call above just added — tracked by this same scoped context, so no lookup by guesswork
			var rc = _db.ChangeTracker.Entries<Receipt>().Select(e => e.Entity)
				.Where(r => r.CompanyID == companyId && r.CustomerId == customerId && r.Method == "Cheque" && r.CashAccountId == holding.Value)
				.OrderByDescending(r => r.ID).FirstOrDefault();
			if (rc == null) return (false, "The receipt voucher could not be read back", null);
			_db.ReceiptCheques.Add(new ReceiptCheque
			{
				CompanyID = companyId, ReceiptID = rc.ID, ChequeNo = no, DueDate = dueDate.Date,
				Bank = string.IsNullOrWhiteSpace(bank) ? null : bank.Trim(), CreatedBy = user,
			});
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return (true, null, rc);
		}

		public async Task<(bool ok, string? error)> ClearAsync(int companyId, int chequeId, int bankAccountId, DateTime date, int? userId, CancellationToken ct = default)
		{
			var c = await _db.ReceiptCheques.FirstOrDefaultAsync(x => x.ID == chequeId && x.CompanyID == companyId, ct);
			if (c == null) return (false, "Cheque not found");
			if (c.Status != ReceiptCheque.InHand) return (false, "Only a cheque in hand can be cleared");
			var rc = await _db.Receipts.FirstOrDefaultAsync(r => r.ID == c.ReceiptID && r.CompanyID == companyId, ct);
			if (rc == null || rc.Status != "Posted") return (false, "The cheque's receipt voucher is not posted");
			var bank = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == bankAccountId && a.CompanyID == companyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"), ct);
			if (bank == null) return (false, "Choose a cash or bank account");
			var holding = await HoldingAccountIdAsync(companyId, ct);
			if (holding == null) return (false, "The cheques-under-collection account (1106) is missing from the chart of accounts");
			var amount = rc.AmountBase ?? rc.Amount;   // the holding account was debited in the functional currency

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var (ok, err, je) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto",
				Description = $"تحصيل الشيك {c.ChequeNo} — سند {rc.ReceiptNo}", DescriptionEn = $"Cheque {c.ChequeNo} cleared — receipt {rc.ReceiptNo}",
				CurrencyId = await _currency.GetFunctionalCurrencyIdAsync(companyId, null), SourceType = "ChequeClearing", SourceId = c.ID,
				Lines = new()
				{
					new JournalLineInput { AccountId = bank.ID, Debit = amount, Description = $"شيك {c.ChequeNo}", DescriptionEn = $"Cheque {c.ChequeNo}" },
					new JournalLineInput { AccountId = holding.Value, Credit = amount, Description = $"شيك {c.ChequeNo}", DescriptionEn = $"Cheque {c.ChequeNo}" },
				},
			}, userId);
			if (!ok || je == null) return (false, err);
			c.Status = ReceiptCheque.Cleared; c.ClearedAt = DateTime.UtcNow; c.ClearedToAccountID = bank.ID; c.ClearingEntryID = je.ID;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> BounceAsync(int companyId, int chequeId, string reason, int? userId, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(reason)) return (false, "Say why the cheque bounced");
			var c = await _db.ReceiptCheques.FirstOrDefaultAsync(x => x.ID == chequeId && x.CompanyID == companyId, ct);
			if (c == null) return (false, "Cheque not found");
			if (c.Status == ReceiptCheque.Bounced) return (false, "The cheque has already bounced");
			var rc = await _db.Receipts.FirstOrDefaultAsync(r => r.ID == c.ReceiptID && r.CompanyID == companyId, ct);
			if (rc == null) return (false, "The cheque's receipt voucher was not found");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			if (c.Status == ReceiptCheque.Cleared && c.ClearingEntryID != null)
			{
				var (cok, cerr, _) = await _journals.ReverseAsync(c.ClearingEntryID.Value, userId, $"Cheque {c.ChequeNo} returned by the bank");
				if (!cok) return (false, cerr);
			}
			if (rc.Status == "Posted" && rc.JournalEntryId != null)
			{
				var (rok, rerr, _) = await _journals.ReverseAsync(rc.JournalEntryId.Value, userId, $"Cheque {c.ChequeNo} bounced — receipt {rc.ReceiptNo}");
				if (!rok) return (false, rerr);
			}
			rc.Status = "Reversed";
			// the invoices this receipt had settled are open again: equal and opposite allocation rows, nothing deleted
			var allocs = await _db.ReceiptAllocations.AsNoTracking().Where(a => a.CompanyID == companyId && a.ReceiptId == rc.ID).ToListAsync(ct);
			foreach (var a in allocs.GroupBy(a => a.SalesInvoiceId))
			{
				var net = a.Sum(x => x.ForeignAmount);
				if (net == 0) continue;
				var first = a.First();
				_db.ReceiptAllocations.Add(new ReceiptAllocation
				{
					CompanyID = companyId, ReceiptId = rc.ID, SalesInvoiceId = a.Key, ForeignAmount = -net,
					InvoiceRate = first.InvoiceRate, ReceiptRate = first.ReceiptRate, ArBase = -a.Sum(x => x.ArBase), FxDiff = -a.Sum(x => x.FxDiff), CreatedAt = DateTime.UtcNow,
				});
			}
			c.Status = ReceiptCheque.Bounced; c.BouncedAt = DateTime.UtcNow; c.BounceReason = reason.Trim();
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return (true, null);
		}

		public sealed record ChequeRow(int Id, string ChequeNo, string? Bank, DateTime DueDate, string Status, decimal Amount, string Customer,
			string? ReceiptNo, DateTime ReceiptDate, string? ClearedTo, string? BounceReason);

		public async Task<(List<ChequeRow> rows, int total, decimal inHand)> SearchAsync(int companyId, string? q, string? status, int page, int pageSize, bool ar, CancellationToken ct = default)
		{
			var rows = from c in _db.ReceiptCheques.AsNoTracking()
					   join r in _db.Receipts.AsNoTracking() on c.ReceiptID equals r.ID
					   join cu in _db.Customers.AsNoTracking() on r.CustomerId equals cu.ID
					   where c.CompanyID == companyId
					   select new { c, r, cu };
			if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(x => x.c.Status == status);
			foreach (var t in SearchTerms.Parse(q))
			{
				var term = t;
				rows = rows.Where(x => x.c.ChequeNo.Contains(term) || (x.c.Bank != null && x.c.Bank.Contains(term)) || x.cu.Name.Contains(term)
					|| (x.cu.NameEn != null && x.cu.NameEn.Contains(term)) || (x.r.ReceiptNo != null && x.r.ReceiptNo.Contains(term)));
			}
			var total = await rows.CountAsync(ct);
			var inHand = await _db.ReceiptCheques.AsNoTracking().Where(c => c.CompanyID == companyId && c.Status == ReceiptCheque.InHand)
				.Join(_db.Receipts.AsNoTracking(), c => c.ReceiptID, r => r.ID, (c, r) => r.AmountBase ?? r.Amount).SumAsync(ct);
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var list = await rows.OrderBy(x => x.c.Status == ReceiptCheque.InHand ? 0 : 1).ThenBy(x => x.c.DueDate).ThenByDescending(x => x.c.ID)
				.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
			var accIds = list.Where(x => x.c.ClearedToAccountID != null).Select(x => x.c.ClearedToAccountID!.Value).Distinct().ToList();
			var accs = await _db.Accounts.AsNoTracking().Where(a => accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => ar || string.IsNullOrWhiteSpace(a.NameEn) ? a.Name : a.NameEn, ct);
			return (list.Select(x => new ChequeRow(x.c.ID, x.c.ChequeNo, x.c.Bank, x.c.DueDate, x.c.Status, x.r.Amount,
				ar || string.IsNullOrWhiteSpace(x.cu.NameEn) ? x.cu.Name : x.cu.NameEn!, x.r.ReceiptNo, x.r.ReceiptDate,
				x.c.ClearedToAccountID is int a && accs.TryGetValue(a, out var n) ? n : null, x.c.BounceReason)).ToList(), total, inHand);
		}
	}
}
