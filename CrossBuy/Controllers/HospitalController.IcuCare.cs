using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H238 — the ICU patient's scores (GCS, APACHE II), continuous infusions and device bundles. The service
	// checks `admit` or `consult`; the routes ask only for `read` and let it refuse.
	public partial class HospitalController
	{
		private HospitalIcuCareService? _icuCareSvc;
		private HospitalIcuCareService IcuCareSvc => _icuCareSvc ??= ActivatorUtilities.CreateInstance<HospitalIcuCareService>(HttpContext.RequestServices);

		private async Task<IActionResult> IcuCareAsync(Func<BusinessContext, Task<HospitalResult>> run, CancellationToken ct)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await run(ctx));
		}

		[HttpPost("Icu/Gcs"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuGcs([FromBody] HospitalIcuGcsInput input, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.GcsAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Apache"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuApache([FromBody] HospitalIcuApacheInput input, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.ApacheAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Infusions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuInfusionStart([FromBody] HospitalIcuInfusionInput input, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.StartInfusionAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Infusions/{id:int}/Titrate"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuInfusionTitrate(int id, [FromBody] HospitalIcuTitrateInput input, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.TitrateAsync(ctx, id, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Infusions/{id:int}/Stop"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuInfusionStop(int id, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.StopInfusionAsync(ctx, id, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Devices"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuDeviceInsert([FromBody] HospitalIcuDeviceInput input, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.InsertDeviceAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Devices/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuDeviceRemove(int id, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.RemoveDeviceAsync(ctx, id, DateTime.UtcNow, ct), ct);

		[HttpPost("Icu/Devices/{id:int}/Bundle"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> IcuDeviceBundle(int id, [FromBody] HospitalIcuBundleInput input, CancellationToken ct = default)
			=> IcuCareAsync(ctx => IcuCareSvc.CheckBundleAsync(ctx, id, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);
	}
}
