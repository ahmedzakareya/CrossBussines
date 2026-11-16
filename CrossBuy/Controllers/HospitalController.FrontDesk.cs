using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H139: the front desk, one workspace. The screen carries:
	//   · the one search;
	//   · the day's board (searched, filtered, paged);
	//   · the inbox;
	//   · the patient card in a side drawer, with check-in, no-show and walk-in.
	// The appointment endpoints belong to the outpatient screens. The desk calls the outpatient service through its own
	// actions here.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDeskMoveInput { public string? To { get; set; } }
		public sealed class HospitalDeskWalkInInput { public int PatientId { get; set; } public int PractitionerId { get; set; } public string? VisitType { get; set; } }

		private HospitalFrontDeskService? _frontDesk;
		private HospitalFrontDeskService FrontDeskSvc => _frontDesk ??= ActivatorUtilities.CreateInstance<HospitalFrontDeskService>(HttpContext.RequestServices,
			_outpatient, ActivatorUtilities.CreateInstance<HospitalPortalBookingService>(HttpContext.RequestServices),
			ActivatorUtilities.CreateInstance<HospitalInsuranceService>(HttpContext.RequestServices), _billing);

		private static (DateTime Today, int NowMinute, DateTime NowLocal) DeskClock(DateTime nowUtc)
		{
			var local = HospitalNursingService.UtcToWard(nowUtc);
			return (local.Date, local.Hour * 60 + local.Minute, local);
		}

		[HttpGet("FrontDesk")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDesk(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var now = DateTime.UtcNow; var (today, minute, local) = DeskClock(now);
			var board = await FrontDeskSvc.BoardAsync(ctx, today, minute, now, ct);
			var where = await ActivatorUtilities.CreateInstance<HospitalWhereaboutsService>(HttpContext.RequestServices).CountsAsync(ctx, local, ct);
			ViewBag.Inbox = await FrontDeskSvc.InboxAsync(ctx, now, ct);
			ViewBag.StageCounts = board.GroupBy(r => r.Stage).ToDictionary(g => g.Key, g => g.Count());
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.Doctors = await _outpatient.PractitionersAsync(ctx, ct);
			ViewBag.Today = today;
			return View(HospitalFrontDeskService.Stats(board, where));
		}

		[HttpGet("FrontDesk/Board")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskBoard(string? stage, int? department, int? doctor, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var now = DateTime.UtcNow; var (today, minute, _) = DeskClock(now);
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
			var list = rows.ToList();
			PageHeaders(list.Count, page, pageSize);
			ViewBag.Today = today;
			return PartialView("_DeskRows", list.Skip((page - 1) * pageSize).Take(pageSize).ToList());
		}

		[HttpGet("FrontDesk/Search")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskSearch(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			ViewBag.Query = q;
			return PartialView("_DeskSearch", await FrontDeskSvc.SearchAsync(ctx, q, DeskClock(DateTime.UtcNow).Today, 12, ct));
		}

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
			await MoneyFormatAsync(ctx, ct);
			return PartialView("_DeskCard", card);
		}

		[HttpPost("FrontDesk/Appointments/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskMove(int id, [FromBody] HospitalDeskMoveInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FrontDeskSvc.MoveAsync(ctx, id, input?.To ?? "", ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("FrontDesk/WalkIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskWalkIn([FromBody] HospitalDeskWalkInInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			return Result(await FrontDeskSvc.WalkInAsync(ctx, input?.PatientId ?? 0, input?.PractitionerId ?? 0, input?.VisitType, ctx.EmployeeId, now, DeskClock(now).NowLocal, ct));
		}
	}
}
