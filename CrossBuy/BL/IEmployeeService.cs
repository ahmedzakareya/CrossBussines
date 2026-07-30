using CrossBuy.Models.Context.Admin;

namespace CrossBuy.BL
{
   public interface IEmployeeService
	{
		Task<ViewModel.EmployeeViewModel> GetByIdAsync(int id);
		Task<ViewModel.EmployeeViewModel> GetEmployeeByUserIdAsync(string userId);
		Task<List<ViewModel.EmployeeListItemDto>> GetAllAsync();

		Task<ViewModel.EmployeeViewModel> SaveEmployeeAsync(ViewModel.EmployeeViewModel model, Microsoft.AspNetCore.Http.IFormFile profileImage, string webRootPath);
		Task<ViewModel.EmployeeViewModel> SaveEmployeeImageAsync(int employeeId, Microsoft.AspNetCore.Http.IFormFile profileImage, string webRootPath);
	}
}
