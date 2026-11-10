using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H21 — medication routing rules, with no database.
	// ==========================================================================================
	public static class HospitalMedRoutingRules
	{
		/// The routing the prototype's rules suggest: a controlled drug and a STAT dose go by porter; anything else by tube.
		/// (The ward cabinet is chosen by the pharmacist when the ward stocks the drug.)
		public static string SuggestedVia(string priority, bool controlled) =>
			controlled || priority == HospitalMedPriority.Stat ? HospitalMedVia.Porter : HospitalMedVia.Tube;

		/// The minutes from prescription to the bedside the hospital aims for.
		public static int TargetMinutes(string priority) => priority switch
		{
			HospitalMedPriority.Stat => 15,
			HospitalMedPriority.Urgent => 30,
			_ => 60,
		};

		/// From the ward cabinet the drug is not prepared nor dispatched: those steps are not needed.
		public static bool Skips(string via, byte stage) =>
			via == HospitalMedVia.Cabinet && (stage == HospitalMedStage.Prepared || stage == HospitalMedStage.Dispatched);

		/// The step that comes after `stage` on this route (null once administered).
		public static byte? Next(string via, byte stage)
		{
			for (int s = stage + 1; s <= HospitalMedStage.Last; s++)
				if (!Skips(via, (byte)s)) return (byte)s;
			return null;
		}

		/// Why a delivery cannot be raised, or null.
		public static string? CreateRefusal(string? priority, string? via, bool controlled, decimal quantity, bool stocked)
		{
			if (priority == null || !HospitalMedPriority.All.Contains(priority)) return "Choose the priority";
			if (via == null || !HospitalMedVia.All.Contains(via)) return "Choose how it travels";
			if (controlled && via != HospitalMedVia.Porter) return "A controlled drug goes by porter, signed for on receipt";
			if (quantity < 0) return "The quantity cannot be negative";
			if (stocked && quantity <= 0) return "Enter how many units leave the store";
			if (quantity > 1000) return "That is more than one delivery carries";
			return null;
		}

		/// Why the step `next` cannot be done now, or null: in order, once, and the second signature for a controlled drug.
		public static string? StepRefusal(byte current, byte next, string via, bool cancelled, bool controlled, int? by, int? witness)
		{
			if (cancelled) return "This delivery is cancelled";
			if (current >= HospitalMedStage.Last) return "This dose is already given";
			if (Next(via, current) != next) return "This step is not the next one";
			if (controlled && (next == HospitalMedStage.Received || next == HospitalMedStage.Administered))
			{
				if (witness == null) return "A controlled drug needs a second signature";
				if (witness == by) return "The witness must be someone else";
			}
			return null;
		}

		/// The wristband scan must read the patient's own MRN.
		public static bool WristbandMatches(string? scanned, string mrn) =>
			!string.IsNullOrWhiteSpace(scanned) && string.Equals(scanned.Trim(), mrn.Trim(), StringComparison.OrdinalIgnoreCase);

		/// Late: still on its way past its target (an administered or cancelled delivery is never late).
		public static bool IsLate(string priority, DateTime prescribedAt, DateTime? administeredAt, DateTime? cancelledAt, DateTime nowUtc) =>
			administeredAt == null && cancelledAt == null && (nowUtc - prescribedAt).TotalMinutes > TargetMinutes(priority);

		/// The average minutes from prescription to the bedside over the deliveries given (null when none).
		public static int? Turnaround(IEnumerable<(DateTime PrescribedAt, DateTime AdministeredAt)> given)
		{
			var m = given.Select(g => (g.AdministeredAt - g.PrescribedAt).TotalMinutes).Where(x => x >= 0).ToList();
			return m.Count == 0 ? null : (int)Math.Round(m.Average(), MidpointRounding.AwayFromZero);
		}

		/// The scheduled dose a delivery answers: the earliest of the day's doses not yet recorded, up to an hour ahead.
		public static DateTime? DoseToAnswer(IEnumerable<DateTime> dosesToday, IEnumerable<DateTime> recorded, DateTime nowUtc)
		{
			var taken = recorded.ToHashSet();
			return dosesToday.Where(d => !taken.Contains(d) && d <= nowUtc.AddHours(1)).OrderBy(d => d).Cast<DateTime?>().FirstOrDefault();
		}
	}
}
