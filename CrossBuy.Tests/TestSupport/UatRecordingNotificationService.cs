using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;

namespace CrossBuy.Tests.TestSupport
{
	// TAB-5 test support — a recording INotificationService for the committed UAT suites.
	//
	// WHY THIS FILE EXISTS. Two committed TAB-5 test files were red on a clean checkout because the recorder
	// they used lived inside an UNTRACKED Tasks/Calendar test suite (TasksCalendarIntegrationTests.cs). Landing
	// that whole suite to obtain one helper would have absorbed a foreign feature's tests, so the double is
	// extracted here instead: the minimum reusable piece, owned by TAB-5, with nothing else attached.
	//
	// WHY THE NAME IS PREFIXED. The untracked suite declares its own `RecordingNotificationService` in namespace
	// `CrossBuy.Tests`. Re-using that exact name — even in a different namespace — would make the reference
	// ambiguous (CS0104) in the current dirty working tree, where both files are present. A distinct name
	// compiles unambiguously whether or not the other suite is on disk. When that suite lands, the two can be
	// collapsed into one helper; until then this is the only definition the committed tests depend on.
	//
	// IT IMPLEMENTS ONLY A COMMITTED CONTRACT. `INotificationService` is committed at
	// CrossBuy/BL/INotificationService.cs, and both members below match that interface exactly.
	//
	// WHY IT STILL WRITES A Notification ROW, rather than recording and nothing else. The behaviour under test
	// depends on it: TaskNotificationService decides "Duplicate" by READING the table —
	// `_db.Notifications.AnyAsync(n => n.DedupKey == dedupKey && n.RecipientEmployeeID == recipientId)`
	// (TaskNotificationService.cs:225-229). A recorder that only collected calls would never let that branch
	// fire, so the committed overdue tests would keep passing while silently proving less. The double therefore
	// persists, and it brings NO infrastructure of its own: it writes through whichever CrossDbContext the
	// caller already owns, which for these suites is in-memory SQLite. No server, no network, no I/O, no timers,
	// no background work — deterministic and test-only.
	public sealed class UatRecordedNotification
	{
		public int RecipientEmployeeId { get; init; }
		public string? Type { get; init; }
		public int? RefId { get; init; }
		public int? CompanyId { get; init; }
		public int? ActorEmployeeId { get; init; }
		public string? DedupKey { get; init; }
		public string? EntityType { get; init; }
		public int? EntityId { get; init; }
		public string? Url { get; init; }
		public string? BodyAr { get; init; }
	}

	public sealed class UatRecordingNotificationService : INotificationService
	{
		private readonly CrossDbContext _db;

		/// Everything that WOULD have been delivered, in call order.
		public List<UatRecordedNotification> Sent { get; } = new();

		public UatRecordingNotificationService(CrossDbContext db) { _db = db; }

		public async Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
			string? bodyAr, string? bodyEn, string type, int? refId = null,
			string? url = null, int? companyId = null, int? actorEmployeeId = null,
			string? priority = null, string? category = null, string? dedupKey = null,
			DateTime? expiresAt = null, string? icon = null,
			string? entityType = null, int? entityId = null)
		{
			Sent.Add(new UatRecordedNotification
			{
				RecipientEmployeeId = recipientEmployeeId, Type = type, RefId = refId, CompanyId = companyId,
				ActorEmployeeId = actorEmployeeId, DedupKey = dedupKey, EntityType = entityType,
				EntityId = entityId, Url = url, BodyAr = bodyAr,
			});

			_db.Notifications.Add(new Notification
			{
				RecipientEmployeeID = recipientEmployeeId, TitleAr = titleAr, TitleEn = titleEn,
				BodyAr = bodyAr, BodyEn = bodyEn, Type = type, RefId = refId, CompanyID = companyId,
				ActorEmployeeID = actorEmployeeId, DedupKey = dedupKey, Url = url,
				EntityType = entityType, EntityId = entityId, IsRead = false,
				// The real NotificationService stamps CreatedAt (NotificationService.cs:49,118). Omitting it here
				// left every persisted row with a null timestamp, so anything deriving "when did this happen?"
				// from the notification — task escalation reports the escalation time from exactly this column —
				// read null and could not tell a caller when. A double that drops a field the contract always
				// writes is not a smaller double, it is a wrong one.
				CreatedAt = DateTime.UtcNow,
			});
			await _db.SaveChangesAsync();
		}

		/// Role broadcast is not exercised by the committed UAT suites. It returns 0 rather than throwing so a
		/// producer that fans out by role does not fail for a reason unrelated to the assertion.
		public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
			string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
			string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
	}
}
