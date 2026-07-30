using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class OpeningGlLineInput { public int AccountId { get; set; } public decimal Debit { get; set; } public decimal Credit { get; set; } public int? CostCenterId { get; set; } }
	public class OpeningCheck { public string Name { get; set; } = ""; public string NameEn { get; set; } = ""; public decimal A { get; set; } public decimal B { get; set; } public bool Ok { get; set; } }

	public interface IOpeningBalanceService
	{
		Task<OpeningBalanceControl> GetControlAsync(int companyId);
		Task<List<OpeningBalance>> GetLogAsync(int companyId, string? kind = null);
		Task<(bool ok, string? error, decimal total)> PostStockAsync(int companyId, DateTime cutoff, List<OpeningStockLineInput> lines, string? userId);
		Task<(bool ok, string? error)> PostArAsync(int companyId, DateTime cutoff, int customerId, decimal amount, string? userId);
		Task<(bool ok, string? error)> PostApAsync(int companyId, DateTime cutoff, int vendorId, decimal amount, string? userId);
		Task<(bool ok, string? error)> PostAssetAsync(int companyId, DateTime cutoff, FixedAssetInput input, decimal openingAccumDep, string? userId);
		Task<(bool ok, string? error)> PostGlAsync(int companyId, DateTime cutoff, List<OpeningGlLineInput> lines, string? userId);
		Task<(bool ok, string? error)> FinalizeAsync(int companyId, string? userId);
		Task<List<OpeningCheck>> VerifyAsync(int companyId);
	}

	public class OpeningBalanceService : IOpeningBalanceService
	{
		private readonly CrossDbContext _db;
		private readonly IJournalEntryService _journals;
		private readonly IStockService _stock;
		private readonly IFixedAssetService _assets;
		public OpeningBalanceService(CrossDbContext db, IJournalEntryService journals, IStockService stock, IFixedAssetService assets)
		{ _db = db; _journals = journals; _stock = stock; _assets = assets; }

		private static decimal R(decimal d) => Math.Round(d, 2);

		public async Task<OpeningBalanceControl> GetControlAsync(int companyId)
		{
			var c = await _db.OpeningBalanceControls.FirstOrDefaultAsync(x => x.CompanyID == companyId);
			if (c == null) { c = new OpeningBalanceControl { CompanyID = companyId, Finalized = false }; _db.OpeningBalanceControls.Add(c); await _db.SaveChangesAsync(); }
			return c;
		}

		public Task<List<OpeningBalance>> GetLogAsync(int companyId, string? kind = null) =>
			_db.OpeningBalances.AsNoTracking().Where(o => o.CompanyID == companyId && (kind == null || o.Kind == kind)).OrderByDescending(o => o.ID).ToListAsync();

		private async Task<string?> GuardAsync(int companyId)
		{
			var c = await GetControlAsync(companyId);
			return c.Finalized ? "تم تثبيت الأرصدة الافتتاحية — لا يمكن الإضافة أو التعديل" : null;
		}
		private Task<bool> ExistsAsync(int companyId, string kind, string entityRef) =>
			_db.OpeningBalances.AnyAsync(o => o.CompanyID == companyId && o.Kind == kind && o.EntityRef == entityRef);

		private async Task<int?> AccId(int companyId, string code) =>
			await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		// attach a default cost center to any line whose account requires one
		private async Task FixCcAsync(int companyId, List<JournalLineInput> lines)
		{
			var ids = lines.Select(l => l.AccountId).Distinct().ToList();
			var req = await _db.Accounts.AsNoTracking().Where(a => ids.Contains(a.ID) && a.RequireCostCenter).Select(a => a.ID).ToListAsync();
			if (req.Count == 0) return;
			var cc = await _db.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();
			foreach (var l in lines) if (req.Contains(l.AccountId) && l.CostCenterId == null) l.CostCenterId = cc;
		}

		private async Task LogAsync(int companyId, string kind, string entityRef, string? desc, decimal amount, int? jeId, DateTime cutoff, string? userId)
		{
			_db.OpeningBalances.Add(new OpeningBalance { CompanyID = companyId, Kind = kind, EntityRef = entityRef, Description = desc, Amount = R(amount), JournalEntryId = jeId, CutoffDate = cutoff, CreatedBy = userId, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
		}

		public async Task<(bool ok, string? error, decimal total)> PostStockAsync(int companyId, DateTime cutoff, List<OpeningStockLineInput> lines, string? userId)
		{
			var g = await GuardAsync(companyId); if (g != null) return (false, g, 0);
			// duplicate guard per (item, warehouse)
			foreach (var l in (lines ?? new()))
				if (await ExistsAsync(companyId, "Stock", $"item:{l.ItemId}/wh:{l.WarehouseId}"))
					return (false, $"للصنف {l.ItemId} في المخزن {l.WarehouseId} رصيد افتتاحي مُدخل بالفعل", 0);
			var (ok, err, jeId, total) = await _stock.PostOpeningStockAsync(companyId, cutoff, lines!, userId);
			if (!ok) return (false, err, 0);
			foreach (var l in lines!) await LogAsync(companyId, "Stock", $"item:{l.ItemId}/wh:{l.WarehouseId}", "مخزون افتتاحي", l.Qty * l.UnitCost, jeId, cutoff, userId);
			return (true, null, total);
		}

		public async Task<(bool ok, string? error)> PostArAsync(int companyId, DateTime cutoff, int customerId, decimal amount, string? userId)
		{
			var g = await GuardAsync(companyId); if (g != null) return (false, g);
			if (amount <= 0) return (false, "المبلغ يجب أن يكون أكبر من صفر");
			if (await ExistsAsync(companyId, "AR", $"cust:{customerId}")) return (false, "هذا العميل له رصيد افتتاحي مُدخل بالفعل");
			var cust = await _db.Customers.FirstOrDefaultAsync(c => c.ID == customerId && c.CompanyID == companyId);
			if (cust == null) return (false, "العميل غير موجود");
			var obe = await AccId(companyId, "3301"); if (obe == null) return (false, "حساب الرصيد الافتتاحي (3301) غير موجود");

			var inv = new SalesInvoice { CompanyID = companyId, CustomerId = customerId, InvoiceDate = cutoff.Date, SubTotal = R(amount), TaxTotal = 0, GrandTotal = R(amount), Status = "Posted", Notes = "رصيد افتتاحي", CreatedAt = DateTime.UtcNow };
			_db.SalesInvoices.Add(inv); await _db.SaveChangesAsync();
			inv.InvoiceNo = $"OB-AR-{inv.ID:D5}";
			inv.Lines.Add(new SalesInvoiceLine { SalesInvoiceId = inv.ID, LineNo = 1, ItemDescription = "رصيد افتتاحي", Qty = 1, UnitPrice = R(amount), RevenueAccountId = obe.Value, LineTotal = R(amount) });
			await _db.SaveChangesAsync();

			var lines = new List<JournalLineInput>
			{
				new() { AccountId = cust.ControlAccountId, Debit = R(amount), Credit = 0, Description = $"رصيد افتتاحي عميل {cust.Name}" },
				new() { AccountId = obe.Value, Debit = 0, Credit = R(amount), Description = "رصيد افتتاحي - عملاء" },
			};
			await FixCcAsync(companyId, lines);
			var (ok, err, je) = await _journals.CreateAndPostAsync(new JournalEntryInput { CompanyID = companyId, EntryDate = cutoff, JournalType = "Opening", SourceType = "OpeningARBalance", SourceId = inv.ID, Description = $"رصيد افتتاحي عميل {cust.Name}", Lines = lines }, null);
			if (!ok) { _db.SalesInvoices.Remove(inv); await _db.SaveChangesAsync(); return (false, err); }
			inv.JournalEntryId = je!.ID; await _db.SaveChangesAsync();
			await LogAsync(companyId, "AR", $"cust:{customerId}", $"رصيد افتتاحي عميل {cust.Name}", amount, je.ID, cutoff, userId);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> PostApAsync(int companyId, DateTime cutoff, int vendorId, decimal amount, string? userId)
		{
			var g = await GuardAsync(companyId); if (g != null) return (false, g);
			if (amount <= 0) return (false, "المبلغ يجب أن يكون أكبر من صفر");
			if (await ExistsAsync(companyId, "AP", $"vend:{vendorId}")) return (false, "هذا المورد له رصيد افتتاحي مُدخل بالفعل");
			var ven = await _db.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "المورد غير موجود");
			var obe = await AccId(companyId, "3301"); if (obe == null) return (false, "حساب الرصيد الافتتاحي (3301) غير موجود");

			var inv = new PurchaseInvoice { CompanyID = companyId, VendorId = vendorId, InvoiceDate = cutoff.Date, SubTotal = R(amount), TaxTotal = 0, GrandTotal = R(amount), Status = "Posted", Notes = "رصيد افتتاحي", CreatedAt = DateTime.UtcNow };
			_db.PurchaseInvoices.Add(inv); await _db.SaveChangesAsync();
			inv.InvoiceNo = $"OB-AP-{inv.ID:D5}";
			inv.Lines.Add(new PurchaseInvoiceLine { PurchaseInvoiceId = inv.ID, LineNo = 1, ItemDescription = "رصيد افتتاحي", Qty = 1, UnitPrice = R(amount), ExpenseAccountId = obe.Value, LineTotal = R(amount) });
			await _db.SaveChangesAsync();

			var lines = new List<JournalLineInput>
			{
				new() { AccountId = obe.Value, Debit = R(amount), Credit = 0, Description = "رصيد افتتاحي - موردون" },
				new() { AccountId = ven.ControlAccountId, Debit = 0, Credit = R(amount), Description = $"رصيد افتتاحي مورد {ven.Name}" },
			};
			await FixCcAsync(companyId, lines);
			var (ok, err, je) = await _journals.CreateAndPostAsync(new JournalEntryInput { CompanyID = companyId, EntryDate = cutoff, JournalType = "Opening", SourceType = "OpeningAPBalance", SourceId = inv.ID, Description = $"رصيد افتتاحي مورد {ven.Name}", Lines = lines }, null);
			if (!ok) { _db.PurchaseInvoices.Remove(inv); await _db.SaveChangesAsync(); return (false, err); }
			inv.JournalEntryId = je!.ID; await _db.SaveChangesAsync();
			await LogAsync(companyId, "AP", $"vend:{vendorId}", $"رصيد افتتاحي مورد {ven.Name}", amount, je.ID, cutoff, userId);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> PostAssetAsync(int companyId, DateTime cutoff, FixedAssetInput input, decimal openingAccumDep, string? userId)
		{
			var g = await GuardAsync(companyId); if (g != null) return (false, g);
			var (ok, err, asset) = await _assets.CreateOpeningAssetAsync(companyId, input, openingAccumDep, cutoff, null);
			if (!ok) return (false, err);
			await LogAsync(companyId, "Asset", $"asset:{asset!.ID}", $"أصل افتتاحي {asset.Name}", asset.Cost, asset.AcquisitionJournalEntryId, cutoff, userId);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> PostGlAsync(int companyId, DateTime cutoff, List<OpeningGlLineInput> lines, string? userId)
		{
			var g = await GuardAsync(companyId); if (g != null) return (false, g);
			lines = (lines ?? new()).Where(l => l.AccountId > 0 && (l.Debit > 0 || l.Credit > 0)).ToList();
			if (lines.Count == 0) return (false, "أضف سطرًا واحدًا على الأقل");
			var obe = await AccId(companyId, "3301"); if (obe == null) return (false, "حساب الرصيد الافتتاحي (3301) غير موجود");

			// per-account duplicate guard (prevents entering the same GL account twice)
			var codes = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && lines.Select(x => x.AccountId).Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.Code);
			foreach (var l in lines)
			{
				var code = codes.GetValueOrDefault(l.AccountId, l.AccountId.ToString());
				if (await ExistsAsync(companyId, "GL", $"acc:{code}")) return (false, $"الحساب {code} له رصيد افتتاحي مُدخل بالفعل");
			}

			var jlines = lines.Select(l => new JournalLineInput { AccountId = l.AccountId, Debit = R(l.Debit), Credit = R(l.Credit), CostCenterId = l.CostCenterId, Description = "رصيد افتتاحي" }).ToList();
			decimal net = R(jlines.Sum(l => l.Debit) - jlines.Sum(l => l.Credit));   // OBE absorbs the residual
			if (net > 0) jlines.Add(new JournalLineInput { AccountId = obe.Value, Debit = 0, Credit = net, Description = "رصيد افتتاحي - تسوية" });
			else if (net < 0) jlines.Add(new JournalLineInput { AccountId = obe.Value, Debit = -net, Credit = 0, Description = "رصيد افتتاحي - تسوية" });
			await FixCcAsync(companyId, jlines);

			var (ok, err, je) = await _journals.CreateAndPostAsync(new JournalEntryInput { CompanyID = companyId, EntryDate = cutoff, JournalType = "Opening", SourceType = "OpeningGL", SourceId = 0, Description = "ميزان مراجعة افتتاحي", Lines = jlines }, null);
			if (!ok) return (false, err);
			foreach (var l in lines)
			{
				var code = codes.GetValueOrDefault(l.AccountId, l.AccountId.ToString());
				await LogAsync(companyId, "GL", $"acc:{code}", $"رصيد افتتاحي حساب {code}", l.Debit - l.Credit, je!.ID, cutoff, userId);
			}
			return (true, null);
		}

		public async Task<(bool ok, string? error)> FinalizeAsync(int companyId, string? userId)
		{
			var c = await GetControlAsync(companyId);
			if (c.Finalized) return (false, "سبق تثبيت الأرصدة الافتتاحية");
			var checks = await VerifyAsync(companyId);
			if (checks.Any(x => !x.Ok)) return (false, "لا يمكن التثبيت — توجد ثوابت غير متطابقة، راجع تقرير التحقق");
			c.Finalized = true; c.FinalizedAt = DateTime.UtcNow; c.FinalizedBy = userId;
			c.CutoffDate ??= (await _db.OpeningBalances.Where(o => o.CompanyID == companyId).Select(o => (DateTime?)o.CutoffDate).FirstOrDefaultAsync());
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// net of a GL account by code = Σ(Debit - Credit) over posted entry lines
		private async Task<decimal> GlNetAsync(int companyId, string code)
		{
			var accId = await AccId(companyId, code);
			if (accId == null) return 0;
			return await _db.JournalEntryLines.AsNoTracking()
				.Where(l => l.AccountId == accId.Value)
				.SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0m;
		}

		public async Task<List<OpeningCheck>> VerifyAsync(int companyId)
		{
			var res = new List<OpeningCheck>();

			// 1) stock valuation == inventory GL (1103)
			decimal stockVal = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId).SumAsync(b => (decimal?)b.TotalValue) ?? 0m;
			decimal inv1103 = await GlNetAsync(companyId, "1103");
			res.Add(new OpeningCheck { Name = "قيمة المخزون == حساب المخزون 1103", NameEn = "Stock value == 1103", A = R(stockVal), B = R(inv1103), Ok = R(stockVal) == R(inv1103) });

			// 2) AR control (1102) == open customer balances (Σ posted invoices − Σ receipts)
			decimal arCtrl = await GlNetAsync(companyId, "1102");
			decimal arSub = (await _db.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").SumAsync(i => (decimal?)i.GrandTotal) ?? 0m)
						  - (await _db.Receipts.AsNoTracking().Where(r => r.CompanyID == companyId && r.Status == "Posted").SumAsync(r => (decimal?)r.Amount) ?? 0m);
			res.Add(new OpeningCheck { Name = "حساب العملاء 1102 == دفتر العملاء", NameEn = "AR control == subledger", A = R(arCtrl), B = R(arSub), Ok = R(arCtrl) == R(arSub) });

			// 3) AP control (2101) == open vendor balances (credit-normal)
			decimal apCtrl = -await GlNetAsync(companyId, "2101");   // liability normal = Cr − Dr
			decimal apSub = (await _db.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").SumAsync(i => (decimal?)i.GrandTotal) ?? 0m)
						  - (await _db.Payments.AsNoTracking().Where(p => p.CompanyID == companyId && p.Status == "Posted").SumAsync(p => (decimal?)p.Amount) ?? 0m);
			res.Add(new OpeningCheck { Name = "حساب الموردين 2101 == دفتر الموردين", NameEn = "AP control == subledger", A = R(apCtrl), B = R(apSub), Ok = R(apCtrl) == R(apSub) });

			// 4) trial balance balanced (ΣDr == ΣCr over all posted lines)
			decimal td = await _db.JournalEntryLines.AsNoTracking().SumAsync(l => (decimal?)l.Debit) ?? 0m;
			decimal tc = await _db.JournalEntryLines.AsNoTracking().SumAsync(l => (decimal?)l.Credit) ?? 0m;
			res.Add(new OpeningCheck { Name = "ميزان المراجعة متوازن (Σمدين == Σدائن)", NameEn = "Trial balance balanced", A = R(td), B = R(tc), Ok = R(td) == R(tc) });

			// 5) FIFO layers == on-hand qty (FIFO items)
			decimal layerQty = await _db.StockCostLayers.AsNoTracking().Where(l => l.CompanyID == companyId).SumAsync(l => (decimal?)l.QtyRemaining) ?? 0m;
			decimal fifoBalQty = await (from b in _db.StockBalances.AsNoTracking()
										join it in _db.Items.AsNoTracking() on b.ItemId equals it.ID
										where b.CompanyID == companyId && it.CostingMethod == "FIFO"
										select (decimal?)b.QtyOnHand).SumAsync() ?? 0m;
			res.Add(new OpeningCheck { Name = "طبقات FIFO == رصيد أصناف FIFO", NameEn = "FIFO layers == FIFO on-hand", A = R(layerQty), B = R(fifoBalQty), Ok = R(layerQty) == R(fifoBalQty) });

			// 6) opening balance equity nets to zero (clearing fully cleared)
			decimal obe = await GlNetAsync(companyId, "3301");
            res.Add(new OpeningCheck { Name = "حساب الرصيد الافتتاحي = صفر", NameEn = "Opening Balance Equity == 0", A = R(obe), B = 0m, Ok = R(obe) == 0m });

			return res;
		}
	}
}
