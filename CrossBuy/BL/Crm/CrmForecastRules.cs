using System.Globalization;

namespace CrossBuy.BL.CrmIdentity
{
	/// CRM C5 — the arithmetic of a forecast, with no database: periods, the category a deal falls into, the roll-up and
	/// who may sign off whose forecast. Shadow-tested in CrmForecastRulesTests.
	public static class CrmForecastRules
	{
		public static readonly string[] Categories = { "Commit", "BestCase", "Pipeline", "Omitted" };

		/// A period is a month "2026-11" or a quarter "2026-Q4". Anything else is not a period.
		public sealed record Period(string Key, int Year, int FirstMonth, int Months)
		{
			public bool IsQuarter => Months == 3;
			public DateTime Start => new(Year, FirstMonth, 1);
			public DateTime End => Start.AddMonths(Months);          // exclusive
			public IEnumerable<string> MonthKeys => Enumerable.Range(0, Months).Select(i => Start.AddMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture));
			public Period Previous => IsQuarter ? Quarter(Start.AddMonths(-3)) : Month(Start.AddMonths(-1));
			public Period Next => IsQuarter ? Quarter(Start.AddMonths(3)) : Month(Start.AddMonths(1));
		}

		public static Period Month(DateTime d) => new(d.ToString("yyyy-MM", CultureInfo.InvariantCulture), d.Year, d.Month, 1);
		public static Period Quarter(DateTime d) { var q = (d.Month - 1) / 3; return new($"{d.Year}-Q{q + 1}", d.Year, q * 3 + 1, 3); }

		public static Period? Parse(string? key)
		{
			if (string.IsNullOrWhiteSpace(key)) return null;
			key = key.Trim();
			if (key.Length == 7 && key[4] == '-' && int.TryParse(key[..4], out var y) && y is >= 2000 and <= 2100)
			{
				if (key[5] == 'Q' && key[6] is >= '1' and <= '4') return Quarter(new DateTime(y, (key[6] - '1') * 3 + 1, 1));
				if (int.TryParse(key[5..], out var m) && m is >= 1 and <= 12) return Month(new DateTime(y, m, 1));
			}
			return null;
		}

		/// The category a deal is in. A closed deal is closed whatever was chosen; an open deal is what its owner chose,
		/// or — until someone chooses — what its probability implies (75+ commit, 50+ best case, else pipeline).
		public static string CategoryOf(string? stage, int probability, string? chosen)
		{
			if (string.Equals(stage, "Won", StringComparison.OrdinalIgnoreCase)) return "Won";
			if (string.Equals(stage, "Lost", StringComparison.OrdinalIgnoreCase)) return "Lost";
			if (chosen != null && Categories.Contains(chosen)) return chosen;
			return probability >= 75 ? "Commit" : probability >= 50 ? "BestCase" : "Pipeline";
		}

		public sealed record Deal(int Owner, decimal Amount, string Category);

		public sealed record Rollup(decimal Quota, decimal Won, decimal Commit, decimal BestCase, decimal Pipeline)
		{
			/// What the person says will close: what is already won plus what they commit to.
			public decimal Forecast => Won + Commit;
			/// The upside call: forecast plus the best-case deals.
			public decimal Upside => Won + Commit + BestCase;
			public decimal Gap => Math.Max(0, Quota - Won);
			public int? Attainment => Quota > 0 ? (int)Math.Round(Won * 100 / Quota) : null;
			public int? ForecastPercent => Quota > 0 ? (int)Math.Round(Forecast * 100 / Quota) : null;
			/// Open pipeline (everything not omitted) over what is still missing; 3x is the usual healthy cover.
			public decimal? Coverage => Gap > 0 ? Math.Round((Commit + BestCase + Pipeline) / Gap, 1) : null;
			public static Rollup operator +(Rollup a, Rollup b) => new(a.Quota + b.Quota, a.Won + b.Won, a.Commit + b.Commit, a.BestCase + b.BestCase, a.Pipeline + b.Pipeline);
			public static readonly Rollup Zero = new(0, 0, 0, 0, 0);
		}

		public static Rollup Roll(decimal quota, IEnumerable<Deal> deals)
		{
			decimal won = 0, commit = 0, best = 0, pipe = 0;
			foreach (var d in deals)
				switch (d.Category)
				{
					case "Won": won += d.Amount; break;
					case "Commit": commit += d.Amount; break;
					case "BestCase": best += d.Amount; break;
					case "Pipeline": pipe += d.Amount; break;
					// Lost and Omitted count for nothing
				}
			return new(quota, won, commit, best, pipe);
		}

		/// A manager signs off a forecast only for someone in their own team, never their own, and only while it waits.
		public static string? ReviewRefusal(int reviewer, int submitter, IReadOnlySet<int> reviewerTeam, string status)
		{
			if (reviewer == submitter) return "You cannot sign off your own forecast";
			if (!reviewerTeam.Contains(submitter)) return "This person is not in your team";
			if (status != "Submitted") return "This forecast was already decided";
			return null;
		}
	}
}
