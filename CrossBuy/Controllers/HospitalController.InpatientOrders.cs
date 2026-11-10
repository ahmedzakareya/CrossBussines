using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H75: lab tests and imaging for inpatients, from the nursing station. The catalogue to choose from, the
	// stay's orders with where each stands, an order placed (charged to the stay's bill), and one cancelled with why.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalInpatientOrderService? _ipOrderSvc;
		private HospitalInpatientOrderService InpatientOrderSvc => _ipOrderSvc ??= ActivatorUtilities.CreateInstance<HospitalInpatientOrderService>(HttpContext.RequestServices);

		public sealed class HospitalInpatientCancelInput { public string? Reason { get; set; } }

		[HttpGet("Admissions/{id:int}/Diagnostics")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AdmissionDiagnostics(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ar");
			var catalog = await InpatientOrderSvc.CatalogAsync(ctx, ct);
			var orders = await InpatientOrderSvc.ForAdmissionAsync(ctx, id, ct);
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			return Json(new
			{
				catalog = catalog.Select(c => new { id = c.ServiceId, kind = c.Kind, code = c.Code, name = ar ? c.Name : c.NameEn, contrast = c.Contrast }),
				orders = orders.Select(o => new
				{
					id = o.Order.ID, kind = o.Order.Kind, accession = o.Order.AccessionNo, code = o.Order.Code, name = ar ? o.Order.Name : o.Order.NameEn,
					priority = _t["prio:" + o.Order.Priority].Value, priorityCode = o.Order.Priority,
					status = o.Order.Status == Models.Context.Hospital.HospitalInpatientOrderStatus.Cancelled ? _t["Cancelled"].Value : o.Stage == null ? "—" : _t[(o.Order.Kind == Models.Context.Hospital.HospitalInpatientOrderKind.Lab ? "lab:" : "rad:") + o.Stage].Value,
					cancelled = o.Order.Status == Models.Context.Hospital.HospitalInpatientOrderStatus.Cancelled, reason = o.Order.CancelReason,
					cancellable = o.Cancellable, contrastAlert = o.ContrastAlert,
					at = HospitalNursingService.UtcToWard(o.Order.OrderedAt).ToString("dd/MM HH:mm", inv),
				}),
			});
		}

		[HttpPost("Admissions/Diagnostics"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> OrderDiagnostics([FromBody] HospitalInpatientOrderInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, outcome) = await InpatientOrderSvc.OrderAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, placed = outcome!.Placed, contrast = outcome.ContrastWarnings });
		}

		[HttpPost("Admissions/Diagnostics/{orderId:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CancelDiagnostic(int orderId, [FromBody] HospitalInpatientCancelInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InpatientOrderSvc.CancelAsync(ctx, orderId, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
