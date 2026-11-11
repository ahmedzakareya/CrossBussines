namespace CrossBuy.Models.Context.Admin
{
	public class LeaveTypes : BaseEntity
	{
        public int ID { get; set; }
        // HR0 (2026-11-11): the owning company. Filtered by CompanyQueryFilters and guarded on write; null = no company
        // (an orphan no company sees). Set by deploy/sql/hr0_company_isolation.sql for existing rows.
        public int? CompanyID { get; set; }
        public string NameAr { get; set; }
        public string NameEn { get; set; }
        public string? Notes { get; set; }
        public bool IsEncashable { get; set; }   // eligible for cash payout & leave provision (HR-2f)
        public ICollection<LeavePolicies> LeavePolicies { get; set; }


	}
}
