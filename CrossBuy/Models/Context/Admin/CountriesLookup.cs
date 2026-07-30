using System.ComponentModel.DataAnnotations;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace CrossBuy.Models.Context.Admin
{
	public class CountriesLookup
	{
		[Key]
		public int ID { get; set; } 
		public string CountryName { get; set; } 
		public string CountryNameAr { get; set; } 
		public string NationalityName { get; set; } 
		public string NationalityNameEn { get; set; } 
		public string Flage { get; set; }

		public virtual ICollection<Companies> Companies { get; set; } 
		public virtual ICollection<Employee> Employees { get; set; } 
		public virtual ICollection<Branch> Branches { get; set; } 
	}
}
