namespace CrossBuy.Models.Context.Admin
{
	public class JobTitle
	{
		public int ID { get; set; } 
		// HR0 (2026-11-11): the owning company. Filtered by CompanyQueryFilters and guarded on write; null = no company
		// (an orphan no company sees). Set by deploy/sql/hr0_company_isolation.sql for existing rows.
		public int? CompanyID { get; set; }
		public string Title { get; set; }
		public string TitleAr { get; set; }

		public string Description { get; set; } 
		public virtual ICollection<Employee> Employees { get; set; }
	}
}
