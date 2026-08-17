using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Calendar
{
	// ==========================================================================================
	// CALENDAR SCHEDULING — recurrence, time zone, resources.
	//
	// SATELLITE TABLES, not columns on CalendarEvents. Every existing calendar screen reads
	// CalendarEvents; adding columns there would make an unapplied script break the calendar that
	// four workstreams already use. A missing satellite table breaks only recurrence.
	//
	// An event WITHOUT a schedule row is exactly what it is today: one occurrence, stored in UTC.
	// ==========================================================================================

	public static class RecurrenceKinds
	{
		public const string None = "None";
		public const string Daily = "Daily";
		public const string Weekly = "Weekly";
		public const string Monthly = "Monthly";     // same day-of-month
		public const string Yearly = "Yearly";

		public static readonly IReadOnlyList<string> All = new[] { None, Daily, Weekly, Monthly, Yearly };
		public static bool IsKnown(string? k) => k != null && All.Contains(k, StringComparer.Ordinal);
	}

	/// The scheduling facts an event needs beyond a single start and end.
	///
	/// The time zone is stored as an IANA/Windows id and NOT as an offset. An offset is only true
	/// until the next DST change: a 09:00 weekly meeting stored as "UTC+3" silently becomes 08:00 or
	/// 10:00 local after the clocks move. Storing the ZONE keeps "09:00 local, every week" true.
	public class CalendarEventSchedule
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EventId { get; set; }

		/// The zone the series' wall-clock times are expressed in. Null means "the company default",
		/// resolved at read time — never silently treated as UTC.
		public string? TimeZoneId { get; set; }

		public string RecurrenceKind { get; set; } = RecurrenceKinds.None;

		/// Every N days/weeks/months/years. 1 = every one. Never 0 — a zero interval is an infinite
		/// loop wearing the clothes of a schedule.
		public int Interval { get; set; } = 1;

		/// Weekly only: which days, as "Mon,Wed,Fri". Empty means "the weekday the series starts on".
		public string? ByWeekdays { get; set; }

		/// A series ends by DATE or by COUNT — never neither. An unbounded series has no last
		/// occurrence to compute, so every expansion would be capped by an arbitrary limit instead.
		public DateTime? UntilLocalDate { get; set; }
		public int? OccurrenceCount { get; set; }

		/// Dates (local, yyyy-MM-dd, comma separated) removed from the series — a cancelled single
		/// occurrence. Deleting the row would delete the whole series.
		public string? ExceptionDates { get; set; }

		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	/// A bookable thing that is not a person: a meeting room, a vehicle, a projector. Resources are
	/// what the resource view has rows for, and what conflict detection protects from double booking.
	public class CalendarResource
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Kind { get; set; } = "Room";     // Room | Vehicle | Equipment | Other
		public int? Capacity { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class CalendarEventResource
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EventId { get; set; }
		public int ResourceId { get; set; }
	}

	public static class CalendarSchedulingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CalendarEventSchedule>(e =>
			{
				// One schedule per event. Two would be two answers to "when does this repeat".
				e.HasIndex(x => new { x.CompanyId, x.EventId }).IsUnique();
			});

			b.Entity<CalendarResource>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
			});

			b.Entity<CalendarEventResource>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.EventId, x.ResourceId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.ResourceId });
			});
		}
	}
}
