using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P6-هـ: owned-equipment depreciation allocation to a project.
	// Post = reclassification via the EXISTING JournalEntryService:
	//   Dr 510104 project execution cost [ProjectId] / Cr 520103 depreciation expense (asset's DepExpenseAccountId) [UNtagged]
	// Both legs carry a cost center (RequireCostCenter). Does NOT touch AccumDepAccountId, FixedAsset.AccumulatedDepreciation
	// or DepreciationRuns → the depreciation schedule is untouched. No new accounting writer. Post-once guard via Status.
	public class EquipmentAllocInput
	{
		public int FixedAssetId { get; set; }
		public DateTime PeriodDate { get; set; }
		public decimal? Hours { get; set; }
		public decimal? Rate { get; set; }
		public decimal Amount { get; set; }
		public string? Note { get; set; }
	}

	// public DTO for the screen (asset picker rows + monthly-depreciation reference)
	public class EquipmentAssetRow
	{
		public int AssetId { get; set; }
		public string Label { get; set; } = "";
		public decimal MonthlyDepreciation { get; set; }
		public decimal NetBookValue { get; set; }
	}

	public interface IEquipmentDepreciationService
	{
		Task<List<EquipmentDepreciationAllocation>> GetForProjectAsync(int companyId, int projectId);
		Task<EquipmentDepreciationAllocation?> GetAsync(int companyId, int id);
		Task<List<EquipmentAssetRow>> GetAssetsAsync(int companyId);
		Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int allocId, EquipmentAllocInput input, int? userId);
		Task<(bool ok, string? error)> PostAsync(int companyId, int id, int? userId);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
	}

	public class EquipmentDepreciationService : IEquipmentDepreciationService
	{
		public const string ProjectCostAccountCode = "510104";   // Dr — project execution cost
		private readonly CrossDbContext _db;
		private readonly IJournalEntryService _je;
		private readonly IFixedAssetService _assets;
		public EquipmentDepreciationService(CrossDbContext db, IJournalEntryService je, IFixedAssetService assets) { _db = db; _je = je; _assets = assets; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private static bool IsAr() => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

		private async Task HydrateAssetsAsync(int companyId, List<EquipmentDepreciationAllocation> list)
		{
			if (list.Count == 0) return;
			var ids = list.Select(x => x.FixedAssetId).Distinct().ToList();
			var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyID == companyId && ids.Contains(a.ID)).ToDictionaryAsync(a => a.ID);
			foreach (var a in list) if (assets.TryGetValue(a.FixedAssetId, out var fa)) a.Asset = fa;
		}

		public async Task<List<EquipmentDepreciationAllocation>> GetForProjectAsync(int companyId, int projectId)
		{
			var list = await _db.EquipmentDepreciationAllocations.AsNoTracking()
				.Where(x => x.CompanyID == companyId && x.ProjectId == projectId)
				.OrderByDescending(x => x.AllocationNo).ToListAsync();
			await HydrateAssetsAsync(companyId, list);
			return list;
		}

		public async Task<EquipmentDepreciationAllocation?> GetAsync(int companyId, int id)
		{
			var a = await _db.EquipmentDepreciationAllocations.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (a != null) await HydrateAssetsAsync(companyId, new List<EquipmentDepreciationAllocation> { a });
			return a;
		}

		// active/owned equipment = fixed assets available to depreciate; monthly depreciation shown as a reference
		public async Task<List<EquipmentAssetRow>> GetAssetsAsync(int companyId)
		{
			var isAr = IsAr();
			var assets = await _db.FixedAssets.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Status != "Disposed")
				.OrderBy(a => a.AssetNo).ThenBy(a => a.Name).ToListAsync();
			return assets.Select(a => new EquipmentAssetRow
			{
				AssetId = a.ID,
				Label = (string.IsNullOrWhiteSpace(a.AssetNo) ? "" : a.AssetNo + " — ") + (isAr ? a.Name : (string.IsNullOrWhiteSpace(a.NameEn) ? a.Name : a.NameEn!)),
				MonthlyDepreciation = R(_assets.MonthlyDepreciation(a)),
				NetBookValue = R(a.NetBookValue)
			}).ToList();
		}

		public async Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int allocId, EquipmentAllocInput input, int? userId)
		{
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (prj == null) return (false, "المشروع غير موجود", 0);
			var asset = await _db.FixedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.FixedAssetId && a.CompanyID == companyId);
			if (asset == null) return (false, "اختر معدة (أصلًا ثابتًا) صحيحة", 0);
			var amount = R(input.Amount);
			if (amount <= 0) return (false, "أدخل حصة إهلاك أكبر من صفر", 0);

			EquipmentDepreciationAllocation hdr;
			if (allocId > 0)
			{
				hdr = await _db.EquipmentDepreciationAllocations.FirstOrDefaultAsync(x => x.ID == allocId && x.CompanyID == companyId) ?? throw new InvalidOperationException("التحميل غير موجود");
				if (hdr.Status != "Draft") return (false, "لا يمكن تعديل تحميل مرحّل", 0);
			}
			else
			{
				int nextNo = (await _db.EquipmentDepreciationAllocations.Where(x => x.CompanyID == companyId && x.ProjectId == projectId).Select(x => (int?)x.AllocationNo).MaxAsync() ?? 0) + 1;
				hdr = new EquipmentDepreciationAllocation { CompanyID = companyId, ProjectId = projectId, AllocationNo = nextNo, Status = "Draft", CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.EquipmentDepreciationAllocations.Add(hdr);
			}
			hdr.FixedAssetId = input.FixedAssetId;
			hdr.PeriodDate = input.PeriodDate == default ? DateTime.Today : input.PeriodDate;
			hdr.Hours = input.Hours;
			hdr.Rate = input.Rate;
			hdr.Amount = amount;
			hdr.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
			await _db.SaveChangesAsync();
			return (true, null, hdr.ID);
		}

		public async Task<(bool ok, string? error)> PostAsync(int companyId, int id, int? userId)
		{
			var hdr = await _db.EquipmentDepreciationAllocations.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (hdr == null) return (false, "التحميل غير موجود");
			if (hdr.Status == "Posted") return (false, "التحميل مرحّل بالفعل");   // post-once
			if (hdr.Amount <= 0) return (false, "حصة الإهلاك صفر");

			var asset = await _db.FixedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.ID == hdr.FixedAssetId && a.CompanyID == companyId);
			if (asset == null) return (false, "المعدة غير موجودة");
			if (asset.DepExpenseAccountId <= 0) return (false, "المعدة بلا حساب مصروف إهلاك مُهيّأ");

			var costAcc = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == ProjectCostAccountCode).Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (costAcc == null) return (false, $"حساب تكلفة التنفيذ ({ProjectCostAccountCode}) غير مُهيّأ");

			// both legs need a cost center (RequireCostCenter): asset's → project's → first company cost center
			var prjCc = await _db.Projects.AsNoTracking().Where(p => p.ID == hdr.ProjectId).Select(p => p.CostCenterId).FirstOrDefaultAsync();
			int? cc = asset.CostCenterId ?? prjCc ?? await _db.CostCenters.AsNoTracking().Where(x => x.CompanyID == companyId).OrderBy(x => x.ID).Select(x => (int?)x.ID).FirstOrDefaultAsync();
			if (cc == null) return (false, "القيد يتطلب مركز تكلفة ولا يوجد مركز تكلفة معرّف");

			var amount = R(hdr.Amount);
			var curId = await _db.Currencies.Select(c => c.ID).FirstOrDefaultAsync();
			var input = new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = hdr.PeriodDate, JournalType = "Manual", CurrencyId = curId,
				SourceType = "EquipmentDepAllocation", SourceId = hdr.ID,
				Description = "تحميل إهلاك معدة على المشروع — " + (asset.Name ?? ""), DescriptionEn = "Equipment depreciation to project — " + (asset.NameEn ?? asset.Name ?? ""),
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = costAcc.Value, Debit = amount, Credit = 0, ProjectId = hdr.ProjectId, CostCenterId = cc, Description = "حصة إهلاك المعدة (تكلفة المشروع)" },
					new() { AccountId = asset.DepExpenseAccountId, Debit = 0, Credit = amount, ProjectId = null, CostCenterId = cc, Description = "إعادة تصنيف مصروف الإهلاك إلى المشروع" },
				}
			};
			var (ok, jerr, entry) = await _je.CreateAndPostAsync(input, userId);
			if (!ok) return (false, jerr);
			hdr.Status = "Posted"; hdr.PostedAt = DateTime.UtcNow; hdr.PostedBy = userId; hdr.JournalEntryId = entry!.ID;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var hdr = await _db.EquipmentDepreciationAllocations.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (hdr == null) return (false, "التحميل غير موجود");
			if (hdr.Status == "Posted") return (false, "لا يمكن حذف تحميل مرحّل");
			_db.EquipmentDepreciationAllocations.Remove(hdr);
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
