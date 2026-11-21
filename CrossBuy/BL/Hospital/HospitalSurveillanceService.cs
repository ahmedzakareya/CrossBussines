using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H212 — clinical surveillance: the record watched for what a busy ward misses, without anyone asking.
	//   · POSSIBLE SEPSIS — on every new set of observations: qSOFA ≥ 2 (respiratory rate ≥ 22, systolic ≤ 100, altered
	//     mentation), or SIRS ≥ 2 (temperature > 38 or < 36, pulse > 90, respiratory rate > 20) with an infection on
	//     record (an open infection case or isolation). Due within the hour. One open alert per admission; once
	//     answered, it fires again only on a reading taken 12 hours later.
	//   · VTE — every admitted patient is assessed for venous thromboembolism within 24 hours of admission; the prompt
	//     opens on admission (found by the sweep) and is answered with the decision: prophylaxis ordered, not indicated,
	//     or contraindicated (the last two with the reason). Once per admission.
	// An alert past its time is OVERDUE. The evaluation runs after each observation is recorded and on each read of the
	// surveillance list, so nothing depends on someone opening the right screen first.
	// ==========================================================================================
	public sealed record HospitalSurveillanceRow(HospitalSurveillanceAlert Alert, string Patient, string PatientEn, string Mrn, string? Bed, string? Ward, string? WardEn, bool Overdue, HospitalFace? AnsweredBy = null, string? PatientPhoto = null);

	public static class HospitalSurveillanceRules
	{
		public const int SepsisDueMinutes = 60, VteDueHours = 24, SepsisQuietHours = 12;

		/// qSOFA: respiratory rate ≥ 22, systolic ≤ 100, altered mentation (anything but Alert on ACVPU).
		public static (int Score, List<string> Met) Qsofa(int? rr, int? sbp, string? avpu)
		{
			var met = new List<string>();
			if (rr is >= 22) met.Add("RR=" + rr);
			if (sbp is <= 100) met.Add("SBP=" + sbp);
			if (!string.IsNullOrEmpty(avpu) && avpu != HospitalConsciousness.Alert) met.Add("AVPU=" + avpu);
			return (met.Count, met);
		}

		/// SIRS: temperature > 38 or < 36, pulse > 90, respiratory rate > 20, and (when the laboratory has one from the last
		/// 24 hours) a white count > 12 or < 4 ×10⁹/L.
		public static (int Score, List<string> Met) Sirs(decimal? temp, int? hr, int? rr, decimal? wbc = null)
		{
			var met = new List<string>();
			if (temp is > 38m or < 36m) met.Add("T=" + temp!.Value.ToString("0.0", CultureInfo.InvariantCulture));
			if (hr is > 90) met.Add("HR=" + hr);
			if (rr is > 20) met.Add("RR=" + rr);
			if (wbc is > 12m or < 4m) met.Add("WBC=" + wbc!.Value.ToString("0.0", CultureInfo.InvariantCulture));
			return (met.Count, met);
		}

		public const decimal LactateHigh = 2m, LactateShock = 4m;

		/// Why the reading (with the latest white count and lactate of the last 24 hours) fires a sepsis alert, or null:
		/// qSOFA ≥ 2; or, with an infection on record, SIRS ≥ 2 or a lactate ≥ 2 mmol/L.
		public static (string Criteria, int Score)? Sepsis(HospitalObservation o, bool infection, decimal? wbc = null, decimal? lactate = null)
		{
			string Lac() => lactate is decimal l && l >= LactateHigh ? ";LAC=" + l.ToString("0.0", CultureInfo.InvariantCulture) : "";
			var q = Qsofa(o.RespRate, o.SystolicBp, o.Consciousness);
			if (q.Score >= 2) return ("qSOFA;" + string.Join(";", q.Met) + Lac(), q.Score);
			var s = Sirs(o.TempC, o.HeartRate, o.RespRate, wbc);
			if (s.Score >= 2 && infection) return ("SIRS;" + string.Join(";", s.Met) + Lac() + ";INF", s.Score);
			if (lactate is decimal lac && lac >= LactateHigh && infection) return ("LACTATE" + Lac() + ";INF", s.Score);
			return null;
		}

		// ---- the Padua prediction score (VTE risk of a medical inpatient); 4 or more is high risk ----
		public static readonly IReadOnlyList<(string Key, int Points)> PaduaItems = new[]
		{
			("cancer", 3), ("previousVte", 3), ("immobility", 3), ("thrombophilia", 3), ("traumaSurgery", 2), ("age70", 1),
			("heartRespFailure", 1), ("miStroke", 1), ("infectionRheum", 1), ("obesity", 1), ("hormones", 1),
		};
		public const int PaduaHigh = 4;

		public static int Padua(IEnumerable<string>? items) =>
			(items ?? Array.Empty<string>()).Distinct().Sum(k => PaduaItems.FirstOrDefault(p => p.Key == k).Points);

		/// Whether a new sepsis alert may open now: none open, and the last one raised at least 12 hours before this reading.
		public static bool SepsisMayFire(IEnumerable<HospitalSurveillanceAlert> earlier, DateTime readingAt) =>
			!earlier.Any(a => a.Kind == HospitalSurveillanceKind.Sepsis && (a.Status == HospitalSurveillanceStatus.Open || readingAt < a.RaisedAt.AddHours(SepsisQuietHours)));

		public static bool Overdue(HospitalSurveillanceAlert a, DateTime nowUtc) => a.Status == HospitalSurveillanceStatus.Open && nowUtc > a.DueAt;

		public static string? AnswerRefusal(HospitalSurveillanceAlert a, string? response, string? note)
		{
			if (a.Status != HospitalSurveillanceStatus.Open) return "This alert is already answered";
			if (string.IsNullOrEmpty(response) || !HospitalSurveillanceResponse.For(a.Kind).Contains(response)) return "Choose the decision";
			bool needsWhy = response is HospitalSurveillanceResponse.NotSepsis or HospitalSurveillanceResponse.NotIndicated or HospitalSurveillanceResponse.Contraindicated;
			if (needsWhy && string.IsNullOrWhiteSpace(note)) return "Write the reason for this decision";
			return (note?.Trim().Length ?? 0) > 500 ? "The text is too long" : null;
		}

		/// A VTE answer is scored on the Padua items (an empty list means none applies); a high-risk patient is not "not indicated".
		public static string? VteRefusal(string? response, IReadOnlyList<string>? padua)
		{
			if (padua == null) return "Score the Padua items first";
			if (padua.Any(k => !PaduaItems.Any(p => p.Key == k))) return "not_found";
			return response == HospitalSurveillanceResponse.NotIndicated && Padua(padua) >= PaduaHigh
				? "Padua 4 or more is high risk: order prophylaxis, or record why it is contraindicated" : null;
		}
	}

	public sealed class HospitalSurveillanceService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSurveillanceService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalSurveillanceAlert> Alerts(BusinessContext ctx) => _db.Set<HospitalSurveillanceAlert>().Where(x => x.CompanyId == ctx.CompanyId);

		/// After an observation: the admission's latest reading checked for sepsis. Never fails the caller (the table may
		/// not be installed); returns whether an alert opened.
		public static async Task<bool> EvaluateAsync(CrossDbContext db, int companyId, int admissionId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var adm = await db.HospitalAdmissions.AsNoTracking().Where(a => a.ID == admissionId && a.CompanyId == companyId && a.Status == HospitalAdmissionStatus.Active).Select(a => new { a.ID, a.PatientId }).FirstOrDefaultAsync(ct);
				if (adm == null) return false;
				var o = await db.Set<HospitalObservation>().AsNoTracking().Where(x => x.CompanyId == companyId && x.AdmissionId == admissionId).OrderByDescending(x => x.TakenAt).ThenByDescending(x => x.ID).FirstOrDefaultAsync(ct);
				if (o == null) return false;
				bool infection = await db.Set<HospitalIsolation>().AnyAsync(x => x.CompanyId == companyId && x.PatientId == adm.PatientId && x.EndedAt == null, ct)
					|| await db.Set<HospitalInfectionCase>().AnyAsync(x => x.CompanyId == companyId && x.PatientId == adm.PatientId && x.Status != HospitalInfectionStatus.RuledOut, ct);
				var (wbc, lactate) = await LabsAsync(db, companyId, adm.PatientId, nowUtc, ct);
				if (HospitalSurveillanceRules.Sepsis(o, infection, wbc, lactate) is not var (criteria, score)) return false;
				var earlier = await db.Set<HospitalSurveillanceAlert>().AsNoTracking().Where(a => a.CompanyId == companyId && a.AdmissionId == admissionId && a.Kind == HospitalSurveillanceKind.Sepsis).ToListAsync(ct);
				if (!HospitalSurveillanceRules.SepsisMayFire(earlier, o.TakenAt)) return false;
				db.Add(new HospitalSurveillanceAlert
				{
					CompanyId = companyId, PatientId = adm.PatientId, AdmissionId = adm.ID, Kind = HospitalSurveillanceKind.Sepsis, Criteria = criteria, Score = score, ObservationId = o.ID,
					RaisedAt = nowUtc, DueAt = nowUtc.AddMinutes(HospitalSurveillanceRules.SepsisDueMinutes), Status = HospitalSurveillanceStatus.Open,
				});
				await db.SaveChangesAsync(ct);
				return true;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { db.ChangeTracker.Clear(); return false; }
		}

		/// The latest white count and lactate of the last 24 hours (by the analyte's code: WBC / LOINC 6690-2; LACT, LAC,
		/// LOINC 2524-7 or 32693-4 — or a name with "lactate").
		public static async Task<(decimal? Wbc, decimal? Lactate)> LabsAsync(CrossDbContext db, int companyId, int patientId, DateTime nowUtc, CancellationToken ct)
		{
			var since = nowUtc.AddHours(-24);
			var rows = await (from r in db.Set<HospitalLabResult>().AsNoTracking()
							  join o in db.HospitalLabOrders.AsNoTracking() on r.LabOrderId equals o.ID
							  join a in db.Set<HospitalLabAnalyte>().AsNoTracking() on r.AnalyteId equals a.ID
							  where o.CompanyId == companyId && o.PatientId == patientId && r.Value != null && o.ResultedAt >= since
								 && (a.Code == "WBC" || a.Code == "6690-2" || a.Code == "LACT" || a.Code == "LAC" || a.Code == "2524-7" || a.Code == "32693-4" || a.NameEn.Contains("lactate"))
							  orderby o.ResultedAt descending
							  select new { a.Code, a.NameEn, r.Value }).ToListAsync(ct);
			decimal? wbc = rows.FirstOrDefault(x => x.Code is "WBC" or "6690-2")?.Value;
			decimal? lac = rows.FirstOrDefault(x => x.Code is not ("WBC" or "6690-2"))?.Value;
			return (wbc, lac);
		}

		/// Every active admission: the sepsis check on its latest reading, and its VTE prompt if it has none. Returns how many opened.
		public async Task<int> SweepAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var active = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active)
				.Select(a => new { a.ID, a.PatientId, a.AdmittedAt }).ToListAsync(ct);
			if (active.Count == 0) return 0;
			var ids = active.Select(a => a.ID).ToList();
			var withVte = (await Alerts(ctx).Where(a => a.Kind == HospitalSurveillanceKind.Vte && ids.Contains(a.AdmissionId)).Select(a => a.AdmissionId).ToListAsync(ct)).ToHashSet();
			int opened = 0;
			foreach (var a in active.Where(a => !withVte.Contains(a.ID)))
			{
				_db.Add(new HospitalSurveillanceAlert
				{
					CompanyId = ctx.CompanyId, PatientId = a.PatientId, AdmissionId = a.ID, Kind = HospitalSurveillanceKind.Vte, Criteria = "ADM",
					RaisedAt = nowUtc, DueAt = a.AdmittedAt.AddHours(HospitalSurveillanceRules.VteDueHours), Status = HospitalSurveillanceStatus.Open,
				});
				opened++;
			}
			if (opened > 0) await _db.SaveChangesAsync(ct);
			foreach (var a in active) if (await EvaluateAsync(_db, ctx.CompanyId, a.ID, nowUtc, ct)) opened++;
			return opened;
		}

		public async Task<HospitalResult> AnswerAsync(BusinessContext ctx, int id, string? response, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default, IReadOnlyList<string>? padua = null)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return HospitalResult.Fail("forbidden");
			var a = await Alerts(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (HospitalSurveillanceRules.AnswerRefusal(a, response, note) is string why) return HospitalResult.Fail(why);
			string criteria = a.Criteria; int? score = a.Score;
			if (a.Kind == HospitalSurveillanceKind.Vte)
			{
				if (HospitalSurveillanceRules.VteRefusal(response, padua) is string vte) return HospitalResult.Fail(vte);
				score = HospitalSurveillanceRules.Padua(padua);
				criteria = "PADUA" + (padua!.Count == 0 ? "" : ";" + string.Join(";", padua.Distinct()));
			}
			string? n = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			int done = await Alerts(ctx).Where(x => x.ID == id && x.Status == HospitalSurveillanceStatus.Open).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Criteria, criteria).SetProperty(x => x.Score, score)
				.SetProperty(x => x.Status, HospitalSurveillanceStatus.Answered).SetProperty(x => x.Response, response).SetProperty(x => x.Note, n)
				.SetProperty(x => x.AnsweredAt, nowUtc).SetProperty(x => x.AnsweredByEmployeeId, employeeId), ct);
			return done == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("Someone else answered this alert: open it again");
		}

		public async Task<(IReadOnlyList<HospitalSurveillanceRow> Rows, int Total, int OpenSepsis, int OpenVte, int Overdue, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? kind, string? status,
			bool overdueOnly, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalSurveillanceRow>(), 0, 0, 0, 0, true);
			try
			{
				await SweepAsync(ctx, nowUtc, ct);
				var query = from a in Alerts(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							where p.CompanyId == ctx.CompanyId
							select new { a, p };
				if (!string.IsNullOrEmpty(kind)) query = query.Where(x => x.a.Kind == kind);
				if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.a.Status == status);
				if (overdueOnly) query = query.Where(x => x.a.Status == HospitalSurveillanceStatus.Open && x.a.DueAt < nowUtc);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.p.Name.Contains(s) || x.p.NameEn.Contains(s) || x.p.Mrn.Contains(s)); }
				int total = await query.CountAsync(ct);
				var open = Alerts(ctx).Where(a => a.Status == HospitalSurveillanceStatus.Open);
				int sepsis = await open.CountAsync(a => a.Kind == HospitalSurveillanceKind.Sepsis, ct), vte = await open.CountAsync(a => a.Kind == HospitalSurveillanceKind.Vte, ct);
				int late = await open.CountAsync(a => a.DueAt < nowUtc, ct);
				page = Math.Max(1, page);
				var rows = await query.OrderByDescending(x => x.a.Status == HospitalSurveillanceStatus.Open).ThenByDescending(x => x.a.Kind == HospitalSurveillanceKind.Sepsis).ThenBy(x => x.a.DueAt).ThenByDescending(x => x.a.ID)
					.Skip((page - 1) * size).Take(size).ToListAsync(ct);
				var adm = rows.Select(x => x.a.AdmissionId).Distinct().ToList();
				var beds = (await (from s in _db.HospitalBedStays.AsNoTracking()
								   join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
								   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
								   join w in _db.HospitalWards.AsNoTracking() on r.WardId equals w.ID
								   where s.CompanyId == ctx.CompanyId && s.EndedAt == null && adm.Contains(s.AdmissionId)
								   select new { s.AdmissionId, Bed = b.Code, w.Name, w.NameEn }).ToListAsync(ct))
					.GroupBy(x => x.AdmissionId).ToDictionary(g => g.Key, g => g.First());
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(x => x.a.AnsweredByEmployeeId), ct);
				return (rows.Select(x =>
				{
					var bd = beds.GetValueOrDefault(x.a.AdmissionId);
					return new HospitalSurveillanceRow(x.a, x.p.Name, x.p.NameEn, x.p.Mrn, bd?.Bed, bd?.Name, bd?.NameEn, HospitalSurveillanceRules.Overdue(x.a, nowUtc),
						x.a.AnsweredByEmployeeId is int e ? faces.GetValueOrDefault(e) : null) { PatientPhoto = x.p.PhotoPath };
				}).ToList(), total, sepsis, vte, late, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalSurveillanceRow>(), 0, 0, 0, 0, false); }
		}
	}
}
