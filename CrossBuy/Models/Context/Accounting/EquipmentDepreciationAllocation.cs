using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P6-هـ: owned-equipment depreciation allocation to a project (تحميل حصة إهلاك المعدة على المشروع).
	// The equipment is an EXISTING FixedAsset. On post, a reclassification journal is created via the EXISTING
	// JournalEntryService: Dr 510104 project execution cost [ProjectId] / Cr 520103 depreciation expense (the asset's
	// own DepExpenseAccountId), both carrying a cost center; the credit is UNtagged so ProfitabilityAsync counts only
	// the debit. This does NOT touch AccumDepAccountId (accumulated depreciation / contra-asset), FixedAsset.
	// AccumulatedDepreciation, or DepreciationRuns — the depreciation schedule stays intact. No new accounting writer.
	// Manual amount (with the asset's monthly depreciation shown as a reference; optional hours × rate helper only
	// fills the amount). Post-once guard via Status (Draft → Posted, JournalEntryId set, no re-post). Feeds the
	// project budget/profitability "equipment" bucket (EquipmentCost estimate in the BOQ).
	public class EquipmentDepreciationAllocation
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }              // → Project (P0)
		public int AllocationNo { get; set; }           // sequence per project
		public int FixedAssetId { get; set; }           // → FixedAsset (owned equipment)
		public DateTime PeriodDate { get; set; }        // the period this share belongs to
		public decimal? Hours { get; set; }             // optional helper: equipment hours on the project
		public decimal? Rate { get; set; }              // optional helper: depreciation rate per hour
		public decimal Amount { get; set; }             // the depreciation share charged to the project
		public string? Note { get; set; }
		public string Status { get; set; } = "Draft";   // Draft (editable) / Posted (reclass GL posted)
		public int? JournalEntryId { get; set; }        // the reclass entry created on post
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? PostedAt { get; set; }
		public int? PostedBy { get; set; }

		[NotMapped] public FixedAsset? Asset { get; set; }   // hydrated for display (not mapped)
	}
}
