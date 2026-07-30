namespace CrossBuy.Models
{
	public class BaseEntity
	{
		public int? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? updatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
	}
}
