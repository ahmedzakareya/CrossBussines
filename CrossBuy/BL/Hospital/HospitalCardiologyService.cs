using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H218 — cardiac studies. A doctor requests a 12-lead ECG or a transthoracic echo with the indication; the
	// cardiologist reports it:
	//   · ECG — rate, rhythm, PR, QRS, QT and axis; the QTc is CALCULATED (Bazett: QT / √RR, RR = 60 / rate), never typed.
	//   · ECHO — ejection fraction, chambers, valves, wall motion, pericardium.
	// The interpretation is required. A CRITICAL finding is flagged by the system, not by memory: a rate under 40 or over
	// 150, a QTc of 500 ms or more, or an ejection fraction under 30% — the list shows it in red and the reason beside it.
	// ==========================================================================================
	public sealed class HospitalCardioRequestInput { public int PatientId { get; set; } public string? Kind { get; set; } public string? Indication { get; set; } }

	public sealed class HospitalCardioReportInput
	{
		public int? HeartRate { get; set; }
		public string? Rhythm { get; set; }
		public int? PrMs { get; set; }
		public int? QrsMs { get; set; }
		public int? QtMs { get; set; }
		public int? Axis { get; set; }
		public int? EfPercent { get; set; }
		public string? Chambers { get; set; }
		public string? Valves { get; set; }
		public string? WallMotion { get; set; }
		public string? Pericardium { get; set; }
		public string? Interpretation { get; set; }
	}

	public sealed record HospitalEcgImport(string Ack, string? Detail, int? StudyId);

	/// H221 — the ECG machine's message read into a study: which OBX is which measurement (LOINC, the IEEE 11073 MDC code,
	/// or the measurement's own name), the machine's statement, the study it names (OBR-2 / OBR-3 = the study number).
	public static class HospitalEcgImportRules
	{
		public static string? FieldOf(string code, string text)
		{
			var c = (code ?? "").Trim().ToUpperInvariant(); var t = (text ?? "").Trim().ToUpperInvariant();
			if (c is "8867-4" or "147842" or "MDC_ECG_HEART_RATE" or "HR" || t is "HR" or "HEART RATE" or "VENT RATE" or "VENTRICULAR RATE") return "HR";
			if (c is "8625-6" or "148066" or "MDC_ECG_TIME_PD_PR" or "PR" || t is "PR" or "PR INTERVAL") return "PR";
			if (c is "8633-0" or "148065" or "MDC_ECG_TIME_PD_QRS" or "QRS" || t is "QRS" or "QRS DURATION") return "QRS";
			if (c is "8634-8" or "148064" or "MDC_ECG_TIME_PD_QT_GL" or "QT" || t is "QT" or "QT INTERVAL") return "QT";
			if (c is "8632-2" or "147236" or "MDC_ECG_ANGLE_QRS_FRONT" or "AXIS" or "QRS AXIS" || t is "AXIS" or "QRS AXIS" or "R AXIS") return "AXIS";
			if (c is "18844-1" or "11524-6" or "INTERP" or "INTERPRETATION" || t.Contains("INTERPRET") || t.Contains("STATEMENT") || t.Contains("DIAGNOSIS")) return "TEXT";
			return null;   // QTc is never taken from a machine: the system calculates it
		}

		public static int? Int(string v) => decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
	}

	public sealed record HospitalCardioRow(HospitalCardioStudy Study, string Patient, string PatientEn, string Mrn, HospitalFace? Reader, HospitalFace? Requester, string? PatientPhoto = null);

	public static class HospitalCardioRules
	{
		/// Bazett: QTc = QT / √(RR in seconds), RR = 60 / heart rate.
		public static int? Qtc(int? qtMs, int? rate) => qtMs is int qt && rate is int hr && hr > 0 ? (int)Math.Round(qt / Math.Sqrt(60.0 / hr)) : null;

		/// The critical findings in a report ("" when none).
		public static string Critical(string kind, int? rate, int? qtc, int? ef)
		{
			var why = new List<string>();
			if (kind == HospitalCardioKind.Ecg)
			{
				if (rate is < 40) why.Add("HR<40");
				if (rate is > 150) why.Add("HR>150");
				if (qtc is >= 500) why.Add("QTc>=500");
			}
			else if (ef is < 30) why.Add("EF<30");
			return string.Join(";", why);
		}

		public static string? RequestRefusal(HospitalCardioRequestInput i)
		{
			if (string.IsNullOrEmpty(i.Kind) || !HospitalCardioKind.All.Contains(i.Kind)) return "Choose the study";
			if (string.IsNullOrWhiteSpace(i.Indication)) return "Write the indication";
			return i.Indication.Trim().Length > 500 ? "The text is too long" : null;
		}

		public static string? ReportRefusal(HospitalCardioStudy s, HospitalCardioReportInput i)
		{
			if (s.Status != HospitalCardioStatus.Requested) return "This study is already reported";
			if (string.IsNullOrWhiteSpace(i.Interpretation)) return "Write the interpretation";
			if (s.Kind == HospitalCardioKind.Ecg)
			{
				if (i.HeartRate is not (>= 15 and <= 300)) return "Write the heart rate";
				if (string.IsNullOrWhiteSpace(i.Rhythm)) return "Write the rhythm";
				if (i.QtMs is < 200 or > 700 || i.PrMs is < 40 or > 600 || i.QrsMs is < 40 or > 300 || i.Axis is < -180 or > 180) return "A measurement is outside what an ECG can show";
			}
			else if (i.EfPercent is not (>= 5 and <= 90)) return "Write the ejection fraction (5 to 90%)";
			return (i.Interpretation.Trim().Length > 2000) ? "The text is too long" : null;
		}
	}

	public sealed class HospitalCardiologyService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalCardiologyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalCardioStudy> Studies(BusinessContext ctx) => _db.Set<HospitalCardioStudy>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		public async Task<HospitalResult> RequestAsync(BusinessContext ctx, HospitalCardioRequestInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalCardioRules.RequestRefusal(input) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var nos = await Studies(ctx).Where(s => s.StudyNo.StartsWith("CARD-")).Select(s => s.StudyNo).ToListAsync(ct);
			int n = 0; foreach (var no in nos) if (int.TryParse(no.AsSpan(5), out var v) && v > n) n = v;
			var s = new HospitalCardioStudy { CompanyId = ctx.CompanyId, StudyNo = "CARD-" + (n + 1).ToString("000000"), PatientId = input.PatientId, Kind = input.Kind!, Indication = Clip(input.Indication, 500)!, Status = HospitalCardioStatus.Requested, RequestedAt = nowUtc, RequestedByEmployeeId = employeeId };
			_db.Add(s);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(s.ID);
		}

		public async Task<HospitalResult> ReportAsync(BusinessContext ctx, int id, HospitalCardioReportInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var s = await Studies(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			if (HospitalCardioRules.ReportRefusal(s, input) is string why) return HospitalResult.Fail(why);
			if (s.Kind == HospitalCardioKind.Ecg)
			{
				s.HeartRate = input.HeartRate; s.Rhythm = Clip(input.Rhythm, 100); s.PrMs = input.PrMs; s.QrsMs = input.QrsMs; s.QtMs = input.QtMs; s.Axis = input.Axis;
				s.QtcMs = HospitalCardioRules.Qtc(input.QtMs, input.HeartRate);
			}
			else
			{
				s.EfPercent = input.EfPercent; s.Chambers = Clip(input.Chambers, 500); s.Valves = Clip(input.Valves, 500); s.WallMotion = Clip(input.WallMotion, 500); s.Pericardium = Clip(input.Pericardium, 200);
			}
			var crit = HospitalCardioRules.Critical(s.Kind, s.HeartRate, s.QtcMs, s.EfPercent);
			s.Critical = crit.Length > 0; s.CriticalWhy = crit.Length > 0 ? crit : null;
			s.Interpretation = Clip(input.Interpretation, 2000); s.Status = HospitalCardioStatus.Reported; s.ReportedAt = nowUtc; s.ReportedByEmployeeId = employeeId;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateConcurrencyException) { return HospitalResult.Fail("Someone else reported this study: open it again"); }
			return HospitalResult.Success(s.ID);
		}

		/// H221: an ECG machine's ORU^R01 filed on the requested ECG study it names (as the device reading; the cardiologist
		/// still confirms and signs). Checked with the rights of the employee the interface acts for.
		public async Task<HospitalEcgImport> ImportAsync(BusinessContext ctx, string? raw, DateTime nowUtc, CancellationToken ct = default)
		{
			var (msg, why) = HospitalHl7.ParseOru(raw);
			if (msg == null) return new("AR", why, null);
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return new("AR", "forbidden", null);
			var nos = new[] { msg.Placer, msg.Filler }.Where(x => !string.IsNullOrEmpty(x)).ToList();
			var s = await Studies(ctx).FirstOrDefaultAsync(x => nos.Contains(x.StudyNo), ct);
			if (s == null) return new("AR", "No ECG study has this number", null);
			if (s.Kind != HospitalCardioKind.Ecg) return new("AR", "This study is not an ECG", s.ID);
			if (s.Status != HospitalCardioStatus.Requested) return new("AR", "This study is already reported", s.ID);
			int known = 0; var unknown = new List<string>(); var text = new List<string>();
			foreach (var o in msg.Results)
			{
				switch (HospitalEcgImportRules.FieldOf(o.Code, o.Text))
				{
					case "HR": s.HeartRate = HospitalEcgImportRules.Int(o.Value) is int hr and >= 15 and <= 300 ? hr : s.HeartRate; known++; break;
					case "PR": s.PrMs = HospitalEcgImportRules.Int(o.Value) is int pr and >= 40 and <= 600 ? pr : s.PrMs; known++; break;
					case "QRS": s.QrsMs = HospitalEcgImportRules.Int(o.Value) is int qrs and >= 40 and <= 300 ? qrs : s.QrsMs; known++; break;
					case "QT": s.QtMs = HospitalEcgImportRules.Int(o.Value) is int qt and >= 200 and <= 700 ? qt : s.QtMs; known++; break;
					case "AXIS": s.Axis = HospitalEcgImportRules.Int(o.Value) is int ax and >= -180 and <= 180 ? ax : s.Axis; known++; break;
					case "TEXT": if (!string.IsNullOrWhiteSpace(o.Value)) text.Add(o.Value.Trim()); known++; break;
					default: unknown.Add(string.IsNullOrEmpty(o.Code) ? o.Text : o.Code); break;
				}
			}
			if (known == 0) return new("AE", "No ECG measurement in the message" + (unknown.Count > 0 ? ": " + string.Join(", ", unknown.Take(5)) : ""), s.ID);
			s.QtcMs = HospitalCardioRules.Qtc(s.QtMs, s.HeartRate);
			s.DeviceName = Clip(msg.Device, 60); s.DeviceInterpretation = Clip(string.Join(" · ", text), 1000); s.DeviceReadAt = nowUtc;
			await _db.SaveChangesAsync(ct);
			return new(unknown.Count == 0 ? "AA" : "AE", unknown.Count == 0 ? null : "Not read: " + string.Join(", ", unknown.Take(5)), s.ID);
		}

		public async Task<(IReadOnlyList<HospitalCardioRow> Rows, int Total, int Waiting, int Critical, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? kind, string? status, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalCardioRow>(), 0, 0, 0, true);
			try
			{
				var query = from s in Studies(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							where p.CompanyId == ctx.CompanyId
							select new { s, p };
				if (!string.IsNullOrEmpty(kind)) query = query.Where(x => x.s.Kind == kind);
				if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.s.Status == status);
				if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.s.StudyNo.Contains(t) || x.p.Name.Contains(t) || x.p.NameEn.Contains(t) || x.p.Mrn.Contains(t)); }
				int total = await query.CountAsync(ct);
				int waiting = await Studies(ctx).CountAsync(s => s.Status == HospitalCardioStatus.Requested, ct);
				int critical = await Studies(ctx).CountAsync(s => s.Critical, ct);
				var rows = await query.OrderBy(x => x.s.Status == HospitalCardioStatus.Reported).ThenByDescending(x => x.s.ID).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.SelectMany(x => new[] { x.s.ReportedByEmployeeId, x.s.RequestedByEmployeeId }), ct);
				HospitalFace? F(int? id) => id is int i ? faces.GetValueOrDefault(i) : null;
				return (rows.Select(x => new HospitalCardioRow(x.s, x.p.Name, x.p.NameEn, x.p.Mrn, F(x.s.ReportedByEmployeeId), F(x.s.RequestedByEmployeeId)) { PatientPhoto = x.p.PhotoPath }).ToList(), total, waiting, critical, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalCardioRow>(), 0, 0, 0, false); }
		}
	}
}
