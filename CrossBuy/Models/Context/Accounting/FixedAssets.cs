using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Phase-6 Fixed Assets & Depreciation entities.

	public class AssetCategory
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int DefaultUsefulLifeMonths { get; set; } = 60;
		public string DepreciationMethod { get; set; } = "StraightLine";
		public int CostAccountId { get; set; }          // 1201 by default
		public int AccumDepAccountId { get; set; }       // 1202
		public int DepExpenseAccountId { get; set; }     // 520103
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	public class FixedAsset
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? AssetNo { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int? CategoryId { get; set; }
		public DateTime AcquisitionDate { get; set; }
		public decimal Cost { get; set; }
		public decimal SalvageValue { get; set; }
		public int UsefulLifeMonths { get; set; } = 60;
		public string DepreciationMethod { get; set; } = "StraightLine";
		public int CostAccountId { get; set; }
		public int AccumDepAccountId { get; set; }
		public int DepExpenseAccountId { get; set; }
		public int? CostCenterId { get; set; }
		public decimal AccumulatedDepreciation { get; set; }   // running
		public DateTime? LastDepreciationDate { get; set; }
		public string Status { get; set; } = "Active";          // Active / FullyDepreciated / Disposed
		public DateTime? DisposalDate { get; set; }
		public decimal? DisposalProceeds { get; set; }
		public int? AcquisitionJournalEntryId { get; set; }
		public int? DisposalJournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }

		[NotMapped] public decimal NetBookValue => Cost - AccumulatedDepreciation;
	}

	public class DepreciationRun
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public DateTime PeriodDate { get; set; }   // month being depreciated (month-end)
		public DateTime RunDate { get; set; }
		public decimal TotalAmount { get; set; }
		public int AssetCount { get; set; }
		public string Status { get; set; } = "Posted";
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<DepreciationLine> Lines { get; set; } = new List<DepreciationLine>();
	}

	public class DepreciationLine
	{
		public int ID { get; set; }
		public int DepreciationRunId { get; set; }
		public int FixedAssetId { get; set; }
		public decimal Amount { get; set; }
		public decimal AccumulatedAfter { get; set; }
		public decimal NetBookValueAfter { get; set; }
		[ForeignKey(nameof(DepreciationRunId))] public DepreciationRun? Run { get; set; }
	}
}
