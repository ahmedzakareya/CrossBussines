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

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
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
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Intake/Submit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> SubmitIntake([FromBody] HospitalIntakeSubmitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await IntakeSvc.SubmitAsync(ctx, input, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, ticket = r.Args![0], urgent = r.Args[1] });
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
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
			ViewBag.Counts = await WhereSvc.CountsAsync(ctx, DateTime.Now, ct);   // H114: the desk's day at a glance
			return View();
		}

		private HospitalWhereaboutsService? _whereSvc;
		private HospitalWhereaboutsService WhereSvc => _whereSvc ??= ActivatorUtilities.CreateInstance<HospitalWhereaboutsService>(HttpContext.RequestServices);

		/// H114: "where is the patient now?" — the desk's search: each match with every place they are in right now.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Intake/Where")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> IntakeWhere(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return Unauthorized();
			bool en = UiEnglish;
			var rows = await WhereSvc.SearchAsync(ctx, q, 8, DateTime.Now, ct);
			return Json(new
			{
				ok = true,
				rows = rows.Select(r => new
				{
					id = r.PatientId, mrn = r.Mrn, name = en ? (string.IsNullOrWhiteSpace(r.NameEn) ? r.Name : r.NameEn) : r.Name, photo = r.PhotoPath,
					url = Url.Action("Patient", new { id = r.PatientId }), restricted = r.Restricted,
					now = r.Now.Select(w => new { kind = w.Kind, place = en ? w.PlaceEn : w.Place, detail = en ? w.DetailEn : w.Detail, live = w.Live,
						since = w.Since?.ToString("HH:mm"), map = w.FloorId is int f ? Url.Action("Map", new { floor = f }) : null }),
				}),
			});
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
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

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
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

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
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
