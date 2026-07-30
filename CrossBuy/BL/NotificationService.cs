using CrossBuy.Hubs;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class NotificationService : INotificationService
	{
		private readonly CrossDbContext _context;
		private readonly IHubContext<NotificationsHub> _hub;

		public NotificationService(CrossDbContext context, IHubContext<NotificationsHub> hub)
		{
			_context = context;
			_hub = hub;
		}

		public async Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
			string? bodyAr, string? bodyEn, string type, int? refId = null,
			string? url = null, int? companyId = null, int? actorEmployeeId = null,
			string? priority = null, string? category = null, string? dedupKey = null,
			DateTime? expiresAt = null, string? icon = null)
		{
			// Dedup: if an unread notification with the same key already exists for this recipient, skip.
			if (!string.IsNullOrEmpty(dedupKey) &&
				await _context.Notifications.AnyAsync(x => x.RecipientEmployeeID == recipientEmployeeId && x.DedupKey == dedupKey && !x.IsRead))
				return;

			// Resolve company from the recipient when the caller didn't pass one (tenant isolation).
			if (companyId == null)
				companyId = await _context.Employee.AsNoTracking()
					.Where(e => e.ID == recipientEmployeeId).Select(e => (int?)e.EmpCompanyID).FirstOrDefaultAsync();

			var meta = NotificationTypes.Meta(type);
			var effPriority = priority ?? meta.priority;
			var effCategory = category ?? meta.category;
			// mute: suppress only Normal-priority notifications of a muted category (High/Critical always deliver)
			if (effPriority == nameof(NotificationTypes.Prio.Normal) &&
				await _context.NotificationMutes.AnyAsync(x => x.EmployeeId == recipientEmployeeId && x.Category == effCategory))
				return;

			var n = new Notification
			{
				RecipientEmployeeID = recipientEmployeeId,
				TitleAr = titleAr, TitleEn = titleEn, BodyAr = bodyAr, BodyEn = bodyEn,
				Type = type, RefId = refId, IsRead = false, CreatedAt = DateTime.UtcNow,
				CompanyID = companyId, Url = url ?? NotificationTypes.UrlFor(type, refId), ActorEmployeeID = actorEmployeeId,
				Priority = effPriority, Category = effCategory,
				Icon = icon ?? meta.icon, DedupKey = dedupKey, ExpiresAt = expiresAt,
			};
			_context.Notifications.Add(n);
			await _context.SaveChangesAsync();

			// the actor's photo → shown as the notification avatar (falls back to a category icon when absent)
			string? actorAvatar = actorEmployeeId.HasValue
				? await _context.Employee.AsNoTracking().Where(e => e.ID == actorEmployeeId.Value).Select(e => e.ProfileImage).FirstOrDefaultAsync()
				: null;
			await PushAsync(n, actorAvatar);
		}

		// Real-time payload → the recipient's company-scoped SignalR group.
		private Task PushAsync(Notification n, string? actorAvatar = null)
		{
			var payload = new
			{
				id = n.ID, titleAr = n.TitleAr, titleEn = n.TitleEn, bodyAr = n.BodyAr, bodyEn = n.BodyEn,
				type = n.Type, refId = n.RefId, isRead = n.IsRead, createdAt = n.CreatedAt,
				url = n.Url, priority = n.Priority, category = n.Category, icon = n.Icon,
				actorAvatar = string.IsNullOrEmpty(actorAvatar) ? null : actorAvatar,
			};
			return _hub.Clients.Group(NotificationsHub.GroupFor(n.RecipientEmployeeID))
				.SendAsync("notification", payload);
		}

		public async Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
			string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
			string type, int? refId = null, int? exceptEmployeeId = null)
		{
			if (roles == null || roles.Length == 0) return 0;

			List<int> recipients;
			if (string.Equals(scope, "acc", StringComparison.OrdinalIgnoreCase))
			{
				recipients = await _context.AccountingUserRoles.AsNoTracking()
					.Where(r => r.CompanyID == companyId && roles.Contains(r.Role))
					.Select(r => r.EmployeeId).Distinct().ToListAsync();
			}
			else // "inv"
			{
				recipients = await _context.InventoryUserRoles.AsNoTracking()
					.Where(r => r.CompanyID == companyId && roles.Contains(r.Role))
					.Select(r => r.EmployeeId).Distinct().ToListAsync();
			}

			if (exceptEmployeeId.HasValue) recipients.RemoveAll(id => id == exceptEmployeeId.Value);
			if (recipients.Count == 0) return 0;

			// Batch: build one row per recipient, insert in a single SaveChanges, then push each in real-time.
			var meta = NotificationTypes.Meta(type);
			// mute: drop Normal-priority recipients who muted this category (High/Critical always deliver)
			if (meta.priority == nameof(NotificationTypes.Prio.Normal))
			{
				var muted = await _context.NotificationMutes.AsNoTracking()
					.Where(x => recipients.Contains(x.EmployeeId) && x.Category == meta.category)
					.Select(x => x.EmployeeId).ToListAsync();
				if (muted.Count > 0) recipients.RemoveAll(id => muted.Contains(id));
				if (recipients.Count == 0) return 0;
			}
			var url = NotificationTypes.UrlFor(type, refId);
			var now = DateTime.UtcNow;
			var rows = recipients.Select(emp => new Notification
			{
				RecipientEmployeeID = emp, TitleAr = titleAr, TitleEn = titleEn, BodyAr = bodyAr, BodyEn = bodyEn,
				Type = type, RefId = refId, IsRead = false, CreatedAt = now,
				CompanyID = companyId, ActorEmployeeID = exceptEmployeeId, Url = url,
				Priority = meta.priority, Category = meta.category, Icon = meta.icon,
			}).ToList();
			_context.Notifications.AddRange(rows);
			await _context.SaveChangesAsync();

			string? actorAvatar = exceptEmployeeId.HasValue
				? await _context.Employee.AsNoTracking().Where(e => e.ID == exceptEmployeeId.Value).Select(e => e.ProfileImage).FirstOrDefaultAsync()
				: null;
			foreach (var n in rows) await PushAsync(n, actorAvatar);
			return recipients.Count;
		}
	}
}
