using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P1 BOQ (Bill of Quantities). A project's estimated work items.
	// OPERATIONAL / ESTIMATE ONLY: no GL, no journal entries — this is a costing/estimate table linked to ProjectId.
	// Flexible: the user defines any items/units for any activity type (nothing assumed).
	public class BoqItem
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }              // → Project (P0)
		public int? ParentId { get; set; }              // self-ref: null = main item/section header; else a sub-item under it
		public int SortOrder { get; set; }
		public string? Code { get; set; }               // optional item no. (e.g. "1", "1.1")
		public string Description { get; set; } = "";    // الوصف (required)
		public string? DescriptionEn { get; set; }        // English twin (shown when UI is not Arabic; falls back to Description)
		public string? Unit { get; set; }                // الوحدة: متر/طن/عدد… (free text — flexible)
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }           // سعر الوحدة للعميل  → line value = Quantity × UnitPrice
		// ---- estimated cost breakdown (تقديري — total amounts per line, NOT posted to GL) ----
		public decimal? MaterialCost { get; set; }       // مواد
		public decimal? LaborCost { get; set; }          // عمالة
		public decimal? SubcontractCost { get; set; }    // باطن
		public decimal? EquipmentCost { get; set; }      // معدات
		public int? VariationOrderId { get; set; }       // P6-د: null = original scope; set = added by a variation order
		public DateTime? CreatedAt { get; set; }

		[NotMapped] public decimal LineValue => Math.Round(Quantity * UnitPrice, 2);
		[NotMapped] public decimal EstimatedCost => Math.Round((MaterialCost ?? 0m) + (LaborCost ?? 0m) + (SubcontractCost ?? 0m) + (EquipmentCost ?? 0m), 2);
		[NotMapped] public decimal Margin => Math.Round(LineValue - EstimatedCost, 2);
	}
}
