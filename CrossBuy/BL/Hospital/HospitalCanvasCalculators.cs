using System.Text.Json;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H100 — the canvas calculators, recomputed by the SERVER from the drawing's own state (the browser's
	// figures are never stored). The burn chart (the approved prototype screen cv-burn):
	//   * each body region (front / back) has a depth: none, superficial partial, deep partial, full thickness;
	//   * %TBSA counts partial + full thickness by the rule of nines (adult). A child under 15 uses Lund–Browder's
	//     larger head: the head's extra share comes off the legs, by age band (0, 1, 5, 10 years);
	//   * Parkland: 4 mL × kg × %TBSA in 24 h, half in the first 8 h FROM THE BURN; 20 % or more → a burn centre.
	// ==========================================================================================
	public sealed record HospitalBurnResult(IReadOnlyDictionary<string, int> Depths, decimal? Kg, decimal Tbsa, int? FluidsMl, bool Refer, IReadOnlyDictionary<string, decimal> Share);

	public static class HospitalBurnRules
	{
		public const int Depths = 4;   // 0 none · 1 superficial partial · 2 deep partial · 3 full thickness

		/// Every region of the chart: the body regions on the front (F) and the back (B); the perineum has no back.
		public static IReadOnlyList<string> Cells() => HospitalCanvasRules.BodyRegions.SelectMany(r => r.Key == "groin" ? new[] { r.Key + "F" } : new[] { r.Key + "F", r.Key + "B" }).ToList();

		/// Lund–Browder's half-head (per view) by age band; 15 and over: the adult rule of nines (4.5).
		public static decimal HeadHalf(int? ageYears) => ageYears switch { null => 4.5m, < 1 => 9.5m, < 5 => 8.5m, < 10 => 6.5m, < 15 => 5.5m, _ => 4.5m };

		/// Each cell's % of the body for this age: the head's extra share is taken evenly off the four leg halves.
		public static IReadOnlyDictionary<string, decimal> Share(int? ageYears)
		{
			decimal head = HeadHalf(ageYears), extra = (head - 4.5m) * 2, perLeg = extra / 4;
			var d = new Dictionary<string, decimal>();
			foreach (var c in Cells())
			{
				var r = HospitalCanvasRules.BodyRegions.First(x => c.StartsWith(x.Key) && c.Length == x.Key.Length + 1);
				d[c] = r.Key == "head" ? head : r.Key is "legR" or "legL" ? r.Nines - perLeg : r.Nines;
			}
			return d;
		}

		/// Reads the drawing's state {"d":{"armRF":2,…},"kg":70}; refuses an unknown region or depth.
		public static (HospitalBurnResult? Result, string? Error) Read(string? state, int? ageYears)
		{
			var depths = new Dictionary<string, int>(); decimal? kg = null;
			var cells = Cells();
			if (!string.IsNullOrEmpty(state))
			{
				try
				{
					using var doc = JsonDocument.Parse(state);
					var root = doc.RootElement;
					if (root.ValueKind != JsonValueKind.Object) return (null, "The drawing's data could not be read");
					if (root.TryGetProperty("d", out var d))
					{
						if (d.ValueKind != JsonValueKind.Object) return (null, "The drawing's data could not be read");
						foreach (var p in d.EnumerateObject())
						{
							if (!cells.Contains(p.Name) || p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var v) || v < 0 || v >= Depths) return (null, "The drawing's data could not be read");
							if (v > 0) depths[p.Name] = v;
						}
					}
					if (root.TryGetProperty("kg", out var k) && k.ValueKind == JsonValueKind.Number)
					{
						var w = k.GetDecimal();
						if (w is <= 0 or > 400) return (null, "The weight is between 0 and 400 kg");
						kg = Math.Round(w, 1);
					}
				}
				catch (Exception e) when (e is JsonException or FormatException) { return (null, "The drawing's data could not be read"); }
			}
			var share = Share(ageYears);
			decimal tbsa = depths.Where(x => x.Value >= 1).Sum(x => share[x.Key]);
			int? fluids = kg is decimal kgv && tbsa > 0 ? (int)Math.Round(4 * kgv * tbsa) : null;
			return (new HospitalBurnResult(depths, kg, tbsa, fluids, tbsa >= 20, share), null);
		}

		/// The state as it is STORED: the depths and the weight, with the server's own figures beside them.
		public static string Store(HospitalBurnResult r) => JsonSerializer.Serialize(new { d = r.Depths, kg = r.Kg, tbsa = r.Tbsa, fluidsMl = r.FluidsMl, refer = r.Refer });
	}
	// ==========================================================================================
	// HOSPITAL H101 — the audiogram (the approved prototype screen cv-audio), with the ASHA symbols: air O right / X left,
	// bone < right / > left (stored as AR, AL, BR, BL), 250–8000 Hz, -10 to 120 dB HL in 5 dB steps.
	//   * pure-tone average = air at 500, 1000, 2000, 4000 Hz; graded by WHO 2021 (World report on hearing);
	//   * an air–bone gap of 15 dB or more at any of those frequencies → a conductive loss on that ear;
	//   * the two ears' averages 15 dB or more apart → rule out a retrocochlear cause.
	// ==========================================================================================
	public sealed record HospitalAudioResult(IReadOnlyDictionary<string, int> Points, decimal? PtaR, decimal? PtaL, string? GradeR, string? GradeL, bool GapR, bool GapL, bool Asymmetry);

	public static class HospitalAudioRules
	{
		public static readonly IReadOnlyList<int> Freq = new[] { 250, 500, 1000, 2000, 4000, 8000 };
		public static readonly IReadOnlyList<int> PtaFreq = new[] { 500, 1000, 2000, 4000 };
		public static readonly IReadOnlyList<string> Symbols = new[] { "AR", "AL", "BR", "BL" };

		/// WHO 2021 grades of the better/each ear's average (dB HL).
		public static string Grade(decimal pta) => pta switch
		{
			< 20 => "Normal", < 35 => "Mild", < 50 => "Moderate", < 65 => "Moderately severe", < 80 => "Severe", < 95 => "Profound", _ => "Complete",
		};

		private static decimal? Pta(IReadOnlyDictionary<string, int> p, string sym)
		{
			var v = PtaFreq.Select(f => p.TryGetValue(sym + f, out var x) ? (int?)x : null).ToList();
			return v.Any(x => x == null) ? null : Math.Round((decimal)v.Sum(x => x!.Value) / v.Count, 1);
		}

		private static bool Gap(IReadOnlyDictionary<string, int> p, string air, string bone) =>
			PtaFreq.Any(f => p.TryGetValue(air + f, out var a) && p.TryGetValue(bone + f, out var b) && a - b >= 15);

		/// Reads {"p":{"AR500":30,…}}; a point off the chart, off the 5 dB steps or at another frequency is refused.
		public static (HospitalAudioResult? Result, string? Error) Read(string? state)
		{
			var pts = new Dictionary<string, int>();
			if (!string.IsNullOrEmpty(state))
			{
				try
				{
					using var doc = JsonDocument.Parse(state);
					if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, "The drawing's data could not be read");
					if (doc.RootElement.TryGetProperty("p", out var p))
					{
						if (p.ValueKind != JsonValueKind.Object) return (null, "The drawing's data could not be read");
						foreach (var x in p.EnumerateObject())
						{
							string sym = x.Name.Length > 2 ? x.Name[..2] : "";
							if (!Symbols.Contains(sym) || !int.TryParse(x.Name[2..], out var f) || !Freq.Contains(f)) return (null, "The drawing's data could not be read");
							if (x.Value.ValueKind != JsonValueKind.Number || !x.Value.TryGetInt32(out var db) || db < -10 || db > 120 || db % 5 != 0) return (null, "A threshold is between -10 and 120 dB, in 5 dB steps");
							pts[x.Name] = db;
						}
					}
				}
				catch (JsonException) { return (null, "The drawing's data could not be read"); }
			}
			decimal? r = Pta(pts, "AR"), l = Pta(pts, "AL");
			return (new HospitalAudioResult(pts, r, l, r is decimal a ? Grade(a) : null, l is decimal b ? Grade(b) : null,
				Gap(pts, "AR", "BR"), Gap(pts, "AL", "BL"), r is decimal ra && l is decimal la && Math.Abs(ra - la) >= 15), null);
		}

		public static string Store(HospitalAudioResult r) => JsonSerializer.Serialize(new { p = r.Points, ptaR = r.PtaR, ptaL = r.PtaL, gradeR = r.GradeR, gradeL = r.GradeL, gapR = r.GapR, gapL = r.GapL, asym = r.Asymmetry });
	}

	// ==========================================================================================
	// HOSPITAL H102 — the 28-joint homunculus (the approved prototype screen cv-joints) → DAS28-ESR:
	//   0.56·√TJC28 + 0.28·√SJC28 + 0.70·ln(ESR) + 0.014·patient global (0–100);
	//   > 5.1 high activity · > 3.2 moderate · ≥ 2.6 low · < 2.6 remission (EULAR).
	// Each joint: 0 clear · 1 tender · 2 swollen · 3 both. Without an ESR there is no score — it is never guessed.
	// ==========================================================================================
	public sealed record HospitalDas28Result(IReadOnlyDictionary<string, int> Joints, int Tender, int Swollen, decimal? Esr, int? Global, decimal? Das28, string? Band);

	public static class HospitalDas28Rules
	{
		/// The 28 joints: shoulders, elbows, wrists, knees, MCP 1–5 and PIP 1–5 (the thumb's IP counts as PIP 1), right and left.
		public static readonly IReadOnlyList<string> Joints = new[] { "Sh", "El", "Wr", "Kn" }.Concat(Enumerable.Range(1, 5).Select(i => "MCP" + i)).Concat(Enumerable.Range(1, 5).Select(i => "PIP" + i))
			.SelectMany(j => new[] { j + "R", j + "L" }).ToList();

		public static string Band(decimal das) => das > 5.1m ? "High" : das > 3.2m ? "Moderate" : das >= 2.6m ? "Low" : "Remission";

		/// Reads {"j":{"WrR":3,…},"esr":32,"pga":55}; an unknown joint or state, an ESR outside 1–150 or a global outside 0–100 is refused.
		public static (HospitalDas28Result? Result, string? Error) Read(string? state)
		{
			var joints = new Dictionary<string, int>(); decimal? esr = null; int? pga = null;
			const string bad = "The drawing's data could not be read";
			if (!string.IsNullOrEmpty(state))
			{
				try
				{
					using var doc = JsonDocument.Parse(state);
					var root = doc.RootElement;
					if (root.ValueKind != JsonValueKind.Object) return (null, bad);
					if (root.TryGetProperty("j", out var j))
					{
						if (j.ValueKind != JsonValueKind.Object) return (null, bad);
						foreach (var x in j.EnumerateObject())
						{
							if (!Joints.Contains(x.Name) || x.Value.ValueKind != JsonValueKind.Number || !x.Value.TryGetInt32(out var v) || v is < 0 or > 3) return (null, bad);
							if (v > 0) joints[x.Name] = v;
						}
					}
					if (root.TryGetProperty("esr", out var e) && e.ValueKind == JsonValueKind.Number)
					{
						var v = e.GetDecimal(); if (v is < 1 or > 150) return (null, "The ESR is between 1 and 150 mm/h"); esr = Math.Round(v, 0);
					}
					if (root.TryGetProperty("pga", out var g) && g.ValueKind == JsonValueKind.Number)
					{
						if (!g.TryGetInt32(out var v) || v is < 0 or > 100) return (null, "The patient's global assessment is between 0 and 100"); pga = v;
					}
				}
				catch (Exception ex) when (ex is JsonException or FormatException) { return (null, bad); }
			}
			int tj = joints.Count(x => x.Value is 1 or 3), sj = joints.Count(x => x.Value is 2 or 3);
			decimal? das = esr is decimal es && pga is int pg
				? Math.Round((decimal)(0.56 * Math.Sqrt(tj) + 0.28 * Math.Sqrt(sj) + 0.70 * Math.Log((double)es) + 0.014 * pg), 2) : null;
			return (new HospitalDas28Result(joints, tj, sj, esr, pga, das, das is decimal d ? Band(d) : null), null);
		}

		public static string Store(HospitalDas28Result r) => JsonSerializer.Serialize(new { j = r.Joints, esr = r.Esr, pga = r.Global, tender = r.Tender, swollen = r.Swollen, das28 = r.Das28, band = r.Band });
	}
	// ==========================================================================================
	// HOSPITAL H103 — the family tree in NSGC notation (the approved prototype screen cv-pedigree): square male, circle
	// female, diamond unknown; filled = affected, slash = deceased, centre dot = carrier, arrow = the patient (proband);
	// a double partner line = consanguinity — very important in the Gulf and Egypt for inherited disease.
	// The tree is people and links; the server keeps only those, and reads what the tree yields:
	//   * a consanguineous couple anywhere in the tree;
	//   * a condition in two generations or more (the affected people's condition, as written);
	//   * then: screen the unaffected first-degree relatives of the patient for it.
	// ==========================================================================================
	public sealed class HospitalPedigreePerson
	{
		public int Id { get; set; }
		public string Sex { get; set; } = "U";
		/// -2 grandparents · -1 parents, aunts, uncles · 0 the patient, siblings, partners, cousins · 1 children · 2 grandchildren.
		public int Gen { get; set; }
		public int? Father { get; set; }
		public int? Mother { get; set; }
		public bool Proband { get; set; }
		public bool Affected { get; set; }
		public bool Deceased { get; set; }
		public bool Carrier { get; set; }
		/// The condition, as the doctor writes it (affects the "generations" reading), and a short note (age, cause of death).
		public string? Cond { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalPedigreeCouple { public int A { get; set; } public int B { get; set; } public bool Cons { get; set; } }

	public sealed record HospitalPedigreeYield(bool Consanguinity, IReadOnlyList<(string Cond, int Generations)> Conditions, IReadOnlyList<(string Cond, IReadOnlyList<int> People)> Screen);
	public sealed record HospitalPedigreeResult(IReadOnlyList<HospitalPedigreePerson> People, IReadOnlyList<HospitalPedigreeCouple> Couples, HospitalPedigreeYield Yield);

	public static class HospitalPedigreeRules
	{
		public const int MaxPeople = 60;
		private const string Bad = "The family tree could not be read";
		private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];

		/// Reads {"people":[…],"couples":[…]}; the links must point at people of the tree, a parent one generation up,
		/// exactly one patient. Text is clipped; nothing else is kept.
		public static (HospitalPedigreeResult? Result, string? Error) Read(string? state, string probandSex)
		{
			List<HospitalPedigreePerson> people; List<HospitalPedigreeCouple> couples;
			if (string.IsNullOrEmpty(state) || state == "{}")
			{
				people = new() { new HospitalPedigreePerson { Id = 1, Sex = probandSex is "M" or "F" ? probandSex : "U", Gen = 0, Proband = true } };
				couples = new();
			}
			else
			{
				try
				{
					using var doc = JsonDocument.Parse(state);
					if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, Bad);
					people = doc.RootElement.TryGetProperty("people", out var pe) && pe.ValueKind == JsonValueKind.Array ? pe.Deserialize<List<HospitalPedigreePerson>>(Json) ?? new() : new();
					couples = doc.RootElement.TryGetProperty("couples", out var co) && co.ValueKind == JsonValueKind.Array ? co.Deserialize<List<HospitalPedigreeCouple>>(Json) ?? new() : new();
				}
				catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { return (null, Bad); }
			}
			if (people.Count == 0 || people.Count > MaxPeople) return (null, "A family tree holds 1 to 60 people");
			var ids = people.Select(x => x.Id).ToList();
			if (ids.Distinct().Count() != ids.Count || ids.Any(x => x <= 0)) return (null, Bad);
			if (people.Count(x => x.Proband) != 1) return (null, "The tree has exactly one patient");
			var byId = people.ToDictionary(x => x.Id);
			foreach (var x in people)
			{
				if (x.Sex is not ("M" or "F" or "U") || x.Gen is < -3 or > 3) return (null, Bad);
				if (x.Father is int f && (!byId.TryGetValue(f, out var fa) || fa.Sex == "F" || fa.Gen != x.Gen - 1)) return (null, "A father is a man one generation up");
				if (x.Mother is int m && (!byId.TryGetValue(m, out var mo) || mo.Sex == "M" || mo.Gen != x.Gen - 1)) return (null, "A mother is a woman one generation up");
				x.Cond = Clip(x.Cond, 60); x.Note = Clip(x.Note, 60);
				if (x.Affected && x.Cond == null) return (null, "Write the condition of every affected relative");
				if (!x.Affected) x.Cond = null;
			}
			foreach (var c in couples)
				if (c.A == c.B || !byId.TryGetValue(c.A, out var a) || !byId.TryGetValue(c.B, out var b) || a.Gen != b.Gen) return (null, "Partners are two people of one generation");
			couples = couples.GroupBy(c => (Math.Min(c.A, c.B), Math.Max(c.A, c.B))).Select(g => new HospitalPedigreeCouple { A = g.Key.Item1, B = g.Key.Item2, Cons = g.Any(c => c.Cons) }).ToList();
			// every pair of parents is a couple, so the tree draws their partner line
			foreach (var x in people.Where(p => p.Father != null && p.Mother != null))
			{
				int a = Math.Min(x.Father!.Value, x.Mother!.Value), b = Math.Max(x.Father!.Value, x.Mother!.Value);
				if (!couples.Any(c => c.A == a && c.B == b)) couples.Add(new HospitalPedigreeCouple { A = a, B = b });
			}
			return (new HospitalPedigreeResult(people, couples, Yield(people, couples)), null);
		}

		/// What the tree yields: consanguinity; each condition and the generations it is in; whom to screen.
		public static HospitalPedigreeYield Yield(IReadOnlyList<HospitalPedigreePerson> people, IReadOnlyList<HospitalPedigreeCouple> couples)
		{
			var conds = people.Where(p => p.Affected && p.Cond != null).GroupBy(p => p.Cond!.Trim().ToLowerInvariant())
				.Select(g => (Cond: g.First().Cond!, Generations: g.Select(p => p.Gen).Distinct().Count())).OrderByDescending(x => x.Generations).ToList();
			var pr = people.Single(p => p.Proband);
			// first degree: parents, siblings (a shared parent) and children of the patient
			var first = people.Where(p => p.Id != pr.Id && (p.Id == pr.Father || p.Id == pr.Mother || p.Father == pr.Id || p.Mother == pr.Id
				|| (p.Gen == pr.Gen && ((p.Father != null && p.Father == pr.Father) || (p.Mother != null && p.Mother == pr.Mother))))).ToList();
			var screen = conds.Where(c => c.Generations >= 2)
				.Select(c => (c.Cond, (IReadOnlyList<int>)first.Where(p => !p.Deceased && !(p.Affected && string.Equals(p.Cond, c.Cond, StringComparison.OrdinalIgnoreCase))).Select(p => p.Id).ToList()))
				.Where(x => x.Item2.Count > 0).ToList();
			return new HospitalPedigreeYield(couples.Any(c => c.Cons), conds, screen);
		}

		public static string Store(HospitalPedigreeResult r) => JsonSerializer.Serialize(new
		{
			people = r.People.Select(p => new { id = p.Id, sex = p.Sex, gen = p.Gen, father = p.Father, mother = p.Mother, proband = p.Proband, affected = p.Affected, deceased = p.Deceased, carrier = p.Carrier, cond = p.Cond, note = p.Note }),
			couples = r.Couples.Select(c => new { a = c.A, b = c.B, cons = c.Cons }),
		});
	}
	// ==========================================================================================
	// HOSPITAL H112 — the patient's pain report (the prototype's canvas layer 8): where it hurts (marks on the figures),
	// how strong NOW on the 0–10 numeric rating scale (NRS, LOINC 72514-3), and the words for it (a fixed list, as on
	// the McGill / Brief Pain Inventory diagrams). Patient-reported: stored apart, never as a clinician's measurement.
	// ==========================================================================================
	public sealed record HospitalPainReport(int? Nrs, IReadOnlyList<string> Words);

	public static class HospitalPainRules
	{
		public const string NrsCode = "72514-3";
		public static readonly IReadOnlyList<string> Words = new[] { "burning", "stabbing", "throbbing", "aching", "pressing", "tingling", "cramping", "spreading" };

		/// Reads {"nrs":7,"words":["burning"]}; the strength is required (0–10), the words come from the fixed list only.
		public static (HospitalPainReport? Result, string? Error) Read(string? state)
		{
			if (string.IsNullOrEmpty(state) || state == "{}") return (null, "Choose how strong the pain is, from 0 to 10");
			try
			{
				using var doc = JsonDocument.Parse(state);
				var r = doc.RootElement;
				if (r.ValueKind != JsonValueKind.Object) return (null, "The drawing's data could not be read");
				if (!r.TryGetProperty("nrs", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var nrs) || nrs is < 0 or > 10)
					return (null, "Choose how strong the pain is, from 0 to 10");
				var words = new List<string>();
				if (r.TryGetProperty("words", out var w))
				{
					if (w.ValueKind != JsonValueKind.Array) return (null, "The drawing's data could not be read");
					foreach (var x in w.EnumerateArray())
					{
						if (x.ValueKind != JsonValueKind.String || !Words.Contains(x.GetString()!)) return (null, "The drawing's data could not be read");
						if (!words.Contains(x.GetString()!)) words.Add(x.GetString()!);
					}
				}
				return (new HospitalPainReport(nrs, words), null);
			}
			catch (JsonException) { return (null, "The drawing's data could not be read"); }
		}

		public static string Store(HospitalPainReport r) => JsonSerializer.Serialize(new { nrs = r.Nrs, words = r.Words });
	}
	// ==========================================================================================
	// HOSPITAL H113 — a calibrated measure on a clinical photo (the prototype's canvas layer 4). A photo has no scale
	// of its own: the doctor marks a known length on a ruler lying in the picture (the calibration), then the lesion's
	// longest length and, optionally, its widest across it. All points are the photo's own pixels. The margin of error
	// is one pixel at each end of the lesion's line plus one at each end of the ruler's, carried through the scale.
	// ==========================================================================================
	public sealed class HospitalPhotoMeasureInput
	{
		/// x1, y1, x2, y2 in the photo's pixels.
		public decimal[]? Ruler { get; set; }
		public decimal? RulerMm { get; set; }
		public decimal[]? Length { get; set; }
		public decimal[]? Width { get; set; }
	}

	public sealed record HospitalPhotoMeasure(decimal LengthMm, decimal? WidthMm, decimal PlusMinusMm, decimal MmPerPx);

	public static class HospitalPhotoMeasureRules
	{
		public const decimal MinRulerPx = 50, MaxPx = 20000;

		private static decimal? Px(decimal[]? l)
		{
			if (l == null || l.Length != 4 || l.Any(v => v < 0 || v > MaxPx)) return null;
			double dx = (double)(l[2] - l[0]), dy = (double)(l[3] - l[1]);
			return (decimal)Math.Sqrt(dx * dx + dy * dy);
		}

		public static (HospitalPhotoMeasure? Result, string? Error) Measure(HospitalPhotoMeasureInput i)
		{
			if (i.RulerMm is not decimal mm || mm < 1 || mm > 300) return (null, "Give the ruler's length in millimetres (1 to 300)");
			if (Px(i.Ruler) is not decimal ruler) return (null, "Mark the two ends of the ruler's length on the photo");
			if (ruler < MinRulerPx) return (null, "Mark a longer stretch of the ruler: at least 50 pixels on the photo");
			if (Px(i.Length) is not decimal len || len < 2) return (null, "Mark the two ends of the lesion's length");
			decimal? wid = null;
			if (i.Width != null)
			{
				if (Px(i.Width) is not decimal w || w < 2) return (null, "Mark the two ends of the lesion's width");
				if (w > len) return (null, "The width is across the length: it cannot be longer than the length");
				wid = w;
			}
			decimal per = mm / ruler;
			decimal L = Math.Round(len * per, 1, MidpointRounding.AwayFromZero);
			if (L < 0.1m || L > 500) return (null, "The measured length is out of range: check the ruler");
			decimal? W = wid is decimal ww ? Math.Max(0.1m, Math.Round(ww * per, 1, MidpointRounding.AwayFromZero)) : null;
			decimal pm = Math.Max(0.1m, Math.Round(2 * per + len * per * 2 / ruler, 1, MidpointRounding.AwayFromZero));
			return (new HospitalPhotoMeasure(L, W, pm, Math.Round(per, 4, MidpointRounding.AwayFromZero)), null);
		}

		public static string Store(HospitalPhotoMeasureInput i, HospitalPhotoMeasure r) => JsonSerializer.Serialize(new
		{
			ruler = i.Ruler, rulerMm = i.RulerMm, length = i.Length, width = i.Width,
			lengthMm = r.LengthMm, widthMm = r.WidthMm, pm = r.PlusMinusMm,
		});

		/// The stored measure read again from its own points (never trusted from its stored results).
		public static HospitalPhotoMeasure? Read(string? json)
		{
			if (string.IsNullOrEmpty(json)) return null;
			try
			{
				var i = JsonSerializer.Deserialize<HospitalPhotoMeasureInput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				return i == null ? null : Measure(i).Result;
			}
			catch (JsonException) { return null; }
		}

		/// A saved-again mark keeps its measure only while its size is still the measured one.
		public static string? KeepIfSame(string? json, decimal? sizeMm) => Read(json) is { } r && sizeMm == r.LengthMm ? json : null;
	}
}
