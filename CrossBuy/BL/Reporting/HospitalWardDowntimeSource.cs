using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H211 — the DOWNTIME ward summary: what a ward needs on paper when the system is down. One row per active
	// medicine of every patient in a bed of the ward (a patient with none still gets one row): the bed, the patient with
	// allergies, blood group and isolation, the admission's diagnosis and attending doctor; the medicine, its dose, route
	// and schedule, when it was last given and when the next dose is due. The printed copy carries empty boxes for the
	// doses given during the outage — charted on paper, entered once the system is back.
	// Another company's ward is an empty set.
	// ============================================================================================
	public sealed class HospitalWardDowntimeSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalWardDowntimeSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.WardDowntime;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var wardId = query.Parameters.GetInt("WardId");
			if (wardId is not > 0) return builder.Build(totalRowCount: 0);
			var ward = await _db.HospitalWards.AsNoTracking().Where(w => w.ID == wardId.Value && w.CompanyId == ctx.CompanyId).Select(w => new { w.Code, w.Name, w.NameEn }).FirstOrDefaultAsync(ct);
			if (ward == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var nowUtc = DateTime.UtcNow;

			var here = await (from s in _db.HospitalBedStays.AsNoTracking()
							  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							  join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && r.WardId == wardId.Value && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
							  orderby b.Code
							  select new { Bed = b.Code, a, p }).ToListAsync(ct);
			var admIds = here.Select(h => h.a.ID).ToList();
			var patIds = here.Select(h => h.p.ID).ToList();
			var orders = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && admIds.Contains(o.AdmissionId) && o.Status == HospitalMedOrderStatus.Active)
				.OrderBy(o => o.CreatedAt).ToListAsync(ct);
			var orderIds = orders.Select(o => o.ID).ToList();
			var lastGiven = (await _db.Set<HospitalMarEntry>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && orderIds.Contains(m.OrderId) && m.Status == HospitalMarStatus.Given)
				.GroupBy(m => m.OrderId).Select(g => new { g.Key, At = g.Max(m => m.RecordedAt) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.At);
			var iso = (await _db.Set<HospitalIsolation>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.EndedAt == null && patIds.Contains(x.PatientId))
				.Select(x => new { x.PatientId, x.Precaution, x.Organism }).ToListAsync(ct))
				.GroupBy(x => x.PatientId).ToDictionary(g => g.Key, g => string.Join(" · ", g.Select(x => Precaution(x.Precaution, ar) + (string.IsNullOrWhiteSpace(x.Organism) ? "" : " (" + x.Organism + ")"))));
			var attIds = here.Where(h => h.a.AttendingEmployeeId != null).Select(h => h.a.AttendingEmployeeId!.Value).Distinct().ToList();
			var att = await _db.Employee.AsNoTracking().Where(e => attIds.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => ar ? e.FullName ?? e.FullNameEn : e.FullNameEn ?? e.FullName, ct);
			var org = await TradeDocumentOrg.LoadAsync(_db, ctx, ar, ct);
			var wardNow = HospitalNursingService.UtcToWard(nowUtc);

			int n = 0;
			foreach (var h in here)
			{
				var mine = orders.Where(o => o.AdmissionId == h.a.ID).ToList();
				var rowsFor = mine.Count == 0 ? new List<HospitalMedOrder?> { null } : mine.Cast<HospitalMedOrder?>().ToList();
				foreach (var o in rowsFor)
				{
					var row = new Dictionary<string, object?>(StringComparer.Ordinal)
					{
						["LineNo"] = ++n,
						["CompanyName"] = org.CompanyName,
						["CompanyAddress"] = org.CompanyAddress,
						["CompanyPhone"] = org.CompanyPhone,
						["DocumentNo"] = ward.Code,
						["DocumentDate"] = nowUtc.ToLocalTime(),
						["Ward"] = HospitalDocumentRows.P(ar, ward.Name, ward.NameEn),
						["PatientKey"] = h.Bed + " · " + h.p.Mrn,
						["Bed"] = h.Bed,
						["PatientName"] = HospitalDocumentRows.P(ar, h.p.Name, h.p.NameEn),
						["PatientMrn"] = h.p.Mrn,
						["PatientAge"] = HospitalDocumentRows.Age(h.p.BirthDate, ar),
						["PatientSex"] = HospitalDocumentRows.Sex(h.p.Sex, ar),
						["BloodGroup"] = h.p.BloodGroup,
						["Allergies"] = string.IsNullOrWhiteSpace(h.p.Allergies) ? (ar ? "لا توجد حساسية مسجلة" : "No allergy recorded") : (ar ? "حساسية: " : "Allergy: ") + h.p.Allergies,
						["Isolation"] = iso.TryGetValue(h.p.ID, out var i) ? (ar ? "عزل: " : "Isolation: ") + i : null,
						["Diagnosis"] = h.a.Diagnosis,
						["DoctorName"] = h.a.AttendingEmployeeId is int ae && att.TryGetValue(ae, out var dn) ? dn : null,
						["Admitted"] = h.a.AdmittedAt.ToLocalTime(),
					};
					if (o != null)
					{
						var hours = HospitalNursingRules.ParseHours(o.Hours) ?? Array.Empty<int>();
						DateTime? next = null;
						if (!o.AsNeeded && hours.Count > 0)
							next = HospitalNursingRules.DosesOn(wardNow.Date, hours, o.StartAt, o.StoppedAt, HospitalNursingService.WardToUtc)
								.Concat(HospitalNursingRules.DosesOn(wardNow.Date.AddDays(1), hours, o.StartAt, o.StoppedAt, HospitalNursingService.WardToUtc))
								.Where(t => t > nowUtc).Select(t => (DateTime?)t).FirstOrDefault();
						row["Drug"] = o.Drug + (o.Controlled ? (ar ? " (مراقب)" : " (controlled)") : "");
						row["Dose"] = o.Dose;
						row["Route"] = o.Route;
						row["Schedule"] = o.AsNeeded ? (ar ? "عند اللزوم" : "As needed") : string.Join(" ", hours.Select(x => x.ToString("00") + ":00"));
						row["LastGiven"] = lastGiven.TryGetValue(o.ID, out var lg) ? lg.ToLocalTime() : null;
						row["NextDue"] = next?.ToLocalTime();
					}
					else row["Drug"] = ar ? "لا أدوية فعالة" : "No active medicine";
					builder.AddRow(row);
				}
			}
			return builder.Build(totalRowCount: n);
		}

		private static string Precaution(string p, bool ar) => p switch
		{
			HospitalPrecaution.Contact => ar ? "تلامسي" : "Contact",
			HospitalPrecaution.Droplet => ar ? "رذاذي" : "Droplet",
			HospitalPrecaution.Airborne => ar ? "هوائي" : "Airborne",
			HospitalPrecaution.Protective => ar ? "وقائي" : "Protective",
			_ => p,
		};
	}
}
