using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/employees")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class EmployeesApiController : ControllerBase
	{
		private readonly CrossDbContext _context;

		public EmployeesApiController(CrossDbContext context)
		{
			_context = context;
		}

		// GET /api/employees?search=&companyId=   — the employees directory (HR module)
		[HttpGet]
		public async Task<IActionResult> List(string? search = null, int? companyId = null)
		{
			var q = _context.Employee.AsNoTracking();
			if (companyId.HasValue && companyId.Value > 0)
				q = q.Where(e => e.EmpCompanyID == companyId.Value);
			if (!string.IsNullOrWhiteSpace(search))
			{
				var s = search.Trim();
				q = q.Where(e =>
					(e.FullName != null && e.FullName.Contains(s)) ||
					(e.FullNameEn != null && e.FullNameEn.Contains(s)) ||
					(e.Email != null && e.Email.Contains(s)));
			}

			var data = await q
				.OrderBy(e => e.FullName)
				.Select(e => new
				{
					id = e.ID,
					fullNameAr = e.FullName,
					fullNameEn = e.FullNameEn,
					email = e.Email,
					phoneNumber = e.PhoneNumber,
					isActive = e.IsActive,
					profileImage = e.ProfileImage,
					jobTitleAr = e.JobTitle.TitleAr,
					jobTitleEn = e.JobTitle.Title,
					companyAr = e.Company.ComoanyNameAr,
					companyEn = e.Company.CompanyName,
					branchAr = e.Branch.NameAr,
					branchEn = e.Branch.Name
				})
				.ToListAsync();

			return Ok(new { success = true, count = data.Count, data });
		}

		// GET /api/employees/{id}  — full detail for one employee
		[HttpGet("{id:int}")]
		public async Task<IActionResult> Detail(int id)
		{
			var e = await _context.Employee.AsNoTracking()
				.Where(x => x.ID == id)
				.Select(e => new
				{
					id = e.ID,
					fullNameAr = e.FullName,
					fullNameEn = e.FullNameEn,
					firstName = e.FirstName,
					lastName = e.LastName,
					email = e.Email,
					phoneNumber = e.PhoneNumber,
					address = e.Address,
					gender = e.Gender,
					maritalStatus = e.MaritalStatus,
					dateOfBirth = e.DateOfBirth,
					dateOfJoining = e.DateOfJoining,
					profileImage = e.ProfileImage,
					isActive = e.IsActive,
					jobTitleAr = e.JobTitle.TitleAr,
					jobTitleEn = e.JobTitle.Title,
					companyAr = e.Company.ComoanyNameAr,
					companyEn = e.Company.CompanyName,
					branchAr = e.Branch.NameAr,
					branchEn = e.Branch.Name
				})
				.FirstOrDefaultAsync();

			if (e == null) return NotFound(new { success = false, message = "الموظف غير موجود" });
			return Ok(new { success = true, data = e });
		}
	}
}
