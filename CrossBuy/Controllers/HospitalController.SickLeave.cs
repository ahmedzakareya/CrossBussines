using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H56: sick leave certificates, on the patient's file. Issued by a doctor (`consult`), voided with a reason
	// by a doctor or the administrator; printed only through Report Studio (Hospital.SickLeave).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSickLeaveService? _sickSvc;
		private HospitalSickLeaveService SickSvc => _sickSvc ??= ActivatorUtilities.CreateInstance<HospitalSickLeaveService>(HttpContext.RequestServices);

		public sealed class HospitalVoidInput { public string? Reason { get; set; } }

		[HttpPost("Patient/{id:int}/SickLeaves"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> IssueSickLeave(int id, [FromBody] HospitalSickLeaveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SickSvc.IssueAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		public sealed class HospitalMohAuthInput { public string? Reference { get; set; } }

		/// H208: the Ministry of Health's authentication of a certificate, by its reference.
		[HttpPost("SickLeaves/{id:int}/Moh"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SickLeaveMoh(int id, [FromBody] HospitalMohAuthInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SickSvc.MohAuthAsync(ctx, id, input?.Reference, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("SickLeaves/{id:int}/Void"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> VoidSickLeave(int id, [FromBody] HospitalVoidInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SickSvc.VoidAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
