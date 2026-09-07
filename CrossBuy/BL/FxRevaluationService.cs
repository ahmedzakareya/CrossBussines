using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class FxRevalLine
	{
		public string PartyType { get; set; } = "";   // AR | AP
		public string Party { get; set; } = "";
		public string CurrencyCode { get; set; } = "";
		public int ControlAccountId { get; set; }
		public decimal ForeignOutstanding { get; set; }
		public decimal BookBase { get; set; }
		public decimal ClosingRate { get; set; }
		public decimal RevaluedBase { get; set; }
		public decimal Diff { get; set; }             // RevaluedBase − BookBase (signed)
	}

	public class FxRevalPreview
	{
		public DateTime AsOfDate { get; set; }
		public string RateType { get; set; } = "Central";
		public List<FxRevalLine> Lines { get; set; } = new();
		public decimal TotalArDiff { get; set; }
		public decimal TotalApDiff { get; set; }
		public decimal TotalBankDiff { get; set; }
		public bool HasAny => Lines.Count > 0;
	}

	public interface IFxRevaluationService
	{
		Task<FxRevalPreview> PreviewAsync(int companyId, DateTime asOf, string rateType);
		Task<(bool ok, string? error, int? runId)> PostAsync(int companyId, DateTime asOf, string rateType, string? userId);
		Task<List<FxRevaluationRun>> HistoryAsync(int companyId);
	}

	/// Multi-Currency 1-6: period-end revaluation of OPEN foreign AR/AP balances at the closing rate.
	/// Posts the unrealized FX (4903 gain / 5903 loss) against the control accounts, then an automatic
	/// reversing entry dated the next day — so the all-time control-account balance nets to zero and the
	/// AR/AP==subledger invariant stays intact, while each period's P&L/balance sheet reflect the revaluation.
	public class FxRevaluationService : IFxRevaluationService
	{
		private readonly CrossDbContext _db;
		private readonly ICurrencyService _currency;
		private readonly IJournalEntryService _journals;
		public FxRevaluationService(CrossDbContext db, ICurrencyService currency, IJournalEntryService journals)
		{ _db = db; _currency = currency; _journals = journals; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		public async Task<FxRevalPreview> PreviewAsync(int companyId, DateTime asOf, string rateType)
		{
			var p = new FxRevalPreview { AsOfDate = asOf.Date, RateType = rateType };
			// culture-aware party name: show English (NameEn) when UI is not Arabic, fall back to Arabic
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var curByCode = await _db.Currencies.AsNoTracking().ToDictionaryAsync(c => c.ID, c => c.Code);
			var rateCache = new Dictionary<int, decimal>();
			async Task<decimal> ClosingRate(int curId)
			{
				if (rateCache.TryGetValue(curId, out var r)) return r;
				var (_, eff) = await _currency.ToBaseAsync(1m, curId, functional, asOf, rateType);
				rateCache[curId] = eff; return eff;
			}

			// ---- AR: open foreign sales invoices (remaining foreign × rate diff) ----
			var arInv = await (from i in _db.SalesInvoices.AsNoTracking()
							   where i.CompanyID == companyId && i.Status == "Posted" && i.CurrencyId != null && i.CurrencyId != functional
							   join c in _db.Customers.AsNoTracking() on i.CustomerId equals c.ID
							   select new { i.ID, i.GrandTotal, i.CurrencyId, i.ExchangeRate, c.ControlAccountId, Party = (isEn && c.NameEn != null && c.NameEn != "") ? c.NameEn : c.Name }).ToListAsync();
			var arSettled = (await _db.ReceiptAllocations.AsNoTracking().Where(a => a.CompanyID == companyId)
							 .GroupBy(a => a.SalesInvoiceId).Select(g => new { Inv = g.Key, F = g.Sum(x => x.ForeignAmount) }).ToListAsync())
							 .ToDictionary(x => x.Inv, x => x.F);
			foreach (var grp in arInv.GroupBy(x => new { x.CurrencyId, x.ControlAccountId, x.Party }))
			{
				decimal outF = 0, bookBase = 0;
				foreach (var i in grp)
				{
					var rem = R(i.GrandTotal - (arSettled.TryGetValue(i.ID, out var s) ? s : 0m));
					if (rem <= 0) continue;
					outF += rem; bookBase += R(rem * ((i.ExchangeRate ?? 1m)));
				}
				if (outF <= 0) continue;
				var cr = await ClosingRate(grp.Key.CurrencyId!.Value);
				var reval = R(outF * cr); var diff = R(reval - bookBase);
				p.Lines.Add(new FxRevalLine { PartyType = "AR", Party = grp.Key.Party, CurrencyCode = curByCode.GetValueOrDefault(grp.Key.CurrencyId!.Value, "?"), ControlAccountId = grp.Key.ControlAccountId, ForeignOutstanding = outF, BookBase = bookBase, ClosingRate = cr, RevaluedBase = reval, Diff = diff });
				p.TotalArDiff += diff;
			}

			// ---- AP: open foreign purchase invoices ----
			var apInv = await (from i in _db.PurchaseInvoices.AsNoTracking()
							   where i.CompanyID == companyId && i.Status == "Posted" && i.CurrencyId != null && i.CurrencyId != functional
							   join v in _db.Vendors.AsNoTracking() on i.VendorId equals v.ID
							   select new { i.ID, i.GrandTotal, i.CurrencyId, i.ExchangeRate, v.ControlAccountId, Party = (isEn && v.NameEn != null && v.NameEn != "") ? v.NameEn : v.Name }).ToListAsync();
			var apSettled = (await _db.PaymentAllocations.AsNoTracking().Where(a => a.CompanyID == companyId)
							 .GroupBy(a => a.PurchaseInvoiceId).Select(g => new { Inv = g.Key, F = g.Sum(x => x.ForeignAmount) }).ToListAsync())
							 .ToDictionary(x => x.Inv, x => x.F);
			foreach (var grp in apInv.GroupBy(x => new { x.CurrencyId, x.ControlAccountId, x.Party }))
			{
				decimal outF = 0, bookBase = 0;
				foreach (var i in grp)
				{
					var rem = R(i.GrandTotal - (apSettled.TryGetValue(i.ID, out var s) ? s : 0m));
					if (rem <= 0) continue;
					outF += rem; bookBase += R(rem * ((i.ExchangeRate ?? 1m)));
				}
				if (outF <= 0) continue;
				var cr = await ClosingRate(grp.Key.CurrencyId!.Value);
				var reval = R(outF * cr); var diff = R(reval - bookBase);
				p.Lines.Add(new FxRevalLine { PartyType = "AP", Party = grp.Key.Party, CurrencyCode = curByCode.GetValueOrDefault(grp.Key.CurrencyId!.Value, "?"), ControlAccountId = grp.Key.ControlAccountId, ForeignOutstanding = outF, BookBase = bookBase, ClosingRate = cr, RevaluedBase = reval, Diff = diff });
				p.TotalApDiff += diff;
			}

			// ---- Bank: foreign cash/bank accounts restated to the closing rate (ForeignBalance × rate vs GL carrying value) ----
			var banks = await _db.BankAccounts.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.IsActive && b.CurrencyId != null && b.CurrencyId != functional && b.ForeignBalance != null && b.ForeignBalance != 0)
				.ToListAsync();
			foreach (var b in banks)
			{
				decimal foreign = b.ForeignBalance!.Value;
				decimal bookBase = R(await _db.JournalEntryLines.AsNoTracking().Where(l => l.AccountId == b.GlAccountId).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0m);
				var cr = await ClosingRate(b.CurrencyId!.Value);
				var reval = R(foreign * cr); var diff = R(reval - bookBase);
				p.Lines.Add(new FxRevalLine { PartyType = "Bank", Party = b.BankName, CurrencyCode = curByCode.GetValueOrDefault(b.CurrencyId!.Value, "?"), ControlAccountId = b.GlAccountId, ForeignOutstanding = foreign, BookBase = bookBase, ClosingRate = cr, RevaluedBase = reval, Diff = diff });
				p.TotalBankDiff += diff;
			}
			return p;
		}

		public async Task<(bool ok, string? error, int? runId)> PostAsync(int companyId, DateTime asOf, string rateType, string? userId)
		{
			var prev = await PreviewAsync(companyId, asOf, rateType);
			if (!prev.HasAny) return (false, "There are no open foreign-currency balances to revalue", null);

			var gain = await AccIdAsync(companyId, "4903");
			var loss = await AccIdAsync(companyId, "5903");
			if (gain == null || loss == null) return (false, "The unrealised exchange-difference accounts (4903/5903) are not configured", null);

			// build the revaluation lines (each control adjustment paired with its FX offset → balanced)
			var lines = new List<JournalLineInput>();
			foreach (var l in prev.Lines)
			{
				if (l.Diff == 0) continue;
				if (l.PartyType != "AP")   /* AR or Bank — an ASSET (Dr asset / Cr 4903 on gain) */
				{
					if (l.Diff > 0) { lines.Add(new() { AccountId = l.ControlAccountId, Debit = l.Diff, Credit = 0, Description = $"Revaluation of receivables for {l.Party}" }); lines.Add(new() { AccountId = gain.Value, Debit = 0, Credit = l.Diff, Description = "Unrealised exchange gain" }); }
					else { lines.Add(new() { AccountId = l.ControlAccountId, Debit = 0, Credit = -l.Diff, Description = $"Revaluation of receivables for {l.Party}" }); lines.Add(new() { AccountId = loss.Value, Debit = -l.Diff, Credit = 0, Description = "Unrealised exchange loss" }); }
				}
				else // AP — liability
				{
					if (l.Diff > 0) { lines.Add(new() { AccountId = l.ControlAccountId, Debit = 0, Credit = l.Diff, Description = $"Revaluation of payables for {l.Party}" }); lines.Add(new() { AccountId = loss.Value, Debit = l.Diff, Credit = 0, Description = "Unrealised exchange loss" }); }
					else { lines.Add(new() { AccountId = l.ControlAccountId, Debit = -l.Diff, Credit = 0, Description = $"Revaluation of payables for {l.Party}" }); lines.Add(new() { AccountId = gain.Value, Debit = 0, Credit = -l.Diff, Description = "Unrealised exchange gain" }); }
				}
			}
			if (lines.Count == 0) return (false, "There is no difference to revalue (the rates match)", null);

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = asOf.Date, JournalType = "Auto", SourceType = "FxRevaluation",
				Description = $"إعادة تقييم عملات أجنبية {asOf:yyyy-MM-dd}", DescriptionEn = $"FX revaluation {asOf:yyyy-MM-dd}", Lines = lines,
			}, userId != null && int.TryParse(userId, out var uid) ? uid : (int?)null);
			if (!ok) return (false, err, null);

			// automatic reversal dated the next day (start of next period)
			var revLines = lines.Select(x => new JournalLineInput { AccountId = x.AccountId, Debit = x.Credit, Credit = x.Debit, CostCenterId = x.CostCenterId, Description = "Revaluation reversal" }).ToList();
			var (rok, rerr, rentry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = asOf.Date.AddDays(1), JournalType = "Auto", SourceType = "FxRevaluationReversal",
				Description = $"عكس إعادة تقييم عملات {asOf:yyyy-MM-dd}", DescriptionEn = $"FX revaluation reversal {asOf:yyyy-MM-dd}", Lines = revLines,
			}, userId != null && int.TryParse(userId, out var uid2) ? uid2 : (int?)null);
			if (!rok)
			{
				// undo the revaluation so nothing is left unbalanced
				await _journals.ReverseAsync(entry!.ID, null, "Could not create the automatic reversing entry");
				return (false, $"Could not post the automatic reversing entry: {rerr}", null);
			}

			var run = new FxRevaluationRun
			{
				CompanyID = companyId, AsOfDate = asOf.Date, RateType = rateType, Status = "Posted",
				TotalArDiff = R(prev.TotalArDiff), TotalApDiff = R(prev.TotalApDiff), TotalBankDiff = R(prev.TotalBankDiff),
				JournalEntryId = entry!.ID, ReversalEntryId = rentry!.ID, CreatedBy = userId, CreatedAt = DateTime.UtcNow,
			};
			_db.FxRevaluationRuns.Add(run);
			await _db.SaveChangesAsync();
			return (true, null, run.ID);
		}

		public Task<List<FxRevaluationRun>> HistoryAsync(int companyId) =>
			_db.FxRevaluationRuns.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.ID).ToListAsync();
	}
}
