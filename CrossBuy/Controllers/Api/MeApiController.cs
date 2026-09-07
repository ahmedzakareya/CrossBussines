using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/me")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class MeApiController : ControllerBase
	{
		private readonly IEmployeeService _employeeService;
		private readonly IAdministrativeStructureService _structureService;
		private readonly CrossDbContext _context;
		private readonly ILeaveDashboardService _dashboard;

		public MeApiController(
			IEmployeeService employeeService,
			IAdministrativeStructureService structureService,
			CrossDbContext context,
			ILeaveDashboardService dashboard)
		{
			_employeeService = employeeService;
			_structureService = structureService;
			_context = context;
			_dashboard = dashboard;
		}

		// GET /api/me/dashboard — leave balances per type + self-service stats
		[HttpGet("dashboard")]
		public async Task<IActionResult> Dashboard()
		{
			var userId = CurrentUserId;
			if (string.IsNullOrEmpty(userId)) return Unauthorized();
			var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
			if (emp == null) return NotFound(new { success = false, message = "Employee not found" });

			var d = await _dashboard.BuildAsync(emp.ID);
			return Ok(new
			{
				success = true,
				data = new
				{
					hasPolicy = d.HasPolicy,
					policyAr = d.PolicyNameAr,
					policyEn = d.PolicyNameEn,
					myPending = d.MyPending,
					myApprovedThisYear = d.MyApprovedThisYear,
					pendingApprovals = d.PendingApprovals,
					teamSize = d.TeamSize,
					total = d.Total,
					approved = d.Approved,
					pending = d.Pending,
					rejected = d.Rejected,
					thisMonth = d.ThisMonth,
					lastMonth = d.LastMonth,
					pctChange = d.PctChange,
					byType = d.ByType.Select(s => new { s.LeaveTypeId, s.NameAr, s.NameEn, s.Count, s.Pct }),
					monthly = d.Monthly.Select(m => new { m.Year, m.Month, m.Count }),
					balances = d.Balances.Select(b => new
					{
						leaveTypeId = b.LeaveTypeId,
						nameAr = b.NameAr,
						nameEn = b.NameEn,
						entitlement = b.Entitlement,
						used = b.Used,
						remaining = b.Remaining
					})
				}
			});
		}

		private string? CurrentUserId =>
			User.FindFirstValue(ClaimTypes.NameIdentifier);

		// GET /api/me/documents — the employee's attached documents
		[HttpGet("documents")]
		public async Task<IActionResult> Documents()
		{
			var userId = CurrentUserId;
			if (string.IsNullOrEmpty(userId)) return Unauthorized();
			var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
			if (emp == null) return NotFound(new { success = false, message = "Employee not found" });

			var docs = await _context.Attachments.AsNoTracking()
				.Where(a => a.EmployeeID == emp.ID)
				.OrderByDescending(a => a.Id)
				.Select(a => new
				{
					id = a.Id,
					name = a.AttachName,
					path = a.attachPath,
					uploadedAt = a.CreatedAt
				})
				.ToListAsync();
			return Ok(new { success = true, data = docs });
		}

		// GET /api/me/activity — recent real activity derived from the employee's leave requests
		[HttpGet("activity")]
		public async Task<IActionResult> Activity()
		{
			var userId = CurrentUserId;
			if (string.IsNullOrEmpty(userId)) return Unauthorized();
			var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
			if (emp == null) return NotFound(new { success = false, message = "Employee not found" });

			var reqs = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Where(r => r.EmployeeID == emp.ID)
				.OrderByDescending(r => r.ID)
				.Take(15)
				.ToListAsync();

			var raw = new List<(DateTime at, object item)>();
			foreach (var r in reqs)
			{
				var tAr = r.LeaveType?.NameAr ?? "إجازة";
				var tEn = r.LeaveType?.NameEn ?? "leave";
				var created = r.CreatedAt ?? r.StartDate;
				raw.Add((created, new
				{
					type = "leave_submitted",
					titleAr = "تقديم طلب إجازة",
					titleEn = "Leave request submitted",
					descAr = $"طلب {tAr} لمدة {r.Days} يوم",
					descEn = $"{tEn} request for {r.Days} day(s)",
					at = created
				}));
				if (r.Status != 0 && r.DecisionAt.HasValue)
				{
					raw.Add((r.DecisionAt.Value, new
					{
						type = r.Status == 1 ? "leave_approved" : "leave_rejected",
						titleAr = r.Status == 1 ? "تمت الموافقة على إجازة" : "تم رفض طلب إجازة",
						titleEn = r.Status == 1 ? "Leave approved" : "Leave rejected",
						descAr = $"طلب {tAr} ({r.Days} يوم)",
						descEn = $"{tEn} request ({r.Days} day(s))",
						at = r.DecisionAt.Value
					}));
				}
			}
			var ordered = raw.OrderByDescending(x => x.at).Take(6).Select(x => x.item);
			return Ok(new { success = true, data = ordered });
		}

		// GET /api/me/profile  — the logged-in employee's full data (with lookup names)
		[HttpGet("profile")]
		public async Task<IActionResult> Profile()
		{
			var userId = CurrentUserId;
			if (string.IsNullOrEmpty(userId)) return Unauthorized();

			var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
			if (emp == null) return NotFound(new { success = false, message = "Employee not found" });

			var jobTitle = emp.JobTitleID.HasValue
				? await _context.JobTitles.AsNoTracking().FirstOrDefaultAsync(j => j.ID == emp.JobTitleID.Value)
				: null;
			var company = emp.EmpCompanyID.HasValue
				? await _context.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == emp.EmpCompanyID.Value)
				: null;
			var branch = emp.BranchID.HasValue
				? await _context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == emp.BranchID.Value)
				: null;

			// entity for the fields not on the view model (status, employment type, department)
			var ent = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == emp.ID);
			var dept = (ent != null && ent.DepartmentID.HasValue)
				? await _context.Hierarchicals.AsNoTracking().FirstOrDefaultAsync(h => h.H_ID == ent.DepartmentID.Value)
				: null;

			return Ok(new
			{
				success = true,
				data = new
				{
					emp.ID,
					emp.FirstName,
					emp.LastName,
					emp.FullName,
					emp.FullNameEn,
					emp.Email,
					emp.PhoneNumber,
					emp.Address,
					emp.Gender,
					emp.MaritalStatus,
					emp.ProfileImage,
					emp.DateOfBirth,
					emp.DateOfJoining,
					isActive = ent?.IsActive ?? true,
					employmentType = ent?.EmploymentType,
					departmentAr = dept?.H_Name,
					departmentEn = dept?.H_NameEn,
					jobTitleId = emp.JobTitleID,
					jobTitleAr = jobTitle?.TitleAr,
					jobTitleEn = jobTitle?.Title,
					companyId = emp.EmpCompanyID,
					companyAr = company?.ComoanyNameAr,
					companyEn = company?.CompanyName,
					branchId = emp.BranchID,
					branchAr = branch?.NameAr,
					branchEn = branch?.Name
				}
			});
		}

		// GET /api/me/position — the employee's place in the org tree:
		// the chain above them (company › branch › … › position) and the people directly under them.
		[HttpGet("position")]
		public async Task<IActionResult> Position()
		{
			var userId = CurrentUserId;
			if (string.IsNullOrEmpty(userId)) return Unauthorized();

			var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
			if (emp == null) return NotFound(new { success = false, message = "Employee not found" });

			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var typeNames = await _context.HierarchicalTypes.AsNoTracking()
				.ToDictionaryAsync(t => t.ID, t => new { t.TypeNameAr, t.TypeNameEn });

			// my own node: an Employee-type node (5) pointing at my employee id
			var myNode = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == emp.ID);
			if (myNode == null)
				return Ok(new { success = true, placed = false });

			// image lookups per node type (company logo / branch logo / employee photo)
			var compImgs = await _context.Companies.AsNoTracking()
				.Where(c => c.CompanyImage != null && c.CompanyImage != "")
				.ToDictionaryAsync(c => c.CompanyID, c => c.CompanyImage);
			var branchImgs = await _context.Branches.AsNoTracking()
				.Where(b => b.ImageUrl != null && b.ImageUrl != "")
				.ToDictionaryAsync(b => b.ID, b => b.ImageUrl);
			var empImgs = await _context.Employee.AsNoTracking()
				.Where(e => e.ProfileImage != null && e.ProfileImage != "")
				.ToDictionaryAsync(e => e.ID, e => e.ProfileImage);

			static string? NormImg(string? p)
			{
				if (string.IsNullOrEmpty(p)) return null;
				p = p.Replace("\\", "/");
				if (!p.StartsWith("/") && !p.StartsWith("http")) p = "/" + p;
				return p;
			}

			string? ImageFor(Models.Context.Admin.Hierarchical h)
			{
				if (h.H_ObjectID == null) return null;
				var oid = h.H_ObjectID.Value;
				if (h.H_Type == 1 && compImgs.TryGetValue(oid, out var ci)) return NormImg(ci);
				if (h.H_Type == 2 && branchImgs.TryGetValue(oid, out var bi)) return NormImg(bi);
				if (h.H_Type == 5 && empImgs.TryGetValue(oid, out var pi)) return NormImg(pi);
				return null;
			}

			var byId = all.ToDictionary(h => h.H_ID);
			object NodeDto(Models.Context.Admin.Hierarchical h) => new
			{
				id = h.H_ID,
				nameAr = h.H_Name,
				nameEn = h.H_NameEn,
				type = h.H_Type,
				typeNameAr = h.H_Type.HasValue && typeNames.ContainsKey(h.H_Type.Value) ? typeNames[h.H_Type.Value].TypeNameAr : null,
				typeNameEn = h.H_Type.HasValue && typeNames.ContainsKey(h.H_Type.Value) ? typeNames[h.H_Type.Value].TypeNameEn : null,
				image = ImageFor(h),
			};

			// walk up to the root, then reverse so it reads top-down (company first)
			var chain = new List<Models.Context.Admin.Hierarchical>();
			var cursor = myNode.H_Parent;
			var guard = 0;
			while (cursor.HasValue && byId.ContainsKey(cursor.Value) && guard++ < 50)
			{
				var node = byId[cursor.Value];
				chain.Add(node);
				cursor = node.H_Parent;
			}
			chain.Reverse();

			// my immediate position = my parent node (type 4)
			var positionNode = myNode.H_Parent.HasValue && byId.ContainsKey(myNode.H_Parent.Value)
				? byId[myNode.H_Parent.Value] : null;

			// people directly under me: employees sitting under my child positions
			var myChildren = all.Where(h => h.H_Parent == myNode.H_ID).ToList();
			var subordinates = new List<object>();
			foreach (var childPos in myChildren.OrderBy(c => c.Sort ?? 0))
			{
				var people = all.Where(h => h.H_Parent == childPos.H_ID && h.H_Type == 5)
					.OrderBy(p => p.Sort ?? 0);
				foreach (var p in people)
					subordinates.Add(new
					{
						id = p.H_ID,
						nameAr = p.H_Name,
						nameEn = p.H_NameEn,
						positionAr = childPos.H_Name,
						positionEn = childPos.H_NameEn,
						image = ImageFor(p)
					});
			}

			return Ok(new
			{
				success = true,
				placed = true,
				me = new { id = myNode.H_ID, nameAr = myNode.H_Name, nameEn = myNode.H_NameEn,
						   positionAr = positionNode?.H_Name, positionEn = positionNode?.H_NameEn,
						   image = ImageFor(myNode) },
				ancestors = chain.Select(NodeDto),
				subordinates
			});
		}

		// GET /api/me/structure  — the org hierarchy of the employee's company (flat list; client builds the tree)
		[HttpGet("structure")]
		public async Task<IActionResult> Structure()
		{
			var userId = CurrentUserId;
			if (string.IsNullOrEmpty(userId)) return Unauthorized();

			var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
			if (emp == null) return NotFound(new { success = false, message = "Employee not found" });

			var nodes = emp.EmpCompanyID.HasValue
				? await _structureService.GetByCompanyAsync(emp.EmpCompanyID.Value)
				: new List<ViewModel.HierarchicalDto>();

			// Best-effort: flag the node that represents the employee's branch, if present.
			var myNodeId = nodes
				.FirstOrDefault(n => emp.BranchID.HasValue && n.H_ObjectID == emp.BranchID.Value && n.H_Type == 2)?.H_ID;

			return Ok(new
			{
				success = true,
				myNodeId,
				companyId = emp.EmpCompanyID,
				nodes = nodes.Select(n => new
				{
					id = n.H_ID,
					parentId = n.H_Parent,
					nameAr = n.H_Name,
					nameEn = n.H_NameEn,
					type = n.H_Type,
					objectId = n.H_ObjectID,
					sort = n.Sort
				})
			});
		}
	}
}
