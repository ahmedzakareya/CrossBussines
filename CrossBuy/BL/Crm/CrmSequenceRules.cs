using System.Text.RegularExpressions;

namespace CrossBuy.BL.CrmIdentity
{
	/// CRM C6 — the arithmetic of sequences and booking, with no database. Shadow-tested in CrmSequenceRulesTests.
	public static class CrmSequenceRules
	{
		public static readonly string[] Kinds = { "Email", "WhatsApp", "Call", "Task" };
		public const int MaxSteps = 12;

		/// When a step falls due, in the owner's local time. Day 0 runs at enrolment; any later day runs at the start of
		/// the working day. A day the owner does not work (their weekly rest or a public holiday) moves the step to the
		/// next working day — at most 30 days on, so a mis-set calendar can never park a step forever.
		public static DateTime DueAt(DateTime enrolledLocal, int dayOffset, Func<DateTime, bool> isWorkDay, TimeSpan workStart, bool skipNonWorkDays)
		{
			var day = enrolledLocal.Date.AddDays(dayOffset);
			var moved = false;
			if (skipNonWorkDays)
				for (var i = 0; i < 30 && !isWorkDay(day); i++) { day = day.AddDays(1); moved = true; }
			if (dayOffset == 0 && !moved) return enrolledLocal;
			return day + workStart;
		}

		static readonly Regex Token = new(@"\{(name|first|rep|company|booking)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		/// Fills {name} {first} {rep} {company} {booking}. A token with no value becomes empty, never the raw braces —
		/// a customer must not receive "{company}". Any other brace text is the author's and is left alone.
		public static string Render(string? text, IReadOnlyDictionary<string, string?> values)
			=> Token.Replace(text ?? "", m => values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v ?? "" : "");

		public static string FirstName(string? full) => (full ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

		public sealed record Step(int DayOffset, string Kind, string? Subject, string? Body, string? Template);

		/// What is wrong with a sequence's steps, or null. Days never go backwards; each kind carries what it needs.
		public static string? StepsRefusal(IReadOnlyList<Step> steps)
		{
			if (steps.Count == 0) return "A sequence needs at least one step";
			if (steps.Count > MaxSteps) return "A sequence has at most 12 steps";
			for (var i = 0; i < steps.Count; i++)
			{
				var s = steps[i];
				if (!Kinds.Contains(s.Kind)) return "Unknown step type";
				if (s.DayOffset is < 0 or > 365) return "A step's day is between 0 and 365";
				if (i > 0 && s.DayOffset < steps[i - 1].DayOffset) return "Steps must be in day order";
				if (s.Kind == "Email" && (string.IsNullOrWhiteSpace(s.Subject) || string.IsNullOrWhiteSpace(s.Body))) return "An e-mail step needs a subject and a message";
				if (s.Kind == "WhatsApp" && string.IsNullOrWhiteSpace(s.Template)) return "A WhatsApp step needs an approved template";
				if (s.Kind is "Call" or "Task" && string.IsNullOrWhiteSpace(s.Subject)) return "A call or task step needs a title";
			}
			return null;
		}

		/// The reason a running enrolment ends now, or null. A conversion beats a booking beats a reply beats a
		/// withdrawn consent: the strongest thing that happened is the one the report should name.
		public static string? StopReason(bool converted, bool booked, bool replied, bool optedOut)
			=> converted ? "Converted" : booked ? "Booked" : replied ? "Replied" : optedOut ? "OptOut" : null;

		/// The free starts of one day, in local time: inside working hours, not before the earliest allowed moment,
		/// and clear of every busy span widened by the buffer on both sides.
		public static List<DateTime> Slots(DateTime dayLocal, TimeSpan workStart, TimeSpan workEnd, int duration, int buffer,
			IEnumerable<(DateTime start, DateTime end)> busyLocal, DateTime earliestLocal)
		{
			var list = new List<DateTime>();
			if (duration <= 0 || workEnd <= workStart) return list;
			var busy = busyLocal.Select(b => (s: b.start.AddMinutes(-buffer), e: b.end.AddMinutes(buffer))).ToList();
			var step = TimeSpan.FromMinutes(duration + buffer);
			for (var t = dayLocal.Date + workStart; t + TimeSpan.FromMinutes(duration) <= dayLocal.Date + workEnd; t += step)
			{
				var end = t.AddMinutes(duration);
				if (t < earliestLocal) continue;
				if (busy.Any(b => t < b.e && end > b.s)) continue;
				list.Add(t);
			}
			return list;
		}
	}
}
