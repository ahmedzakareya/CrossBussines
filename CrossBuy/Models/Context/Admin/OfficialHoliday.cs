namespace CrossBuy.Models.Context.Admin
{
	// عطلة رسمية على مستوى الشركة — تُستبعَد من حساب أيام الإجازة والغياب
	public class OfficialHoliday
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string NameAr { get; set; } = "";
		public string? NameEn { get; set; }
		public DateTime HolidayDate { get; set; }
		public bool IsRecurring { get; set; }      // true = تتكرر سنويًا بنفس اليوم/الشهر
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
