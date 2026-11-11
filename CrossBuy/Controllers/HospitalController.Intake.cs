using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H91: the reception self-intake. The tablet (full screen, a kiosk account holding `book`) and its
	// submission; the desk's list (searched, filtered, paged; unacknowledged danger signs first) and one intake with
	// its candidate files; acknowledging an alert, linking, opening a new file, rejecting.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSelfIntakeService? _intakeSvc;
		private HospitalSelfIntakeService IntakeSvc => _intakeSvc ??= ActivatorUtilities.CreateInstance<HospitalSelfIntakeService>(HttpContext.RequestServices);

		[HttpGet("Intake/Kiosk")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> IntakeKiosk(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			return View();
		}

		/// The tablet's submission: answers with the ticket and whether the patient must go to the desk now — never
		/// with anything about an existing file.
		[HttpPost("Intake/Submit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> SubmitIntake([FromBody] HospitalIntakeSubmitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await IntakeSvc.SubmitAsync(ctx, input, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, ticket = r.Args![0], urgent = r.Args[1] });
		}

		[HttpGet("Intake")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Intake(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready, urgent) = await IntakeSvc.ListAsync(ctx, null, null, 1, 1, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.Urgent = urgent;
			return View();
		}

		[HttpGet("Intake/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> IntakeData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _, urgent) = await IntakeSvc.ListAsync(ctx, q, filter, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			Response.Headers["X-Urgent"] = urgent.ToString();
			return PartialView("_IntakeRows", rows);
		}

		[HttpGet("Intake/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> IntakeReview(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await IntakeSvc.PageAsync(ctx, id, DateTime.UtcNow, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanDesk = await IntakeSvc.CanDeskAsync(ctx, ct);
			return View(page);
		}

		[HttpPost("Intake/{id:int}/{step}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> DecideIntake(int id, string step, [FromBody] HospitalIntakeDecisionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			return Result(step switch
			{
				"Ack" => await IntakeSvc.AcknowledgeAsync(ctx, id, ctx.EmployeeId, now, ct),
				"Link" => await IntakeSvc.LinkAsync(ctx, id, input, ctx.EmployeeId, now, ct),
				"NewFile" => await IntakeSvc.NewFileAsync(ctx, id, input, ctx.EmployeeId, now, ct),
				"Reject" => await IntakeSvc.RejectAsync(ctx, id, input, ctx.EmployeeId, now, ct),
				_ => HospitalResult.Fail("not_found"),
			});
		}
	}
}
