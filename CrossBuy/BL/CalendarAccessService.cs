using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// ==========================================================================================
	// CALENDAR AUTHORIZATION — the missing module access service.
	//
	// WHY IT EXISTS: CalendarEvent was registered with PermissionScope = ScopeNone, and
	// DefaultPermissionAdapter denies every action except View for a scope with no access service.
	// That is correct behaviour — "no module policy exists, so nothing can be granted" — but it meant
	// a new calendar write could not be authorized at all, and the authorization analyzer rightly
	// refused it. The fix is the one the platform asks for: give Calendar a real policy.
	//
	// WHAT THE DATA SUPPORTS, and therefore what this implements:
	//   CalendarEvent.OwnerEmpId · CalendarEvent.Scope (Personal | Company) · CompanyID ·
	//   CalendarEventAttendee.EmployeeId · the company-intersected manager hierarchy.
	// WHAT IT DOES NOT: there is no visibility tier, no delegation and no shared-calendar concept on
	// CalendarEvent. Inventing one would be a rule with nothing behind it.
	//
	// NOTE ON CASING: CalendarEvent.CompanyID — capital D, unlike TaskItem.CompanyId. A predicate
	// copied between the two will not compile, which is the good outcome.
	// ==========================================================================================

	public interface ICalendarAccessService : IModuleAccessService
	{
		// Deliberately adds nothing to IModuleAccessService. It exists so a caller can name the
		// calendar policy in its constructor instead of picking one out of an IEnumerable — and,
		// because it INHERITS IModuleAccessService, the authorization analyzer recognises a call on
		// it as a real authority without the analyzer being changed.
		Task<bool> CanEventAsync(BusinessContext context, string action, int eventId,
			CancellationToken cancellationToken = default);
	}

	/// Four actions. `manage` is company-wide calendar administration; there is no `delete` distinct
	/// from `edit` because the calendar's own delete rule is the same owner rule as its edit rule.
	public static class CalendarActions
	{
		public const string Read = "read";
		public const string Create = "create";
		public const string Edit = "edit";
		public const string Manage = "manage";

		public static readonly IReadOnlyCollection<string> All = new[] { Read, Create, Edit, Manage };
	}

	public static class CalendarRoles
	{
		public const string CalendarAdministrator = "CalendarAdministrator";
		public const string CalendarSupervisor = "CalendarSupervisor";

		public static readonly IReadOnlyList<string> All = new[] { CalendarAdministrator, CalendarSupervisor };
	}

	public sealed class CalendarAccessService : ModuleAccessServiceBase, ICalendarAccessService
	{
		private readonly CrossDbContext _db;
		private readonly IOrgHierarchy _org;
		private readonly ILogger<CalendarAccessService> _log;

		// NOTE: unlike TasksAccessService this takes NO Func<IPlatformPermissionProvider>. It has no
		// linked-entity gate to ask about, so it never needs the provider — and not taking it keeps
		// this service out of the IModuleAccessService → provider → adapter → IModuleAccessService
		// cycle entirely, rather than deferring it.
		public CalendarAccessService(
			CrossDbContext db, IPlatformRoleDirectory roles, IOrgHierarchy org,
			ILogger<CalendarAccessService> log) : base(roles, log)
		{ _db = db; _org = org; _log = log; }

		public override string Scope => EntityRegistry.ScopeCalendar;
		public override IReadOnlyCollection<string> Actions => CalendarActions.All;

		public Task<bool> CanEventAsync(BusinessContext context, string action, int eventId,
			CancellationToken cancellationToken = default)
			=> CanAsync(context, action, PermissionTarget.ForEntity(EntityRegistry.CalendarEvent, eventId), cancellationToken);

		protected override async Task<bool> EvaluateAsync(
			BusinessContext context, string action, PermissionTarget? target,
			IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken)
		{
			int me = context.EmployeeId!.Value;
			bool admin = Holds(grants, CalendarRoles.CalendarAdministrator);
			bool supervisor = Holds(grants, CalendarRoles.CalendarSupervisor);

			// `create` is not about an existing record, so it is decided before any lookup. Every
			// employee may create their own calendar entries — that is what a personal calendar is.
			if (action == CalendarActions.Create) return true;

			bool companyWide = action switch
			{
				CalendarActions.Read => admin || supervisor,
				CalendarActions.Edit => admin || supervisor,
				CalendarActions.Manage => admin,
				_ => false,
			};

			// No event named ⇒ a module-level question ("may I open the calendar at all").
			if (target?.EntityId is not int eventId || eventId <= 0
				|| !string.Equals(target.EntityType, EntityRegistry.CalendarEvent, StringComparison.Ordinal))
			{
				return bootstrapOpen ? action != CalendarActions.Manage : companyWide;
			}

			// ---- the event itself. Company comes from the ROW, never from the request. ----
			var ev = await _db.CalendarEvents.AsNoTracking()
				.Where(e => e.Id == eventId && e.DeletedAt == null)
				.Select(e => new { e.CompanyID, e.OwnerEmpId, e.Scope })
				.FirstOrDefaultAsync(cancellationToken);

			// Absent and other-company answer identically — ids cannot be probed.
			if (ev == null || ev.CompanyID != context.CompanyId) return false;

			if (companyWide) return true;

			bool isOwner = ev.OwnerEmpId == me;

			if (action == CalendarActions.Read)
			{
				if (isOwner) return true;
				// A Company-scope event is visible to the company by definition — that is what the scope
				// means, and the calendar screen has always shown it.
				if (string.Equals(ev.Scope, "Company", StringComparison.OrdinalIgnoreCase)) return true;
				// An attendee can see the meeting they were invited to. Without this, an invitee could be
				// notified about an event they are then refused sight of.
				if (await _db.CalendarEventAttendees.AsNoTracking()
						.AnyAsync(a => a.EventId == eventId && a.EmployeeId == me, cancellationToken))
					return true;

				// A manager may read their reports' entries — company-intersected, so it cannot reach
				// across companies.
				var readable = await _org.DirectAndIndirectReportsAsync(context.CompanyId, me, cancellationToken);
				return readable.Contains(ev.OwnerEmpId);
			}

			if (action == CalendarActions.Edit)
			{
				// EDITING is the owner's right, deliberately NOT an attendee's. Being invited to a meeting
				// is not permission to move it; letting an attendee reschedule would silently change the
				// event for everyone else who was invited.
				if (isOwner) return true;
				var editable = await _org.DirectAndIndirectReportsAsync(context.CompanyId, me, cancellationToken);
				return editable.Contains(ev.OwnerEmpId);
			}

			// `manage` reached here means the caller holds no administrator role — deny, do not fall back.
			return false;
		}
	}
}
