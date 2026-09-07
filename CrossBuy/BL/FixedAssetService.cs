using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class FixedAssetInput
	{
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int? CategoryId { get; set; }
		public DateTime AcquisitionDate { get; set; }
		public decimal Cost { get; set; }
		public decimal SalvageValue { get; set; }
		public int UsefulLifeMonths { get; set; } = 60;
		public int? CostCenterId { get; set; }
		public int FundingAccountId { get; set; }   // cash/bank/payable the asset was bought against
		public int? CostAccountId { get; set; }
		public int? AccumDepAccountId { get; set; }
		public int? DepExpenseAccountId { get; set; }
		public string? Notes { get; set; }
	}

	public interface IFixedAssetService
	{
		Task<List<AssetCategory>> GetCategoriesAsync(int companyId);
		Task<(bool ok, string? error, AssetCategory? cat)> CreateCategoryAsync(int companyId, string name, string? nameEn, int life, int costAcc, int accumAcc, int expAcc);
		Task<List<FixedAsset>> GetAssetsAsync(int companyId);
		Task<FixedAsset?> GetAssetAsync(int companyId, int id);
		Task<List<DepreciationLine>> GetAssetHistoryAsync(int assetId);
		Task<(bool ok, string? error, FixedAsset? asset)> CreateAssetAsync(int companyId, FixedAssetInput input, int? userId);
		Task<(bool ok, string? error, FixedAsset? asset)> CreateOpeningAssetAsync(int companyId, FixedAssetInput input, decimal openingAccumDep, DateTime cutoff, int? userId);
		Task<List<DepreciationRun>> GetRunsAsync(int companyId);
		Task<DepreciationRun?> GetRunAsync(int companyId, int id);
		Task<(bool ok, string? error, DepreciationRun? run)> RunDepreciationAsync(int companyId, DateTime periodDate, int? userId);
		Task<(bool ok, string? error)> DisposeAssetAsync(int companyId, int assetId, DateTime date, decimal proceeds, int cashAccountId, int? userId);
		decimal MonthlyDepreciation(FixedAsset a);
	}

	public class FixedAssetService : IFixedAssetService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		public FixedAssetService(CrossDbContext context, IJournalEntryService journals) { _context = context; _journals = journals; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private static DateTime MonthEnd(DateTime d) => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _context.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		// create the account if missing (used for gain/loss-on-disposal accounts)
		private async Task<int> EnsureAccAsync(int companyId, string code, string ar, string en, string typeCode, string parentCode, string? cashFlow)
		{
			var existing = await AccIdAsync(companyId, code);
			if (existing != null) return existing.Value;
			var typeId = await _context.AccountTypes.Where(t => t.Code == typeCode).Select(t => t.ID).FirstOrDefaultAsync();
			var parentId = await AccIdAsync(companyId, parentCode);
			var acc = new Account
			{
				CompanyID = companyId, Code = code, Name = ar, NameEn = en,
				AccountTypeId = typeId, ParentId = parentId, IsPostable = true, IsActive = true,
				CashFlowCategory = cashFlow, CreatedAt = DateTime.UtcNow,
			};
			_context.Accounts.Add(acc);
			await _context.SaveChangesAsync();
			return acc.ID;
		}

		public decimal MonthlyDepreciation(FixedAsset a)
		{
			if (a.UsefulLifeMonths <= 0) return 0;
			return R((a.Cost - a.SalvageValue) / a.UsefulLifeMonths);
		}

		public async Task<List<AssetCategory>> GetCategoriesAsync(int companyId) =>
			await _context.AssetCategories.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Name).ToListAsync();

		public async Task<(bool ok, string? error, AssetCategory? cat)> CreateCategoryAsync(int companyId, string name, string? nameEn, int life, int costAcc, int accumAcc, int expAcc)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "اسم الفئة مطلوب", null);
			var c = new AssetCategory
			{
				CompanyID = companyId, Name = name.Trim(), NameEn = nameEn, DefaultUsefulLifeMonths = life <= 0 ? 60 : life,
				CostAccountId = costAcc, AccumDepAccountId = accumAcc, DepExpenseAccountId = expAcc, IsActive = true, CreatedAt = DateTime.UtcNow,
			};
			_context.AssetCategories.Add(c);
			await _context.SaveChangesAsync();
			return (true, null, c);
		}

		public async Task<List<FixedAsset>> GetAssetsAsync(int companyId) =>
			await _context.FixedAssets.AsNoTracking().Where(a => a.CompanyID == companyId).OrderByDescending(a => a.ID).ToListAsync();

		public async Task<FixedAsset?> GetAssetAsync(int companyId, int id) =>
			await _context.FixedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);

		public async Task<List<DepreciationLine>> GetAssetHistoryAsync(int assetId) =>
			await _context.DepreciationLines.AsNoTracking()
				.Join(_context.DepreciationRuns, l => l.DepreciationRunId, r => r.ID, (l, r) => new { l, r })
				.Where(x => x.l.FixedAssetId == assetId)
				.OrderBy(x => x.r.PeriodDate)
				.Select(x => x.l)
				.ToListAsync();

		public async Task<(bool ok, string? error, FixedAsset? asset)> CreateAssetAsync(int companyId, FixedAssetInput input, int? userId)
		{
			if (string.IsNullOrWhiteSpace(input.Name)) return (false, "Asset name is required", null);
			if (input.Cost <= 0) return (false, "The cost must be greater than zero", null);
			if (input.SalvageValue < 0 || input.SalvageValue >= input.Cost) return (false, "The salvage value must be between zero and less than the cost", null);
			if (input.UsefulLifeMonths <= 0) return (false, "The useful life must be greater than zero", null);

			AssetCategory? cat = input.CategoryId.HasValue
				? await _context.AssetCategories.FirstOrDefaultAsync(c => c.ID == input.CategoryId && c.CompanyID == companyId) : null;

			var costAcc = input.CostAccountId ?? cat?.CostAccountId ?? await AccIdAsync(companyId, "1201") ?? 0;
			var accumAcc = input.AccumDepAccountId ?? cat?.AccumDepAccountId ?? await AccIdAsync(companyId, "1202") ?? 0;
			var expAcc = input.DepExpenseAccountId ?? cat?.DepExpenseAccountId ?? await AccIdAsync(companyId, "520103") ?? 0;
			if (costAcc == 0 || accumAcc == 0 || expAcc == 0) return (false, "The fixed-asset accounts are not configured in the chart of accounts", null);
			if (input.FundingAccountId <= 0) return (false, "Choose the funding account (cash/bank/supplier)", null);

			var asset = new FixedAsset
			{
				CompanyID = companyId, Name = input.Name.Trim(), NameEn = input.NameEn, CategoryId = input.CategoryId,
				AcquisitionDate = input.AcquisitionDate.Date, Cost = R(input.Cost), SalvageValue = R(input.SalvageValue),
				UsefulLifeMonths = input.UsefulLifeMonths, DepreciationMethod = "StraightLine",
				CostAccountId = costAcc, AccumDepAccountId = accumAcc, DepExpenseAccountId = expAcc,
				CostCenterId = input.CostCenterId, AccumulatedDepreciation = 0, Status = "Active",
				Notes = input.Notes, CreatedAt = DateTime.UtcNow,
			};
			_context.FixedAssets.Add(asset);
			await _context.SaveChangesAsync();
			asset.AssetNo = $"FA-{asset.AcquisitionDate:yyyy}-{asset.ID:D4}";
			await _context.SaveChangesAsync();

			// acquisition journal: Dr asset cost / Cr funding (cash/bank/payable)
			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = asset.AcquisitionDate, JournalType = "Auto", SourceType = "FixedAsset", SourceId = asset.ID,
				Description = $"اقتناء أصل ثابت {asset.AssetNo} - {asset.Name}", DescriptionEn = $"Acquisition {asset.AssetNo}",
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = costAcc, Debit = asset.Cost, Credit = 0, CostCenterId = asset.CostCenterId, Description = "تكلفة الأصل" },
					new() { AccountId = input.FundingAccountId, Debit = 0, Credit = asset.Cost, Description = "Payment for the asset" },
				},
			}, userId);
			if (!ok)
			{
				_context.FixedAssets.Remove(asset);
				await _context.SaveChangesAsync();
				return (false, err, null);
			}
			asset.AcquisitionJournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			return (true, null, asset);
		}

		// Opening fixed asset (Go-Live): records the asset with its pre-existing accumulated depreciation
		// up to the cutoff, so future RunDepreciation continues on the REMAINING life (not from zero).
		// Entry: Dr Asset Cost (1201) / Cr Accumulated Depreciation (1202) [= accum] / Cr Opening Balance Equity (3301) [= NBV].
		public async Task<(bool ok, string? error, FixedAsset? asset)> CreateOpeningAssetAsync(int companyId, FixedAssetInput input, decimal openingAccumDep, DateTime cutoff, int? userId)
		{
			if (string.IsNullOrWhiteSpace(input.Name)) return (false, "Asset name is required", null);
			if (input.Cost <= 0) return (false, "The cost must be greater than zero", null);
			if (input.SalvageValue < 0 || input.SalvageValue >= input.Cost) return (false, "The salvage value must be between zero and less than the cost", null);
			if (input.UsefulLifeMonths <= 0) return (false, "The useful life must be greater than zero", null);
			if (openingAccumDep < 0 || openingAccumDep > R(input.Cost - input.SalvageValue)) return (false, "The opening accumulated depreciation must be between zero and (cost - salvage)", null);

			AssetCategory? cat = input.CategoryId.HasValue
				? await _context.AssetCategories.FirstOrDefaultAsync(c => c.ID == input.CategoryId && c.CompanyID == companyId) : null;
			var costAcc = input.CostAccountId ?? cat?.CostAccountId ?? await AccIdAsync(companyId, "1201") ?? 0;
			var accumAcc = input.AccumDepAccountId ?? cat?.AccumDepAccountId ?? await AccIdAsync(companyId, "1202") ?? 0;
			var expAcc = input.DepExpenseAccountId ?? cat?.DepExpenseAccountId ?? await AccIdAsync(companyId, "520103") ?? 0;
			if (costAcc == 0 || accumAcc == 0 || expAcc == 0) return (false, "The fixed-asset accounts are not configured", null);
			var obe = await AccIdAsync(companyId, "3301");
			if (obe == null) return (false, "The opening-balance account (3301) does not exist", null);
			var cc = input.CostCenterId ?? await _context.CostCenters.Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();

			decimal cost = R(input.Cost), accum = R(openingAccumDep), nbv = R(cost - accum);
			var asset = new FixedAsset
			{
				CompanyID = companyId, Name = input.Name.Trim(), NameEn = input.NameEn, CategoryId = input.CategoryId,
				AcquisitionDate = input.AcquisitionDate.Date, Cost = cost, SalvageValue = R(input.SalvageValue),
				UsefulLifeMonths = input.UsefulLifeMonths, DepreciationMethod = "StraightLine",
				CostAccountId = costAcc, AccumDepAccountId = accumAcc, DepExpenseAccountId = expAcc,
				CostCenterId = cc, AccumulatedDepreciation = accum, LastDepreciationDate = MonthEnd(cutoff),
				Status = nbv <= R(input.SalvageValue) ? "FullyDepreciated" : "Active",
				Notes = input.Notes, CreatedAt = DateTime.UtcNow,
			};
			_context.FixedAssets.Add(asset);
			await _context.SaveChangesAsync();
			asset.AssetNo = $"FA-{asset.AcquisitionDate:yyyy}-{asset.ID:D4}";
			await _context.SaveChangesAsync();

			var lines = new List<JournalLineInput> { new() { AccountId = costAcc, Debit = cost, Credit = 0, CostCenterId = cc, Description = "Opening asset cost" } };
			if (accum > 0) lines.Add(new() { AccountId = accumAcc, Debit = 0, Credit = accum, CostCenterId = cc, Description = "Opening accumulated depreciation" });
			if (nbv > 0) lines.Add(new() { AccountId = obe.Value, Debit = 0, Credit = nbv, Description = "رصيد افتتاحي - أصل" });

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{ CompanyID = companyId, EntryDate = cutoff, JournalType = "Opening", SourceType = "OpeningAsset", SourceId = asset.ID, Description = $"أصل ثابت افتتاحي {asset.AssetNo}", DescriptionEn = $"Opening asset {asset.AssetNo}", Lines = lines }, userId);
			if (!ok) { _context.FixedAssets.Remove(asset); await _context.SaveChangesAsync(); return (false, err, null); }
			asset.AcquisitionJournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			return (true, null, asset);
		}

		public async Task<List<DepreciationRun>> GetRunsAsync(int companyId) =>
			await _context.DepreciationRuns.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.PeriodDate).ToListAsync();

		public async Task<DepreciationRun?> GetRunAsync(int companyId, int id) =>
			await _context.DepreciationRuns.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId);

		public async Task<(bool ok, string? error, DepreciationRun? run)> RunDepreciationAsync(int companyId, DateTime periodDate, int? userId)
		{
			var period = MonthEnd(periodDate);
			var exists = await _context.DepreciationRuns.AnyAsync(r => r.CompanyID == companyId && r.PeriodDate == period && r.Status == "Posted");
			if (exists) return (false, $"Depreciation for this period ({period:yyyy/MM}) has already been calculated", null);

			var assets = await _context.FixedAssets
				.Where(a => a.CompanyID == companyId && a.Status == "Active" && a.AcquisitionDate <= period)
				.ToListAsync();
			if (assets.Count == 0) return (false, "There are no depreciable assets in this period", null);

			int? fallbackCc = await _context.CostCenters.Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();

			var run = new DepreciationRun { CompanyID = companyId, PeriodDate = period, RunDate = DateTime.UtcNow.Date, Status = "Posted", CreatedAt = DateTime.UtcNow };
			var jlines = new List<JournalLineInput>();
			decimal total = 0; int count = 0;

			foreach (var a in assets)
			{
				// skip if already depreciated for this month
				if (a.LastDepreciationDate.HasValue && MonthEnd(a.LastDepreciationDate.Value) >= period) continue;
				var depreciable = a.Cost - a.SalvageValue;
				var remaining = R(depreciable - a.AccumulatedDepreciation);
				if (remaining <= 0) { a.Status = "FullyDepreciated"; continue; }
				var monthly = MonthlyDepreciation(a);
				var amount = Math.Min(monthly, remaining);
				if (amount <= 0) continue;

				a.AccumulatedDepreciation = R(a.AccumulatedDepreciation + amount);
				a.LastDepreciationDate = period;
				if (a.AccumulatedDepreciation >= depreciable) a.Status = "FullyDepreciated";

				run.Lines.Add(new DepreciationLine { FixedAssetId = a.ID, Amount = amount, AccumulatedAfter = a.AccumulatedDepreciation, NetBookValueAfter = R(a.Cost - a.AccumulatedDepreciation) });
				jlines.Add(new JournalLineInput { AccountId = a.DepExpenseAccountId, Debit = amount, Credit = 0, CostCenterId = a.CostCenterId ?? fallbackCc, Description = $"Depreciation of {a.AssetNo}" });
				jlines.Add(new JournalLineInput { AccountId = a.AccumDepAccountId, Debit = 0, Credit = amount, Description = $"Accumulated depreciation of {a.AssetNo}" });
				total += amount; count++;
			}

			if (count == 0)
			{
				await _context.SaveChangesAsync();  // persist any FullyDepreciated status changes
				return (false, "There are no depreciation amounts to calculate in this period", null);
			}

			run.TotalAmount = R(total); run.AssetCount = count;
			_context.DepreciationRuns.Add(run);
			await _context.SaveChangesAsync();

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = period, JournalType = "Auto", SourceType = "Depreciation", SourceId = run.ID,
				Description = $"إهلاك شهر {period:yyyy/MM}", DescriptionEn = $"Depreciation {period:yyyy/MM}", Lines = jlines,
			}, userId);
			if (!ok)
			{
				// undo accumulations
				_context.DepreciationLines.RemoveRange(run.Lines);
				_context.DepreciationRuns.Remove(run);
				await _context.SaveChangesAsync();
				return (false, err, null);
			}
			run.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			return (true, null, run);
		}

		public async Task<(bool ok, string? error)> DisposeAssetAsync(int companyId, int assetId, DateTime date, decimal proceeds, int cashAccountId, int? userId)
		{
			var a = await _context.FixedAssets.FirstOrDefaultAsync(x => x.ID == assetId && x.CompanyID == companyId);
			if (a == null) return (false, "Asset not found");
			if (a.Status == "Disposed") return (false, "The asset has already been disposed of");
			if (proceeds < 0) return (false, "The sale value cannot be negative");

			var nbv = R(a.Cost - a.AccumulatedDepreciation);
			var jlines = new List<JournalLineInput>();
			// remove accumulated depreciation
			if (a.AccumulatedDepreciation > 0)
				jlines.Add(new JournalLineInput { AccountId = a.AccumDepAccountId, Debit = a.AccumulatedDepreciation, Credit = 0, Description = "Reversal of accumulated depreciation" });
			// proceeds received
			if (proceeds > 0)
				jlines.Add(new JournalLineInput { AccountId = cashAccountId, Debit = R(proceeds), Credit = 0, Description = "Proceeds from the sale of an asset" });
			// remove asset cost
			jlines.Add(new JournalLineInput { AccountId = a.CostAccountId, Debit = 0, Credit = a.Cost, Description = "Derecognition of the asset cost" });

			var diff = R(nbv - proceeds);   // >0 loss, <0 gain
			if (diff > 0)
			{
				var lossAcc = await EnsureAccAsync(companyId, "5901", "خسائر بيع أصول ثابتة", "Loss on Asset Disposal", "EXP", "5", "Investing");
				jlines.Add(new JournalLineInput { AccountId = lossAcc, Debit = diff, Credit = 0, Description = "Loss on the sale of an asset" });
			}
			else if (diff < 0)
			{
				var gainAcc = await EnsureAccAsync(companyId, "4901", "أرباح بيع أصول ثابتة", "Gain on Asset Disposal", "REV", "4", "Investing");
				jlines.Add(new JournalLineInput { AccountId = gainAcc, Debit = 0, Credit = -diff, Description = "Gain on the sale of an asset" });
			}

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = "FixedAssetDisposal", SourceId = a.ID,
				Description = $"استبعاد أصل {a.AssetNo} - {a.Name}", DescriptionEn = $"Disposal {a.AssetNo}", Lines = jlines,
			}, userId);
			if (!ok) return (false, err);

			a.Status = "Disposed"; a.DisposalDate = date.Date; a.DisposalProceeds = R(proceeds); a.DisposalJournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
