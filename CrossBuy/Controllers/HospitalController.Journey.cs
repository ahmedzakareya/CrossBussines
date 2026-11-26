using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H272 — the patient's journey: after a step is done, the screen asks what comes next for this patient and
	// offers to go there (when the user may do it) or says which department does it.
	// ==========================================================================================
	public partial class HospitalController
	{
		/// The steps the patient still has, in order; `done` names the step the screen just finished, for the heading.
		[HospitalReceptionAllowed]   // the desk asks it after a check-in or a payment
		[HttpGet("Journey/{patientId:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Journey(int patientId, string? done, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == patientId && x.CompanyId == ctx.CompanyId)
				.Select(x => new { x.ID, x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstOrDefaultAsync(ct);
			if (p == null) return NotFound(new { ok = false, code = "not_found" });
			var steps = await HospitalJourney.StepsAsync(_db, ctx, _billing, patientId, ClinicToday(), DateTime.UtcNow, Url.Content("~/Hospital"), ct);
			var rows = new List<object>();
			foreach (var s in steps)
				rows.Add(new
				{
					key = s.Key, now = s.Now, url = s.Url,
					title = _t["Journey step: " + s.Key].Value, where = _t["Journey place: " + s.Where].Value,
					allowed = await _access.CanAsync(ctx, s.Action, null, ct),
				});
			return Json(new
			{
				ok = true,
				patient = new { id = p.ID, name = DisplayName.Of(p.Name, p.NameEn), mrn = p.Mrn, photo = p.PhotoPath },
				done = string.IsNullOrEmpty(done) ? null : _t["Journey step: " + done].Value,
				steps = rows,
				text = new
				{
					title = _t["The patient's way"].Value, next = _t["Next step"].Value, go = _t["Go now"].Value, stay = _t["Stay here"].Value,
					stepDone = _t["Step done"].Value, complete = _t["The patient's journey is complete: nothing is waiting."].Value,
					notYours = _t["This step is done at: {0}. It is not in your permissions."].Value, then = _t["After that"].Value,
				},
			});
		}
	}
}
