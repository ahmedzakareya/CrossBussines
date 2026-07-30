namespace CrossBuy.Models.Context.Admin
{
	public class LeaveTypes : BaseEntity
	{
        public int ID { get; set; }
        public string NameAr { get; set; }
        public string NameEn { get; set; }
        public string? Notes { get; set; }
        public bool IsEncashable { get; set; }   // eligible for cash payout & leave provision (HR-2f)
        public ICollection<LeavePolicies> LeavePolicies { get; set; }


	}
}
