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

		public HrApiController(CrossDbContext context)
		{
			_context = context;
		}

		// GET /api/hr/summary — dashboard counts
		[HttpGet("summary")]
		public async Task<IActionResult> Summary()
		{
			return Ok(new
			{
				success = true,
				employees = await _context.Employee.CountAsync(),
				activeEmployees = await _context.Employee.CountAsync(e => e.IsActive),
				companies = await _context.Companies.CountAsync(),
				branches = await _context.Branches.CountAsync(),
				jobTitles = await _context.JobTitles.CountAsync(),
				orgNodes = await _context.Hierarchicals.CountAsync(),
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
