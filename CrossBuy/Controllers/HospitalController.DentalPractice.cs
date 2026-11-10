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
	// HOSPITAL — H16: the dental practice. The chair day (the dentistry landing page), lab cases, and recall &
	// sterilisation. Every rule is HospitalDentalPracticeService's. Built from the request's services like
	// H11–H15, so Program.cs and the shared constructor are untouched. Wall-clock times are the facility's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalVisitMoveInput { public string To { get; set; } = ""; }
		public sealed class HospitalLabCaseMoveInput { public string To { get; set; } = ""; public string? Fault { get; set; } public string? Reason { get; set; } }
		public sealed class HospitalIndicatorInput { public string Kind { get; set; } = ""; public string Result { get; set; } = ""; }
		public sealed class HospitalKitUseInput { public int PatientId { get; set; } public int? AppointmentId { get; set; } }

		private HospitalDentalPracticeService? _practiceSvc;
		private HospitalDentalPracticeService PracticeSvc => _practiceSvc ??= ActivatorUtilities.CreateInstance<HospitalDentalPracticeService>(HttpContext.RequestServices, DentalSvc);

		/// The chair day: every chair's appointments for the day, at the length of their procedures.
		[HttpGet("Dental")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Dental(DateTime? day, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var d = (day ?? DateTime.Now).Date;
			ViewBag.PlanStats = (await DentalSvc.PlansAsync(ctx, DateTime.UtcNow, ct)).Stats;
			ViewBag.AcceptedThisMonth = await AcceptedThisMonthAsync(ctx, ct);
			ViewBag.Procedures = await DentalSvc.ProceduresAsync(ctx, ct);
			ViewBag.Dentists = await DentalSvc.DentistsAsync(ctx, ct);
			ViewBag.AllChairs = await PracticeSvc.ChairsAsync(ctx, false, ct);
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await PracticeSvc.DayAsync(ctx, d, DateTime.Now, ct));
		}

		/// The value of the work patients accepted this month (the plans' own figure, for the chair day's tile).
		private async Task<(decimal Accepted, decimal Presented, string? Currency, int Decimals)> AcceptedThisMonthAsync(BusinessContext ctx, CancellationToken ct)
		{
			var (rows, stats) = await DentalSvc.PlansAsync(ctx, DateTime.UtcNow, ct);
			var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
			var mine = rows.Where(r => r.Plan.CreatedAt >= month && r.Currency == stats.Currency).ToList();
			decimal presented = mine.Sum(r => r.Quote.Gross);
			decimal accepted = 0;
			foreach (var r in mine.Where(r => r.State is "Accepted" or "Partial"))
			{
				var v = await DentalSvc.PlanAsync(ctx, r.Plan.ID, DateTime.UtcNow, ct);
				accepted += v?.Phases.Where(p => p.Phase.Decision == HospitalDentalDecision.Accepted).SelectMany(p => p.Lines).Sum(l => l.Line.Fee) ?? 0;
			}
			return (accepted, presented, stats.Currency, stats.Decimals);
		}

		[HttpPost("Dental/Chair"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DentalSaveChair([FromBody] HospitalChairInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.SaveChairAsync(ctx, input, ct));
		}

		[HttpPost("Dental/Book"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> DentalBook([FromBody] HospitalChairBookingInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.BookAsync(ctx, input, ctx.EmployeeId, DateTime.Now, ct));
		}

		[HttpPost("Dental/Visit/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> DentalVisitMove(int id, [FromBody] HospitalVisitMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.MoveAsync(ctx, id, input.To, ctx.EmployeeId, DateTime.Now, ct));
		}

		/// A patient's coming chair appointments (the lab prescription's fit appointment picker).
		[HttpGet("Dental/Patient/{patientId:int}/Visits")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalPatientVisits(int patientId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var today = DateTime.Now.Date;
			var rows = await (from v in _db.Set<HospitalDentalVisit>().AsNoTracking()
							  join a in _db.HospitalAppointments.AsNoTracking() on v.AppointmentId equals a.ID
							  join c in _db.Set<HospitalDentalChair>().AsNoTracking() on v.ChairId equals c.ID
							  where v.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.AppointmentDate >= today && a.Status == HospitalAppointmentStatus.Booked
							  orderby a.AppointmentDate, a.StartMinute
							  select new { a.ID, a.AppointmentDate, a.StartMinute, c.Name, c.NameEn }).Take(20).ToListAsync(ct);
			return Json(new { ok = true, results = rows.Select(r => new { id = r.ID, text = r.AppointmentDate.ToString("yyyy-MM-dd") + " " + HospitalUi.Clock(r.StartMinute) + " · " + DisplayName.Of(r.Name, r.NameEn) }) });
		}

		// ---------------- lab ----------------

		[HttpGet("Dental/Lab")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalLab(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Stats = await PracticeSvc.LabStatsAsync(ctx, DateTime.Now.Date, ct);
			ViewBag.Labs = await PracticeSvc.LabsAsync(ctx, ct);
			ViewBag.Facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanMove = ViewBag.CanConsult == true || await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await PracticeSvc.CasesAsync(ctx, DateTime.Now, ct));
		}

		[HttpPost("Dental/Labs"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DentalSaveLab([FromBody] HospitalDentalLabInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.SaveLabAsync(ctx, input, ct));
		}

		[HttpPost("Dental/LabCase"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalCreateCase([FromBody] HospitalLabCaseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.CreateCaseAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// `read` at the door; the service asks `consult` or `book` (the desk receives a case from the courier).
		[HttpPost("Dental/LabCase/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalMoveCase(int id, [FromBody] HospitalLabCaseMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.MoveCaseAsync(ctx, id, input.To, input.Fault, input.Reason, DateTime.UtcNow, ct));
		}

		// ---------------- recall & sterilisation ----------------

		[HttpGet("Dental/Recall")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalRecall(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, stats) = await PracticeSvc.RecallsAsync(ctx, DateTime.Now.Date, ct);
			ViewBag.Stats = stats;
			ViewBag.Cycles = await PracticeSvc.CyclesAsync(ctx, ct);
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(rows);
		}

		[HttpPost("Dental/Recall/{id:int}/Contact"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> DentalRecallContact(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.RecallContactAsync(ctx, id, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Steril"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalCreateCycle([FromBody] HospitalSterilCycleInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.CreateCycleAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, DateTime.Now, ct));
		}

		[HttpPost("Dental/Steril/{id:int}/Indicator"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalIndicator(int id, [FromBody] HospitalIndicatorInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.RecordIndicatorAsync(ctx, id, input.Kind, input.Result, ct));
		}

		[HttpPost("Dental/Steril/{id:int}/Use"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalUseKit(int id, [FromBody] HospitalKitUseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PracticeSvc.UseKitAsync(ctx, id, input.PatientId, input.AppointmentId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
