using CrossBuy.BL;
using CrossBuy.BL.Comm;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H70: SMS to patients. The outbox (searched and paged on the server), a message typed to a patient or a
	// number, the words of each kind, the patients who asked for no messages, and "send the queue" through the connected
	// provider (ISmsSender; until one is registered the queue is kept and the screen says so). Opening the screen writes
	// tomorrow's reminders (once each).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSmsService? _smsSvc;
		private HospitalSmsService SmsSvc => _smsSvc ??= ActivatorUtilities.CreateInstance<HospitalSmsService>(HttpContext.RequestServices);
		private ISmsSender SmsSender => HttpContext.RequestServices.GetService<ISmsSender>() ?? new NoSmsSender();

		public sealed class HospitalSmsOptOutInput { public int PatientId { get; set; } public string? Reason { get; set; } }

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Sms")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Sms(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			await SmsSvc.SweepRemindersAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Figures = await SmsSvc.FiguresAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Templates = await SmsSvc.TemplatesAsync(ctx, ct);
			ViewBag.OptOuts = await SmsSvc.OptOutsAsync(ctx, ct);
			ViewBag.Provider = SmsSender.Configured ? SmsSender.Name : null;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			return View();
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Sms/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SmsData(string? q, string? kind, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total) = await SmsSvc.ListAsync(ctx, q, string.IsNullOrEmpty(kind) ? null : kind, string.IsNullOrEmpty(state) ? null : state, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			return PartialView("_SmsRows", rows);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Sms"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> ComposeSms([FromBody] HospitalSmsComposeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SmsSvc.ComposeAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Sms/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> CancelSms(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SmsSvc.CancelAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Sms/Send"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SendSmsQueue(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await SmsSvc.DispatchAsync(ctx, SmsSender, DateTime.UtcNow, ct);
			// the number sent rides in the id (HospitalResult carries one int)
			return r.Ok ? Json(new { ok = true, sent = r.Id }) : Result(r);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Sms/Templates"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveSmsTemplate([FromBody] HospitalSmsTemplateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SmsSvc.SaveTemplateAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Sms/OptOut"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> SmsOptOut([FromBody] HospitalSmsOptOutInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SmsSvc.OptOutAsync(ctx, input.PatientId, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Sms/OptIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> SmsOptIn([FromBody] HospitalSmsOptOutInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SmsSvc.OptInAsync(ctx, input.PatientId, ct));
		}
	}
}
