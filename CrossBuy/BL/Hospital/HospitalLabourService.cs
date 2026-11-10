using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H86 — the partograph. The active phase starts at the first observation with the cervix at 4 cm or more; the
	// alert line runs from there at 1 cm an hour, the action line four hours to its right. A later dilatation to the right
	// of a line has crossed it. The other warnings come from each observation's own measures.
	// ==========================================================================================
	public sealed class HospitalLabourObservationInput
	{
		public int LabourId { get; set; }
		/// Ward-local; empty means now.
		public DateTime? At { get; set; }
		public decimal? CervixCm { get; set; }
		public int? DescentFifths { get; set; }
		public int? ContractionsPer10 { get; set; }
		public int? ContractionSeconds { get; set; }
		public int? FetalHeartRate { get; set; }
		public string? Liquor { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? Pulse { get; set; }
		public decimal? TemperatureC { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalLabourCloseInput
	{
		public string? Outcome { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalLabourWarning
	{
		public const string AlertLine = "AlertLine";
		public const string ActionLine = "ActionLine";
		public const string FetalHeart = "FetalHeart";
		public const string Meconium = "Meconium";
		public const string Tachysystole = "Tachysystole";
		public const string Hypertension = "Hypertension";
		public const string Fever = "Fever";
	}

	public sealed record HospitalLabourPoint(HospitalLabourObservation Obs, double Hours, IReadOnlyList<string> Warnings);
	public sealed record HospitalLabourPage(HospitalLabour Labour, string Name, string NameEn, string Mrn, string? Photo, DateTime? ActiveStart, decimal? ActiveCervix,
		IReadOnlyList<HospitalLabourPoint> Points, IReadOnlyList<string> Current, IReadOnlyDictionary<int, string> People);
	public sealed record HospitalLabourRow(HospitalLabour Labour, int PatientId, string Name, string NameEn, string Mrn, string? Bed, HospitalLabourObservation? Last, IReadOnlyList<string> Warnings, int MinutesSinceLast);

	public static class HospitalLabourRules
	{
		public const decimal ActivePhaseCm = 4m;
		public const double ActionHoursRight = 4;

		/// The first observation of the active phase (cervix ≥ 4 cm), ordered by time; null before it.
		public static (DateTime At, decimal Cm)? ActiveStart(IEnumerable<HospitalLabourObservation> obs)
		{
			var f = obs.Where(o => o.CervixCm >= ActivePhaseCm).OrderBy(o => o.At).FirstOrDefault();
			return f == null ? null : (f.At, f.CervixCm!.Value);
		}

		/// Where the alert line expects the cervix to be `hours` after the active-phase start (capped at 10 cm).
		public static decimal AlertCm(decimal startCm, double hours) => Math.Min(10m, startCm + (decimal)hours);

		/// Crossed the alert line: dilatation behind 1 cm/h. Crossed the action line: behind it by four hours or more.
		public static IReadOnlyList<string> LineWarnings((DateTime At, decimal Cm)? start, HospitalLabourObservation o)
		{
			var w = new List<string>();
			if (start == null || o.CervixCm == null || o.At <= start.Value.At || o.CervixCm >= 10) return w;
			double h = (o.At - start.Value.At).TotalHours;
			if (o.CervixCm < AlertCm(start.Value.Cm, h - ActionHoursRight) && h >= ActionHoursRight) w.Add(HospitalLabourWarning.ActionLine);
			else if (o.CervixCm < AlertCm(start.Value.Cm, h)) w.Add(HospitalLabourWarning.AlertLine);
			return w;
		}

		public static IReadOnlyList<string> Warnings((DateTime At, decimal Cm)? start, HospitalLabourObservation o)
		{
			var w = new List<string>(LineWarnings(start, o));
			if (o.FetalHeartRate is int f && (f < 110 || f > 160)) w.Add(HospitalLabourWarning.FetalHeart);
			if (o.Liquor == HospitalLiquor.Meconium) w.Add(HospitalLabourWarning.Meconium);
			if (o.ContractionsPer10 > 5) w.Add(HospitalLabourWarning.Tachysystole);
			if (o.BpSystolic >= 140 || o.BpDiastolic >= 90) w.Add(HospitalLabourWarning.Hypertension);
			if (o.TemperatureC >= 38m) w.Add(HospitalLabourWarning.Fever);
			return w;
		}

		public static string? ObservationRefusal(HospitalLabourObservationInput i, DateTime atUtc, DateTime startedAtUtc, DateTime nowUtc, decimal? lastCervix)
		{
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < startedAtUtc.AddMinutes(-5)) return "The time is before the labour started";
			if (i.CervixCm is < 0 or > 10) return "The cervix is between 0 and 10 cm";
			if (i.CervixCm != null && lastCervix != null && i.CervixCm < lastCervix - 1) return "The cervix cannot close by more than a centimetre: check the reading";
			if (i.DescentFifths is < 0 or > 5) return "The descent is between 0 and 5 fifths";
			if (i.ContractionsPer10 is < 0 or > 10) return "The contractions are between 0 and 10 in ten minutes";
			if (i.FetalHeartRate is < 50 or > 240) return "The fetal heart rate is not plausible";
			if (!string.IsNullOrEmpty(i.Liquor) && !HospitalLiquor.All.Contains(i.Liquor)) return "Choose the liquor";
			if ((i.BpSystolic == null) != (i.BpDiastolic == null)) return "Write both blood pressure numbers";
			if (i.TemperatureC is < 33 or > 43) return "The temperature is not plausible";
			bool any = i.CervixCm != null || i.DescentFifths != null || i.ContractionsPer10 != null || i.FetalHeartRate != null || !string.IsNullOrEmpty(i.Liquor)
				|| i.BpSystolic != null || i.Pulse != null || i.TemperatureC != null || !string.IsNullOrWhiteSpace(i.Note);
			return any ? null : "Write at least one measure or a note";
		}

		public static string? StartRefusal(DateTime startUtc, DateTime admittedAtUtc, DateTime nowUtc)
		{
			if (startUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (startUtc < nowUtc.AddHours(-24)) return "The labour's start can be written back a day at most";
			if (startUtc < admittedAtUtc.AddMinutes(-5)) return "The time is before the admission";
			return null;
		}

		public static string? CloseRefusal(HospitalLabour l, string? outcome)
		{
			if (l.Status != HospitalLabourStatus.Active) return "This labour is already closed";
			if (!HospitalLabourOutcome.All.Contains(outcome ?? "")) return "Choose how the labour ended";
			return null;
		}
	}

	public sealed class HospitalLabourService
	{
		public const string NoTables = "The partograph is not installed yet (hospital_h86_labour.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalLabourService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalLabour> Labours(BusinessContext ctx) => _db.Set<HospitalLabour>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalLabourObservation> Obs(BusinessContext ctx) => _db.Set<HospitalLabourObservation>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<bool> WardAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		/// The labour is opened when the midwife opens the partograph, often hours after it began: the start may be written
		/// back up to a day, never before the admission and never in the future.
		public async Task<HospitalResult> StartAsync(BusinessContext ctx, int admissionId, DateTime? startedAtLocal, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await (from x in _db.HospitalAdmissions.AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on x.PatientId equals p.ID
						   where x.ID == admissionId && x.CompanyId == ctx.CompanyId select new { x.ID, x.PatientId, x.Status, x.AdmittedAt, p.Sex }).FirstOrDefaultAsync(ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			if (a.Sex != HospitalPatientSex.Female) return HospitalResult.Fail("A labour is opened on a female patient's stay");
			var startUtc = startedAtLocal == null ? nowUtc : HospitalNursingService.WardToUtc(startedAtLocal.Value);
			if (HospitalLabourRules.StartRefusal(startUtc, a.AdmittedAt, nowUtc) is string bad) return HospitalResult.Fail(bad);
			try
			{
				var open = await Labours(ctx).Where(x => x.AdmissionId == a.ID && x.Status == HospitalLabourStatus.Active).Select(x => (int?)x.ID).FirstOrDefaultAsync(ct);
				if (open != null) return HospitalResult.Success(open);   // already open: go to it
				var row = new HospitalLabour { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId, Status = HospitalLabourStatus.Active, StartedAt = startUtc, StartedByEmployeeId = employeeId };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ObserveAsync(BusinessContext ctx, HospitalLabourObservationInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var l = await Labours(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.LabourId, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				if (l.Status != HospitalLabourStatus.Active) return HospitalResult.Fail("This labour is already closed");
				var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
				var lastCervix = await Obs(ctx).Where(o => o.LabourId == l.ID && o.CervixCm != null && o.At <= at).OrderByDescending(o => o.At).Select(o => o.CervixCm).FirstOrDefaultAsync(ct);
				if (HospitalLabourRules.ObservationRefusal(input, at, l.StartedAt, nowUtc, lastCervix) is string why) return HospitalResult.Fail(why);
				var row = new HospitalLabourObservation
				{
					CompanyId = ctx.CompanyId, LabourId = l.ID, PatientId = l.PatientId, At = at, CervixCm = input.CervixCm, DescentFifths = input.DescentFifths,
					ContractionsPer10 = input.ContractionsPer10, ContractionSeconds = input.ContractionSeconds, FetalHeartRate = input.FetalHeartRate,
					Liquor = string.IsNullOrEmpty(input.Liquor) ? null : input.Liquor, BpSystolic = input.BpSystolic, BpDiastolic = input.BpDiastolic, Pulse = input.Pulse,
					TemperatureC = input.TemperatureC, Note = Clip(input.Note, 300), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, HospitalLabourCloseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var l = await Labours(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				if (HospitalLabourRules.CloseRefusal(l, input.Outcome) is string why) return HospitalResult.Fail(why);
				l.Status = HospitalLabourStatus.Closed; l.Outcome = input.Outcome; l.ClosedAt = nowUtc; l.ClosedByEmployeeId = employeeId; l.Note = Clip(input.Note, 500);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(l.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalLabourPage?> PageAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			try
			{
				var l = await Labours(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (l == null) return null;
				var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == l.PatientId).Select(x => new { x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstAsync(ct);
				var obs = await Obs(ctx).AsNoTracking().Where(o => o.LabourId == id).OrderBy(o => o.At).ThenBy(o => o.ID).ToListAsync(ct);
				var start = HospitalLabourRules.ActiveStart(obs);
				var points = obs.Select(o => new HospitalLabourPoint(o, (o.At - l.StartedAt).TotalHours, HospitalLabourRules.Warnings(start, o))).ToList();
				var eids = obs.Where(o => o.CreatedByEmployeeId != null).Select(o => o.CreatedByEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalLabourPage(l, p.Name, p.NameEn ?? "", p.Mrn, p.PhotoPath, start?.At, start?.Cm, points, points.LastOrDefault()?.Warnings ?? Array.Empty<string>(), people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The labours in progress, the most worrying first: its latest warnings, and how long since anyone looked.
		public async Task<(IReadOnlyList<HospitalLabourRow> Rows, bool Ready)> ActiveAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var ls = await (from l in Labours(ctx).AsNoTracking()
								join p in _db.HospitalPatients.AsNoTracking() on l.PatientId equals p.ID
								where l.Status == HospitalLabourStatus.Active
								select new { l, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct);
				var ids = ls.Select(x => x.l.ID).ToList();
				var obs = await Obs(ctx).AsNoTracking().Where(o => ids.Contains(o.LabourId)).ToListAsync(ct);
				var adm = ls.Select(x => x.l.AdmissionId).ToList();
				var beds = await (from s in _db.HospitalBedStays.AsNoTracking() join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
								  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && adm.Contains(s.AdmissionId) select new { s.AdmissionId, b.Code }).ToListAsync(ct);
				var rows = ls.Select(x =>
				{
					var mine = obs.Where(o => o.LabourId == x.l.ID).OrderBy(o => o.At).ToList();
					var last = mine.LastOrDefault();
					var w = last == null ? Array.Empty<string>() : HospitalLabourRules.Warnings(HospitalLabourRules.ActiveStart(mine), last);
					int since = (int)Math.Round((nowUtc - (last?.At ?? x.l.StartedAt)).TotalMinutes);
					return new HospitalLabourRow(x.l, x.l.PatientId, x.Name, x.NameEn ?? "", x.Mrn, beds.FirstOrDefault(b => b.AdmissionId == x.l.AdmissionId)?.Code, last, w, since);
				}).OrderByDescending(r => r.Warnings.Count).ThenByDescending(r => r.MinutesSinceLast).ToList();
				return (rows, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalLabourRow>(), false); }
		}
	}
}
