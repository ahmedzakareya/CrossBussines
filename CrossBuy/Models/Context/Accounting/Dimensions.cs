namespace CrossBuy.Models.Context.Accounting
{
	// Phase-2 analytical dimensions. Cost centers are linked to the CrossBuy org tree (Hierarchicals)
	// so any expense tagged with a cost center rolls up to a branch/department automatically.

	/// مركز التكلفة — Cost center (mirrors a Hierarchicals branch/administrative-body node).
	public class CostCenter
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? ParentId { get; set; }
		public int? SourceHierarchicalId { get; set; }   // → Hierarchicals.H_ID
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	/// مشروع — Project dimension (optional, for project-based costing).
	/// Projects & Contracting (P0): extended with contracting fields — ALL nullable/additive so the existing
	/// ProjectId GL plumbing + ProfitabilityAsync keep working unchanged. Cost/revenue attribution stays via ProjectId.
	public class Project
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public bool IsActive { get; set; } = true;
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public decimal? Budget { get; set; }
		public DateTime? CreatedAt { get; set; }
		// ---- Contracting extensions (P0) — all nullable, do NOT affect GL/stock posting ----
		public int? CustomerId { get; set; }         // العميل = عميل AR قائم (reuse Customer)
		public string? Location { get; set; }         // موقع المشروع
		public decimal? ContractValue { get; set; }   // قيمة العقد الإجمالية
		public string? Status { get; set; }           // Draft / Active / OnHold / Completed / Cancelled
		public int? ActivityTypeId { get; set; }      // → ProjectActivityType (user-defined, flexible)
		public int? CostCenterId { get; set; }        // ربط اختياري بمركز تكلفة (تجميع تنظيمي)
		// ---- P2 contract terms (nullable) — read later by progress billing; no GL effect by themselves ----
		public decimal? AdvancePercent { get; set; }   // نسبة الدفعة المقدّمة من قيمة العقد
		public decimal? RetentionPercent { get; set; } // نسبة المحتجز من كل مستخلص
	}

	/// نوع نشاط المشروع — user-defined project activity type (flexible; the table starts EMPTY, the user fills it).
	/// Nothing hard-coded: construction / roads / electromechanical / maintenance / IT / events … the user decides.
	public class ProjectActivityType
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}
}
