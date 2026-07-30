using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace CrossBuy.Models.Context.Admin
{
	public class CompanyType
	{
		public int Id { get; set; } 
		public string NameAr { get; set; } 
		public string? NameEn { get; set; } 
		public ICollection<Companies> Companies { get; set; }
	}
}
