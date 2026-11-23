using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H29, the theatre schedule (the prototype's #/or). Every rule is HospitalTheatreService's; built from
	// the request's services like H11–H28, so Program.cs is untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalTheatreService? _theatreSvc;
		private HospitalTheatreService TheatreSvc => _theatreSvc ??= ActivatorUtilities.CreateInstance<HospitalTheatreService>(HttpContext.RequestServices, _billing);
		private HospitalTheatreKitService? _kitSvc;
		private HospitalTheatreKitService KitSvc => _kitSvc ??= ActivatorUtilities.CreateInstance<HospitalTheatreKitService>(HttpContext.RequestServices);

		public sealed class HospitalKitSetInput { public int ServiceId { get; set; } public int? KitItemId { get; set; } }
		public sealed class HospitalTheatreStoreInput { public int? WarehouseId { get; set; } }
		public sealed class HospitalImplantUseInput { public int ConsignmentId { get; set; } }

		[HttpGet("Theatre")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Theatre(DateTime? day, int? @case, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var d = (day ?? ClinicToday()).Date;
			var (model, ready) = await TheatreSvc.DayAsync(ctx, d, ct);
			ViewBag.Ready = ready;
			ViewBag.Today = ClinicToday();
			ViewBag.Selected = @case;
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.Team = await TheatreSvc.TeamAsync(ctx, ct);
			ViewBag.Procedures = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.IsActive && s.Category == HospitalServiceCategories.Procedure)
				.OrderBy(s => s.Code).Select(s => new HospitalChoice(s.ID, s.Name, s.NameEn, s.Code)).ToListAsync(ct);
			// H37: the chosen case's kit (the same choice the page makes: picked, else in progress, else next planned)
			var cases = (model?.Cases ?? Array.Empty<HospitalTheatreCase>()).Where(c => c.Case.Status != HospitalSurgeryStatus.Cancelled).ToList();
			var sel = cases.FirstOrDefault(c => c.Case.ID == @case) ?? cases.FirstOrDefault(c => c.Case.Status == HospitalSurgeryStatus.InProgress)
				?? cases.Where(c => c.Case.Status == HospitalSurgeryStatus.Planned).OrderBy(c => c.Case.StartMinute).FirstOrDefault();
			ViewBag.Kit = sel == null ? null : await KitSvc.KitAsync(ctx, sel.Case, ct);
			ViewBag.CaseConsentNo = sel == null ? null : await TheatreSvc.ConsentNoAsync(ctx, sel.Case.ID, ct);   // H60
			ViewBag.CaseSets = sel == null ? new List<string>() : await TheatreSvc.IssuedSetsAsync(ctx, sel.Case.ID, ct);   // the sterile sets issued to it
			// H38: the consignment implants used on this case, and those still in the store
			if (sel != null)
			{
				var cons = ActivatorUtilities.CreateInstance<HospitalConsignmentService>(HttpContext.RequestServices, _billing);
				ViewBag.CaseImplants = (await cons.ListAsync(ctx, sel.Case.ID, false, ct)).Rows;
				ViewBag.StoreImplants = (await cons.ListAsync(ctx, null, true, ct)).Rows;
			}
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			if (ViewBag.CanManage == true)
			{
				ViewBag.KitItems = await KitSvc.KitItemsAsync(ctx, ct);
				ViewBag.Stores = await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code)
					.Select(w => new HospitalChoice(w.ID, w.Name, w.NameEn, w.Code)).ToListAsync(ct);
			}
			return View(model);
		}

		[HttpPost("Theatre/Book"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> TheatreBook([FromBody] HospitalSurgeryInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await TheatreSvc.BookAsync(ctx, input, ctx.EmployeeId, DateTime.Now, ct));
		}

		/// `read` at the door; the service asks `book` to cancel and `consult` to start or close.
		[HttpPost("Theatre/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> TheatreMove(int id, [FromBody] HospitalSurgeryMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			// H260: a case is not closed while its operative note is unsigned
			if (input.To == HospitalSurgeryStatus.Done && !await OpNoteSvc.SignedOrNotInstalledAsync(ctx.CompanyId, id, ct))
				return Result(HospitalResult.Fail("Sign the operative note before closing the case"));
			var r = await TheatreSvc.MoveAsync(ctx, id, input, ctx.EmployeeId, DateTime.Now, ct);
			// H37: a closed case's kit leaves the theatre store now; a short store never undoes the close — the card keeps
			// "not issued" and the reason, and the kit is issued from there once the store is stocked
			if (r.Ok && input.To == HospitalSurgeryStatus.Done)
			{
				var kit = await KitSvc.IssueAsync(ctx, id, ctx.UserId, DateTime.UtcNow, ct);
				return Json(new { ok = true, id, kitIssued = kit.Ok, kitError = kit.Ok || kit.Error == "This procedure has no kit set" ? null : _t[kit.Error ?? "The change could not be saved", kit.Args ?? Array.Empty<object>()].Value });
			}
			return Result(r);
		}

		/// H38: a consignment implant used on this case (charged to the patient, a draft PO to its supplier).
		[HttpPost("Theatre/{id:int}/Implants"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> TheatreImplant(int id, [FromBody] HospitalImplantUseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var cons = ActivatorUtilities.CreateInstance<HospitalConsignmentService>(HttpContext.RequestServices, _billing);
			return Result(await cons.UseAsync(ctx, input.ConsignmentId, id, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Theatre/{id:int}/Kit/Issue"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> TheatreKitIssue(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitSvc.IssueAsync(ctx, id, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Theatre/Kit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TheatreKitSet([FromBody] HospitalKitSetInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitSvc.SetKitAsync(ctx, input.ServiceId, input.KitItemId, DateTime.UtcNow, ct));
		}

		[HttpPost("Theatre/Store"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TheatreStoreSet([FromBody] HospitalTheatreStoreInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitSvc.SetStoreAsync(ctx, input.WarehouseId, DateTime.UtcNow, ct));
		}

		public sealed class HospitalSurgeryCheckInput { public string Item { get; set; } = ""; public bool On { get; set; } }

		[HttpPost("Theatre/{id:int}/Check"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> TheatreCheck(int id, [FromBody] HospitalSurgeryCheckInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await TheatreSvc.CheckAsync(ctx, id, input.Item, input.On, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
