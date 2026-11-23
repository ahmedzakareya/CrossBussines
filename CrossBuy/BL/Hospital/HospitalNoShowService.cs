using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalNoShowRow(HospitalAppointment Appointment, string PatientName, string? PatientNameEn, string? Photo, string Mrn, string? Phone,
		HospitalFace? Doctor, int Score, string Band, IReadOnlyList<string> Reasons, string? Confirmation);
	public sealed record HospitalNoShowValidation(int Cases, int Events, decimal? Auc, IReadOnlyList<(string Band, int Cases, int Events)> Bands, bool Reliable);
	public sealed record HospitalNoShowPage(IReadOnlyList<HospitalNoShowRow> Rows, int Total, int High, HospitalNoShowValidation Validation);

	// ==========================================================================================
	// HOSPITAL H263 — the desk's list of the coming week's booked appointments with their no-show risk (highest first)
	// and the model's validation on the last 12 months. The desk calls through the existing confirmation (H141).
	// ==========================================================================================
	public sealed class HospitalNoShowService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalNoShowService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private async Task<Dictionary<int, string>> ConfirmationsAsync(int companyId, List<int> apptIds, CancellationToken ct)
		{
			try { return await _db.Set<HospitalDeskConfirmation>().AsNoTracking().Where(c => c.CompanyId == companyId && apptIds.Contains(c.AppointmentId)).ToDictionaryAsync(c => c.AppointmentId, c => c.Status, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(); }
		}

		public async Task<HospitalNoShowPage?> PageAsync(BusinessContext ctx, string? band, string? q, int page, int size, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return null;
			var fromDay = todayLocal.Date; var toDay = fromDay.AddDays(7);
			var yearAgo = fromDay.AddYears(-1);
			// history: every appointment of the last 12 months that ended Done or NoShow, plus the coming week's
			var hist = await _db.HospitalAppointments.AsNoTracking()
				.Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate >= yearAgo.AddYears(-1) && a.AppointmentDate < toDay)
				.Select(a => new { a.ID, a.PatientId, a.AppointmentDate, a.Status, a.CreatedAt }).ToListAsync(ct);
			var births = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToDictionaryAsync(p => p.ID, p => p.BirthDate, ct);
			HospitalNoShowInputs Inputs(int patientId, DateTime date, DateTime created, string? conf)
			{
				var before = hist.Where(h => h.PatientId == patientId && h.AppointmentDate < date && h.AppointmentDate >= date.AddYears(-1)).ToList();
				return new HospitalNoShowInputs(before.Count(h => h.Status == HospitalAppointmentStatus.NoShow), before.Count(h => h.Status == HospitalAppointmentStatus.Done),
					Math.Max(0, (int)(date.Date - created.ToLocalTime().Date).TotalDays), HospitalPatientService.AgeOn(births.GetValueOrDefault(patientId), date), conf);
			}

			// validation on the past year's outcomes
			var past = hist.Where(h => h.AppointmentDate >= yearAgo && h.AppointmentDate < fromDay && (h.Status == HospitalAppointmentStatus.Done || h.Status == HospitalAppointmentStatus.NoShow)).ToList();
			var pastConf = await ConfirmationsAsync(ctx.CompanyId, past.Select(p => p.ID).ToList(), ct);
			var cases = past.Select(p => new HospitalRiskCase(HospitalNoShowRules.Score(Inputs(p.PatientId, p.AppointmentDate, p.CreatedAt, pastConf.GetValueOrDefault(p.ID))), p.Status == HospitalAppointmentStatus.NoShow)).ToList();
			int events = cases.Count(c => c.Outcome);
			var validation = new HospitalNoShowValidation(cases.Count, events, HospitalReadmissionRules.Auc(cases), HospitalNoShowRules.Calibration(cases), events >= HospitalNoShowRules.ReliableEvents);

			// the coming week
			var query = from a in _db.HospitalAppointments.AsNoTracking()
						join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
						where a.CompanyId == ctx.CompanyId && a.Status == HospitalAppointmentStatus.Booked && a.AppointmentDate >= fromDay && a.AppointmentDate < toDay
						select new { a, p.Name, p.NameEn, p.PhotoPath, p.Mrn, p.Phone };
			if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.Name.Contains(t) || (x.NameEn ?? "").Contains(t) || x.Mrn.Contains(t) || (x.Phone ?? "").Contains(t)); }
			var list = await query.ToListAsync(ct);
			var conf = await ConfirmationsAsync(ctx.CompanyId, list.Select(x => x.a.ID).ToList(), ct);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId).ToDictionaryAsync(d => d.ID, ct);
			var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, docs.Values.Select(d => d.EmployeeId), ct);
			var scored = list.Select(x =>
			{
				var c = conf.GetValueOrDefault(x.a.ID);
				var inp = Inputs(x.a.PatientId, x.a.AppointmentDate, x.a.CreatedAt, c);
				int s = HospitalNoShowRules.Score(inp);
				HospitalFace? doc = docs.TryGetValue(x.a.PractitionerId, out var d) ? (d.EmployeeId is int e && faces.TryGetValue(e, out var f) ? f : new HospitalFace(d.Name, d.NameEn, null)) : null;
				return new HospitalNoShowRow(x.a, x.Name, x.NameEn, x.PhotoPath, x.Mrn, x.Phone, doc, s, HospitalNoShowRules.Band(s), HospitalNoShowRules.Reasons(inp), c);
			}).ToList();
			int high = scored.Count(r => r.Band == "High");
			if (HospitalNoShowRules.Bands.Contains(band ?? "")) scored = scored.Where(r => r.Band == band).ToList();
			int total = scored.Count;
			var rows = scored.OrderByDescending(r => r.Score).ThenBy(r => r.Appointment.AppointmentDate).ThenBy(r => r.Appointment.StartMinute).Skip((Math.Max(1, page) - 1) * size).Take(size).ToList();
			return new HospitalNoShowPage(rows, total, high, validation);
		}
	}
}
