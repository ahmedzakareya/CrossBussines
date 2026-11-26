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

			// TWO INBOXES, ONE BELL.
			//
			// The kernel writes Notifications; the communication platform writes CommNotifications and
			// its own in-app channel treats that row AS the delivery ("the CommNotification row IS the
			// in-app notification"). Nothing bridged them, so every mention and comment reply landed in
			// a mailbox this bell never opened - the row existed, correct and unread, and nobody saw it.
			//
			// Read here rather than copied on write: a second producer into the kernel table is exactly
			// the "second notification path" the platform’s own dispatcher refuses to become, and it
			// would need the notification worker, whose activation is a deployment decision with an
			// explicit company allow-list.
			var kernel = await _context.Notifications.AsNoTracking()
				.Where(n => n.RecipientEmployeeID == empId.Value)
				.OrderByDescending(n => n.ID)
				.Take(50)
				.Select(n => new NotifyRow
				{
					Id = n.ID,
					Src = "k",
					TitleAr = n.TitleAr, TitleEn = n.TitleEn,
					BodyAr = n.BodyAr, BodyEn = n.BodyEn,
					Type = n.Type, RefId = n.RefId, IsRead = n.IsRead, CreatedAt = n.CreatedAt,
					Url = n.Url, Category = n.Category, Icon = n.Icon, Priority = n.Priority,
					ActorId = n.ActorEmployeeID,
				})
				.ToListAsync();

			// CommNotifications has no Icon column and marks read with a TIMESTAMP, not a flag - the
			// two shapes are reconciled here so the bell keeps one vocabulary.
			var comm = await CommInbox.Notifications.AsNoTracking()
				.Where(n => n.RecipientEmployeeId == empId.Value)
				.OrderByDescending(n => n.Id)
				.Take(50)
				.Select(n => new NotifyRow
				{
					Id = n.Id,
					Src = "c",
					TitleAr = n.TitleAr, TitleEn = n.TitleEn, TitleFr = n.TitleFr,
					BodyAr = n.BodyAr, BodyEn = n.BodyEn, BodyFr = n.BodyFr,
					Type = n.LegacyType, RefId = n.EntityId, IsRead = n.ReadAt != null, CreatedAt = n.CreatedAt,
					Url = n.Url, Category = n.Category, Icon = null, Priority = n.Priority,
					ActorId = n.ActorEmployeeId, CommentId = n.CommentId,
				})
				.ToListAsync();

			// WHAT WAS ACTUALLY SAID, as its own field. The body already contains it, but only as the
			// tail of one sentence - so the bell could not tell the frame ("X mentioned you in a
			// comment on Y:") from the MESSAGE, which is the part the reader is looking for. Returning
			// it separately lets the panel set it apart without guessing where the colon falls.
			var commentIds = comm.Where(r => r.CommentId != null).Select(r => r.CommentId!.Value).Distinct().ToList();
			var excerpts = commentIds.Count == 0
				? new Dictionary<long, string>()
				: await CommInbox.Comments.AsNoTracking()
					.Where(c => commentIds.Contains(c.Id) && c.DeletedAt == null)
					.Select(c => new { c.Id, c.Body })
					.ToDictionaryAsync(c => c.Id, c => c.Body ?? "");

			var actorIds = kernel.Concat(comm).Where(r => r.ActorId != null).Select(r => r.ActorId!.Value).Distinct().ToList();
			var avatars = actorIds.Count == 0
				? new Dictionary<int, string?>()
				: await _context.Employee.AsNoTracking().Where(e => actorIds.Contains(e.ID))
					.Select(e => new { e.ID, e.ProfileImage }).ToDictionaryAsync(e => e.ID, e => e.ProfileImage);

			// Merged on TIME, not on id: the two tables number independently, so ordering by id would
			// interleave them by coincidence.
			var items = kernel.Concat(comm)
				.OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue)
				.Take(50)
				.Select(r => new
				{
					id = r.Id,
					src = r.Src,
					titleAr = r.TitleAr,
					titleEn = r.TitleEn,
                    titleFr = r.TitleFr,
					bodyAr = r.BodyAr,
					bodyEn = r.BodyEn,
                    bodyFr = r.BodyFr,
					type = r.Type,
					refId = r.RefId,
					isRead = r.IsRead,
					createdAt = r.CreatedAt,
					url = r.Url,
					category = r.Category,
					// The KEY stays (the colour map needs it); the LABEL is what the badge prints.
					categoryLabel = CrossBuy.BL.NotificationTypes.CategoryLabel(r.Category),
					// The comm table stores no icon; the catalog already knows one per type.
					icon = r.Icon ?? CrossBuy.BL.NotificationTypes.Meta(r.Type).icon,
					priority = r.Priority,
					actorAvatar = r.ActorId != null && avatars.TryGetValue(r.ActorId.Value, out var pi) ? pi : null,
					// Trimmed here rather than in the panel: a 4000-character comment should not travel to
					// a dropdown that shows one line of it.
					excerpt = r.CommentId != null && excerpts.TryGetValue(r.CommentId.Value, out var ex)
						? (ex.Length > 160 ? ex.Substring(0, 160) : ex)
						: null,
				})
				.ToList();

			var unread = await _context.Notifications
				.CountAsync(n => n.RecipientEmployeeID == empId.Value && !n.IsRead)
				+ await CommInbox.Notifications
					.CountAsync(n => n.RecipientEmployeeId == empId.Value && n.ReadAt == null);

			// HOW MANY EXIST, not just how many were sent. The Take(50) above is a hard cap on what
			// the bell can ever display, and without this number the dropdown cannot tell the reader
			// that it is showing a window rather than the whole mailbox - which is how a badge reading
			// "99+" came to sit above a list of six.
			var total = await _context.Notifications
				.CountAsync(n => n.RecipientEmployeeID == empId.Value)
				+ await CommInbox.Notifications
					.CountAsync(n => n.RecipientEmployeeId == empId.Value);

			return Ok(new { success = true, unread, total, shown = items.Count, data = items });
		}

		// POST /api/notifications/{id}/read?src=k|c
		//
		// The id ALONE is ambiguous now: the two tables number independently, so id 8 exists in both
		// and means different things. `src` says which mailbox the row came from, and the list hands
		// it back exactly as it was given. It defaults to the kernel so an older page keeps working.
		[HttpPost("{id:long}/read")]
		public async Task<IActionResult> MarkRead(long id, string? src)
		{
			var empId = await CurrentEmployeeIdAsync();
			if (empId == null) return Unauthorized();

			if (string.Equals(src, "c", StringComparison.Ordinal))
			{
				var c = await CommInbox.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientEmployeeId == empId.Value);
				if (c == null) return NotFound();
				if (c.ReadAt == null) { c.ReadAt = DateTime.UtcNow; await _context.SaveChangesAsync(); }
				return Ok(new { success = true });
			}

			var kid = (int)id;
			var n = await _context.Notifications.FirstOrDefaultAsync(x => x.ID == kid && x.RecipientEmployeeID == empId.Value);
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

			// "Mark everything read" has to mean EVERYTHING, or the badge comes back on the next poll
			// carrying the inbox this action forgot.
			var unreadComm = await CommInbox.Notifications
				.Where(n => n.RecipientEmployeeId == empId.Value && n.ReadAt == null).ToListAsync();
			foreach (var c in unreadComm) { c.ReadAt = now; }

			if (unread.Count > 0 || unreadComm.Count > 0) await _context.SaveChangesAsync();
			return Ok(new { success = true, marked = unread.Count + unreadComm.Count });
		}

		// The communication platform reaches ITS tables through CommDb - a thin struct over this
		// same CrossDbContext, not a second connection. Named once here so the three call sites
		// below read as one inbox rather than as EF plumbing.
		private CrossBuy.BL.Communication.CommDb CommInbox => new(_context);

		// ONE SHAPE FOR BOTH TABLES. Projecting straight into an anonymous type gave two types EF
		// could not Concat, and merging after materialisation needs a name either way.
		private sealed class NotifyRow
		{
			// LONG, because the communication inbox keys on one. The kernel's int widens; the
			// other way round truncates, which is a silent wrong row rather than an error.
			public long Id { get; set; }
			public string Src { get; set; } = "k";
			public string? TitleAr { get; set; }
			public string? TitleEn { get; set; }
			public string? TitleFr { get; set; }
			public string? BodyAr { get; set; }
			public string? BodyEn { get; set; }
			public string? BodyFr { get; set; }
			public string? Type { get; set; }
			public int? RefId { get; set; }
			public bool IsRead { get; set; }
			public DateTime? CreatedAt { get; set; }
			public string? Url { get; set; }
			public string? Category { get; set; }
			public string? Icon { get; set; }
			public string? Priority { get; set; }
			public int? ActorId { get; set; }
			public long? CommentId { get; set; }
		}
	}
}
