using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// RUNNING A VALIDATED QUERY.
	//
	// Nothing here interprets anything. By the time a query arrives it has already been checked
	// against the surface's declaration, so this code only has to group, add up, and be honest about
	// what it could not add up. That division is the point: the interpreting is in one place and the
	// arithmetic is in another, and neither can be confused for the other during review.
	//
	// THE AGGREGATION IS IN MEMORY, DELIBERATELY. The surface has already narrowed the rows to one
	// company and its own rules, which for sales is a few hundred. Pushing group-by into SQL would
	// buy speed and cost the one thing that matters more — the NULL handling below, which is easy to
	// state in C# and easy to get silently wrong in a generated SQL expression. If a surface ever
	// grows past what memory can take, that surface moves its aggregation down; this runner does not
	// change.
	// =================================================================================================
	public static class AskSurfaceRunner
	{
		/// What an aggregate could NOT see, counted per measure. A sum that quietly skipped a third
		/// of its rows is not a sum, and the reader must be told which measure was thin.
		public sealed class MeasureCoverage
		{
			public string Column { get; init; } = "";
			public int Counted { get; init; }
			public int Skipped { get; init; }
		}

		public sealed class Result
		{
			public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
				= Array.Empty<IReadOnlyDictionary<string, object?>>();
			public int RowsRead { get; init; }
			public int GroupsFound { get; init; }
			public IReadOnlyList<MeasureCoverage> Coverage { get; init; } = Array.Empty<MeasureCoverage>();
			public string? FromDate { get; init; }
			public string? ToDate { get; init; }

			/// The two periods a comparison actually used, taken from the data. Null on an ordinary
			/// answer. Named in the response because "compared with the previous month" is not a
			/// claim a reader can check, and "2026-08 against 2026-07" is.
			public string? Period { get; init; }
			public string? PriorPeriod { get; init; }
		}

		/// Row keys a comparison adds. Prefixed so they cannot collide with a surface's own column.
		public const string PriorValueKey = "__prior";
		public const string ChangeKey = "__change";
		public const string ChangePercentKey = "__changePct";

		public static Result Run(
			AskQuery query, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AskSurface surface)
		{
			var filtered = rows.Where(r => query.Filters.All(f => Matches(r, f))).ToList();

			// The span actually covered, from the data rather than from an assumed window. Every
			// "last 90 days" default this service ever had answered "nothing" on data that stopped
			// 97 days ago, with a straight face.
			var dates = filtered
				.Select(r => r.TryGetValue("date", out var d) ? d as string : null)
				.Where(d => !string.IsNullOrEmpty(d))
				.ToList();

			// ---- A RECORD QUERY returns rows, not totals ------------------------------------------
			//
			// Ordered by the column the caller named and cut to the limit. No grouping, no
			// aggregation, and deliberately no offset: this answers "the last one" and cannot be
			// used to walk the table a page at a time.
			if (query.Measures.Count == 0)
			{
				var ordering = query.OrderBy ?? "";
				var sorted = query.Descending
					? filtered.OrderByDescending(r => Key(r, ordering), StringComparer.Ordinal)
					: filtered.OrderBy(r => Key(r, ordering), StringComparer.Ordinal);

				return new Result
				{
					Rows = sorted.Take(query.Limit).ToList(),
					RowsRead = filtered.Count,
					GroupsFound = filtered.Count,
					Coverage = Array.Empty<MeasureCoverage>(),
					FromDate = dates.Count == 0 ? null : dates.Min(),
					ToDate = dates.Count == 0 ? null : dates.Max(),
				};
			}

			// ---- A COMPARISON ANSWERS "AND HOW DOES THAT COMPARE", a different question ----------
			//
			// This is the surface layer's one step from HOW MUCH towards WHY, and almost all of its
			// difficulty is in refusing to invent a baseline:
			//
			//   * THE PERIODS COME FROM THE DATA, NOT THE CALENDAR. "Last month" taken from today's
			//     date is wrong here - stock movements fall on 28 days out of 312, and the calendar's
			//     previous month may hold nothing at all. The two most recent months that actually HAVE
			//     rows are the ones compared, and the answer names them so the reader can see which.
			//
			//   * A GROUP WITH NO PREVIOUS PERIOD IS NEW, NOT UP BY 100%. Its previous value stays
			//     null. A percentage against zero is undefined, and rendering it as +100% would be the
			//     most misleading number this layer could produce.
			//
			//   * A GROUP THAT DISAPPEARED IS STILL SHOWN. Something that sold last period and not
			//     this one is the most interesting row on the page; dropping it for having no current
			//     value would hide exactly the change worth seeing.
			if (query.ComparePeriods && query.Measures.Count > 0 && surface.Column("month") != null)
			{
				var periods = filtered
					.Select(r => Key(r, "month"))
					.Where(s => s.Length > 0)
					.Distinct(StringComparer.Ordinal)
					.OrderByDescending(s => s, StringComparer.Ordinal)
					.ToList();

				// Fewer than two periods with data means there is nothing to compare against. The
				// caller falls through to the ordinary answer rather than being shown a comparison
				// with an empty side, and Explain says the comparison could not be made.
				if (periods.Count >= 2)
					return Compare(query, filtered, surface, periods[0], periods[1], dates);
			}

			var groups = filtered
				.GroupBy(r => string.Join('\u001f', query.GroupBy.Select(g => Key(r, g))))
				.ToList();

			var coverage = new List<MeasureCoverage>();
			foreach (var m in query.Measures)
			{
				var col = surface.Column(m.Column);
				if (col is not { Nullable: true }) continue;
				var counted = filtered.Count(r => Num(r, m.Column) != null);
				coverage.Add(new MeasureCoverage
				{
					Column = m.Column,
					Counted = counted,
					Skipped = filtered.Count - counted,
				});
			}

			var shaped = new List<(IReadOnlyDictionary<string, object?> Row, decimal? Sort)>();
			foreach (var g in groups)
			{
				var row = new Dictionary<string, object?>();
				var first = g.First();
				foreach (var dim in query.GroupBy) row[dim] = Key(first, dim);

				// A GROUP KEEPS A LINK ONLY IF THE WHOLE GROUP IS ONE RECORD.
				//
				// Taking the first row's link would be the easy thing and would be wrong: "revenue by
				// month" groups hundreds of lines, and linking that row to whichever invoice happened
				// to sort first is a link that WORKS, looks right, and opens the wrong document. A
				// wrong link is worse than no link, because no link is visibly absent and a wrong one
				// is only discovered by the person who acted on it.
				foreach (var link in g.First().Keys.Where(k => k.StartsWith(AskSurface.LinkPrefix, StringComparison.Ordinal)).ToList())
				{
					string? only = null;
					var same = true;
					foreach (var r in g)
					{
						var v = r.TryGetValue(link, out var raw) ? raw as string : null;
						if (only == null) { only = v; continue; }
						if (!string.Equals(only, v, StringComparison.Ordinal)) { same = false; break; }
					}
					if (same && !string.IsNullOrEmpty(only)) row[link] = only;
				}

				decimal? sortValue = null;
				foreach (var m in query.Measures)
				{
					var value = Aggregate(g, m);
					row[m.Column] = value;
					if (string.Equals(query.OrderBy, m.Column, StringComparison.OrdinalIgnoreCase))
						sortValue = value;
				}
				if (query.OrderBy == null && query.Measures.Count > 0)
					sortValue = row.TryGetValue(query.Measures[0].Column, out var v) ? v as decimal? : null;

				shaped.Add((row, sortValue));
			}

			// A GROUP WHOSE SORT MEASURE IS NULL GOES LAST, never first. Ordering by a measure that
			// could not be computed would put the worst data at the top of the answer.
			var ordered = query.Descending
				? shaped.OrderByDescending(x => x.Sort ?? decimal.MinValue)
				: shaped.OrderBy(x => x.Sort ?? decimal.MaxValue);

			return new Result
			{
				Rows = ordered.Take(query.Limit).Select(x => x.Row).ToList(),
				RowsRead = filtered.Count,
				GroupsFound = groups.Count,
				Coverage = coverage,
				FromDate = dates.Count == 0 ? null : dates.Min(),
				ToDate = dates.Count == 0 ? null : dates.Max(),
			};
		}

		/// ONE MEASURE, TWO PERIODS, AND AN HONEST GAP WHERE A BASELINE IS MISSING.
		///
		/// Only the first measure is compared. A row showing three measures each against its own
		/// previous value is a table nobody reads, and the question "did it go up" is always about
		/// one number.
		private static Result Compare(
			AskQuery query,
			List<IReadOnlyDictionary<string, object?>> filtered,
			AskSurface surface,
			string period,
			string prior,
			List<string> dates)
		{
			var m = query.Measures[0];

			// Both periods are grouped by the SAME key, so a group is matched to itself across time
			// rather than by position in two separately sorted lists.
			string KeyOf(IReadOnlyDictionary<string, object?> r) =>
				query.GroupBy.Count == 0 ? "" : string.Join('', query.GroupBy.Select(g => Key(r, g)));

			var now = filtered.Where(r => string.Equals(Key(r, "month"), period, StringComparison.Ordinal)).ToList();
			var was = filtered.Where(r => string.Equals(Key(r, "month"), prior, StringComparison.Ordinal)).ToList();

			var nowBy = now.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
			var wasBy = was.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

			// THE UNION, not the current period's keys. A group that vanished has no row in `now`
			// and is precisely the one worth showing.
			var keys = nowBy.Keys.Union(wasBy.Keys, StringComparer.Ordinal).ToList();

			var shaped = new List<(IReadOnlyDictionary<string, object?> Row, decimal? Sort)>();
			foreach (var k in keys)
			{
				nowBy.TryGetValue(k, out var nowRows);
				wasBy.TryGetValue(k, out var wasRows);

				var current = nowRows == null ? null : Aggregate(nowRows, m);
				var previous = wasRows == null ? null : Aggregate(wasRows, m);

				var row = new Dictionary<string, object?>();
				var sample = (nowRows ?? wasRows)!.First();
				foreach (var dim in query.GroupBy) row[dim] = Key(sample, dim);

				foreach (var link in sample.Keys.Where(x => x.StartsWith(AskSurface.LinkPrefix, StringComparison.Ordinal)))
				{
					var all = (nowRows ?? new List<IReadOnlyDictionary<string, object?>>())
						.Concat(wasRows ?? new List<IReadOnlyDictionary<string, object?>>())
						.Select(r => r.TryGetValue(link, out var v) ? v as string : null)
						.Distinct(StringComparer.Ordinal).ToList();
					if (all.Count == 1 && !string.IsNullOrEmpty(all[0])) row[link] = all[0];
				}

				row[m.Column] = current;
				row[PriorValueKey] = previous;
				// NULL ON EITHER SIDE MEANS NO CHANGE CAN BE STATED. Not zero, not 100% - absent.
				row[ChangeKey] = current == null || previous == null ? null : current - previous;
				// AND A PERCENTAGE NEEDS A NON-ZERO BASE. Dividing by zero here is the difference
				// between "new this period" and "up infinitely", and only one of those is true.
				// AND THE ROUNDING MUST NOT CROSS A MEANING. A fall from 103,039.85 to 7.50 is
				// -99.9927%, which rounds to -100.0% and then reads as "gone" — but 7.50 is not
				// nothing. -100% is a claim about disappearance, so a non-zero current value is
				// never allowed to render as it; it is held just short instead.
				if (current == null || previous == null || previous.Value == 0m)
				{
					row[ChangePercentKey] = null;
				}
				else
				{
					var pct = decimal.Round((current.Value - previous.Value) / Math.Abs(previous.Value) * 100m, 1);
					if (current.Value != 0m && pct <= -100m) pct = -99.9m;
					row[ChangePercentKey] = pct;
				}

				shaped.Add((row, current));
			}

			// Ordered by the CURRENT value, so the answer still reads as "the biggest things", with
			// a group that has none sorting last rather than first.
			var ordered = query.Descending
				? shaped.OrderByDescending(x => x.Sort ?? decimal.MinValue)
				: shaped.OrderBy(x => x.Sort ?? decimal.MaxValue);

			return new Result
			{
				Rows = ordered.Take(query.Limit).Select(x => x.Row).ToList(),
				RowsRead = now.Count + was.Count,
				GroupsFound = keys.Count,
				Coverage = Array.Empty<MeasureCoverage>(),
				FromDate = dates.Count == 0 ? null : dates.Min(),
				ToDate = dates.Count == 0 ? null : dates.Max(),
				Period = period,
				PriorPeriod = prior,
			};
		}

		private static string Key(IReadOnlyDictionary<string, object?> row, string column)
			=> row.TryGetValue(column, out var v) && v != null
				? Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
				: "";

		private static decimal? Num(IReadOnlyDictionary<string, object?> row, string column)
		{
			if (!row.TryGetValue(column, out var v) || v == null) return null;
			return v switch
			{
				decimal d => d,
				int i => i,
				long l => l,
				double db => (decimal)db,
				_ => decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
						NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
			};
		}

		private static decimal? Aggregate(IEnumerable<IReadOnlyDictionary<string, object?>> group, AskMeasure m)
		{
			if (m.Agg == AskAgg.Count) return group.Count();

			// NULLS ARE SKIPPED, NOT ZEROED, and when every value is null the answer is null rather
			// than 0. A sum over nothing is not zero: zero is a measurement and this is its absence.
			var values = group.Select(r => Num(r, m.Column)).Where(v => v != null).Select(v => v!.Value).ToList();
			if (values.Count == 0) return null;

			return m.Agg switch
			{
				AskAgg.Sum => values.Sum(),
				AskAgg.Average => values.Average(),
				AskAgg.Min => values.Min(),
				AskAgg.Max => values.Max(),
				_ => values.Sum(),
			};
		}

		private static bool Matches(IReadOnlyDictionary<string, object?> row, AskFilter f)
		{
			var raw = row.TryGetValue(f.Column, out var v) ? v : null;
			var text = raw == null ? "" : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";

			// A numeric comparison on a numeric column, a folded text comparison otherwise. Both
			// sides are folded for text, because folding one side guarantees a miss — the single
			// most repeated defect in this whole service.
			var number = Num(row, f.Column);
			var wanted = decimal.TryParse(f.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var w)
				? w : (decimal?)null;

			switch (f.Op)
			{
				case AskOp.Equals:
					return number != null && wanted != null
						? number.Value == wanted.Value
						: AskSearch.Normalise(text) == AskSearch.Normalise(f.Value);
				case AskOp.NotEquals:
					return number != null && wanted != null
						? number.Value != wanted.Value
						: AskSearch.Normalise(text) != AskSearch.Normalise(f.Value);
				case AskOp.Contains:
					return AskSearch.Normalise(text).Contains(AskSearch.Normalise(f.Value), StringComparison.Ordinal);
				case AskOp.GreaterThan:
					return number != null && wanted != null
						? number.Value > wanted.Value
						: string.CompareOrdinal(text, f.Value) > 0;
				case AskOp.LessThan:
					return number != null && wanted != null
						? number.Value < wanted.Value
						: string.CompareOrdinal(text, f.Value) < 0;
				case AskOp.Between:
					if (number != null && wanted != null
						&& decimal.TryParse(f.Value2, NumberStyles.Any, CultureInfo.InvariantCulture, out var w2))
						return number.Value >= wanted.Value && number.Value <= w2;
					// Dates are compared as yyyy-MM-dd text, which sorts correctly as text.
					return string.CompareOrdinal(text, f.Value) >= 0
						&& string.CompareOrdinal(text, f.Value2 ?? f.Value) <= 0;
				default:
					return true;
			}
		}
	}
}
