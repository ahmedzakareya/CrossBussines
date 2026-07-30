using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P5-أ: project material issue (أذن صرف مواد للمشروع).
	// Actual material cost: on post, each line issues stock via StockService (Direction -1, ProjectId-tagged,
	// CounterAccountOverride = 510104) → Dr 510104 project cost / Cr Inventory. StockService is the only inventory
	// writer (Cr inventory unchanged → stock_gl intact); no new accounting writer. Feeds ProfitabilityAsync.
	public class ProjectMaterialIssue
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }              // → Project (P0)
		public int IssueNo { get; set; }                // sequence per project
		public DateTime IssueDate { get; set; }
		public int WarehouseId { get; set; }
		public string? Note { get; set; }
		public string Status { get; set; } = "Draft";   // Draft (editable) / Posted (stock issued + GL posted)
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? PostedAt { get; set; }
		public int? PostedBy { get; set; }

		public List<ProjectMaterialIssueLine> Lines { get; set; } = new();
	}

	public class ProjectMaterialIssueLine
	{
		public int ID { get; set; }
		public int IssueId { get; set; }                // → ProjectMaterialIssue
		public int ItemId { get; set; }
		public decimal Qty { get; set; }
		public int? BoqItemId { get; set; }             // optional → BoqItem (estimate-vs-actual later)
		public decimal UnitCost { get; set; }           // snapshot on post
		public decimal TotalCost { get; set; }          // snapshot on post
		public int? StockMovementId { get; set; }       // the StockService movement created on post

		[ForeignKey(nameof(IssueId))]
		public ProjectMaterialIssue? Issue { get; set; }
	}
}
