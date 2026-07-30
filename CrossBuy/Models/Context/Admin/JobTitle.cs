namespace CrossBuy.Models.Context.Admin
{
	public class JobTitle
	{
		public int ID { get; set; } 
		public string Title { get; set; }
		public string TitleAr { get; set; }

		public string Description { get; set; } 
		public virtual ICollection<Employee> Employees { get; set; }
	}
}
