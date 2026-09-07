using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CrossBuy.BL;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/employees")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	// =============================================================================================
	// THE TENANT BOUNDARY ON THIS FILE, and why it has to be written out by hand.
	//
	// Employee is DELIBERATELY excluded from the platform's global company query filters — the company
	// is resolved FROM the employee row, so filtering it would be circular
	// (CompanyQueryFilters.DeliberatelyUnfiltered names it with exactly that reason). That decision is
	// sound, and it is precisely why the two reads below must carry their own predicate: there is no
	// ambient net under an Employee read to catch a missing one.
	//
	// What was here before, measured rather than remembered — a two-company fixture, one action call
	// each:
	//
	//     List(no companyId)  returned the neighbour's employees   → the whole installation
	//     List(companyId=77)  returned the neighbour's employees   → the CALLER chose the tenant
	//     Detail(foreign id)  returned 200 with address and DOB    → an integer was the authorisation
	//
	// The company now comes from the resolved BusinessContext and from nowhere else.
	// =============================================================================================
	public class EmployeesApiController : ControllerBase
	{
		private readonly CrossDbContext _context;
		private readonly IBusinessContextAccessor _contexts;

		public EmployeesApiController(CrossDbContext context, IBusinessContextAccessor contexts)
		{
			_context = context;
			_contexts = contexts;
		}

		// GET /api/employees?search=&companyId=   — the employees directory (HR module)
		//
		// `companyId` SURVIVES ONLY AS A NARROWING FILTER, never as authority. Removing the parameter
		// outright would break existing callers that pass their own company, so it is kept and made
		// powerless: a value that is not the resolved company selects nothing, which is the honest
		// answer to "show me a company that is not yours".
		[HttpGet]
		public async Task<IActionResult> List(string? search = null, int? companyId = null)
		{
			var context = await _contexts.TryGetCurrentAsync(HttpContext?.RequestAborted ?? default);

			// FAIL CLOSED. An unresolved company is the state a broken session or a background call
			// lands in, and the safe answer there is an empty directory, never the whole one.
			if (context is not { CompanyId: > 0 })
				return Ok(new { success = true, count = 0, data = Array.Empty<object>() });

			// THE PREDICATE, unconditional. It is not inside an `if` any more, which is what made the
			// old code's omission case return everything.
			var q = _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == context.CompanyId);

			// A caller-supplied company can only ever NARROW, and only within what is already theirs.
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
				.OrderByDisplayName()
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
		// GET /api/employees/{id} — the personal record: date of birth, address, marital status, phone.
		//
		// THE COMPANY IS IN THE WHERE CLAUSE, not in a check after the read. Loading globally and then
		// comparing would still have materialised the row — and a later refactor that logged it, cached
		// it or returned it on an error path would leak it with nobody noticing the check was cosmetic.
		[HttpGet("{id:int}")]
		public async Task<IActionResult> Detail(int id)
		{
			var context = await _contexts.TryGetCurrentAsync(HttpContext?.RequestAborted ?? default);
			if (context is not { CompanyId: > 0 }) return NotFoundEmployee();

			var e = await _context.Employee.AsNoTracking()
				.Where(x => x.ID == id && x.EmpCompanyID == context.CompanyId)
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

			// ONE ANSWER FOR BOTH. A foreign employee and a missing employee return the identical body,
			// so this endpoint cannot be used to ask "does an id exist somewhere in the installation?".
			// Answering "forbidden" for one and "not found" for the other would be a tenant-existence
			// oracle that needs no further exploit.
			if (e == null) return NotFoundEmployee();
			return Ok(new { success = true, data = e });
		}

		private NotFoundObjectResult NotFoundEmployee()
			=> NotFound(new { success = false, message = "Employee not found" });
	}
}
