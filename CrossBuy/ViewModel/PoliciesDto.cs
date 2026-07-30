
namespace CrossBuy.ViewModel
{
    public class PoliciesDto
    {
        public int ID { get; set; }
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public string? Notes { get; set; }

        public ICollection<LeavePoliciesDto>? LeavePolicies { get; set; }
        public ICollection<SalaryPoliciesDto>? SalaryPolicies { get; set; }
        public ICollection<AttendancePoliciesDto>? AttendancePolicies { get; set; }
    }

}
