using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H13 — the emergency rules, pure: no database, no clock, no access. The service applies them;
	// the tests pin them (CrossBuy.Tests/HospitalEmergencyRulesTests, database-free).
	// ==========================================================================================

	public sealed record HospitalErVitals(int? BpSystolic, int? HeartRate, int? RespRate, int? SpO2, decimal? TemperatureC);

	public sealed record HospitalErTimes(DateTime ArrivedAt, DateTime? SeenAt, string Stage, string? Outcome);

	public static class HospitalEmergencyRules
	{
		/// Where a visit may go from its stage. Forward along the board, back from observation to treatment
		/// (the patient worsened), and to disposition from anywhere once a doctor has seen the patient —
		/// except that a patient may leave without being seen from triage or the waiting lane only.
		public static bool CanMove(string from, string to, bool seen)
		{
			if (from == HospitalErStage.Closed || from == to) return false;
			return (from, to) switch
			{
				(HospitalErStage.Triage, HospitalErStage.Waiting) => true,
				(HospitalErStage.Waiting, HospitalErStage.Treatment) => true,
				(HospitalErStage.Treatment, HospitalErStage.Observation) => true,
				(HospitalErStage.Observation, HospitalErStage.Treatment) => true,
				(_, HospitalErStage.Disposition) => seen,
				_ => false,
			};
		}

		/// Why a visit cannot close with this outcome (null = it can). A visit closes from disposition; a
		/// "left without being seen" closes from triage or waiting, before any doctor took the patient.
		public static string? CloseRefusal(string stage, bool seen, string outcome)
		{
			if (!HospitalErOutcome.All.Contains(outcome)) return "Choose the outcome";
			if (stage == HospitalErStage.Closed) return "This visit is already closed";
			if (outcome == HospitalErOutcome.LeftWithoutBeingSeen)
				return seen || (stage != HospitalErStage.Triage && stage != HospitalErStage.Waiting) ? "A patient a doctor has seen did not leave without being seen" : null;
			return stage == HospitalErStage.Disposition ? null : "Move the visit to disposition first";
		}

		/// The ESI "danger zone" vital signs (ESI v4, adults): any one of them makes an ESI 3–5 patient a candidate
		/// for ESI 2. The triage nurse decides; the screen warns.
		public static bool DangerZone(HospitalErVitals v)
			=> v.HeartRate > 100 || v.RespRate > 20 || v.SpO2 < 92 || v.BpSystolic < 90 || v.TemperatureC >= 39.0m;

		/// The vitals a triage must carry: ESI 1 is triaged on sight, every other level with at least the heart rate and SpO2.
		public static string? TriageRefusal(int esi, HospitalErVitals v)
		{
			if (esi is < 1 or > 5) return "The triage level is 1 to 5";
			if (esi == 1) return null;
			if (v.HeartRate == null || v.SpO2 == null) return "Enter at least the heart rate and SpO2";
			if (v.HeartRate is < 20 or > 250 || v.SpO2 is < 40 or > 100 || v.RespRate is < 4 or > 70 || v.BpSystolic is < 40 or > 300 || v.TemperatureC is < 30 or > 45)
				return "A vital sign is out of the possible range";
			return null;
		}

		/// Minutes from the door to the doctor, or to `now` while the patient still waits.
		public static int WaitMinutes(DateTime arrivedAt, DateTime? seenAt, DateTime now)
			=> (int)Math.Max(0, Math.Floor(((seenAt ?? now) - arrivedAt).TotalMinutes));

		/// The board lanes sort the sickest first, then who waited longest.
		public static IEnumerable<T> BoardOrder<T>(IEnumerable<T> visits, Func<T, int?> esi, Func<T, DateTime> arrivedAt)
			=> visits.OrderBy(v => esi(v) ?? 6).ThenBy(arrivedAt);

		/// The average door-to-doctor of the patients a doctor saw (null when none).
		public static int? DoorToDoctor(IEnumerable<HospitalErTimes> visits)
		{
			var seen = visits.Where(v => v.SeenAt != null).Select(v => (v.SeenAt!.Value - v.ArrivedAt).TotalMinutes).ToList();
			return seen.Count == 0 ? null : (int)Math.Round(seen.Average());
		}

		/// The share of closed visits that left without being seen, in percent (null when none closed).
		public static decimal? LeftWithoutBeingSeenRate(IEnumerable<HospitalErTimes> visits)
		{
			var closed = visits.Where(v => v.Stage == HospitalErStage.Closed).ToList();
			if (closed.Count == 0) return null;
			return Math.Round(closed.Count(v => v.Outcome == HospitalErOutcome.LeftWithoutBeingSeen) * 100m / closed.Count, 1, MidpointRounding.AwayFromZero);
		}
	}
}
