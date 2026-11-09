namespace CrossBuy.Models.Context.Admin
{
	/// HR8 — the shape of the salary transfer file a company uploads to its bank (hr8_bank_file_formats.sql).
	/// Every part of it is a setting, so a bank's template is matched without code (HrBankFileService).
	public class HrBankFileFormat
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? BankName { get; set; }
		public string Delimiter { get; set; } = ",";
		public bool WithBom { get; set; } = true;
		public bool HeaderRow { get; set; } = true;
		public string DateFormat { get; set; } = "dd/MM/yyyy";
		public int DecimalPlaces { get; set; } = 3;
		public string TrailerRow { get; set; } = "None";            // None | CountTotal
		public string FileNamePattern { get; set; } = "SALARIES_{yyyyMM}.csv";
		public string? CompanyAccount { get; set; }
		public string? EmployerCode { get; set; }
		public int PaymentDay { get; set; }                         // 0 = last day of the month
		public string ColumnsJson { get; set; } = "[]";
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeID { get; set; }
	}
}
