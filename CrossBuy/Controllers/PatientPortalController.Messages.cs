using CrossBuy.BL.Hospital;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H249 — the patient's side of secure messaging: their conversations with the clinic about any of the
	// account's people (self or family it holds).
	public partial class PatientPortalController
	{
		private HospitalPortalMessageService? _msgs;
		private HospitalPortalMessageService Msgs => _msgs ??= new HospitalPortalMessageService(_db, _access);

		[HttpGet("Messages")]
		public async Task<IActionResult> Messages(int? id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "messages", ct);
			var (ready, threads, unread) = await Msgs.MyThreadsAsync(me.Ctx.CompanyId, me.Account.ID, ct);
			ViewBag.Ready = ready; ViewBag.Unread = unread;
			ViewBag.Open = id is int t ? await Msgs.MyThreadAsync(me.Ctx.CompanyId, me.Account.ID, t, DateTime.UtcNow, ct) : null;
			return View(threads);
		}

		[HttpPost("Messages"), ValidateAntiForgeryToken]
		public async Task<IActionResult> MessageStart([FromBody] HospitalPortalNewThreadInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			if (input == null) return NotFound(new { ok = false, code = "not_found" });
			var people = (await Booking.ClinicalPeopleAsync(me.Ctx, me.Account.ID, ct)).Select(p => p.PatientId).ToList();
			return Json(await Msgs.StartAsync(me.Ctx.CompanyId, me.Account.ID, people, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Messages/{id:int}"), ValidateAntiForgeryToken]
		public async Task<IActionResult> MessageReply(int id, [FromBody] HospitalPortalReplyInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			return Json(await Msgs.PatientReplyAsync(me.Ctx.CompanyId, me.Account.ID, id, input ?? new(), DateTime.UtcNow, ct));
		}
	}
}
