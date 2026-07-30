namespace CrossBuy.Controllers
{
	// Flattened list rows for the server-side inventory document grids
	// (party name / warehouse code resolved via join so no lookup tables load into the view).
	public class MovementRow
	{
		public int Id { get; set; }
		public int ItemId { get; set; }
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public string? ItemNameEn { get; set; }
		public string WarehouseCode { get; set; } = "";
		public string? BinLocationCode { get; set; }
		public DateTime MovementDate { get; set; }
		public short Direction { get; set; }
		public decimal QtyBase { get; set; }
		public decimal UnitCost { get; set; }
		public decimal TotalCost { get; set; }
		public string? SourceType { get; set; }
		public int? JournalEntryId { get; set; }
	}

	public class PoRow
	{
		public int Id { get; set; }
		public string OrderNo { get; set; } = "";
		public DateTime OrderDate { get; set; }
		public string? PartyName { get; set; }
		public string? PartyNameEn { get; set; }
		public decimal GrandTotal { get; set; }
		public string Status { get; set; } = "";
	}

	public class GrRow
	{
		public int Id { get; set; }
		public string ReceiptNo { get; set; } = "";
		public DateTime ReceiptDate { get; set; }
		public string? PartyName { get; set; }
		public string? PartyNameEn { get; set; }
		public string WarehouseCode { get; set; } = "";
		public int? RefId { get; set; }
		public decimal TotalCost { get; set; }
	}

	public class SoRow
	{
		public int Id { get; set; }
		public string OrderNo { get; set; } = "";
		public DateTime OrderDate { get; set; }
		public string? PartyName { get; set; }
		public string? PartyNameEn { get; set; }
		public decimal GrandTotal { get; set; }
		public string Status { get; set; } = "";
	}

	public class QuoteRow
	{
		public int Id { get; set; }
		public string QuoteNo { get; set; } = "";
		public DateTime QuoteDate { get; set; }
		public DateTime? ValidUntil { get; set; }
		public string? PartyName { get; set; }
		public string? PartyNameEn { get; set; }
		public decimal GrandTotal { get; set; }
		public string Status { get; set; } = "";
	}

	public class DeliveryRow
	{
		public int Id { get; set; }
		public string DeliveryNo { get; set; } = "";
		public DateTime DeliveryDate { get; set; }
		public string? PartyName { get; set; }
		public string? PartyNameEn { get; set; }
		public string WarehouseCode { get; set; } = "";
		public int? RefId { get; set; }
		public decimal TotalCost { get; set; }
	}
}
