using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P6-د: variation order (أمر تغيير). OPERATIONAL / estimate only — NO GL.
	// On approve: New lines → inserted as BOQ items tagged VariationOrderId; Adjust lines → applied in place to the
	// existing BoqItem (Qty/UnitPrice) keeping the old snapshot on the line. Approved value adds to the revised contract
	// value (original ContractValue stays). Financial effect only later via billings/issues on the new/revised items.
	public class VariationOrder
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }              // → Project (P0)
		public int VoNo { get; set; }                   // sequence per project
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }      // English twin (shown when UI is not Arabic; falls back to Description)
		public string? Reason { get; set; }
		public decimal Value { get; set; }              // derived Σ line changes (+/-)
		public string Status { get; set; } = "Draft";   // Draft (editable, no effect) / Approved (applied to BOQ)
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? ApprovedAt { get; set; }
		public int? ApprovedBy { get; set; }

		public List<VariationOrderLine> Lines { get; set; } = new();
	}

	public class VariationOrderLine
	{
		public int ID { get; set; }
		public int VariationOrderId { get; set; }       // → VariationOrder
		public string Kind { get; set; } = "New";       // New (add BOQ item) | Adjust (revise an existing item)
		public int? BoqItemId { get; set; }             // Adjust: the existing BOQ item
		public string? Code { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public string? Unit { get; set; }
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal? MaterialCost { get; set; }
		public decimal? LaborCost { get; set; }
		public decimal? SubcontractCost { get; set; }
		public decimal? EquipmentCost { get; set; }
		public decimal? OldQuantity { get; set; }        // Adjust: snapshot of the original
		public decimal? OldUnitPrice { get; set; }

		[NotMapped] public decimal LineValue => Math.Round(Quantity * UnitPrice, 2);
		[NotMapped] public decimal OldValue => Math.Round((OldQuantity ?? 0m) * (OldUnitPrice ?? 0m), 2);
		// value change this line contributes to the VO total: New = its value ; Adjust = new − old
		[NotMapped] public decimal ValueChange => Kind == "Adjust" ? Math.Round(LineValue - OldValue, 2) : LineValue;

		[ForeignKey(nameof(VariationOrderId))]
		public VariationOrder? Order { get; set; }
	}
}
