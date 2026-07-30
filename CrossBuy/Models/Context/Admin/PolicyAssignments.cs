using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	public class PolicyAssignments:BaseEntity
	{
		///    جدول خاص بتجيل الموظف علي اللائحة
		///    
		public int ID { get; set; }
		public int LeavePolicyTypeID { get; set; } // اللوائح
        public int EmployeeID { get; set; }

		[ForeignKey(nameof(LeavePolicyTypeID))]
		public Policies  Policies { get; set; }

        [ForeignKey(nameof(EmployeeID))]
        public Employee employee { get; set; }

    }
}
