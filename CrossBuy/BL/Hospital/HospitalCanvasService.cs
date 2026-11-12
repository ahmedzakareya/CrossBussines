using System.Text.Json;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H99 — THE CLINICAL CANVAS engine (the approved prototype screens cv-home, cv-body, cv-fundus).
	// A sheet belongs to one visit and one kind. Its marks are re-placed by the SERVER from their coordinates — the
	// region and side are never taken from the browser — and a finding's code comes from the kind's own list, so the
	// fundus colour code cannot be broken by a client. The sheet is signed with the visit (ONE signature, H94): the visit
	// cannot be signed while a mark carried from the last visit is still unconfirmed.
	// ==========================================================================================
	public sealed record HospitalCanvasFinding(string Key, string Ar, string En, string? Code, string Color);
	public sealed record HospitalCanvasRegion(string Key, string Ar, string En, decimal X, decimal Y, decimal W, decimal H, decimal Nines);
	public sealed record HospitalCanvasMarkView(HospitalCanvasMark Mark, HospitalCanvasMark? Previous);
	public sealed record HospitalCanvasPage(HospitalEncounterView Visit, HospitalPatient Patient, string Kind, HospitalCanvasSheet? Sheet,
		IReadOnlyList<HospitalCanvasMark> Marks, IReadOnlyList<HospitalCanvasMark> Last, DateTime? LastAt, bool CanEdit, int NextLesion
		, IReadOnlyList<HospitalCanvasResult>? History = null
		, HospitalCanvasPatientPain? PatientPain = null
		);
	/// H112: what the patient drew on the tablet for this visit — the places, the strength 0–10, the words.
	public sealed record HospitalCanvasPatientPain(HospitalCanvasSheet Sheet, IReadOnlyList<HospitalCanvasMark> Marks, HospitalPainReport Report);
	/// H110: one stored result of a drawing's calculator — the concept's code, the side (R/L or none), the value, when.
	public sealed record HospitalCanvasResult(string Code, string? Side, decimal Value, DateTime At, int? EncounterId);

	public sealed class HospitalCanvasMarkInput
	{
		public string? Finding { get; set; }
		public string? View { get; set; }
		public decimal X { get; set; }
		public decimal Y { get; set; }
		public decimal? SizeMm { get; set; }
		/// The last visit's mark this one continues (a carried mark, confirmed or not).
		public int? CarriedFromMarkId { get; set; }
		public bool Carried { get; set; }
	}

	public sealed class HospitalCanvasSaveInput
	{
		public List<HospitalCanvasMarkInput> Marks { get; set; } = new();
		public string? Ink { get; set; }
		public string? State { get; set; }
		/// H111: the SVG the screen shows, kept (cleaned) as the drawing's picture for printing.
		public string? Svg { get; set; }
	}

	public static class HospitalCanvasRules
	{
		public const int MaxMarks = 200;
		public const int MaxInkChars = 400_000;
		public const int MaxStateChars = 40_000;

		/// The body silhouette: 200×440 per figure, the front at x 0, the back at x 230 (rule-of-nines % of the half).
		public static readonly IReadOnlyList<HospitalCanvasRegion> BodyRegions = new[]
		{
			new HospitalCanvasRegion("head", "الرأس والرقبة", "Head & neck", 75, 5, 50, 62, 4.5m),
			new HospitalCanvasRegion("trunk", "الجذع", "Trunk", 62, 68, 76, 140, 18),
			new HospitalCanvasRegion("armR", "الذراع الأيمن", "Right arm", 22, 72, 38, 150, 4.5m),
			new HospitalCanvasRegion("armL", "الذراع الأيسر", "Left arm", 140, 72, 38, 150, 4.5m),
			new HospitalCanvasRegion("groin", "العجان", "Perineum", 88, 208, 24, 16, 1),
			new HospitalCanvasRegion("legR", "الساق اليمنى", "Right leg", 62, 210, 38, 225, 9),
			new HospitalCanvasRegion("legL", "الساق اليسرى", "Left leg", 100, 210, 38, 225, 9),
		};

		public static readonly IReadOnlyList<HospitalCanvasFinding> BodyFindings = new[]
		{
			new HospitalCanvasFinding("Naevus", "شامة", "Naevus", null, "#3A5795"), new HospitalCanvasFinding("Papule", "حطاطة", "Papule", null, "#3A5795"),
			new HospitalCanvasFinding("Nodule", "عقيدة", "Nodule", null, "#3A5795"), new HospitalCanvasFinding("Plaque", "لويحة", "Plaque", null, "#3A5795"),
			new HospitalCanvasFinding("Ulcer", "قرحة", "Ulcer", null, "#3A5795"), new HospitalCanvasFinding("Pain", "ألم", "Pain", null, "#3A5795"),
			new HospitalCanvasFinding("Wound", "جرح", "Wound", null, "#3A5795"),
		};

		/// H112: a place where the patient feels pain (SNOMED 22253000 pain).
		public static readonly IReadOnlyList<HospitalCanvasFinding> PainFindings = new[] { new HospitalCanvasFinding("Pain", "ألم", "Pain", "SNOMED 22253000", "#DC2626") };

		/// The CMS retinal-drawing colour code: the finding sets the colour, never the doctor.
		public static readonly IReadOnlyList<HospitalCanvasFinding> FundusFindings = new[]
		{
			new HospitalCanvasFinding("haem", "نزيف", "Haemorrhage", "SNOMED 31341008", "#DC2626"),
			new HospitalCanvasFinding("exud", "ارتشاح / إفراز", "Exudate / oedema", "SNOMED 10569001", "#EAB308"),
			new HospitalCanvasFinding("ma", "تمدد أوعية دقيق", "Microaneurysm", "SNOMED 232020009", "#B91C1C"),
			new HospitalCanvasFinding("nv", "أوعية جديدة مسطحة", "Flat neovascularisation", "SNOMED 75971007", "#7E22CE"),
			new HospitalCanvasFinding("detach", "انفصال شبكية", "Detached retina", "SNOMED 42059000", "#2563EB"),
			new HospitalCanvasFinding("break", "قطع كامل", "Full-thickness break", "SNOMED 232004004", "#DC2626"),
			new HospitalCanvasFinding("pig", "صبغة", "Pigment", "SNOMED 247121005", "#78350F"),
			new HospitalCanvasFinding("vh", "نزيف زجاجي", "Vitreous haemorrhage", "SNOMED 31341008", "#DC2626"),
		};

		public static IReadOnlyList<HospitalCanvasFinding> FindingsOf(string kind) => kind switch
		{
			HospitalCanvasKind.Body => BodyFindings,
			HospitalCanvasKind.Fundus => FundusFindings,
			HospitalCanvasKind.Pain => PainFindings,
			_ => Array.Empty<HospitalCanvasFinding>(),
		};

		private static readonly IReadOnlyDictionary<string, string> Mirror = new Dictionary<string, string> { ["armR"] = "armL", ["armL"] = "armR", ["legR"] = "legL", ["legL"] = "legR" };

		/// The body region and view (F front / B back) under a point, or null off the body.
		public static (HospitalCanvasRegion Region, string View)? BodyAt(decimal x, decimal y)
		{
			string view = x < 220 ? "F" : "B";
			decimal lx = view == "F" ? x : x - 230;
			var r = BodyRegions.FirstOrDefault(q => lx >= q.X && lx <= q.X + q.W && y >= q.Y && y <= q.Y + q.H);
			if (r == null) return null;
			// seen from behind, the patient's right is on the viewer's right: the same drawn arm is the other side
			if (view == "B" && Mirror.TryGetValue(r.Key, out var other)) r = BodyRegions.First(q => q.Key == other);
			return (r, view);
		}

		/// The fundus place of a point on a 300×300 eye: the zone (PP posterior pole · MP mid-periphery · FP far periphery)
		/// and the clock hour, as the doctor says it; null outside the drawing.
		public static string? FundusAt(decimal x, decimal y)
		{
			double dx = (double)x - 150, dy = (double)y - 150, r = Math.Sqrt(dx * dx + dy * dy);
			if (r > 140) return null;
			string zone = r < 60 ? "PP" : r < 105 ? "MP" : "FP";
			int clock = (int)Math.Round(((Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360) / 30);
			return zone + "@" + (clock == 0 ? 12 : clock);
		}

		/// H110: the dictionary codes a drawing's results are stored under.
		public static IReadOnlyList<string> ResultCodes(string kind) => kind switch
		{
			HospitalCanvasKind.Burn => new[] { "TBSA" },
			HospitalCanvasKind.Audio => new[] { "PTA" },
			HospitalCanvasKind.Joints => new[] { "DAS28" },
			HospitalCanvasKind.Pain => new[] { HospitalPainRules.NrsCode },
			_ => Array.Empty<string>(),
		};

		/// H110: the results a SAVED drawing holds — read from the state the server stored (its own figures), never
		/// recomputed from the browser: (code, side, value).
		public static IReadOnlyList<(string Code, string? Side, decimal Value)> Results(string kind, string? stateJson)
		{
			var outp = new List<(string, string?, decimal)>();
			if (string.IsNullOrEmpty(stateJson)) return outp;
			try
			{
				using var d = JsonDocument.Parse(stateJson);
				decimal? N(string k) => d.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
				switch (kind)
				{
					case HospitalCanvasKind.Burn: if (N("tbsa") is decimal t && t > 0) outp.Add(("TBSA", null, t)); break;
					case HospitalCanvasKind.Audio:
						if (N("ptaR") is decimal r) outp.Add(("PTA", "R", r));
						if (N("ptaL") is decimal l) outp.Add(("PTA", "L", l));
						break;
					case HospitalCanvasKind.Joints: if (N("das28") is decimal das) outp.Add(("DAS28", null, das)); break;
					case HospitalCanvasKind.Pain: if (N("nrs") is decimal nrs) outp.Add((HospitalPainRules.NrsCode, null, nrs)); break;
				}
			}
			catch (JsonException) { }
			return outp;
		}

		/// The marks as the server places them, or the first refusal.
		public static (List<HospitalCanvasMark>? Marks, string? Error, object[]? Args) Place(string kind, IReadOnlyList<HospitalCanvasMarkInput> input)
		{
			if (input.Count > MaxMarks) return (null, "A drawing holds at most {0} marks", new object[] { MaxMarks });
			var list = FindingsOf(kind); var outp = new List<HospitalCanvasMark>();
			if (input.Count > 0 && list.Count == 0) return (null, "This drawing has no marks", null);
			foreach (var m in input)
			{
				var f = list.FirstOrDefault(x => x.Key == m.Finding);
				if (f == null) return (null, "Choose the finding of every mark", null);
				if (m.SizeMm is < 0 or > 500) return (null, "A size is between 0 and 500 mm", null);
				var mark = new HospitalCanvasMark { Finding = f.Key, FindingCode = f.Code, X = Math.Round(m.X, 2), Y = Math.Round(m.Y, 2), SizeMm = m.SizeMm, Carried = m.Carried && m.CarriedFromMarkId != null, CarriedFromMarkId = m.CarriedFromMarkId };
				if (kind is HospitalCanvasKind.Body or HospitalCanvasKind.Pain)   // H112: the patient's pain sits on the same figures
				{
					var at = BodyAt(m.X, m.Y);
					if (at == null) return (null, "A mark must sit on the body itself", null);
					mark.Region = at.Value.Region.Key; mark.Side = at.Value.View;
				}
				else if (kind == HospitalCanvasKind.Fundus)
				{
					if (m.View is not ("R" or "L")) return (null, "Say which eye every mark is on", null);
					var at = FundusAt(m.X, m.Y);
					if (at == null) return (null, "A mark must sit inside the fundus", null);
					mark.Region = at; mark.Side = m.View;
				}
				else return (null, "This drawing has no marks", null);
				outp.Add(mark);
			}
			return (outp, null, null);
		}

		/// The ink as it is STORED: a JSON array of strokes {c, w, p:[[x,y,pressure]…]} rebuilt from the numbers alone —
		/// nothing else the browser sent survives. Null with an error when it cannot be read.
		public static (string? Ink, string? Error) Ink(string? ink)
		{
			if (string.IsNullOrEmpty(ink)) return ("[]", null);
			if (ink.Length > MaxInkChars) return (null, "The free drawing is too large to keep");
			const string bad = "The free drawing could not be read";
			try
			{
				using var d = JsonDocument.Parse(ink);
				if (d.RootElement.ValueKind != JsonValueKind.Array) return (null, bad);
				var outp = new List<object>();
				foreach (var s in d.RootElement.EnumerateArray())
				{
					if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("p", out var p) || p.ValueKind != JsonValueKind.Array) return (null, bad);
					string c = s.TryGetProperty("c", out var ce) && ce.ValueKind == JsonValueKind.String && System.Text.RegularExpressions.Regex.IsMatch(ce.GetString()!, "^#[0-9A-Fa-f]{6}$") ? ce.GetString()! : "#7239EA";
					double w = s.TryGetProperty("w", out var we) && we.ValueKind == JsonValueKind.Number ? Math.Clamp(we.GetDouble(), 0.5, 8) : 2.5;
					var pts = new List<double[]>();
					foreach (var q in p.EnumerateArray())
					{
						if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() is < 2 or > 3 || q.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number)) return (null, bad);
						pts.Add(q.EnumerateArray().Select(v => Math.Round(v.GetDouble(), 2)).ToArray());
					}
					if (pts.Count > 1) outp.Add(new { c, w, p = pts });
				}
				return (JsonSerializer.Serialize(outp), null);
			}
			catch (JsonException) { return (null, bad); }
		}

		/// The kind's own state is a JSON object (its calculator reads it); "{}" when there is none.
		public static string? StateRefusal(string? state)
		{
			if (string.IsNullOrEmpty(state)) return null;
			if (state.Length > MaxStateChars) return "The drawing's data is too large to keep";
			try { using var d = JsonDocument.Parse(state); return d.RootElement.ValueKind == JsonValueKind.Object ? null : "The drawing's data could not be read"; }
			catch (JsonException) { return "The drawing's data could not be read"; }
		}

		/// Lesion numbers: a carried mark keeps its lesion's number; a new one takes the patient's next.
		public static void Number(IList<HospitalCanvasMark> marks, IReadOnlyDictionary<int, int> lesionOfMark, int patientMax)
		{
			int next = patientMax;
			foreach (var m in marks)
				m.LesionNo = m.CarriedFromMarkId is int src && lesionOfMark.TryGetValue(src, out var n) ? n : ++next;
		}
	}

	public sealed class HospitalCanvasService
	{
		public const string NoTables = "The clinical canvas is not installed yet (hospital_h99_canvas.sql).";
		public const string PhotoMetadata = "{\"hospital\":\"canvas-mark\"}";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalCanvasService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);

		/// The last SIGNED sheet of this kind for the patient, before this visit, and its marks.
		private async Task<(HospitalCanvasSheet? Sheet, List<HospitalCanvasMark> Marks)> LastAsync(BusinessContext ctx, int patientId, int encounterId, string kind, CancellationToken ct)
		{
			var s = await Q<HospitalCanvasSheet>(ctx).AsNoTracking().Where(x => x.PatientId == patientId && x.Kind == kind && x.EncounterId != encounterId && x.Status == HospitalCanvasStatus.Signed)
				.OrderByDescending(x => x.SignedAt).FirstOrDefaultAsync(ct);
			if (s == null) return (null, new());
			return (s, await Q<HospitalCanvasMark>(ctx).AsNoTracking().Where(m => m.SheetId == s.ID).OrderBy(m => m.LesionNo).ToListAsync(ct));
		}

		public async Task<(HospitalCanvasPage? Model, bool Ready)> SheetAsync(BusinessContext ctx, HospitalConsultationService consult, int encounterId, string kind, CancellationToken ct = default)
		{
			if (!HospitalCanvasKind.All.Contains(kind)) return (null, true);
			var visit = await consult.GetAsync(ctx, encounterId, ct);
			if (visit == null) return (null, true);
			var patient = await Q<HospitalPatient>(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == visit.Encounter.PatientId, ct);
			if (patient == null) return (null, true);
			bool canEdit = visit.Encounter.Status == HospitalEncounterStatus.Draft && await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			try
			{
				var sheet = await Q<HospitalCanvasSheet>(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.EncounterId == encounterId && s.Kind == kind, ct);
				var marks = sheet == null ? new List<HospitalCanvasMark>() : await Q<HospitalCanvasMark>(ctx).AsNoTracking().Where(m => m.SheetId == sheet.ID).OrderBy(m => m.LesionNo).ToListAsync(ct);
				var last = await LastAsync(ctx, patient.ID, encounterId, kind, ct);
				int next = (await Q<HospitalCanvasMark>(ctx).Where(m => m.PatientId == patient.ID).Select(m => (int?)m.LesionNo).MaxAsync(ct) ?? 0) + 1;
				if (sheet == null && canEdit && kind == HospitalCanvasKind.Body)
					// a first opening brings the last visit's marks forward — grey, "carried, unverified", until confirmed
					marks = last.Marks.Select(m => new HospitalCanvasMark { Finding = m.Finding, FindingCode = m.FindingCode, Region = m.Region, Side = m.Side, X = m.X, Y = m.Y, SizeMm = m.SizeMm, LesionNo = m.LesionNo, Carried = true, CarriedFromMarkId = m.ID }).ToList();
				// H110: the patient's earlier results of this drawing's measures (the trend), newest first
				var codes = HospitalCanvasRules.ResultCodes(kind);
				var history = codes.Count == 0 ? new List<HospitalCanvasResult>() : await (from o in Q<HospitalClinicalObservation>(ctx).AsNoTracking().Where(o => o.PatientId == patient.ID && o.Value != null)
									join c in Q<HospitalConcept>(ctx).AsNoTracking() on o.ConceptId equals c.ID
									where codes.Contains(c.Code)
									orderby o.ObservedAt descending
									select new HospitalCanvasResult(c.Code, o.Side, o.Value!.Value, o.ObservedAt, o.EncounterId)).Take(12).ToListAsync(ct);
				// H112: on the body map, what the patient drew of their pain on this visit (a separate, patient-reported layer)
				HospitalCanvasPatientPain? patientPain = null;
				if (kind == HospitalCanvasKind.Body
					&& await Q<HospitalCanvasSheet>(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.EncounterId == encounterId && s.Kind == HospitalCanvasKind.Pain, ct) is { } ps)
					patientPain = new HospitalCanvasPatientPain(ps, await Q<HospitalCanvasMark>(ctx).AsNoTracking().Where(m => m.SheetId == ps.ID).OrderBy(m => m.ID).ToListAsync(ct),
						HospitalPainRules.Read(ps.StateJson).Result ?? new HospitalPainReport(null, Array.Empty<string>()));
				return (new HospitalCanvasPage(visit, patient, kind, sheet, marks, last.Marks, last.Sheet?.SignedAt, canEdit && sheet?.Status != HospitalCanvasStatus.Signed, next, history, patientPain), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new HospitalCanvasPage(visit, patient, kind, null, Array.Empty<HospitalCanvasMark>(), Array.Empty<HospitalCanvasMark>(), null, false, 1), false); }
		}

		/// The open visits a drawing can be made on (the overview's picker): newest first, the patient and the doctor.
		public async Task<IReadOnlyList<(int EncounterId, DateTime StartedAt, string Patient, string PatientEn, string Mrn, string Doctor, string? DoctorEn)>> OpenVisitsAsync(BusinessContext ctx, int take, CancellationToken ct = default)
		{
			var rows = await (from e in Q<HospitalEncounter>(ctx).Where(x => x.Status == HospitalEncounterStatus.Draft)
							  join p in Q<HospitalPatient>(ctx) on e.PatientId equals p.ID
							  join d in Q<HospitalPractitioner>(ctx) on e.PractitionerId equals d.ID
							  orderby e.StartedAt descending, e.ID descending
							  select new { e.ID, e.StartedAt, p.Name, p.NameEn, p.Mrn, D = d.Name, DEn = d.NameEn }).Take(take).ToListAsync(ct);
			return rows.Select(r => (r.ID, r.StartedAt, r.Name, r.NameEn, r.Mrn, r.D, r.DEn)).ToList();
		}

		/// Saves the visit's drawing (its marks, ink and state) as a draft: it is signed with the visit.
		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int encounterId, string kind, HospitalCanvasSaveInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalCanvasKind.All.Contains(kind)) return HospitalResult.Fail("not_found");
			var placed = HospitalCanvasRules.Place(kind, input.Marks ?? new());
			if (placed.Error != null) return HospitalResult.Fail(placed.Error, placed.Args ?? Array.Empty<object>());
			var ink = HospitalCanvasRules.Ink(input.Ink);
			var picture = HospitalCanvasSnapshot.Clean(input.Svg);
			var bad = ink.Error ?? HospitalCanvasRules.StateRefusal(input.State) ?? picture.Error;
			if (bad != null) return HospitalResult.Fail(bad);
			try
			{
				var e = await Q<HospitalEncounter>(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == encounterId, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				if (e.Status != HospitalEncounterStatus.Draft) return HospitalResult.Fail("This visit is signed: its drawings no longer change");
				// H100: a kind with a calculator keeps only what its rules read, and the server's own figures
				string state = "{}";
				if (kind == HospitalCanvasKind.Burn)
				{
					var birth = await Q<HospitalPatient>(ctx).Where(p => p.ID == e.PatientId).Select(p => p.BirthDate).FirstOrDefaultAsync(ct);
					var burn = HospitalBurnRules.Read(input.State, HospitalPatientService.AgeOn(birth, e.StartedAt));
					if (burn.Error != null) return HospitalResult.Fail(burn.Error);
					state = HospitalBurnRules.Store(burn.Result!);
				}
				if (kind == HospitalCanvasKind.Audio)
				{
					var audio = HospitalAudioRules.Read(input.State);
					if (audio.Error != null) return HospitalResult.Fail(audio.Error);
					state = HospitalAudioRules.Store(audio.Result!);
				}
				if (kind == HospitalCanvasKind.Joints)
				{
					var das = HospitalDas28Rules.Read(input.State);
					if (das.Error != null) return HospitalResult.Fail(das.Error);
					state = HospitalDas28Rules.Store(das.Result!);
				}
				if (kind == HospitalCanvasKind.Pain)
				{
					var pain = HospitalPainRules.Read(input.State);
					if (pain.Error != null) return HospitalResult.Fail(pain.Error);
					state = HospitalPainRules.Store(pain.Result!);
				}
				if (kind == HospitalCanvasKind.Pedigree)
				{
					var sex = await Q<HospitalPatient>(ctx).Where(p => p.ID == e.PatientId).Select(p => p.Sex).FirstOrDefaultAsync(ct);
					var tree = HospitalPedigreeRules.Read(input.State, sex ?? "U");
					if (tree.Error != null) return HospitalResult.Fail(tree.Error);
					state = HospitalPedigreeRules.Store(tree.Result!);
				}
				var sheet = await Q<HospitalCanvasSheet>(ctx).FirstOrDefaultAsync(s => s.EncounterId == encounterId && s.Kind == kind, ct);
				if (sheet?.Status == HospitalCanvasStatus.Signed) return HospitalResult.Fail("This visit is signed: its drawings no longer change");
				// a carried mark must continue one of the LAST visit's marks of this patient — never another patient's
				var last = await LastAsync(ctx, e.PatientId, encounterId, kind, ct);
				var lesionOf = last.Marks.ToDictionary(m => m.ID, m => m.LesionNo);
				if (placed.Marks!.Any(m => m.CarriedFromMarkId is int src && !lesionOf.ContainsKey(src))) return HospitalResult.Fail("A carried mark does not belong to this patient's last drawing");
				if (placed.Marks!.Where(m => m.CarriedFromMarkId != null).GroupBy(m => m.CarriedFromMarkId).Any(g => g.Count() > 1)) return HospitalResult.Fail("A lesion is drawn twice");
				int max = await Q<HospitalCanvasMark>(ctx).Where(m => m.PatientId == e.PatientId).Select(m => (int?)m.LesionNo).MaxAsync(ct) ?? 0;
				if (sheet == null)
				{
					sheet = new HospitalCanvasSheet { CompanyId = ctx.CompanyId, PatientId = e.PatientId, EncounterId = encounterId, Kind = kind, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
					_db.Add(sheet);
				}
				else
				{
					var old = await Q<HospitalCanvasMark>(ctx).Where(m => m.SheetId == sheet.ID).ToListAsync(ct);
					// a lesion first drawn on THIS sheet keeps its number when the sheet is saved again
					foreach (var m in placed.Marks!) if (m.CarriedFromMarkId == null) { var same = old.FirstOrDefault(o => o.CarriedFromMarkId == null && o.X == m.X && o.Y == m.Y && o.Finding == m.Finding); if (same != null) { m.LesionNo = same.LesionNo; m.PhotoDocumentId = same.PhotoDocumentId; m.CreatedAt = same.CreatedAt; m.CreatedByEmployeeId = same.CreatedByEmployeeId; } }
					foreach (var m in placed.Marks!) if (m.CarriedFromMarkId is int src) { var same = old.FirstOrDefault(o => o.CarriedFromMarkId == src); if (same != null) { m.PhotoDocumentId = same.PhotoDocumentId; m.VerifiedAt = same.VerifiedAt; m.VerifiedByEmployeeId = same.VerifiedByEmployeeId; } }
					max = Math.Max(max, old.Count == 0 ? 0 : old.Max(o => o.LesionNo));
					_db.RemoveRange(old);
				}
				var fresh = placed.Marks!.Where(m => m.LesionNo == 0).ToList();
				HospitalCanvasRules.Number(fresh, lesionOf, max);
				foreach (var m in placed.Marks!)
				{
					m.CompanyId = ctx.CompanyId; m.PatientId = e.PatientId;
					if (m.CreatedAt == default) { m.CreatedAt = nowUtc; m.CreatedByEmployeeId = employeeId; }
					if (!m.Carried && m.CarriedFromMarkId != null && m.VerifiedAt == null) { m.VerifiedAt = nowUtc; m.VerifiedByEmployeeId = employeeId; }
				}
				sheet.InkJson = ink.Ink!;
				if (picture.Svg != null) sheet.SnapshotSvg = picture.Svg;   // a save with no picture keeps the last one
				sheet.StateJson = state;
				sheet.UpdatedAt = nowUtc;
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				await _db.SaveChangesAsync(ct);
				foreach (var m in placed.Marks!) { m.SheetId = sheet.ID; _db.Add(m); }
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				return HospitalResult.Success(sheet.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Part of the visit's ONE signature: refuses while a drawing still has a carried mark nobody confirmed.
		public async Task<HospitalResult> CheckAsync(BusinessContext ctx, int encounterId, CancellationToken ct = default)
		{
			try
			{
				var open = await (from s in Q<HospitalCanvasSheet>(ctx).Where(s => s.EncounterId == encounterId && s.Status == HospitalCanvasStatus.Draft)
								  join m in Q<HospitalCanvasMark>(ctx).Where(m => m.Carried) on s.ID equals m.SheetId
								  select m.ID).ToListAsync(ct);
				if (open.Count > 0) return HospitalResult.Fail("Confirm or remove the {0} marks carried from the last visit before signing", open.Count);
				return HospitalResult.Success();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Success(); }
		}

		/// Signs the visit's drawings with it: nothing on them changes again.
		public async Task SignAsync(BusinessContext ctx, int encounterId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var sheets = await Q<HospitalCanvasSheet>(ctx).Where(s => s.EncounterId == encounterId && s.Status == HospitalCanvasStatus.Draft).ToListAsync(ct);
				// H110: each drawing's results become observations — stored, searchable and trended like any reading. A
				// result whose concept is not in the dictionary is not invented: it stays in the drawing only.
				var results = sheets.SelectMany(s => HospitalCanvasRules.Results(s.Kind, s.StateJson).Select(r => (Sheet: s, r.Code, r.Side, r.Value))).ToList();
				if (results.Count > 0)
				{
					var rc = results.Select(r => r.Code).Distinct().ToList();
					var concepts = await Q<HospitalConcept>(ctx).AsNoTracking().Where(c => rc.Contains(c.Code) && c.IsActive).ToListAsync(ct);
					foreach (var r in results)
					{
						var c = concepts.FirstOrDefault(x => x.Code == r.Code);
						if (c == null) continue;
						_db.Add(new HospitalClinicalObservation
						{
							CompanyId = ctx.CompanyId, PatientId = r.Sheet.PatientId, ConceptId = c.ID, EncounterId = encounterId, Value = r.Value, Side = r.Side, Unit = c.Unit,
							Flag = HospitalConceptRules.Flag(c, r.Value), ObservedAt = nowUtc, Source = r.Sheet.Kind == HospitalCanvasKind.Pain ? HospitalObsSource.Patient : HospitalObsSource.Canvas,
							RecordedAt = nowUtc, RecordedByEmployeeId = employeeId,
						});
					}
				}
				foreach (var s in sheets)
				{ s.Status = HospitalCanvasStatus.Signed; s.SignedAt = nowUtc; s.SignedByEmployeeId = employeeId; }
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
		}

		/// The visit's drawings, for the visit screen's canvas card: kind → (marks, carried still unconfirmed, signed).
		public async Task<IReadOnlyDictionary<string, (int Marks, int Carried, bool Signed)>> SummaryAsync(BusinessContext ctx, int encounterId, CancellationToken ct = default)
		{
			try
			{
				var rows = await (from s in Q<HospitalCanvasSheet>(ctx).Where(s => s.EncounterId == encounterId)
								  join m in Q<HospitalCanvasMark>(ctx) on s.ID equals m.SheetId into ms
								  select new { s.Kind, s.Status, N = ms.Count(), C = ms.Count(x => x.Carried) }).ToListAsync(ct);
				return rows.ToDictionary(r => r.Kind, r => (r.N, r.C, r.Status == HospitalCanvasStatus.Signed));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new Dictionary<string, (int, int, bool)>(); }
		}

		/// A photo of one mark, into the patient's documents (the File Manager); the platform authorises the upload.
		public async Task<HospitalResult> PhotoAsync(BusinessContext ctx, IPlatformDocumentService docs, int markId, Stream content, long length, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (length <= 0 || length > HospitalPatientPhotoRules.MaxBytes) return HospitalResult.Fail("The photo must be an image of up to 5 MB");
			try
			{
				var m = await Q<HospitalCanvasMark>(ctx).FirstOrDefaultAsync(x => x.ID == markId, ct);
				if (m == null) return HospitalResult.Fail("not_found");
				var s = await Q<HospitalCanvasSheet>(ctx).AsNoTracking().FirstAsync(x => x.ID == m.SheetId, ct);
				if (s.Status == HospitalCanvasStatus.Signed) return HospitalResult.Fail("This visit is signed: its drawings no longer change");
				using var buffer = new MemoryStream();
				await content.CopyToAsync(buffer, ct);
				var kind = HospitalPatientPhotoRules.Sniff(buffer.GetBuffer().AsSpan(0, (int)Math.Min(12, buffer.Length)));
				if (kind is not { } k) return HospitalResult.Fail("The photo must be an image of up to 5 MB");
				buffer.Position = 0;
				string name = $"lesion-{m.LesionNo}-{s.EncounterId}{k.Ext}";
				var r = m.PhotoDocumentId is long existing
					? await docs.ReplaceAsync(existing, buffer, name, k.ContentType, "New photo of lesion " + m.LesionNo, ct)
					: await docs.UploadAsync(new DocumentUploadRequest { EntityType = EntityRegistry.HospitalPatient, EntityId = m.PatientId, FileName = name, ContentType = k.ContentType, Metadata = PhotoMetadata, Reason = "Photo of lesion " + m.LesionNo }, buffer, ct);
				if (!r.Ok) return HospitalResult.Fail(r.ReasonCode == DocumentAccessReasons.Allowed ? "The change could not be saved" : "forbidden");
				m.PhotoDocumentId = r.DocumentId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(m.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A mark's photo bytes: the document must be THIS mark's — an id from a URL never reaches another file.
		public async Task<DocumentContent?> OpenPhotoAsync(BusinessContext ctx, IPlatformDocumentService docs, int markId, CancellationToken ct = default)
		{
			try
			{
				var id = await Q<HospitalCanvasMark>(ctx).AsNoTracking().Where(m => m.ID == markId).Select(m => m.PhotoDocumentId).FirstOrDefaultAsync(ct);
				if (id is not long d) return null;
				bool mine = await _db.Set<PlatformDocument>().AsNoTracking().AnyAsync(x => x.Id == d && x.CompanyID == ctx.CompanyId && x.EntityType == EntityRegistry.HospitalPatient && x.Metadata == PhotoMetadata, ct);
				return mine ? await docs.OpenCurrentAsync(d, ct) : null;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
