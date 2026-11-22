using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalAbgIsolate(int IsolateId, int PatientId, int OrganismId, string Organism, string Group, DateTime CollectedAt,
		IReadOnlyList<(int AntibioticId, string Antibiotic, string Result)> Results);
	public sealed record HospitalAbgCell(int OrganismId, string Organism, string Group, int Isolates, int AntibioticId, string Antibiotic, int Tested, int Susceptible)
	{
		public decimal? Pct => Tested == 0 ? null : Math.Round(Susceptible * 100m / Tested, 0);
		/// CLSI M39: a percentage on fewer than 30 isolates is shown with a caution (or not at all).
		public bool Caution => Tested < HospitalAntibiogramRules.MinIsolates;
	}

	// ==========================================================================================
	// HOSPITAL H231 — the cumulative antibiogram (CLSI M39): for a period, the FIRST isolate of each organism per patient
	// (repeats of the same patient would weight the map towards the sickest), and per organism × antibiotic the share
	// reported Susceptible. "I" is not counted as susceptible. Fewer than 30 tested isolates carry a caution.
	// ==========================================================================================
	public static class HospitalAntibiogramRules
	{
		public const int MinIsolates = 30;

		public static IReadOnlyList<HospitalAbgIsolate> FirstIsolates(IEnumerable<HospitalAbgIsolate> isolates) =>
			isolates.GroupBy(i => (i.PatientId, i.OrganismId)).Select(g => g.OrderBy(i => i.CollectedAt).ThenBy(i => i.IsolateId).First()).ToList();

		public static IReadOnlyList<HospitalAbgCell> Build(IEnumerable<HospitalAbgIsolate> isolates)
		{
			var first = FirstIsolates(isolates);
			var cells = new List<HospitalAbgCell>();
			foreach (var org in first.GroupBy(i => (i.OrganismId, i.Organism, i.Group)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Organism))
			{
				int n = org.Count();
				foreach (var abx in org.SelectMany(i => i.Results).GroupBy(r => (r.AntibioticId, r.Antibiotic)).OrderBy(g => g.Key.Antibiotic))
					cells.Add(new HospitalAbgCell(org.Key.OrganismId, org.Key.Organism, org.Key.Group, n, abx.Key.AntibioticId, abx.Key.Antibiotic,
						abx.Count(), abx.Count(r => r.Result == HospitalMicroSir.Susceptible)));
			}
			return cells;
		}
	}
}
