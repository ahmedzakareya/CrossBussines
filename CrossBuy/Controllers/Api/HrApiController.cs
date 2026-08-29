using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/hr")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class HrApiController : ControllerBase
	{
		private readonly CrossDbContext _context;
		private readonly IBusinessContextAccessor _contexts;

		public HrApiController(CrossDbContext context, IBusinessContextAccessor contexts)
		{
			_context = context;
			_contexts = contexts;
		}

		// GET /api/hr/summary — dashboard counts, for the RESOLVED company only.
		//
		// Every count here used to be unqualified, so one tenant's dashboard reported the installation:
		// a two-company fixture returned employees=2, companies=2, jobTitles=1 to a caller who owned one
		// employee. A count is a disclosure like any other — "how many people work at the other company"
		// is not this caller's to know.
		//
		// `companies` is now 0 or 1 BY DEFINITION: a caller belongs to exactly one company, so the only
		// honest answer is their own. It is kept in the payload rather than dropped so existing callers
		// keep their shape.
		[HttpGet("summary")]
		public async Task<IActionResult> Summary()
		{
			var context = await _contexts.TryGetCurrentAsync(HttpContext?.RequestAborted ?? default);

			// Fail closed: zeros, not totals.
			if (context is not { CompanyId: > 0 })
				return Ok(new { success = true, employees = 0, activeEmployees = 0,
					companies = 0, branches = 0, jobTitles = 0, orgNodes = 0 });

			int companyId = context.CompanyId;

			// Hierarchicals carries NO company column of its own — the org tree is bounded through the
			// employee row, which is the control OrgHierarchy already relies on (it walks the tree and
			// intersects candidates with Employee.EmpCompanyID). The count follows the same boundary
			// rather than inventing a second one: nodes of type 5 are employee nodes whose H_ObjectID is
			// an Employee.ID, so the company is the employee's.
			// int? on both sides: H_ObjectID is nullable, so the projection is too and a NULL node simply
			// does not match — which is correct, an unplaced node belongs to no company.
			var employeeNodeIds = _context.Employee
				.Where(e => e.EmpCompanyID == companyId)
				.Select(e => (int?)e.ID);

			return Ok(new
			{
				success = true,
				employees = await _context.Employee.CountAsync(e => e.EmpCompanyID == companyId),
				activeEmployees = await _context.Employee.CountAsync(e => e.EmpCompanyID == companyId && e.IsActive),
				companies = await _context.Companies.CountAsync(c => c.CompanyID == companyId),
				branches = await _context.Branches.CountAsync(b => b.CompanyID == companyId),
				jobTitles = await _context.JobTitles.CountAsync(),
				orgNodes = await _context.Hierarchicals
					.CountAsync(h => h.H_Type == 5 && employeeNodeIds.Contains(h.H_ObjectID)),
			});
		}

		// ---------- Job Titles (full CRUD) ----------
		public class JobTitleInput
		{
			public string? Title { get; set; }
			public string? TitleAr { get; set; }
			public string? Description { get; set; }
		}

		[HttpGet("jobtitles")]
		public async Task<IActionResult> JobTitles()
		{
			var data = await _context.JobTitles.AsNoTracking()
				.OrderBy(j => j.ID)
				.Select(j => new { id = j.ID, titleEn = j.Title, titleAr = j.TitleAr, description = j.Description,
					employees = _context.Employee.Count(e => e.JobTitleID == j.ID) })
				.ToListAsync();
			return Ok(new { success = true, count = data.Count, data });
		}

		[HttpPost("jobtitles")]
		public async Task<IActionResult> CreateJobTitle([FromBody] JobTitleInput m)
		{
			if (m == null || (string.IsNullOrWhiteSpace(m.Title) && string.IsNullOrWhiteSpace(m.TitleAr)))
				return BadRequest(new { success = false, message = "الاسم مطلوب" });
			var jt = new JobTitle { Title = m.Title ?? "", TitleAr = m.TitleAr ?? "", Description = m.Description ?? "" };
			_context.JobTitles.Add(jt);
			await _context.SaveChangesAsync();
			return Ok(new { success = true, id = jt.ID });
		}

		[HttpPut("jobtitles/{id:int}")]
		public async Task<IActionResult> UpdateJobTitle(int id, [FromBody] JobTitleInput m)
		{
			var jt = await _context.JobTitles.FirstOrDefaultAsync(j => j.ID == id);
			if (jt == null) return NotFound(new { success = false, message = "غير موجود" });
			jt.Title = m.Title ?? jt.Title;
			jt.TitleAr = m.TitleAr ?? jt.TitleAr;
			jt.Description = m.Description ?? jt.Description;
			await _context.SaveChangesAsync();
			return Ok(new { success = true });
		}

		// ---------- Companies (read) ----------
		[HttpGet("companies")]
		public async Task<IActionResult> Companies()
		{
			var data = await _context.Companies.AsNoTracking()
				.OrderBy(c => c.CompanyID)
				.Select(c => new { id = c.CompanyID, nameEn = c.CompanyName, nameAr = c.ComoanyNameAr,
					email = c.Email, phone = c.PhoneNumber,
					branches = _context.Branches.Count(b => b.CompanyID == c.CompanyID),
					employees = _context.Employee.Count(e => e.EmpCompanyID == c.CompanyID) })
				.ToListAsync();
			return Ok(new { success = true, count = data.Count, data });
		}

		// ---------- Branches (read) ----------
		[HttpGet("branches")]
		public async Task<IActionResult> Branches()
		{
			var data = await _context.Branches.AsNoTracking()
				.OrderBy(b => b.ID)
				.Select(b => new { id = b.ID, nameEn = b.Name, nameAr = b.NameAr,
					email = b.Email, phone = b.PhoneNumber, location = b.Location,
					companyAr = b.Company.ComoanyNameAr, companyEn = b.Company.CompanyName })
				.ToListAsync();
			return Ok(new { success = true, count = data.Count, data });
		}
	}
}
