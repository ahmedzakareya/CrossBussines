namespace CrossBuy.Models.Context.Admin
{
	public class HierarchicalType
	{
		public int ID { get; set; } 
		public string? TypeNameAr { get; set; } 
		public string? TypeNameEn { get; set; } 

		// Navigation properties
		public virtual ICollection<Hierarchical>? Hierarchicals { get; set; } 
	}
}
