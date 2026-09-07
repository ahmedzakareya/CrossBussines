using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class EmployeeService : IEmployeeService
	{
		private readonly CrossDbContext _context;

		public EmployeeService(CrossDbContext context)
		{
			_context = context;
		}

		private static ViewModel.EmployeeViewModel MapToViewModel(Employee emp, int? policyId = null)
		{
			return new ViewModel.EmployeeViewModel
			{
				ID            = emp.ID,
				FirstName     = emp.FirstName,
				LastName      = emp.LastName,
				FullName      = emp.FullName,
				FullNameEn    = emp.FullNameEn,
				Address       = emp.Address,
				PhoneNumber   = emp.PhoneNumber,
				Email         = emp.Email,
				UserId        = emp.UserId,
				ProfileImage  = emp.ProfileImage,
				Gender        = emp.Gender,
				MaritalStatus = emp.MaritalStatus,
				DateOfBirth   = emp.DateOfBirth,
				DateOfJoining = emp.DateOfJoining,
				JobTitleID    = emp.JobTitleID,
				BranchID      = emp.BranchID,
				EmpCompanyID  = emp.EmpCompanyID,
				ManufHourlyRate = emp.ManufHourlyRate,
				PolicyID      = policyId
			};
		}

		public async Task<ViewModel.EmployeeViewModel> GetByIdAsync(int id)
		{
			var emp = await _context.Employee
				.Include(e => e.policyAssignments)
				.FirstOrDefaultAsync(e => e.ID == id);
			if (emp == null) return null;
			var policyId = emp.policyAssignments?.FirstOrDefault()?.LeavePolicyTypeID;
			return MapToViewModel(emp, policyId);
		}

		public async Task<List<ViewModel.EmployeeListItemDto>> GetAllAsync()
		{
			return await _context.Employee.AsNoTracking()
				// The DTO below hands the view both names, which is right - only the ORDER was decided by
				// the Arabic one, so an English list came out looking unsorted.
				.OrderByDisplayName()
				.Select(e => new ViewModel.EmployeeListItemDto
				{
					ID            = e.ID,
					FullName      = e.FullName,
					FullNameEn    = e.FullNameEn,
					ProfileImage  = e.ProfileImage,
					JobTitleAr    = e.JobTitle.TitleAr,
					JobTitleEn    = e.JobTitle.Title,
					CompanyAr     = e.Company.ComoanyNameAr,
					CompanyEn     = e.Company.CompanyName,
					BranchAr      = e.Branch.NameAr,
					BranchEn      = e.Branch.Name,
					CountryAr     = e.Country.CountryNameAr,
					CountryEn     = e.Country.CountryName,
					Email         = e.Email,
					PhoneNumber   = e.PhoneNumber,
					IsActive      = e.IsActive
				})
				.ToListAsync();
		}

		public async Task<ViewModel.EmployeeViewModel> GetEmployeeByUserIdAsync(string userId)
		{
			try
			{
				var emp = await _context.Employee
					.Include(e => e.policyAssignments)
					.FirstOrDefaultAsync(e => e.UserId == userId);
				if (emp == null) return null;
				var policyId = emp.policyAssignments?.FirstOrDefault()?.LeavePolicyTypeID;
				return MapToViewModel(emp, policyId);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
				return null;
			}
		}

		public async Task<ViewModel.EmployeeViewModel> SaveEmployeeAsync(
			ViewModel.EmployeeViewModel model,
			Microsoft.AspNetCore.Http.IFormFile profileImage,
			string webRootPath)
		{
			if (model == null) throw new ArgumentNullException(nameof(model));

			// resolve existing employee: by ID → UserId → Email
			Employee emp = null;
			if (model.ID > 0)
				emp = await _context.Employee.Include(e => e.policyAssignments).FirstOrDefaultAsync(e => e.ID == model.ID);
			if (emp == null && !string.IsNullOrEmpty(model.UserId))
				emp = await _context.Employee.Include(e => e.policyAssignments).FirstOrDefaultAsync(e => e.UserId == model.UserId);
			if (emp == null && !string.IsNullOrEmpty(model.Email))
				emp = await _context.Employee.Include(e => e.policyAssignments).FirstOrDefaultAsync(e => e.Email == model.Email);

			var isNew = emp == null;
			if (isNew)
			{
				emp = new Employee();
				_context.Employee.Add(emp);
			}

			emp.FirstName     = model.FirstName;
			emp.LastName      = model.LastName;
			emp.FullName      = string.IsNullOrWhiteSpace(model.FullName)
								? (model.FirstName + " " + model.LastName).Trim()
								: model.FullName;
			emp.Address       = model.Address ?? emp.Address ?? string.Empty;        // NOT NULL column — default empty when absent
			emp.PhoneNumber   = model.PhoneNumber ?? emp.PhoneNumber ?? string.Empty; // NOT NULL column — default empty when absent
			emp.Email         = model.Email;
			if (!string.IsNullOrWhiteSpace(model.UserId)) emp.UserId = model.UserId;  // keep an existing link if the caller didn't pass one
			emp.Gender        = model.Gender        ?? emp.Gender        ?? string.Empty;
			emp.MaritalStatus = model.MaritalStatus ?? emp.MaritalStatus ?? string.Empty;

			if (model.DateOfBirth.HasValue)   emp.DateOfBirth  = model.DateOfBirth.Value;
			if (model.DateOfJoining.HasValue) emp.DateOfJoining = model.DateOfJoining.Value;
			emp.ManufHourlyRate = (model.ManufHourlyRate.HasValue && model.ManufHourlyRate.Value > 0) ? model.ManufHourlyRate : null;
			if (model.JobTitleID.HasValue)    emp.JobTitleID   = model.JobTitleID.Value;
			if (model.BranchID.HasValue)      emp.BranchID     = model.BranchID.Value;
			if (model.EmpCompanyID.HasValue)  emp.EmpCompanyID = model.EmpCompanyID.Value;
			// previously dropped on save — needed by the hire→employee flow (all nullable, non-breaking)
			if (!string.IsNullOrWhiteSpace(model.FullNameEn)) emp.FullNameEn = model.FullNameEn;
			if (model.CountryID.HasValue)     emp.CountryID    = model.CountryID.Value;
			if (model.DepartmentID.HasValue)  emp.DepartmentID = model.DepartmentID.Value;
			if (!string.IsNullOrWhiteSpace(model.EmploymentType)) emp.EmploymentType = model.EmploymentType;

			// handle profile image
			if (profileImage != null && profileImage.Length > 0 && !string.IsNullOrEmpty(webRootPath))
			{
				var uploads = System.IO.Path.Combine(webRootPath, "uploads", "employees");
				if (!System.IO.Directory.Exists(uploads)) System.IO.Directory.CreateDirectory(uploads);
				var fileName = Guid.NewGuid() + System.IO.Path.GetExtension(profileImage.FileName);
				using (var stream = System.IO.File.Create(System.IO.Path.Combine(uploads, fileName)))
					await profileImage.CopyToAsync(stream);
				emp.ProfileImage = "/uploads/employees/" + fileName;
			}
			if (string.IsNullOrEmpty(emp.ProfileImage)) emp.ProfileImage = string.Empty;   // NOT NULL column — default empty when no image uploaded

			try
			{
				await _context.SaveChangesAsync();
			}
			catch (Microsoft.EntityFrameworkCore.DbUpdateException)
			{
				// unique constraint hit — return the existing record
				Employee existing = null;
				if (!string.IsNullOrEmpty(model.UserId))
					existing = await _context.Employee.Include(e => e.policyAssignments).FirstOrDefaultAsync(e => e.UserId == model.UserId);
				if (existing == null && !string.IsNullOrEmpty(model.Email))
					existing = await _context.Employee.Include(e => e.policyAssignments).FirstOrDefaultAsync(e => e.Email == model.Email);
				if (existing != null)
				{
					var existingPolicy = existing.policyAssignments?.FirstOrDefault()?.LeavePolicyTypeID;
					return MapToViewModel(existing, existingPolicy);
				}
				throw;
			}

			// sync PolicyAssignments
			if (model.PolicyID.HasValue && model.PolicyID.Value > 0)
			{
				var assignment = (emp.policyAssignments ?? new List<PolicyAssignments>()).FirstOrDefault();
				if (assignment == null)
				{
					_context.PolicyAssignments.Add(new PolicyAssignments
					{
						EmployeeID       = emp.ID,
						LeavePolicyTypeID = model.PolicyID.Value
					});
				}
				else
				{
					assignment.LeavePolicyTypeID = model.PolicyID.Value;
				}
				await _context.SaveChangesAsync();
			}

			// reload with assignments to get final PolicyID
			await _context.Entry(emp).Collection(e => e.policyAssignments).LoadAsync();
			var policyId = emp.policyAssignments?.FirstOrDefault()?.LeavePolicyTypeID;
			return MapToViewModel(emp, policyId);
		}

		public async Task<ViewModel.EmployeeViewModel> SaveEmployeeImageAsync(
			int employeeId,
			Microsoft.AspNetCore.Http.IFormFile profileImage,
			string webRootPath)
		{
			if (profileImage == null || profileImage.Length == 0) throw new ArgumentNullException(nameof(profileImage));
			var emp = await _context.Employee.FirstOrDefaultAsync(e => e.ID == employeeId);
			if (emp == null) throw new KeyNotFoundException("Employee not found");

			if (!string.IsNullOrEmpty(webRootPath))
			{
				var uploads = System.IO.Path.Combine(webRootPath, "uploads", "employees");
				if (!System.IO.Directory.Exists(uploads)) System.IO.Directory.CreateDirectory(uploads);
				var fileName = Guid.NewGuid() + System.IO.Path.GetExtension(profileImage.FileName);
				using (var stream = System.IO.File.Create(System.IO.Path.Combine(uploads, fileName)))
					await profileImage.CopyToAsync(stream);
				emp.ProfileImage = "/uploads/employees/" + fileName;
				await _context.SaveChangesAsync();
			}
			return MapToViewModel(emp);
		}
	}
}
