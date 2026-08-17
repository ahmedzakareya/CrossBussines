using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	/// An in-app notification delivered to one employee (real-time via SignalR + persisted).
	/// Bilingual (AR/EN); the client picks the language.
	public class Notification : BaseEntity
	{
		public int ID { get; set; }

		public int RecipientEmployeeID { get; set; }   // who receives it

		public string? TitleAr { get; set; }
		public string? TitleEn { get; set; }
		public string? BodyAr { get; set; }
		public string? BodyEn { get; set; }

		public string? Type { get; set; }              // catalog key — see NotificationTypes (leave_submitted | goods_receipt | ...)
		public int? RefId { get; set; }                // related record id (e.g. LeaveRequest.ID)

		public bool IsRead { get; set; }

		// ---- Communication Hub P1 expansion (all nullable / additive) ----
		public int? CompanyID { get; set; }             // tenant isolation — scopes queries + the SignalR group
		public int? BranchID { get; set; }              // optional branch scope
		public string? Url { get; set; }                // deep-link for click-through (generic navigation)
		public string? Priority { get; set; }           // Normal | High | Critical
		public string? Category { get; set; }           // module bucket: HR | Sales | Inventory | Accounting | CRM | Governance | ...
		public int? ActorEmployeeID { get; set; }       // who caused the event (null = system)
		public string? DedupKey { get; set; }           // idempotency — skip if an unread row with the same key exists
		public DateTime? ExpiresAt { get; set; }        // auto-hide stale alerts
		public DateTime? ReadAt { get; set; }           // when it was read
		public string? Icon { get; set; }               // optional KI icon override

		// ---- Platform Kernel slice 2 (additive, nullable) ----
		// The canonical IEntityRegistry code + record id this notification is about. `Type`/`RefId` above are
		// the pre-kernel pair: Type is a catalog key (a MEANING, e.g. "purchase_invoice") and RefId is
		// untyped, so nothing could reliably answer "which entity is this notification about?". These two
		// columns make a notification entity-addressable the same way BusinessEvents is, which is what lets
		// NotificationProjection resolve the click-through URL through the registry.
		public string? EntityType { get; set; }
		public int? EntityId { get; set; }

		[ForeignKey(nameof(RecipientEmployeeID))]
		public Employee? Recipient { get; set; }
	}

	/// A per-employee mute on a notification Category (Communication Hub P2). Muting suppresses only
	/// Normal-priority notifications of that category; High/Critical always deliver.
	public class NotificationMute
	{
		public int ID { get; set; }
		public int EmployeeId { get; set; }
		public string Category { get; set; } = "";
		public DateTime CreatedAt { get; set; }
	}
}
