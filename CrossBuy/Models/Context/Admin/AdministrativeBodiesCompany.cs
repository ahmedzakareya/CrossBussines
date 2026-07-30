using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace CrossBuy.Models.Context.Admin
{
	public class AdministrativeBodiesCompany:BaseEntity
	{
		[Key]
		public int A_ID { get; set; }
        public string NameAr { get; set; }
        public string NameEN { get; set; }
        public string? Notes { get; set; }



    }
}
