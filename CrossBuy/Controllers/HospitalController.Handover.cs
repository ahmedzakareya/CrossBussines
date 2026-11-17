using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H165: the desk's shift handover — the window (what I hold, the cash, to whom, a note), handing over, and
	// the receiver's acknowledgement. Every rule is HospitalDeskHandoverService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDeskHandoverInput { public int? To { get; set; } public string? Note { get; set; } }

		private HospitalDeskHandoverService? _handover;
		private HospitalDeskHandoverService HandoverSvc => _handover ??= ActivatorUtilities.CreateInstance<HospitalDeskHandoverService>(HttpContext.RequestServices);

		/// The start of the desk's day, in UTC.
		private static DateTime DayStartUtc(DateTime nowUtc) { var (today, _, local) = DeskClock(nowUtc); return today - (local - nowUtc); }

		[HospitalReceptionAllowed]   // H165
		[HttpGet("FrontDesk/Handover")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskHandover(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound();
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			ViewBag.Holdings = await HandoverSvc.HoldingsAsync(ctx, me, DayStartUtc(now), today, now, ct);
			ViewBag.People = (await GapSvc.PeopleAsync(ctx, ct)).Where(p => p.Id != me).ToList();
			ViewBag.Recent = (await HandoverSvc.ForMeAsync(ctx, me, ct)).Recent;
			ViewBag.Me = me;
			await MoneyFormatAsync(ctx, ct);
			return PartialView("_DeskHandover");
		}

		[HospitalReceptionAllowed]   // H165
		[HttpPost("FrontDesk/Handover"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		[HospitalIdempotent]   // H166: sent twice, done once
		public async Task<IActionResult> FrontDeskHandOver([FromBody] HospitalDeskHandoverInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			return Result(await HandoverSvc.HandOverAsync(ctx, me, input.To, input.Note, DayStartUtc(now), today, now, ct));
		}

		[HospitalReceptionAllowed]   // H165
		[HttpPost("FrontDesk/Handover/{id:int}/Acknowledge"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskAcknowledgeHandover(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await HandoverSvc.AcknowledgeAsync(ctx, id, me, DateTime.UtcNow, ct));
		}
	}
}
