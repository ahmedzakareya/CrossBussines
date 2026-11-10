using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H66: equipment work orders. The board (figures, report a fault, the orders searched and paged on the
	// server) and each step of an order. A registered asset's repair is logged in Accounting's maintenance records.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalWorkOrderService? _woSvc;
		private HospitalWorkOrderService WorkOrderSvc => _woSvc ??= ActivatorUtilities.CreateInstance<HospitalWorkOrderService>(HttpContext.RequestServices);

		[HttpGet("WorkOrders")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> WorkOrders(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var f = await WorkOrderSvc.FiguresAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = f.Ready; ViewBag.Open = f.Open; ViewBag.UrgentOpen = f.UrgentOpen; ViewBag.AvgHours = f.AvgHours; ViewBag.Done30 = f.Done30;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Assets = await _db.FixedAssets.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && a.Status != "Disposed").OrderBy(a => a.AssetNo)
				.Select(a => new HospitalChoice(a.ID, a.Name, a.NameEn ?? a.Name, a.AssetNo)).ToListAsync(ct);
			ViewBag.Rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).OrderBy(r => r.Number)
				.Select(r => new HospitalChoice(r.ID, r.Number, r.Number, null)).ToListAsync(ct);
			if (ViewBag.CanManage == true)
			{
				var staff = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive).OrderBy(e => e.FullName).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				var photos = await EmployeePhotos.ResolveAsync(_db, ctx, staff.Select(s => s.ID).ToList(), ct);
				ViewBag.Staff = staff.Select(s => (s.ID, Name: s.FullName ?? "", NameEn: s.FullNameEn ?? s.FullName ?? "", Photo: photos.GetValueOrDefault(s.ID))).ToList();
				ViewBag.Accounts = await PostingAccountsAsync(ctx, ct);
			}
			ViewBag.Me = ctx.EmployeeId;
			return View();
		}

		[HttpGet("WorkOrders/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> WorkOrdersData(string? q, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total) = await WorkOrderSvc.ListAsync(ctx, string.IsNullOrEmpty(state) ? null : state, q, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Me = ctx.EmployeeId;
			return PartialView("_WorkOrderRows", rows);
		}

		[HttpPost("WorkOrders"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ReportWorkOrder([FromBody] HospitalWorkOrderInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await WorkOrderSvc.ReportAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("WorkOrders/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MoveWorkOrder(int id, [FromBody] HospitalWorkOrderMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await WorkOrderSvc.MoveAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
