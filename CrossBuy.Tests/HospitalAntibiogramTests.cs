using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H231 — the cumulative antibiogram. What must never happen: a patient sampled five times counted five times
	// (the map leans to the sickest), "I" counted as susceptible, a percentage on a handful of isolates shown as firm,
	// or two organisms' results mixed.
	// ==========================================================================================
	public class HospitalAntibiogramTests
	{
		private static readonly DateTime D = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
		private static HospitalAbgIsolate I(int id, int patient, int org, int day, params (int Abx, string R)[] results) =>
			new(id, patient, org, org == 1 ? "Escherichia coli" : "Staphylococcus aureus", org == 1 ? HospitalMicroGroup.Enterobacterales : HospitalMicroGroup.Staphylococci,
				D.AddDays(day), results.Select(r => (r.Abx, r.Abx == 10 ? "Ceftriaxone" : r.Abx == 11 ? "Meropenem" : "Oxacillin", r.R)).ToList());

		[Fact]
		public void Each_patient_counts_once_per_organism_with_their_first_isolate()
		{
			var cells = HospitalAntibiogramRules.Build(new[]
			{
				I(3, 100, 1, 5, (10, "S")), I(1, 100, 1, 0, (10, "R")), I(2, 100, 1, 2, (10, "R")),   // patient 100: the day-0 isolate counts
				I(4, 200, 1, 1, (10, "S")),
				I(5, 100, 2, 3, (12, "R")),                                                           // the same patient, another organism: counts
			});
			var cro = Assert.Single(cells, c => c.OrganismId == 1 && c.AntibioticId == 10);
			Assert.Equal((2, 2, 1, 50m), (cro.Isolates, cro.Tested, cro.Susceptible, cro.Pct));
			var oxa = Assert.Single(cells, c => c.OrganismId == 2);
			Assert.Equal((1, 0, 0m), (oxa.Tested, oxa.Susceptible, oxa.Pct));
		}

		[Fact]
		public void Intermediate_is_not_susceptible_and_few_isolates_carry_a_caution()
		{
			var many = Enumerable.Range(1, 30).Select(n => I(n, n, 1, 0, (11, n <= 27 ? "S" : "I"))).ToList();
			var mem = Assert.Single(HospitalAntibiogramRules.Build(many));
			Assert.Equal((30, 27, 90m, false), (mem.Tested, mem.Susceptible, mem.Pct, mem.Caution));
			var few = Assert.Single(HospitalAntibiogramRules.Build(many.Take(29)));
			Assert.True(few.Caution);
			Assert.Empty(HospitalAntibiogramRules.Build(Array.Empty<HospitalAbgIsolate>()));
		}

		[Fact]
		public void The_most_frequent_organism_comes_first()
		{
			var cells = HospitalAntibiogramRules.Build(new[] { I(1, 1, 2, 0, (12, "S")), I(2, 2, 1, 0, (10, "S")), I(3, 3, 1, 0, (10, "S")) });
			Assert.Equal(1, cells[0].OrganismId);
		}
	}
}
