using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H50: the hospital dashboard (the prototype's #/dashboard), tied to Report Studio:
	//   · every TILE is a KPI of the platform's catalogue (HospitalKpis) over an H49 management dataset, evaluated by
	//     IKpiEvaluator — the dataset's permission and the KPI's both apply, so a financial tile is "—" for a reader
	//     without the billing desk's right;
	//   · every LIST is the rows of the same dataset (HospitalRegisterSource.ReadAsync), never a second query;
	//   · every card opens its report in /Reports/Viewer, or the live screen it summarises.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed record HospitalDashboardModel(
			IReadOnlyDictionary<string, decimal?> Kpi, DateTime Today, DateTime MonthStart,
			IReadOnlyList<Dictionary<string, object?>> Wards, IReadOnlyList<Dictionary<string, object?>> Er,
			IReadOnlyList<Dictionary<string, object?>> Doctors, IReadOnlyList<Dictionary<string, object?>> Theatre,
			IReadOnlyDictionary<int, string> DoctorPhotos, IReadOnlyDictionary<int, (string? Photo, string NameEn)> ErPatients);

		/// The ER queue is everyone still in the ER, whenever they arrived; a visit open longer than this is a record to close, not a queue.
		private const int ErLookbackDays = 30;

		[HttpGet("Dashboard")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Dashboard(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var today = DateTime.Today; var month = new DateTime(today.Year, today.Month, 1);
			bool ar = DisplayName.IsArabic;

			// ---- the tiles: the KPI catalogue over the reports ----
			var kpi = HttpContext.RequestServices.GetService<IKpiEvaluator>();
			string D(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
			async Task<decimal?> K(string key, DateTime? from, DateTime? to)
			{
				if (kpi == null) return null;
				var p = new Dictionary<string, string?>();
				if (from is { } f) { p["From"] = D(f); p["To"] = D(to ?? f); }
				try { var r = await kpi.EvaluateAsync(key, p, ct); return r.Status == "READY" ? r.Value : null; }
				catch (InvalidOperationException) { return null; }   // a KPI the registry does not hold (an older catalogue)
			}
			var values = new Dictionary<string, decimal?>
			{
				[HospitalKpis.Appointments] = await K(HospitalKpis.Appointments, today, today),
				[HospitalKpis.Seen] = await K(HospitalKpis.Seen, today, today),
				[HospitalKpis.ErWaiting] = await K(HospitalKpis.ErWaiting, today.AddDays(-ErLookbackDays), today),
				[HospitalKpis.ErDoorToDoctor] = await K(HospitalKpis.ErDoorToDoctor, today, today),
				[HospitalKpis.BedsTotal] = await K(HospitalKpis.BedsTotal, today, today),
				[HospitalKpis.BedsOccupied] = await K(HospitalKpis.BedsOccupied, today, today),
				[HospitalKpis.Revenue] = await K(HospitalKpis.Revenue, today, today),
				[HospitalKpis.Collected] = await K(HospitalKpis.Collected, today, today),
				[HospitalKpis.Claimed] = await K(HospitalKpis.Claimed, month, today),
				[HospitalKpis.ClaimsRejected] = await K(HospitalKpis.ClaimsRejected, month, today),
				[HospitalKpis.CriticalOpen] = await K(HospitalKpis.CriticalOpen, today.AddDays(-7), today),
				[HospitalKpis.PreauthPending] = await K(HospitalKpis.PreauthPending, null, null),
			};

			// ---- the lists: the same reports' rows ----
			var todayWindow = HospitalReportWindow.Days(today, today);
			var wards = await new HospitalBedOccupancySource(_db).ReadAsync(ctx, todayWindow, ar, ct);
			var er = (await new HospitalErWaitingSource(_db).ReadAsync(ctx, HospitalReportWindow.Days(today.AddDays(-ErLookbackDays), today), ar, ct))
				.Where(r => r["OpenNow"] is 1).OrderBy(r => r["Esi"] as int? ?? 9).ThenBy(r => r["ArrivedAt"] as DateTime?).Take(5).ToList();
			var doctors = (await new HospitalDoctorProductivitySource(_db).ReadAsync(ctx, todayWindow, ar, ct))
				.Where(r => r["Appointments"] is int n && n > 0).OrderByDescending(r => (int)r["Appointments"]!).Take(6).ToList();
			var theatre = (await new HospitalTheatreSource(_db).ReadAsync(ctx, todayWindow, ar, ct)).Take(4).ToList();

			// the people, by their photos (the owner's rule): doctors through their employee photo, patients through their file
			var pracIds = doctors.Select(r => (int)r["PractitionerId"]!).ToList();
			var emp = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pracIds.Contains(p.ID) && p.EmployeeId != null)
				.ToDictionaryAsync(p => p.ID, p => p.EmployeeId!.Value, ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, emp.Values, ct);
			var doctorPhotos = emp.Where(e => photos.ContainsKey(e.Value)).ToDictionary(e => e.Key, e => photos[e.Value]);
			var pids = er.Where(r => r["PatientId"] is int).Select(r => (int)r["PatientId"]!).ToList();
			var erPatients = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID))
				.ToDictionaryAsync(p => p.ID, p => (p.PhotoPath, p.NameEn ?? p.Name), ct);

			return View(new HospitalDashboardModel(values, today, month, wards, er, doctors, theatre, doctorPhotos,
				erPatients.ToDictionary(x => x.Key, x => ((string?)x.Value.PhotoPath, x.Value.Item2))));
		}
	}
}
