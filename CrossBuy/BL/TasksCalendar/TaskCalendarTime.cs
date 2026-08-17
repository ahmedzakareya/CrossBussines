using System.Globalization;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// TASKS & CALENDAR — THE SHARED TIME MODEL  (integration increment, Phase 7)
	//
	// WHY THIS TYPE EXISTS
	//
	//   Tasks stamps CreatedAt with DateTime.UtcNow. Calendar writes DateTime.Now (server local) and
	//   emits ISO WITHOUT an offset — deliberately, so FullCalendar renders exactly what was typed
	//   (BL/CalendarService.cs:129). Each module is self-consistent. The moment the two are merged onto
	//   one agenda they are not: a task due "09:00" and an event at "09:00" are different instants for
	//   anyone outside the server's zone.
	//
	//   This type is the SEAM. New integration code converts through it; legacy storage is NOT rewritten
	//   by this increment (that is a measured migration, documented as an open decision).
	//
	// THE RULES IT ENFORCES  (owner decisions 7, 8, 9)
	//
	//   * Stored instants are UTC. `ToUtc` refuses a value it cannot interpret rather than guessing.
	//   * ALL-DAY IS A LOCAL DATE, NOT AN INSTANT. An all-day value is carried as a DateOnly and is
	//     never converted through a timezone — that conversion is exactly what moves "1 Ramadan" to the
	//     previous evening for anyone west of the server.
	//   * A boundary value carries an explicit offset (DateTimeOffset), never a bare DateTime.
	//   * A missing or invalid timezone FAILS EXPLICITLY. There is no silent fallback, and no
	//     CompanyID fallback anywhere in this file.
	//   * DateTime.Now is never called here. `UtcNow` is injectable so DST behaviour is testable.
	//
	// WHAT IT DELIBERATELY DOES NOT DO
	//
	//   It does not touch CalendarService, TaskService or any stored column. Applying it to legacy rows
	//   is a separate, measured migration — see Stage-Tasks-Calendar-Integration-05-Time-Model.md §6.
	// ==========================================================================================

	/// Raised when a timezone cannot be resolved. Deliberately an exception rather than a fallback:
	/// a silently-wrong timezone produces silently-wrong times, which is worse than a failed request.
	public sealed class TimeZoneUnresolvedException : InvalidOperationException
	{
		public string? RequestedZoneId { get; }

		public TimeZoneUnresolvedException(string? zoneId)
			: base(zoneId == null
				? "No timezone was supplied and there is no approved company default configured. " +
				  "A displayed or stored time cannot be derived without one."
				: $"Timezone '{zoneId}' could not be resolved on this host.")
		{
			RequestedZoneId = zoneId;
		}
	}

	/// How a point on the agenda is expressed. The distinction is the whole point of the type:
	/// a meeting happens at an INSTANT; a public holiday happens on a DATE.
	public enum AgendaTimeKind
	{
		/// A real instant. Stored UTC, rendered in the viewer's zone.
		Instant = 0,

		/// A local calendar date with no time and no zone. Never converted.
		AllDayDate = 1
	}

	/// A boundary-safe point in time: either an instant (UTC + the offset it should render at) or an
	/// all-day local date. One of the two is set; never both, never neither.
	public sealed class AgendaInstant
	{
		public AgendaTimeKind Kind { get; }

		/// Set when Kind == Instant. Always UTC.
		public DateTime? Utc { get; }

		/// Set when Kind == Instant. The same instant carrying the viewer's offset — what an API returns.
		public DateTimeOffset? Local { get; }

		/// Set when Kind == AllDayDate. A bare local date; it has no zone and is never shifted.
		public DateOnly? Date { get; }

		public string? TimeZoneId { get; }

		private AgendaInstant(AgendaTimeKind kind, DateTime? utc, DateTimeOffset? local, DateOnly? date, string? tz)
		{
			Kind = kind; Utc = utc; Local = local; Date = date; TimeZoneId = tz;
		}

		public static AgendaInstant FromUtc(DateTime utc, TimeZoneInfo zone)
		{
			if (zone == null) throw new TimeZoneUnresolvedException(null);
			var asUtc = TaskCalendarTime.AssumeUtc(utc);
			var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(asUtc, TimeSpan.Zero), zone);
			return new AgendaInstant(AgendaTimeKind.Instant, asUtc, local, null, zone.Id);
		}

		public static AgendaInstant FromAllDay(DateOnly date) =>
			new(AgendaTimeKind.AllDayDate, null, null, date, null);

		/// The value an API emits. An instant carries an explicit offset; an all-day date carries a bare
		/// date string with NO time and NO offset, because attaching either would make it an instant.
		public string ToApiString() => Kind switch
		{
			AgendaTimeKind.AllDayDate => Date!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			_ => Local!.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
		};

		/// Ordering key. An all-day date sorts at the START of its day IN THE VIEWER'S ZONE, which is a
		/// presentation decision and never written back to storage.
		public DateTime SortKeyUtc(TimeZoneInfo zone) => Kind switch
		{
			AgendaTimeKind.AllDayDate =>
				TimeZoneInfo.ConvertTimeToUtc(Date!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone),
			_ => Utc!.Value
		};
	}

	public static class TaskCalendarTime
	{
		/// The clock. Injectable so DST-transition tests are real tests and not calendar-dependent.
		/// It is UtcNow — never Now — and nothing in this file reads the server's local zone.
		public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

		/// Resolves a timezone EXPLICITLY. `zoneId` is the user's or the company's configured zone;
		/// `companyDefaultZoneId` is the approved company default. Both absent is a hard failure — there
		/// is no server-local fallback, because that is the bug this whole type exists to prevent.
		public static TimeZoneInfo ResolveZone(string? zoneId, string? companyDefaultZoneId = null)
		{
			var candidate = !string.IsNullOrWhiteSpace(zoneId) ? zoneId
						  : !string.IsNullOrWhiteSpace(companyDefaultZoneId) ? companyDefaultZoneId
						  : null;

			if (candidate == null) throw new TimeZoneUnresolvedException(null);

			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById(candidate);
			}
			catch (TimeZoneNotFoundException) { throw new TimeZoneUnresolvedException(candidate); }
			catch (InvalidTimeZoneException) { throw new TimeZoneUnresolvedException(candidate); }
		}

		public static bool TryResolveZone(string? zoneId, string? companyDefaultZoneId, out TimeZoneInfo? zone)
		{
			try { zone = ResolveZone(zoneId, companyDefaultZoneId); return true; }
			catch (TimeZoneUnresolvedException) { zone = null; return false; }
		}

		/// Treats a DateTime as UTC. A value already marked Utc passes through; an Unspecified value is
		/// LABELLED (not shifted), because every stored instant in the new seam is written as UTC; a Local
		/// value is converted. Nothing here consults the server zone for an Unspecified value.
		public static DateTime AssumeUtc(DateTime value) => value.Kind switch
		{
			DateTimeKind.Utc => value,
			DateTimeKind.Local => value.ToUniversalTime(),
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
		};

		/// A wall-clock value the user typed, in a named zone → the UTC instant to store.
		/// Ambiguous (DST fall-back) and invalid (spring-forward gap) local times are handled explicitly
		/// rather than left to the framework's default, so the behaviour is stated instead of discovered.
		public static DateTime LocalWallClockToUtc(DateTime wallClock, TimeZoneInfo zone)
		{
			if (zone == null) throw new TimeZoneUnresolvedException(null);
			var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

			// Spring-forward gap: the wall clock never existed. Move forward by the DST delta rather than
			// throwing, and document it — a user who typed 02:30 on a night that has no 02:30 means 03:30.
			if (zone.IsInvalidTime(unspecified))
			{
				var adjustment = zone.GetAdjustmentRules()
					.FirstOrDefault(r => unspecified >= r.DateStart && unspecified <= r.DateEnd)?.DaylightDelta
					?? TimeSpan.FromHours(1);
				unspecified = unspecified.Add(adjustment);
			}

			// Ambiguous (fall-back): the wall clock happened twice. Take the FIRST (daylight) occurrence
			// deterministically. TimeZoneInfo.ConvertTimeToUtc picks standard time; being explicit here
			// means the choice is ours and is testable.
			if (zone.IsAmbiguousTime(unspecified))
			{
				var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
				var chosen = offsets.Max();     // the daylight offset — the first occurrence
				return new DateTimeOffset(unspecified, chosen).UtcDateTime;
			}

			return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
		}

		/// A stored UTC instant → the offset-carrying value an API returns.
		public static DateTimeOffset UtcToOffset(DateTime utc, TimeZoneInfo zone)
		{
			if (zone == null) throw new TimeZoneUnresolvedException(null);
			return TimeZoneInfo.ConvertTime(new DateTimeOffset(AssumeUtc(utc), TimeSpan.Zero), zone);
		}

		/// The local calendar DATE an instant falls on, in a named zone. Used to bucket an agenda by day.
		public static DateOnly LocalDateOf(DateTime utc, TimeZoneInfo zone) =>
			DateOnly.FromDateTime(UtcToOffset(utc, zone).DateTime);

		/// Builds the boundary value for a timed item.
		public static AgendaInstant Timed(DateTime utc, TimeZoneInfo zone) => AgendaInstant.FromUtc(utc, zone);

		/// Builds the boundary value for an all-day item FROM A STORED LOCAL VALUE.
		///
		/// The date component is taken as-is and NO conversion happens. That is the rule: an all-day value
		/// is a local date, so converting it through a zone is what makes a holiday land on the wrong day.
		public static AgendaInstant AllDay(DateTime storedLocalDate) =>
			AgendaInstant.FromAllDay(DateOnly.FromDateTime(storedLocalDate));

		public static AgendaInstant AllDay(DateOnly date) => AgendaInstant.FromAllDay(date);

		/// The UTC window to query for a local date range in a named zone. The end is EXCLUSIVE and covers
		/// the whole of the last local day, so an event late on the final day is not silently dropped.
		public static (DateTime fromUtc, DateTime toUtcExclusive) LocalRangeToUtcWindow(
			DateOnly fromLocal, DateOnly toLocalInclusive, TimeZoneInfo zone)
		{
			if (zone == null) throw new TimeZoneUnresolvedException(null);
			var start = LocalWallClockToUtc(fromLocal.ToDateTime(TimeOnly.MinValue), zone);
			var end = LocalWallClockToUtc(toLocalInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
			return (start, end);
		}
	}
}
