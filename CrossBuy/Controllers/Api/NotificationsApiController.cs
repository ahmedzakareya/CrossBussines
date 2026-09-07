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
	[Route("api/notifications")]
	[Produces("application/json")]
	// accepts both the web Identity cookie and the mobile JWT
	[Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
	public class NotificationsApiController : ControllerBase
	{
		private readonly IEmployeeService _employeeService;
		private readonly CrossDbContext _context;

		public NotificationsApiController(IEmployeeService employeeService, CrossDbContext context)
		{
			_employeeService = employeeService;
			_context = context;
		}

		private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

		private async Task<int?> CurrentEmployeeIdAsync()
		{
			var uid = CurrentUserId;
			if (string.IsNullOrEmpty(uid)) return null;
			var emp = await _employeeService.GetEmployeeByUserIdAsync(uid);
			return emp?.ID;
		}

		// GET /api/notifications — my latest notifications + unread count
		[HttpGet]
		public async Task<IActionResult> List()
		{
			var empId = await CurrentEmployeeIdAsync();
			if (empId == null) return Unauthorized();

			var items = await _context.Notifications.AsNoTracking()
				.Where(n => n.RecipientEmployeeID == empId.Value)
				.OrderByDescending(n => n.ID)
				.Take(50)
				.Select(n => new
				{
					id = n.ID,
					titleAr = n.TitleAr,
					titleEn = n.TitleEn,
					bodyAr = n.BodyAr,
					bodyEn = n.BodyEn,
					type = n.Type,
					refId = n.RefId,
					isRead = n.IsRead,
					createdAt = n.CreatedAt,
					url = n.Url,
					category = n.Category,
					icon = n.Icon,
					priority = n.Priority,
					actorAvatar = _context.Employee.Where(e => e.ID == n.ActorEmployeeID).Select(e => e.ProfileImage).FirstOrDefault(),
				})
				.ToListAsync();

			var unread = await _context.Notifications
				.CountAsync(n => n.RecipientEmployeeID == empId.Value && !n.IsRead);

			// HOW MANY EXIST, not just how many were sent. The Take(50) above is a hard cap on what
			// the bell can ever display, and without this number the dropdown cannot tell the reader
			// that it is showing a window rather than the whole mailbox - which is how a badge reading
			// "99+" came to sit above a list of six.
			var total = await _context.Notifications
				.CountAsync(n => n.RecipientEmployeeID == empId.Value);

			return Ok(new { success = true, unread, total, shown = items.Count, data = items });
		}

		// POST /api/notifications/{id}/read
		[HttpPost("{id:int}/read")]
		public async Task<IActionResult> MarkRead(int id)
		{
			var empId = await CurrentEmployeeIdAsync();
			if (empId == null) return Unauthorized();

			var n = await _context.Notifications.FirstOrDefaultAsync(x => x.ID == id && x.RecipientEmployeeID == empId.Value);
			if (n == null) return NotFound();
			if (!n.IsRead) { n.IsRead = true; n.ReadAt = DateTime.UtcNow; await _context.SaveChangesAsync(); }
			return Ok(new { success = true });
		}

		// POST /api/notifications/read-all
		[HttpPost("read-all")]
		public async Task<IActionResult> MarkAllRead()
		{
			var empId = await CurrentEmployeeIdAsync();
			if (empId == null) return Unauthorized();

			var unread = await _context.Notifications
				.Where(n => n.RecipientEmployeeID == empId.Value && !n.IsRead).ToListAsync();
			var now = DateTime.UtcNow;
			foreach (var n in unread) { n.IsRead = true; n.ReadAt = now; }
			if (unread.Count > 0) await _context.SaveChangesAsync();
			return Ok(new { success = true, marked = unread.Count });
		}
	}
}
