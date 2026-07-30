namespace CrossBuy.Models.Context.Accounting
{
	// per-company payroll calculation settings (income-tax base + social-insurance wage limits)
	public class PayrollSettings
	{
		[System.ComponentModel.DataAnnotations.Key]
		public int CompanyID { get; set; }
		public decimal PersonalExemptionAnnual { get; set; }      // annual personal tax exemption (0 = none)
		public bool TaxBaseExcludesEmployeeSI { get; set; }        // deduct employee SI share from taxable base
		public decimal SiMinMonthly { get; set; }                 // minimum insurable monthly wage (0 = no floor)
		public decimal SiMaxMonthly { get; set; }                 // maximum insurable monthly wage (0 = no cap)
		public decimal GratuityDaysPerYear { get; set; }          // end-of-service gratuity days per service year (HR-7)
		public decimal StandardMonthlyHours { get; set; } = 176;  // standard monthly working hours (manuf labor rate fallback = BaseSalary ÷ this)
		public DateTime? CreatedAt { get; set; }
	}
}
