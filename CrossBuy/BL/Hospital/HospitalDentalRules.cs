using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H15 — the rules of the dental chart and the treatment plan, with no database.
	//
	//   · FDI / ISO 3950: two digits, quadrant then position from the midline. Permanent 11–48 (quadrants 1–4,
	//     positions 1–8), primary 51–85 (quadrants 5–8, positions 1–5). Quadrants 1, 2, 5, 6 are upper; 1, 4, 5, 8
	//     the patient's right.
	//   · Surfaces M O D B L, with I (incisal) in place of O on incisors and canines; written in chart order.
	//   · A multi-surface code bills its own number of surfaces: one line per tooth, never one per surface.
	//   · The chart of a tooth is its latest mark per surface and per work; a completed extraction leaves the
	//     tooth missing.
	//   · The quotation: the fee of every line not declined, the payer's cover per category, the payer share
	//     capped at what is left of the yearly dental limit; a filling repeated on the same surface within two
	//     years is the patient's.
	// ==========================================================================================

	public sealed record HospitalToothChart(Dictionary<string, string> Surfaces, List<(string Work, string Mark)> Work, bool Missing, bool Impacted, List<string> Planned);

	public sealed record HospitalDentalQuoteLine(int LineId, decimal Fee, decimal CoverPct, decimal PayerShare, bool Repeat);

	public sealed record HospitalDentalQuote(IReadOnlyList<HospitalDentalQuoteLine> Lines, decimal Gross, decimal PayerShare, decimal PatientShare, bool Capped);

	public sealed record HospitalPerioStats(int Sites, int Bleeding, int Deep, int Mobile);

	public static class HospitalDentalRules
	{
		public static readonly IReadOnlyList<int> Upper = new[] { 18, 17, 16, 15, 14, 13, 12, 11, 21, 22, 23, 24, 25, 26, 27, 28 };
		public static readonly IReadOnlyList<int> Lower = new[] { 48, 47, 46, 45, 44, 43, 42, 41, 31, 32, 33, 34, 35, 36, 37, 38 };
		public static readonly IReadOnlyList<int> PrimaryUpper = new[] { 55, 54, 53, 52, 51, 61, 62, 63, 64, 65 };
		public static readonly IReadOnlyList<int> PrimaryLower = new[] { 85, 84, 83, 82, 81, 71, 72, 73, 74, 75 };
		/// Mixed dentition (about 6–12): the first permanent molars and incisors beside the primary canines and molars.
		public static readonly IReadOnlyList<int> MixedUpper = new[] { 16, 55, 54, 53, 12, 11, 21, 22, 63, 64, 65, 26 };
		public static readonly IReadOnlyList<int> MixedLower = new[] { 46, 85, 84, 83, 42, 41, 31, 32, 73, 74, 75, 36 };

		public static bool IsTooth(int n)
		{
			int q = n / 10, p = n % 10;
			return (q is >= 1 and <= 4 && p is >= 1 and <= 8) || (q is >= 5 and <= 8 && p is >= 1 and <= 5);
		}
		public static bool IsPrimary(int n) => n / 10 >= 5;
		public static bool IsUpper(int n) => n / 10 is 1 or 2 or 5 or 6;
		public static bool PatientRight(int n) => n / 10 is 1 or 4 or 5 or 8;
		/// inc, can, pre or mol.
		public static string Kind(int n)
		{
			int u = n % 10;
			if (IsPrimary(n)) return u <= 2 ? "inc" : u == 3 ? "can" : "mol";
			return u <= 2 ? "inc" : u == 3 ? "can" : u <= 5 ? "pre" : "mol";
		}
		public static bool Front(int n) => Kind(n) is "inc" or "can";
		/// Roots by tooth kind: upper molars 3, lower molars 2, the upper first premolar 2, the rest 1.
		public static int Roots(int n)
		{
			var k = Kind(n);
			if (k == "mol") return IsUpper(n) ? 3 : 2;
			if (k == "pre" && IsUpper(n) && n % 10 == 4) return 2;
			return 1;
		}
		public static bool MultiRooted(int n) => Roots(n) > 1;

		/// The dentition a chart opens in when none was chosen: mixed under twelve, primary under six.
		public static string DentitionFor(DateTime? birth, DateTime today)
		{
			if (birth is not DateTime b) return HospitalDentition.Permanent;
			int age = today.Year - b.Year - (today.DayOfYear < b.DayOfYear ? 1 : 0);
			return age < 6 ? HospitalDentition.Primary : age < 12 ? HospitalDentition.Mixed : HospitalDentition.Permanent;
		}

		private const string Order = "MOIDBL";

		/// The surfaces in chart order, or null when one is unknown, repeated, or not on this tooth (O on a front
		/// tooth, I on a back one).
		public static string? NormalizeSurfaces(int tooth, string? raw)
		{
			var s = (raw ?? "").Trim().ToUpperInvariant();
			if (s.Length == 0 || s.Length > 5) return null;
			if (s.Any(c => Order.IndexOf(c) < 0) || s.Distinct().Count() != s.Length) return null;
			if (Front(tooth) ? s.Contains('O') : s.Contains('I')) return null;
			return new string(s.OrderBy(c => Order.IndexOf(c)).ToArray());
		}

		/// Why a procedure cannot go on this tooth and these surfaces, or null.
		public static string? PlacementRefusal(HospitalDentalProcedure p, int? tooth, string? surfaces)
		{
			bool needsTooth = p.NeedsTooth || p.Surfaces != null || p.ToothWork != null;
			if (!needsTooth) return tooth == null && string.IsNullOrEmpty(surfaces) ? null : "This procedure is not done on one tooth";
			if (tooth is not int t || !IsTooth(t)) return "Choose the tooth";
			if (p.Surfaces is int n)
			{
				var norm = NormalizeSurfaces(t, surfaces);
				if (norm == null) return "Choose the surfaces on the tooth";
				if (norm.Length != n) return "This code bills {0} surfaces; choose the code for the surfaces chosen";
			}
			else if (!string.IsNullOrEmpty(surfaces)) return "This procedure is not done on surfaces";
			if (p.ToothWork == HospitalToothWork.Implant && !IsPrimary(t)) return null;
			if (p.ToothWork == HospitalToothWork.Implant) return "An implant replaces a permanent tooth";
			return null;
		}

		/// The chart of every tooth from its marks (voided ones excluded by the caller), latest first wins.
		public static Dictionary<int, HospitalToothChart> Chart(IEnumerable<HospitalDentalFinding> marks, IEnumerable<(int Tooth, string Code)> planned)
		{
			var map = new Dictionary<int, HospitalToothChart>();
			HospitalToothChart Of(int t) => map.TryGetValue(t, out var c) ? c : map[t] = new HospitalToothChart(new(), new(), false, false, new());
			foreach (var g in marks.Where(m => m.VoidedAt == null).OrderBy(m => m.CompletedAt ?? m.RecordedAt).ThenBy(m => m.ID).GroupBy(m => m.Tooth))
			{
				var c = Of(g.Key);
				bool missing = false, impacted = false;
				foreach (var m in g)
				{
					if (m.Surfaces != null) { foreach (var s in m.Surfaces) c.Surfaces[s.ToString()] = m.Status; continue; }
					switch (m.Work)
					{
						case HospitalToothWork.Missing: missing = true; continue;
						case HospitalToothWork.Impacted: impacted = true; continue;
						case HospitalToothWork.Extraction when m.Status is HospitalDentalMark.Completed or HospitalDentalMark.Existing:
							// a tooth taken out is missing: its surfaces and the work on it go with it (what comes after — an implant and its crown — stays)
							missing = true; impacted = false; c.Surfaces.Clear(); c.Work.Clear(); continue;
					}
					c.Work.RemoveAll(w => w.Work == m.Work);
					c.Work.Add((m.Work!, m.Status));
				}
				map[g.Key] = c with { Missing = missing, Impacted = impacted };
			}
			foreach (var (t, code) in planned) Of(t).Planned.Add(code);
			return map;
		}

		/// A plan's state from its phases: presented while none is decided, accepted or declined when all are,
		/// partly accepted otherwise.
		public static string PlanState(IEnumerable<string> decisions)
		{
			var d = decisions.ToList();
			if (d.Count == 0 || d.All(x => x == HospitalDentalDecision.Pending)) return "Presented";
			if (d.All(x => x == HospitalDentalDecision.Declined)) return "Declined";
			if (d.All(x => x == HospitalDentalDecision.Accepted)) return "Accepted";
			return d.Any(x => x == HospitalDentalDecision.Accepted) ? "Partial" : "Presented";
		}

		public static decimal CoverFor(HospitalDentalBenefit? b, string category) => b == null ? 0 : category switch
		{
			HospitalDentalCategory.Diagnostic => b.DiagnosticPct,
			HospitalDentalCategory.Preventive => b.PreventivePct,
			HospitalDentalCategory.Restorative => b.RestorativePct,
			HospitalDentalCategory.Endodontic => b.EndodonticPct,
			HospitalDentalCategory.Prosthetic => b.ProstheticPct,
			HospitalDentalCategory.Surgery => b.SurgeryPct,
			HospitalDentalCategory.Implant => b.ImplantPct,
			HospitalDentalCategory.Cosmetic => b.CosmeticPct,
			_ => 0,
		};

		/// A filling on a surface that had one completed within two years is not paid again (the payer rule).
		public static bool RepeatWithinTwoYears(int? tooth, string? surfaces, IEnumerable<(int Tooth, string Surfaces, DateTime At)> completed, DateTime day) =>
			tooth is int t && !string.IsNullOrEmpty(surfaces) && completed.Any(c => c.Tooth == t && c.At > day.AddYears(-2) && c.Surfaces.Intersect(surfaces).Any());

		public sealed record QuoteInput(int LineId, decimal Fee, string Category, bool NotCovered, bool Declined, bool Repeat);

		/// The quotation of a plan. `usedThisYear` is the payer share already spent from the yearly limit; null
		/// benefit = self-pay.
		public static HospitalDentalQuote Quote(IEnumerable<QuoteInput> lines, HospitalDentalBenefit? benefit, decimal usedThisYear, int decimals)
		{
			var rows = new List<HospitalDentalQuoteLine>();
			decimal gross = 0, payer = 0;
			foreach (var l in lines.Where(x => !x.Declined))
			{
				decimal pct = l.NotCovered || l.Repeat ? 0 : CoverFor(benefit, l.Category);
				decimal share = Math.Round(l.Fee * pct / 100m, decimals, MidpointRounding.AwayFromZero);
				rows.Add(new(l.LineId, l.Fee, pct, share, l.Repeat));
				gross += l.Fee; payer += share;
			}
			bool capped = false;
			if (benefit != null && benefit.YearlyLimit > 0)
			{
				decimal left = Math.Max(0, benefit.YearlyLimit - usedThisYear);
				if (payer > left) { payer = left; capped = true; }
			}
			return new HospitalDentalQuote(rows, gross, payer, gross - payer, capped);
		}

		// ---------------- periodontal chart ----------------

		/// Six comma-separated integers in a range, or null.
		public static int[]? Sites(string? csv, int min, int max)
		{
			var p = (csv ?? "").Split(',', StringSplitOptions.TrimEntries);
			if (p.Length != 6) return null;
			var v = new int[6];
			for (int i = 0; i < 6; i++) if (!int.TryParse(p[i], out v[i]) || v[i] < min || v[i] > max) return null;
			return v;
		}
		public static bool[]? Bleeding(string? s) => s is { Length: 6 } && s.All(c => c is '0' or '1') ? s.Select(c => c == '1').ToArray() : null;

		/// Why a tooth's perio record cannot be kept, or null: depths 0–15 mm, recession −5…15, mobility 0–3,
		/// furcation I–III on a multi-rooted tooth only.
		public static string? PerioRefusal(int tooth, string? depths, string? recession, string? bleeding, int? mobility, int? furcation)
		{
			if (!IsTooth(tooth) || IsPrimary(tooth)) return "Tooth {0}: perio is charted on the permanent teeth";
			if (Sites(depths, 0, 15) == null) return "Tooth {0}: enter six pocket depths of 0 to 15 mm";
			if (Sites(recession, -5, 15) == null) return "Tooth {0}: enter six recession values of -5 to 15 mm";
			if (Bleeding(bleeding) == null) return "Tooth {0}: mark bleeding on the six sites";
			if (mobility is < 0 or > 3) return "Tooth {0}: mobility is graded 0 to 3";
			if (furcation != null && (furcation is < 1 or > 3 || !MultiRooted(tooth))) return "Tooth {0}: furcation is graded I to III on a multi-rooted tooth";
			return null;
		}

		public static HospitalPerioStats PerioStats(IEnumerable<HospitalPerioTooth> teeth)
		{
			int sites = 0, bleed = 0, deep = 0, mobile = 0;
			foreach (var t in teeth)
			{
				var d = Sites(t.Depths, 0, 15); var b = Bleeding(t.Bleeding);
				if (d == null || b == null) continue;
				sites += 6; bleed += b.Count(x => x); deep += d.Count(x => x >= 5);
				if (t.Mobility is > 0) mobile++;
			}
			return new HospitalPerioStats(sites, bleed, deep, mobile);
		}

		/// "TP-2026-0214": the year, then the next number of that year.
		public static string NextPlanNo(IEnumerable<string> existing, int year)
		{
			string prefix = "TP-" + year + "-";
			int n = 0;
			foreach (var no in existing) if (no.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(no.AsSpan(prefix.Length), out var v) && v > n) n = v;
			return prefix + (n + 1).ToString("0000");
		}
	}
}
