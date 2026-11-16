using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H138: the unit-dose cart fill. Choose a ward and a 24-hour window, see the fill list patient by patient
	// with the item totals, then fill it.
	// ==========================================================================================
	public partial class HospitalController
	{
		public static class CartWindows
		{
			public const string Now = "now", Tomorrow = "tomorrow";
			public static readonly IReadOnlyList<string> All = new[] { Now, Tomorrow };
		}

		public sealed class HospitalCartFillInput { public int WardId { get; set; } public string? Window { get; set; } }

		private HospitalCartFillService? _cart;
		private HospitalCartFillService CartSvc => _cart ??= ActivatorUtilities.CreateInstance<HospitalCartFillService>(HttpContext.RequestServices,
			ActivatorUtilities.CreateInstance<HospitalMedRoutingService>(HttpContext.RequestServices), _pharmacy);

		/// The window's start on the ward clock: the next whole hour, or tomorrow at midnight.
		private static DateTime CartStart(string? window, DateTime nowUtc)
		{
			var ward = HospitalNursingService.UtcToWard(nowUtc);
			return window == CartWindows.Tomorrow ? HospitalNursingService.WardToUtc(ward.Date.AddDays(1))
				: HospitalNursingService.WardToUtc(new DateTime(ward.Year, ward.Month, ward.Day, ward.Hour, 0, 0).AddHours(1));
		}

		[HttpGet("Pharmacy/CartFill")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CartFill(int? ward, string? window, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var wards = await CartSvc.WardsAsync(ctx, ct);
			int? pick = wards.Any(w => w.Id == ward) ? ward : wards.OrderByDescending(w => w.Patients).Select(w => (int?)w.Id).FirstOrDefault();
			window = CartWindows.All.Contains(window ?? "") ? window : CartWindows.Now;
			ViewBag.Wards = wards; ViewBag.Window = window;
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			return View(pick == null ? null : await CartSvc.PreviewAsync(ctx, pick.Value, CartStart(window, DateTime.UtcNow), ct));
		}

		[HttpPost("Pharmacy/CartFill"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> CartFillSend([FromBody] HospitalCartFillInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, fill) = await CartSvc.FillAsync(ctx, input?.WardId ?? 0, CartStart(input?.Window, DateTime.UtcNow), ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, id = fill!.FillId, sent = fill.Deliveries, skipped = fill.Skipped, problems = fill.Problems });
		}
	}
}
