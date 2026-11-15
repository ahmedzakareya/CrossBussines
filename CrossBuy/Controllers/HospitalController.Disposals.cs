using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H135: expiry and disposal. Two tabs:
	//   · what to deal with: expired or near-expiry batches in the pharmacy store, and everything in quarantine;
	//   · what was done: destroyed or returned, with value, document and witness.
	// A batch is destroyed (witnessed) or returned to its supplier from the theme's modals.
	// ==========================================================================================
	public partial class HospitalController
	{
		public static class DisposalTabs
		{
			public const string Todo = "todo", Done = "done";
			public static readonly IReadOnlyList<string> All = new[] { Todo, Done };
		}

		private HospitalStockDisposalService? _disposals;
		private HospitalStockDisposalService DisposalSvc => _disposals ??= ActivatorUtilities.CreateInstance<HospitalStockDisposalService>(HttpContext.RequestServices,
			_pharmacy, (IHospitalWitness)ActivatorUtilities.CreateInstance<HospitalPasswordWitness>(HttpContext.RequestServices));

		/// The company's currency label and its decimals (the dinar has three), for the values on these screens.
		private async Task MoneyFormatAsync(BusinessContext ctx, CancellationToken ct)
		{
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.MoneyDp = await HttpContext.RequestServices.GetRequiredService<ICurrencyRounding>().DecimalsAsync(ctx.CompanyId, null);
		}

		private static DateTime WardToday() => HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;

		[HttpGet("Pharmacy/Disposals")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Disposals(string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Tab = DisposalTabs.All.Contains(tab ?? "") ? tab : DisposalTabs.Todo;
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			await MoneyFormatAsync(ctx, ct);
			if (ViewBag.CanPharmacy == true)
			{
				// the witness: anyone else with a sign-in (they confirm with their own password)
				var wit = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive && e.UserId != null && e.ID != ctx.EmployeeId)
					.OrderBy(e => e.FullName).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				var wp = await EmployeePhotos.ResolveAsync(_db, ctx, wit.Select(s => s.ID).ToList(), ct);
				ViewBag.Witnesses = wit.Select(s => (s.ID, Name: s.FullName ?? "", NameEn: s.FullNameEn ?? s.FullName ?? "", Photo: (string?)wp.GetValueOrDefault(s.ID))).ToList();
				ViewBag.Vendors = (await _db.Vendors.AsNoTracking().Where(v => v.CompanyID == ctx.CompanyId).OrderBy(v => v.Name).Select(v => new { v.ID, v.Name, v.NameEn }).ToListAsync(ct))
					.Select(v => (v.ID, Name: v.Name, NameEn: v.NameEn)).ToList();
			}
			return View(await DisposalSvc.StatsAsync(ctx, WardToday(), ct));
		}

		[HttpGet("Pharmacy/Disposals/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DisposalsData(string? tab, string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			await MoneyFormatAsync(ctx, ct);
			if (tab == DisposalTabs.Done)
			{
				var (rows, total) = await DisposalSvc.DoneAsync(ctx, q, filter, page, pageSize, ct);
				PageHeaders(total, page, pageSize);
				return PartialView("_DisposalDoneRows", rows);
			}
			var (todo, count) = await DisposalSvc.CandidatesPageAsync(ctx, q, filter, page, pageSize, WardToday(), ct);
			PageHeaders(count, page, pageSize);
			return PartialView("_DisposalTodoRows", todo);
		}

		[HttpGet("Pharmacy/Disposals/Supplier")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DisposalSupplier(int itemId, string batchNo, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			return Json(new { vendorId = await DisposalSvc.SupplierOfAsync(ctx, itemId, batchNo, ct) });
		}

		[HttpPost("Pharmacy/Disposals/Destroy"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> DisposalDestroy([FromBody] HospitalDisposalInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DisposalSvc.DestroyAsync(ctx, input, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/Disposals/Return"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> DisposalReturn([FromBody] HospitalDisposalInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DisposalSvc.ReturnAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, WardToday(), ct));
		}
	}
}
