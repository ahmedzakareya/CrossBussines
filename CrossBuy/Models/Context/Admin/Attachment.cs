namespace CrossBuy.Models.Context.Admin
{
	public class Attachment:BaseEntity
	{
		public int Id { get; set; }
		public string AttachName { get; set; }
        public string attachPath { get; set; }

		public int FormID { get; set; }


		public int RequestID { get; set; }

		public int? EmployeeID { get; set; }   // ربط المستند بالموظف
	}
}
