using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H77: inpatient consultation requests. The consultations screen (asked of my specialties, asked by me,
	// all — searched, filtered and paged), and the request, accept, answer and cancel actions; the nursing station opens
	// the request window and shows each patient's open requests.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalConsultRequestService? _consultReqSvc;
		private HospitalConsultRequestService ConsultReqSvc => _consultReqSvc ??= ActivatorUtilities.CreateInstance<HospitalConsultRequestService>(HttpContext.RequestServices);

		public sealed class HospitalConsultCancelInput { public string? Reason { get; set; } }

		[HttpGet("Consults")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Consults(string? scope, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await ConsultReqSvc.ListAsync(ctx, "all", null, null, null, 1, 5, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.Scope = scope is "asked" or "all" ? scope : "mine";
			return View();
		}

		[HttpGet("Consults/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConsultsData(string? scope, string? status, string? q, int? admissionId, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _) = await ConsultReqSvc.ListAsync(ctx, scope, status, q, admissionId, page, pageSize, DateTime.UtcNow, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			return PartialView("_ConsultRows", rows);
		}

		[HttpGet("Consults/Specialties")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConsultSpecialties(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var list = await ConsultReqSvc.SpecialtiesAsync(ctx, ct);
			return Json(list.Select(d => new { id = d.Id, name = DisplayName.Of(d.Name, d.NameEn), doctors = d.Doctors.Select(p => new { id = p.Id, name = DisplayName.Of(p.Name, p.NameEn) }) }));
		}

		[HttpPost("Consults"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> RequestConsult([FromBody] HospitalConsultRequestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsultReqSvc.RequestAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Consults/{id:int}/Accept"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AcceptConsult(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsultReqSvc.AcceptAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Consults/{id:int}/Answer"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AnswerConsult(int id, [FromBody] HospitalConsultAnswerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsultReqSvc.AnswerAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Consults/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CancelConsult(int id, [FromBody] HospitalConsultCancelInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsultReqSvc.CancelAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
