using CrossBuy.ViewModel;

namespace CrossBuy.BL
{
	//   اللوائح 
	public interface IPolicesService
	{
		Task < List< LeaveTypesDto>> GetAllLeaveTypesAsync();
		Task<LeaveTypesDto> SaveLeaveType(LeaveTypesDto model);
		Task<LeaveTypesDto> GetLeaveTypeByIDAsync(int ID);
		Task<List< PoliciesDto>> GetAllPoliciesAsync();
		Task<PoliciesDto> SavePolicyAsync(PoliciesDto model , CancellationToken ct = default);
		Task<PoliciesDto?> GetPolicyAsync(int id, CancellationToken ct = default);
        Task<SalaryPoliciesDto?> GetSalaryPolicyAsync(int id, CancellationToken ct = default);
        Task<SalaryPoliciesDto> SaveSalaryPolicyAsync(SalaryPoliciesDto model, CancellationToken ct = default);
		Task<LeavePoliciesDto> SaveLeavePolicyAsync(LeavePoliciesDto model, CancellationToken ct = default);
		Task<List<LeaveTypesDto>> GetLeaveTypesAsync(CancellationToken ct = default);
		Task<AttendancePoliciesDto> SaveAttendancePolicyAsync(AttendancePoliciesDto model, CancellationToken ct = default);
    }
}
