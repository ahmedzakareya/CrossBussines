using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H32/H36, ward requisitions to stores (the prototype's #/requisitions) and their setting: each supply
	// unit's store, the store it draws from, its pars, and WHO asks and WHO gives for it (owner ruling: a setting, not
	// a role in code). Every rule is HospitalSupplyService's; the door is `read`, the service decides the duty.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSupplyService? _supplySvc;
		private HospitalSupplyService SupplySvc => _supplySvc ??= ActivatorUtilities.CreateInstance<HospitalSupplyService>(HttpContext.RequestServices);

		public sealed class HospitalSupplyApproveInput { public decimal Qty { get; set; } }
		public sealed class HospitalConsignmentReceiveInput
		{
			public int VendorId { get; set; } public int ItemId { get; set; } public List<string> Serials { get; set; } = new();
			public string? Lot { get; set; } public DateTime? Expiry { get; set; } public decimal Price { get; set; }
		}
		private HospitalConsignmentService? _consSvc;
		private HospitalConsignmentService ConsSvc => _consSvc ??= ActivatorUtilities.CreateInstance<HospitalConsignmentService>(HttpContext.RequestServices, _billing);
		public sealed class HospitalSupplyPersonInput { public int EmployeeId { get; set; } public string Duty { get; set; } = ""; }
		private HospitalControlledService? _cdSvc;
		private HospitalControlledService CdSvc => _cdSvc ??= ActivatorUtilities.CreateInstance<HospitalControlledService>(HttpContext.RequestServices,
			ActivatorUtilities.CreateInstance<HospitalPasswordWitness>(HttpContext.RequestServices));
		public sealed class HospitalControlledRecordInput
		{
			public int WarehouseId { get; set; } public int ItemId { get; set; } public string Kind { get; set; } = ""; public decimal Qty { get; set; }
			public int? PatientId { get; set; } public string? Note { get; set; } public int WitnessId { get; set; } public string? Password { get; set; }
		}
		public sealed class HospitalControlledItemInput { public int ItemId { get; set; } public bool On { get; set; } }

		[HttpGet("Supply")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Supply(int? id, int? ward, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await SupplySvc.BoardAsync(ctx, DateTime.UtcNow.Date, ct);
			ViewBag.Ready = ready;
			var units = await SupplySvc.UnitsAsync(ctx, ct);
			ViewBag.Units = units;
			// the units this person may ask for (the New requisition window offers only these)
			var mine = new List<HospitalSupplyUnit>();
			foreach (var u in units.Where(u => u.IsActive)) if (await SupplySvc.MayAsync(ctx, u.ID, HospitalSupplyDuty.Request, ct)) mine.Add(u);
			ViewBag.RequestUnits = mine;
			int? pick = id ?? board?.Requests.FirstOrDefault()?.Req.ID;
			var view = pick is int p ? await SupplySvc.RequisitionAsync(ctx, p, ct) : null;
			ViewBag.Selected = view;
			if (view != null)
			{
				ViewBag.MayRequest = await SupplySvc.MayAsync(ctx, view.Row.Unit.ID, HospitalSupplyDuty.Request, ct);
				ViewBag.MayApprove = await SupplySvc.MayAsync(ctx, view.Row.Unit.ID, HospitalSupplyDuty.Approve, ct);
			}
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			// H38: the consignment implants (the prototype's lower-left card)
			var (cons, consReady) = await ConsSvc.ListAsync(ctx, null, false, ct);
			ViewBag.Consignments = cons; ViewBag.ConsReady = consReady;
			ViewBag.MayStore = ViewBag.CanManage == true || await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			if (ViewBag.MayStore == true)
				ViewBag.Vendors = await _db.Vendors.AsNoTracking().Where(v => v.CompanyID == ctx.CompanyId).OrderBy(v => v.Name)
					.Select(v => new HospitalChoice(v.ID, v.Name, v.NameEn, null)).ToListAsync(ct);
			// H39: the controlled-drugs register of one ward store (the prototype's lower-right card), today
			var stores = await CdSvc.WardStoresAsync(ctx, ct);
			var store = stores.FirstOrDefault(s => s.WarehouseId == ward);
			if (store.WarehouseId == 0) store = stores.FirstOrDefault();
			ViewBag.CdStores = stores; ViewBag.CdStore = store;
			var (cdLines, cdEntries, cdReady) = store.WarehouseId == 0 ? (Array.Empty<HospitalControlledLine>(), Array.Empty<HospitalControlledEntryRow>(), true)
				: await CdSvc.RegisterAsync(ctx, store.WarehouseId, DateTime.UtcNow.Date, ct);
			ViewBag.CdLines = cdLines; ViewBag.CdEntries = cdEntries; ViewBag.CdReady = cdReady;
			ViewBag.CdItems = await CdSvc.ControlledItemsAsync(ctx, ct);
			ViewBag.CdMayRecord = ViewBag.CanManage == true || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			if (ViewBag.CdMayRecord == true && store.WarehouseId != 0)
			{
				// the witness: anyone else with a sign-in (she confirms with her own password)
				var wit = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive && e.UserId != null && e.ID != ctx.EmployeeId)
					.OrderBy(e => e.FullName).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				var wp = await EmployeePhotos.ResolveAsync(_db, ctx, wit.Select(s => s.ID).ToList(), ct);
				ViewBag.CdWitnesses = wit.Select(s => (s.ID, Name: s.FullName ?? "", NameEn: s.FullNameEn ?? s.FullName ?? "", Photo: wp.GetValueOrDefault(s.ID))).ToList();
			}
			return View(board);
		}

		[HttpGet("Supply/Items")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupplyItems(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var items = await SupplySvc.SearchItemsAsync(ctx, q, ct);
			return Json(new { ok = true, items = items.Select(i => new { id = i.Id, text = i.Code + " — " + DisplayName.Of(i.Name, i.NameEn) }) });
		}

		[HttpPost("Supply/Requisitions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupplyCreate([FromBody] HospitalRequisitionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.CreateAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Supply/Requisition/{id:int}/Lines"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupplySetLine(int id, [FromBody] HospitalRequisitionLineInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.SetLineAsync(ctx, id, input, ct));
		}

		[HttpPost("Supply/Requisition/{id:int}/Lines/{itemId:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupplyRemoveLine(int id, int itemId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.RemoveLineAsync(ctx, id, itemId, ct));
		}

		[HttpPost("Supply/Requisition/{id:int}/Lines/{itemId:int}/Approve"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupplyApproveLine(int id, int itemId, [FromBody] HospitalSupplyApproveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.ApproveLineAsync(ctx, id, itemId, input.Qty, ct));
		}

		[HttpPost("Supply/Requisition/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupplyMove(int id, [FromBody] HospitalRequisitionMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.MoveAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- H38: consignment implants ----------------

		[HttpPost("Supply/Consignments"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConsignmentReceive([FromBody] HospitalConsignmentReceiveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsSvc.ReceiveAsync(ctx, input.VendorId, input.ItemId, input.Serials, input.Lot, input.Expiry, input.Price, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Supply/Consignments/{id:int}/Return"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConsignmentReturn(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsSvc.ReturnAsync(ctx, id, DateTime.UtcNow, ct));
		}

		// ---------------- H39: the controlled-drugs register ----------------

		[HttpPost("Supply/Controlled/Record"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ControlledRecord([FromBody] HospitalControlledRecordInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CdSvc.RecordAsync(ctx, input.WarehouseId, input.ItemId, input.Kind, input.Qty, input.PatientId, input.Note,
				input.WitnessId, input.Password, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Supply/Controlled/Items"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ControlledItem([FromBody] HospitalControlledItemInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CdSvc.SetControlledAsync(ctx, input.ItemId, input.On, DateTime.UtcNow, ct));
		}

		// ---------------- the setting (`manage`) ----------------

		[HttpGet("Supply/Units")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SupplyUnits(int? unit, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var units = await SupplySvc.UnitsAsync(ctx, ct);
			var u = units.FirstOrDefault(x => x.ID == unit) ?? units.FirstOrDefault();
			ViewBag.Unit = u;
			ViewBag.Pars = u == null ? Array.Empty<HospitalSupplyItemRow>() : await SupplySvc.ParsAsync(ctx, u.ID, ct);
			ViewBag.People = u == null ? Array.Empty<HospitalSupplyService.PersonRow>() : await SupplySvc.PeopleAsync(ctx, u.ID, ct);
			ViewBag.Warehouses = await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && w.IsActive)
				.OrderBy(w => w.Code).Select(w => new HospitalChoice(w.ID, w.Name ?? w.Code, w.NameEn ?? w.Name ?? w.Code, w.Code)).ToListAsync(ct);
			ViewBag.Wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId)
				.OrderBy(w => w.Code).Select(w => new HospitalChoice(w.ID, w.Name, w.NameEn ?? w.Name, w.Code)).ToListAsync(ct);
			var staff = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive)
				.OrderBy(e => e.FullName).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, staff.Select(s => s.ID).ToList(), ct);
			ViewBag.Staff = staff.Select(s => (s.ID, Name: s.FullName ?? "", NameEn: s.FullNameEn ?? s.FullName ?? "", Photo: photos.GetValueOrDefault(s.ID))).ToList();
			return View(units);
		}

		[HttpPost("Supply/Units"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SupplySaveUnit([FromBody] HospitalSupplyUnitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.SaveUnitAsync(ctx, input, ct));
		}

		[HttpPost("Supply/Units/{id:int}/Pars"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SupplySavePar(int id, [FromBody] HospitalSupplyParInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.SaveParAsync(ctx, id, input, ct));
		}

		[HttpPost("Supply/Units/{id:int}/Pars/{itemId:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SupplyRemovePar(int id, int itemId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.RemoveParAsync(ctx, id, itemId, ct));
		}

		[HttpPost("Supply/Units/{id:int}/People"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SupplyAddPerson(int id, [FromBody] HospitalSupplyPersonInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.AddPersonAsync(ctx, id, input.EmployeeId, input.Duty, DateTime.UtcNow, ct));
		}

		[HttpPost("Supply/People/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SupplyRemovePerson(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SupplySvc.RemovePersonAsync(ctx, id, ct));
		}
	}
}
