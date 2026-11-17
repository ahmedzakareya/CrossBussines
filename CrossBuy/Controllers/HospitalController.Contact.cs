using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H163: documents and communication at the desk — the window (contact and help, representatives, scans,
	// consents, messages), and its acts. Every rule is HospitalPatientContactService's (the opt-out is the SMS
	// service's; the scans' bytes are the document platform's).
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDeskMessageInput { public int? AppointmentId { get; set; } public string? Body { get; set; } }
		public sealed class HospitalDeskReasonInput { public string? Reason { get; set; } }

		private HospitalPatientContactService? _contact;
		private HospitalPatientContactService ContactSvc => _contact ??= ActivatorUtilities.CreateInstance<HospitalPatientContactService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H163
		[HttpGet("FrontDesk/Patient/{id:int}/Contact")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskContact(int id, int? appointment, string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var v = await ContactSvc.ViewAsync(ctx, id, ct);
			if (v == null) return NotFound();
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			ViewBag.Appointment = appointment; ViewBag.Today = today; ViewBag.Tab = tab;
			return PartialView("_DeskContact", v);
		}

		[HospitalReceptionAllowed]   // H163
		[HttpPost("FrontDesk/Patient/{id:int}/Contact"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskSaveContact(int id, [FromBody] HospitalContactInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContactSvc.SavePreferenceAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H163
		[HttpPost("FrontDesk/Patient/{id:int}/Representatives"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskAddRepresentative(int id, [FromBody] HospitalRepresentativeInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			return Result(await ContactSvc.AddRepresentativeAsync(ctx, id, input, ctx.EmployeeId, today, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H163
		[HttpPost("FrontDesk/Representatives/{id:int}/Stop"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskStopRepresentative(int id, [FromBody] HospitalDeskReasonInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContactSvc.StopRepresentativeAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H163: a scan goes to the document platform (the File Manager rule)
		[HttpPost("FrontDesk/Patient/{id:int}/Scans"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		[RequestSizeLimit(11_000_000)]
		public async Task<IActionResult> FrontDeskUploadScan(int id, string? kind, IFormFile? file, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return NotFound(new { ok = false, code = "not_found" });
			if (file == null) return Result(HospitalResult.Fail("The scan must be an image or a PDF of up to 10 MB"));
			// ask before anything reaches the store: a refused caller never leaves an orphan
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return Result(HospitalResult.Fail("forbidden"));
			await using var bytes = file.OpenReadStream();
			return Result(await ContactSvc.UploadScanAsync(ctx, id, kind, bytes, file.Length, ct));
		}

		[HospitalReceptionAllowed]   // H163
		[HttpGet("FrontDesk/Patient/{id:int}/Scans/{doc:long}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskScan(int id, long doc, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return NotFound();
			var f = await ContactSvc.OpenScanAsync(ctx, id, doc, ct);
			if (f == null) return NotFound();
			Response.Headers["Cache-Control"] = "private, no-store";
			return File(f.Content, f.ContentType);
		}

		[HospitalReceptionAllowed]   // H163
		[HttpPost("FrontDesk/Patient/{id:int}/Message"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskMessage(int id, [FromBody] HospitalDeskMessageInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContactSvc.SendMessageAsync(ctx, id, input.AppointmentId, input.Body, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H163: "no messages at all" is the SMS opt-out
		[HttpPost("FrontDesk/Patient/{id:int}/OptOut"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskOptOut(int id, [FromBody] HospitalDeskReasonInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ActivatorUtilities.CreateInstance<HospitalSmsService>(HttpContext.RequestServices).OptOutAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H163
		[HttpPost("FrontDesk/Patient/{id:int}/OptIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskOptIn(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ActivatorUtilities.CreateInstance<HospitalSmsService>(HttpContext.RequestServices).OptInAsync(ctx, id, ct));
		}
	}
}
