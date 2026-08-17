namespace CrossBuy.BL
{
	public interface INotificationService
	{
		/// Persist a notification for an employee and push it in real-time via SignalR.
		/// The trailing parameters are the Communication-Hub P1 enrichment (all optional → existing callers are unchanged):
		/// url = click-through deep-link; companyId = tenant scope (auto-resolved from the recipient when null);
		/// actorEmployeeId = who caused it; priority/category/icon default from the NotificationTypes catalog when null;
		/// dedupKey = skip if an UNREAD row with the same key already exists for this recipient; expiresAt = auto-hide.
		/// Platform Kernel slice 2 adds two more optional trailing parameters, following the same additive
		/// convention: entityType/entityId record WHICH business object the notification is about, using a
		/// canonical IEntityRegistry code. Existing callers are unchanged and leave them null.
		Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
			string? bodyAr, string? bodyEn, string type, int? refId = null,
			string? url = null, int? companyId = null, int? actorEmployeeId = null,
			string? priority = null, string? category = null, string? dedupKey = null,
			DateTime? expiresAt = null, string? icon = null,
			string? entityType = null, int? entityId = null);

		/// Broadcast a notification to every employee holding one of <paramref name="roles"/>
		/// in the given area. <paramref name="scope"/> = "acc" (AccountingUserRoles) or
		/// "inv" (InventoryUserRoles). Skips <paramref name="exceptEmployeeId"/> (the actor).
		/// Returns the number of recipients notified.
		Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
			string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
			string type, int? refId = null, int? exceptEmployeeId = null);
	}
}
