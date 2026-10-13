using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class EmployeeService : IEmployeeService
	{
		private readonly CrossDbContext _context;

		public EmployeeService(CrossDbContext context, CrossBuy.BL.Platform.IBusinessContextAccessor contexts)
		{
			_context = context;
			_contexts = contexts;
		}

		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
		private const string EmployeeNotFoundOrNotYours = "Employee not found.";

		private async Task<int?> ResolveCallerCompanyAsync()
		{
			var ctx = await _contexts.TryGetCurrentAsync();
			return ctx is not null && ctx.CompanyId > 0 ? ctx.CompanyId : null;
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

		// =========================================================================================
		// THE EMPLOYEE DIRECTORY IS ONE COMPANY'S, NOT EVERY COMPANY'S.
		//
		// This read had no company predicate at all, and Employee is listed in
		// CompanyQueryFilters.DeliberatelyUnfiltered — "the company is resolved FROM it, filtering is
		// circular" — so the global filter does not cover it either. Nothing stood between a signed-in
		// user and every tenant's staff directory: name, job title, company, branch, email and phone.
		// Measured, not inferred: the administrator of a company holding exactly ONE employee opened
		// this screen and read ten people from two other companies.
		//
		// AN UNRESOLVED CALLER SEES NOBODY. This is a read, so there is no row whose own company could
		// stand in for the caller's the way SaveEmployeeAsync lets an existing employee keep theirs.
		// Both callers are HTTP screens that already render an empty list, so empty is the honest
		// answer rather than a degraded one.
		// =========================================================================================
		public async Task<List<ViewModel.EmployeeListItemDto>> GetAllAsync()
		{
			var callerCompany = await ResolveCallerCompanyAsync();
			if (callerCompany is null) return new List<ViewModel.EmployeeListItemDto>();

			return await _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == callerCompany.Value)
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

			// The caller's company, when a request context resolves at all.
			var callerCompany = await ResolveCallerCompanyAsync();

			// A form that names a DIFFERENT company than the caller's is refused rather than quietly
			// ignored. Silently overwriting it would make an attempt to write into another tenant
			// indistinguishable from an ordinary save, both to the caller and in the logs.
			if (callerCompany is not null && model.EmpCompanyID.HasValue
				&& model.EmpCompanyID.Value != callerCompany.Value)
				throw new KeyNotFoundException(EmployeeNotFoundOrNotYours);

			// resolve existing employee: by ID → UserId → Email, WITHIN the caller's company when one
			// resolved. Every one of these lookups used to span all companies unconditionally, so
			// naming another tenant's row by id, user or address was enough to load and overwrite it.
			Employee emp = null;
			if (model.ID > 0)
			{
				emp = await _context.Employee.Include(e => e.policyAssignments)
					.FirstOrDefaultAsync(e => e.ID == model.ID
						&& (callerCompany == null || e.EmpCompanyID == callerCompany.Value));

				// Naming an id that exists in another company must be refused, not treated as "no such
				// employee" — otherwise the save would fall through and CREATE a row here instead.
				if (emp == null && await _context.Employee.AnyAsync(e => e.ID == model.ID))
					throw new KeyNotFoundException(EmployeeNotFoundOrNotYours);
			}
			if (emp == null && !string.IsNullOrEmpty(model.UserId))
				emp = await _context.Employee.Include(e => e.policyAssignments)
					.FirstOrDefaultAsync(e => e.UserId == model.UserId
						&& (callerCompany == null || e.EmpCompanyID == callerCompany.Value));
			if (emp == null && !string.IsNullOrEmpty(model.Email))
				emp = await _context.Employee.Include(e => e.policyAssignments)
					.FirstOrDefaultAsync(e => e.Email == model.Email
						&& (callerCompany == null || e.EmpCompanyID == callerCompany.Value));

			var isNew = emp == null;

			// With no resolved context there is no caller company to stamp. An existing employee keeps
			// the company already on their row; a brand-new one has nothing safe to take, so it stops.
			if (callerCompany is null)
			{
				if (isNew)
					throw new InvalidOperationException(
						"No company could be resolved for the caller, so a new employee may not be created.");

				// A form naming a company other than the employee's own is still a move attempt, and is
				// still refused — the "cannot move an employee between companies" rule does not depend
				// on a request context existing.
				if (model.EmpCompanyID.HasValue && model.EmpCompanyID.Value != emp.EmpCompanyID)
					throw new KeyNotFoundException(EmployeeNotFoundOrNotYours);
			}
			if (isNew)
			{
				emp = new Employee();

				// A NEW EMPLOYEE STARTS ACTIVE. Nothing set IsActive on this path, so every employee
				// created through the screen was saved inactive — and the form has no field to change
				// it, so there was no way back. An inactive employee is refused a grant
				// ("Employee N is inactive and cannot receive a new grant"), which is how this surfaced:
				// a brand-new hire could be created and then given no role at all.
				//
				// Only on CREATE: an existing employee keeps whatever IsActive they already had, so
				// editing somebody's phone number cannot silently reactivate a leaver.
				emp.IsActive = true;

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

			// THE COMPANY IS STAMPED, NOT ACCEPTED. This line used to read
			// `if (model.EmpCompanyID.HasValue) emp.EmpCompanyID = model.EmpCompanyID.Value;`, which let
			// the posted form decide the tenant on create AND on update — so an employee could also be
			// MOVED out of the caller's company. It is the resolved company on both paths now; with no
			// context the row keeps what it already had. A mismatch was refused above either way.
			if (callerCompany is not null) emp.EmpCompanyID = callerCompany.Value;
			// previously dropped on save — needed by the hire→employee flow (all nullable, non-breaking)
			if (!string.IsNullOrWhiteSpace(model.FullNameEn)) emp.FullNameEn = model.FullNameEn;
			if (model.CountryID.HasValue)     emp.CountryID    = model.CountryID.Value;
			if (model.DepartmentID.HasValue)  emp.DepartmentID = model.DepartmentID.Value;
			if (!string.IsNullOrWhiteSpace(model.EmploymentType)) emp.EmploymentType = model.EmploymentType;

			// handle profile image
			if (profileImage != null && profileImage.Length > 0 && !string.IsNullOrEmpty(webRootPath))
			{
				emp.ProfileImage = await StoreProfileImageAsync(profileImage, webRootPath);
			}
			if (string.IsNullOrEmpty(emp.ProfileImage)) emp.ProfileImage = string.Empty;   // NOT NULL column — default empty when no image uploaded

			try
			{
				await _context.SaveChangesAsync();
			}
			catch (Microsoft.EntityFrameworkCore.DbUpdateException)
			{
				// unique constraint hit — return the existing record
				// Scoped like the lookups above: the recovery path must not hand back another company's
				// employee record just because a unique constraint happened to collide with it.
				Employee existing = null;
				if (!string.IsNullOrEmpty(model.UserId))
					existing = await _context.Employee.Include(e => e.policyAssignments)
						.FirstOrDefaultAsync(e => e.UserId == model.UserId
							&& (callerCompany == null || e.EmpCompanyID == callerCompany.Value));
				if (existing == null && !string.IsNullOrEmpty(model.Email))
					existing = await _context.Employee.Include(e => e.policyAssignments)
						.FirstOrDefaultAsync(e => e.Email == model.Email
							&& (callerCompany == null || e.EmpCompanyID == callerCompany.Value));
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
				emp.ProfileImage = await StoreProfileImageAsync(profileImage, webRootPath);
				await _context.SaveChangesAsync();
			}
			return MapToViewModel(emp);
		}

		// ONE place the photo is written, because the failure mode is the same at both call sites and it is
		// not a programming error: on a deployed server the application pool identity frequently has no write
		// right on wwwroot\uploads. The raw exception says "Access to the path 'C:\inetpub\...' is denied",
		// which reaches the browser through SaveEmployee's `ex.Message` — it publishes the server's absolute
		// layout to anyone who can open the screen, and it tells the person reading it nothing they can act on.
		// Rethrown as one sentence naming the RELATIVE folder and the fix.
		private static async Task<string> StoreProfileImageAsync(
			Microsoft.AspNetCore.Http.IFormFile profileImage, string webRootPath)
		{
			var uploads = System.IO.Path.Combine(webRootPath, "uploads", "employees");
			var fileName = Guid.NewGuid() + System.IO.Path.GetExtension(profileImage.FileName);
			try
			{
				if (!System.IO.Directory.Exists(uploads)) System.IO.Directory.CreateDirectory(uploads);
				using var stream = System.IO.File.Create(System.IO.Path.Combine(uploads, fileName));
				await profileImage.CopyToAsync(stream);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
			{
				throw new InvalidOperationException(
					"The employee photo could not be saved: the server folder wwwroot/uploads/employees is not writable. "
					+ "Grant the application pool identity Modify rights on that folder, then save again.", ex);
			}
			return "/uploads/employees/" + fileName;
		}
	}
}
