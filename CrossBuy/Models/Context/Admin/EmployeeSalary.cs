namespace CrossBuy.Models.Context.Admin
{
	/// HR4 (2026-11-08) — ONE EMPLOYEE'S PAY, EFFECTIVE-DATED. A raise is a NEW ROW from its date; the past is never
	/// overwritten, so a payroll or a settlement for an earlier month reads the pay that was true then. The pay in force on
	/// a date is the row with the latest EffectiveFrom on or before it. An employee without any row falls back to the
	/// salary policy of their assigned leave policy (the legacy shape), so nothing changes for anyone until HR sets a salary.
	public class EmployeeSalary
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public DateTime EffectiveFrom { get; set; }
		public decimal BasicSalary { get; set; }
		public decimal HousingAllowance { get; set; }
		public decimal TransportAllowance { get; set; }
		public decimal OtherAllowances { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}
}
