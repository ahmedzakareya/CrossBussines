using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HOSPITAL H211 — downtime continuity: every ward with the patients in its beds and the active medicines, and its
	// paper summary (Report Studio, Hospital.WardDowntime) to print or save before a planned outage, or each shift.
	public partial class HospitalController
	{
		public sealed record HospitalDowntimeWard(int Id, string Code, string Name, string? NameEn, int Patients, int Medicines, int Isolated);

		[HttpGet("Wards/Downtime")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Downtime(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			int c = ctx.CompanyId;
			var wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == c && w.IsActive).OrderBy(w => w.Code).Select(w => new { w.ID, w.Code, w.Name, w.NameEn }).ToListAsync(ct);
			if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); wards = wards.Where(w => w.Code.Contains(s, StringComparison.OrdinalIgnoreCase) || w.Name.Contains(s) || (w.NameEn ?? "").Contains(s, StringComparison.OrdinalIgnoreCase)).ToList(); }
			var stays = await (from st in _db.HospitalBedStays.AsNoTracking()
							   join r in _db.HospitalRooms.AsNoTracking() on st.RoomId equals r.ID
							   join a in _db.HospitalAdmissions.AsNoTracking() on st.AdmissionId equals a.ID
							   where st.CompanyId == c && st.EndedAt == null && a.Status == HospitalAdmissionStatus.Active
							   select new { r.WardId, st.AdmissionId, st.PatientId }).ToListAsync(ct);
			var adm = stays.Select(x => x.AdmissionId).ToList();
			var meds = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == c && adm.Contains(o.AdmissionId) && o.Status == HospitalMedOrderStatus.Active)
				.GroupBy(o => o.AdmissionId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			var pats = stays.Select(x => x.PatientId).ToList();
			var iso = (await _db.Set<HospitalIsolation>().AsNoTracking().Where(x => x.CompanyId == c && x.EndedAt == null && pats.Contains(x.PatientId)).Select(x => x.PatientId).ToListAsync(ct)).ToHashSet();
			var rows = wards.Select(w =>
			{
				var here = stays.Where(x => x.WardId == w.ID).ToList();
				return new HospitalDowntimeWard(w.ID, w.Code, w.Name, w.NameEn, here.Count, here.Sum(x => meds.GetValueOrDefault(x.AdmissionId)), here.Count(x => iso.Contains(x.PatientId)));
			}).ToList();
			const int size = 20;
			int pages = Math.Max(1, (rows.Count + size - 1) / size); page = Math.Clamp(page, 1, pages);
			ViewBag.Q = q; ViewBag.Page = page; ViewBag.Pages = pages; ViewBag.Total = rows.Count;
			ViewBag.Patients = rows.Sum(r => r.Patients); ViewBag.Medicines = rows.Sum(r => r.Medicines);
			return View(rows.Skip((page - 1) * size).Take(size).ToList());
		}
	}
}
