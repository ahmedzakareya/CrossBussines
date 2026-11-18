using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H185 — the orthodontic record behind the contract (from the dental review: "orthodontics is a contract and a
	// visit counter; there is no diagnosis, no wire sequence, no retention"). The rules are here, without a database:
	//   · a case opens on a diagnosis — Angle class, overjet, overbite, crowding per arch, and the IOTN dental health grade
	//     (1–2 little or no need, 3 borderline, 4–5 great need); a retention-only case taken over from elsewhere excepted;
	//   · an adjustment records the archwires, elastics, oral hygiene and bracket breakages;
	//   · taking the appliance off (Active → Retention) needs the retainer of each arch, and starts the retention reviews at
	//     3, 6 and 12 months, then yearly;
	//   · the warnings a case raises: hygiene poor at two adjustments running (decalcification), three or more breakages, a
	//     retention review overdue.
	// ==========================================================================================
	public static class HospitalOrthoClinicalRules
	{
		public const string WarnHygiene = "Hygiene";
		public const string WarnBreakages = "Breakages";
		public const string WarnReview = "Review";
		public const int BreakagesLimit = 3;
		public static readonly int[] ReviewMonths = { 3, 6, 12 };

		public static string? DiagnosisRefusal(string? angle, decimal? overjet, decimal? overbite, decimal? crowdingUpper, decimal? crowdingLower, int? iotn)
		{
			if (!HospitalOrthoAngle.All.Contains(angle ?? "")) return "Choose the Angle class";
			if (iotn is not (>= 1 and <= 5)) return "Choose the IOTN grade (1 to 5)";
			if (overjet is < -10 or > 20) return "The overjet is −10 to 20 mm";
			if (overbite is < -10 or > 15) return "The overbite is −10 to 15 mm";
			if (crowdingUpper is < -15 or > 20 || crowdingLower is < -15 or > 20) return "Crowding is −15 (spacing) to 20 mm";
			return null;
		}

		/// "Little", "Borderline" or "Great" — the IOTN dental health component's need for treatment.
		public static string? Need(int? iotn) => iotn switch { 1 or 2 => "Little", 3 => "Borderline", 4 or 5 => "Great", _ => null };

		public static string? VisitRefusal(string? hygiene, int? breakages)
		{
			if (hygiene != null && !HospitalOrthoHygiene.All.Contains(hygiene)) return "Choose the oral hygiene";
			if (breakages is < 0 or > 20) return "Breakages are 0 to 20";
			return null;
		}

		public static string? RetentionRefusal(string? upper, string? lower)
		{
			if (!HospitalOrthoRetainer.All.Contains(upper ?? "") || !HospitalOrthoRetainer.All.Contains(lower ?? "")) return "Choose the retainer of each arch";
			if (upper == HospitalOrthoRetainer.None && lower == HospitalOrthoRetainer.None) return "A case in retention wears a retainer on at least one arch";
			return null;
		}

		/// The next retention review after `lastSeen` (the last visit since debonding, or the debond day itself).
		public static DateTime NextReview(DateTime debonded, DateTime lastSeen)
		{
			foreach (var m in ReviewMonths) { var d = debonded.Date.AddMonths(m); if (d > lastSeen.Date) return d; }
			var y = debonded.Date.AddMonths(24);
			while (y <= lastSeen.Date) y = y.AddMonths(12);
			return y;
		}

		/// The last day a retention case was seen since debonding (or the debond day); `visits` newest first.
		public static DateTime LastSeen(DateTime debonded, IReadOnlyList<(DateTime Date, string? Hygiene, int? Breakages)> visits) =>
			visits.Where(v => v.Date > debonded.Date).Select(v => (DateTime?)v.Date).FirstOrDefault() ?? debonded.Date;

		/// The warnings of a case; `visits` newest first.
		public static IReadOnlyList<string> Warnings(HospitalOrthoCase c, IReadOnlyList<(DateTime Date, string? Hygiene, int? Breakages)> visits, DateTime today)
		{
			var w = new List<string>();
			if (!HospitalOrthoStatus.Open(c.Status)) return w;
			if (visits.Count >= 2 && visits[0].Hygiene == HospitalOrthoHygiene.Poor && visits[1].Hygiene == HospitalOrthoHygiene.Poor) w.Add(WarnHygiene);
			if (c.Status == HospitalOrthoStatus.Active && visits.Sum(v => v.Breakages ?? 0) >= BreakagesLimit) w.Add(WarnBreakages);
			if (c.Status == HospitalOrthoStatus.Retention && c.DebondedOn is DateTime off && NextReview(off, LastSeen(off, visits)) < today.Date) w.Add(WarnReview);
			return w;
		}
	}
}
