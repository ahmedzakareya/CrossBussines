using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HOSPITAL H244 — the theatres' block time and the surgeons' preference cards.
	public partial class HospitalController
	{
		private HospitalTheatreBlockService? _blockSvc;
		private HospitalTheatreBlockService BlockSvc => _blockSvc ??= ActivatorUtilities.CreateInstance<HospitalTheatreBlockService>(HttpContext.RequestServices, TheatreSvc);

		[HttpGet("Theatre/Blocks"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> TheatreBlocks(int weeks = 4, string? q = null, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 15;
			var d = await BlockSvc.PageAsync(ctx, ClinicToday(), weeks, q, page, size, ct);
			ViewBag.Weeks = Math.Clamp(weeks, 1, 26); ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.CardTotal + size - 1) / size);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanCard = ViewBag.CanManage == true || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.Procedures = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.IsActive && s.Category == HospitalServiceCategories.Procedure)
				.OrderBy(s => s.Code).Select(s => new HospitalChoice(s.ID, s.Name, s.NameEn, s.Code)).ToListAsync(ct);
			return View(d);
		}

		[HttpPost("Theatre/Blocks"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TheatreBlockSave([FromBody] HospitalBlockInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BlockSvc.SaveBlockAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Theatre/Cards"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> TheatreCardSave([FromBody] HospitalCardInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BlockSvc.SaveCardAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
