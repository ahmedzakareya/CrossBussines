using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H139/H140: the front desk, one workspace, as the approved prototype lays it out
	// (design-output/hospital-prototype, route #/frontdesk). The screen carries:
	//   · the desk strip: who sits here, the shift, the cash in the drawer, the last number called, the clock;
	//   · the one search, narrowed by kind (name, file, ID, phone, queue);
	//   · eight figures; the clinics right now (serving, waiting, delay, next free slot, how full);
	//   · today's board as a list (searched, filtered, paged, flagged) or as a time grid;
	//   · the patient at the counter (check-in steps, money, prints) beside the board;
	//   · the desk's work and the drawer;
	//   · the walk-in window (the doctors' free slots) and the payment window (the patient's open bills).
	// The appointment rules belong to the outpatient service and the money to the billing service; the desk calls them.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDeskMoveInput { public string? To { get; set; } }
		public sealed class HospitalDeskWalkInInput { public int PatientId { get; set; } public int PractitionerId { get; set; } public string? VisitType { get; set; } public int? StartMinute { get; set; } }

		private HospitalFrontDeskService? _frontDesk;
		private HospitalFrontDeskService FrontDeskSvc => _frontDesk ??= ActivatorUtilities.CreateInstance<HospitalFrontDeskService>(HttpContext.RequestServices,
			_outpatient, ActivatorUtilities.CreateInstance<HospitalPortalBookingService>(HttpContext.RequestServices),
			ActivatorUtilities.CreateInstance<HospitalInsuranceService>(HttpContext.RequestServices), _billing);

		private static (DateTime Today, int NowMinute, DateTime NowLocal) DeskClock(DateTime nowUtc)
		{
			var local = HospitalNursingService.UtcToWard(nowUtc);
			return (local.Date, local.Hour * 60 + local.Minute, local);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDesk(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var now = DateTime.UtcNow; var (today, minute, local) = DeskClock(now);
			var board = await FrontDeskSvc.FlagAsync(ctx, await FrontDeskSvc.BoardAsync(ctx, today, minute, now, ct), today, now, ct);
			var where = await ActivatorUtilities.CreateInstance<HospitalWhereaboutsService>(HttpContext.RequestServices).CountsAsync(ctx, local, ct);
			ViewBag.Inbox = await FrontDeskSvc.InboxAsync(ctx, now, ct);
			ViewBag.Unconfirmed = await DeskWorkSvc.UnconfirmedAsync(ctx, today.AddDays(1), ct);   // H141
			ViewBag.StageCounts = board.GroupBy(r => r.Stage).ToDictionary(g => g.Key, g => g.Count());
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.Doctors = await _outpatient.PractitionersAsync(ctx, ct);
			ViewBag.Clinics = await FrontDeskSvc.ClinicsAsync(ctx, local, now, ct);
			ViewBag.Me = await FrontDeskSvc.MeAsync(ctx, today, ct);
			ViewBag.Next = board.FirstOrDefault(r => r.Stage == HospitalDeskStage.Expected && !r.Late) ?? board.FirstOrDefault(r => r.Stage == HospitalDeskStage.Expected);
			ViewBag.Today = today; ViewBag.NowLocal = local;
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			await MoneyFormatAsync(ctx, ct);
			return View(HospitalFrontDeskService.Stats(board, where));
		}

		private async Task<IEnumerable<HospitalDeskRow>> DeskRowsAsync(Models.Platform.BusinessContext ctx, string? stage, int? department, int? doctor, string? q, DateTime now, CancellationToken ct)
		{
			var (today, minute, _) = DeskClock(now);
			IEnumerable<HospitalDeskRow> rows = await FrontDeskSvc.BoardAsync(ctx, today, minute, now, ct);
			if (HospitalDeskStage.All.Contains(stage ?? "")) rows = rows.Where(r => r.Stage == stage);
			if (doctor is int d) rows = rows.Where(r => r.A.PractitionerId == d);
			if (department is int dep)
			{
				var ids = (await _outpatient.PractitionersAsync(ctx, ct)).Where(p => p.Row.DepartmentId == dep).Select(p => p.Row.ID).ToHashSet();
				rows = rows.Where(r => ids.Contains(r.A.PractitionerId));
			}
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				rows = rows.Where(r => r.A.Mrn.Contains(t) || r.A.QueueNumber?.ToString() == t || HospitalFrontDeskRules.NameScore(t, r.A.PatientName, r.A.PatientNameEn) >= 50);
			}
			return rows;
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Board")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskBoard(string? stage, int? department, int? doctor, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var now = DateTime.UtcNow;
			var list = (await DeskRowsAsync(ctx, stage, department, doctor, q, now, ct)).ToList();
			PageHeaders(list.Count, page, pageSize);
			ViewBag.Today = DeskClock(now).Today;
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			await MoneyFormatAsync(ctx, ct);
			return PartialView("_DeskRows", await FrontDeskSvc.FlagAsync(ctx, list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), DeskClock(now).Today, now, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Grid")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskGrid(int? department, int? doctor, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var now = DateTime.UtcNow; var (_, _, local) = DeskClock(now);
			var rows = (await DeskRowsAsync(ctx, null, department, doctor, null, now, ct)).ToList();
			var grid = await FrontDeskSvc.GridAsync(ctx, rows, local, now, department, ct);
			if (doctor is int d) grid = grid with { Doctors = grid.Doctors.Where(x => x.Row.ID == d).ToList() };
			return PartialView("_DeskGrid", grid);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Search")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskSearch(string? q, string? kind, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			ViewBag.Query = q;
			IEnumerable<HospitalDeskCandidate> hits = await FrontDeskSvc.SearchAsync(ctx, q, DeskClock(DateTime.UtcNow).Today, 12, ct);
			if (kind is "name" or "mrn" or "id" or "phone" or "queue") hits = hits.Where(h => h.Reason == kind);
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			var list = hits.ToList();
			// H154: "where is the patient?" — each candidate with where they are now (a confidential file's place stays unsaid)
			var (where, restricted) = await WhereSvc.ForDeskAsync(ctx, list.Select(h => h.PatientId).ToList(), DateTime.Now, ct);
			ViewBag.Where = where; ViewBag.Restricted = restricted;
			return PartialView("_DeskSearch", list);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Patient/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskPatient(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var now = DateTime.UtcNow; var (today, minute, _) = DeskClock(now);
			var card = await FrontDeskSvc.CardAsync(ctx, id, today, minute, now, ct);
			if (card == null) return NotFound();
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			await MoneyFormatAsync(ctx, ct);
			// H154: where the patient is now, and their history as the desk needs it (administrative only)
			var (where, restricted) = await WhereSvc.ForDeskAsync(ctx, new[] { id }, DateTime.Now, ct);
			ViewBag.Where = where.GetValueOrDefault(id) ?? Array.Empty<HospitalWhere>(); ViewBag.Restricted = restricted.Contains(id);
			ViewBag.History = await ActivatorUtilities.CreateInstance<HospitalDeskHistoryService>(HttpContext.RequestServices).HistoryAsync(ctx, id, 8, today, ct);
			ViewBag.Lookalikes = await ActivatorUtilities.CreateInstance<HospitalDeskIdentityService>(HttpContext.RequestServices).LookalikesAsync(ctx, id, 3, ct);   // H157
			return PartialView("_DeskCard", card);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/WalkInSlots")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskWalkInSlots(int patient, int? doctor, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var now = DateTime.UtcNow; var (today, _, local) = DeskClock(now);
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == patient && x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstOrDefaultAsync(ct);
			if (p == null) return NotFound();
			ViewBag.Patient = (p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath);
			ViewBag.Doctor = doctor;
			return PartialView("_DeskWalkIn", await FrontDeskSvc.WalkInSlotsAsync(ctx, local, now, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Patient/{id:int}/Pay")]
		[HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FrontDeskPay(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return NotFound();
			ViewBag.Patient = p;
			ViewBag.Shift = ctx.EmployeeId is int me ? await _billing.OpenShiftOfAsync(ctx, me, ct) : null;
			ViewBag.Deposits = await DepositSvc.ListAsync(ctx, id, ct);   // H143
			await MoneyFormatAsync(ctx, ct);
			return PartialView("_DeskPay", await FrontDeskSvc.OpenBillsAsync(ctx, id, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/Appointments/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskMove(int id, [FromBody] HospitalDeskMoveInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FrontDeskSvc.MoveAsync(ctx, id, input?.To ?? "", ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/WalkIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskWalkIn([FromBody] HospitalDeskWalkInInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			return Result(await FrontDeskSvc.WalkInAsync(ctx, input?.PatientId ?? 0, input?.PractitionerId ?? 0, input?.VisitType, ctx.EmployeeId, now, DeskClock(now).NowLocal, ct, input?.StartMinute));
		}
	}
}
