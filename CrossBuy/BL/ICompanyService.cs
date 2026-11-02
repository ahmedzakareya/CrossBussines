using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;

namespace CrossBuy.BL
{
	public interface ICompanyService
	{
		Task<List<CompanyDto>> GetAllCompaniesAsync();
		Task<CompanyDto> GetCompanyForm();
		Task<CompanyDto> SaveCompanyAsync(CompanyDto companyDto , List<Models.Context.Admin.Attachment> attachments);

		/// <summary>
		/// Repairs ONE company created before first-administrator module seeding existed. Refuses a
		/// company that already holds any module role, and an employee who does not belong to it.
		/// </summary>
		Task<string> BackfillFirstAdministratorModuleRolesAsync(int companyId, int employeeId);
		Task<CompanyDto> GetCompanyData(int? ID);

		 Task<List<BranchiesDto>> GetAllBranchesAsync();
		Task<List<BranchiesDto>> GetBranchesByCompanyAsync(int companyId);
		Task<BranchiesDto> GetBranchByIdAsync(int id);
		Task<BranchiesDto> GetBranchForm();
		Task<BranchiesDto> SaveBranchAsync(BranchiesDto model, List<Models.Context.Admin.Attachment> attachments);
		Task<BranchiesDto> GetBranchData(int? ID);

	}
}
