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
	// HOSPITAL — H15: dentistry. The tooth chart and the periodontal chart of a patient, the treatment plans
	// and their quotation, and the dental setup (a service's dental profile, a payer's dental benefit). Every
	// rule is HospitalDentalService's. Built from the request's services like H11–H14, so Program.cs and the
	// shared constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDentitionInput { public string? Dentition { get; set; } }
		public sealed class HospitalDentalPhaseInput { public string Title { get; set; } = ""; public string TitleEn { get; set; } = ""; }
		public sealed class HospitalDentalDecideInput { public bool Accept { get; set; } public string? Signatory { get; set; } }
		public sealed class HospitalDentalProcedureSaveInput { public HospitalDentalProcedureInput? Profile { get; set; } public bool Remove { get; set; } }

		private HospitalDentalService? _dentalSvc;
		private HospitalDentalService DentalSvc => _dentalSvc ??= ActivatorUtilities.CreateInstance<HospitalDentalService>(HttpContext.RequestServices, InsuranceSvc);

		/// The practitioner the signed-in employee is, if any: who a chart mark is recorded by.
		private async Task<int?> MyPractitionerAsync(BusinessContext ctx, CancellationToken ct) => ctx.EmployeeId is int e
			? await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.EmployeeId == e).Select(d => (int?)d.ID).FirstOrDefaultAsync(ct)
			: null;

		/// The chart of one patient; with no patient, the picker that opens one.
		[HttpGet("Dental/Chart")]
		[HttpGet("Dental/Chart/{patientId:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalChart(int? patientId, DateTime? asOf, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			if (patientId is not int id) return View(null);
			// H180: the chart as it stood at the end of a past day — read-only
			DateTime? asOfUtc = asOf is DateTime d && d.Date < HospitalNursingService.UtcToWard(DateTime.UtcNow).Date ? HospitalNursingService.WardToUtc(d.Date.AddDays(1)).AddTicks(-1) : null;
			if (asOfUtc != null) { ViewBag.AsOf = asOf!.Value.Date; ViewBag.CanConsult = false; }
			var view = await DentalSvc.ChartAsync(ctx, id, DateTime.UtcNow, ct, asOfUtc);
			if (view == null) return NotFound();
			ViewBag.Procedures = await DentalSvc.ProceduresAsync(ctx, ct);
			ViewBag.Dentists = await DentalSvc.DentistsAsync(ctx, ct);
			// H31: the pictures of the patient's imaging orders (the "X-rays & photos" tab)
			ViewBag.Pictures = await RadImageSvc.OfOrdersAsync(ctx, view.Images.Select(i => i.Id).ToList(), ct);
			// printing from the chart: the patient's latest visit that prescribed something (the prescription paper)
			ViewBag.PrintEncounterId = await _db.Set<HospitalEncounter>().AsNoTracking()
				.Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == id && _db.Set<HospitalPrescriptionLine>().Any(l => l.CompanyId == ctx.CompanyId && l.EncounterId == e.ID))
				.OrderByDescending(e => e.StartedAt).Select(e => (int?)e.ID).FirstOrDefaultAsync(ct);
			return View(view);
		}

		/// The chart's patient picker (select2): the same patients the patients screen lists on `read`, each with a photo.
		[HttpGet("Dental/Patients/Search")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SearchDentalPatients(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var page = await _patients.ListAsync(ctx, q, 1, DateTime.UtcNow, ct);
			var ids = page.Rows.Select(p => p.Id).ToList();
			var photos = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && ids.Contains(p.ID)).Select(p => new { p.ID, p.PhotoPath }).ToListAsync(ct);
			return Json(new
			{
				ok = true,
				results = page.Rows.Select(p => new { id = p.Id, text = DisplayName.Of(p.Name, p.NameEn) + " — " + p.Mrn, photo = photos.FirstOrDefault(x => x.ID == p.Id)?.PhotoPath, initials = HospitalUi.Initials(p.NameEn) }),
			});
		}

		[HttpPost("Dental/Chart/{patientId:int}/Dentition"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalDentition(int patientId, [FromBody] HospitalDentitionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.SetDentitionAsync(ctx, patientId, input?.Dentition, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Finding"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalFinding([FromBody] HospitalDentalFindingInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.RecordFindingAsync(ctx, input, await MyPractitionerAsync(ctx, ct), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Finding/{id:int}/Void"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalVoidFinding(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.VoidFindingAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Perio"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalPerio([FromBody] HospitalPerioExamInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.SavePerioExamAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Dental/Plans")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalPlans(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, stats) = await DentalSvc.PlansAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Stats = stats;
			return View(rows);
		}

		[HttpGet("Dental/Plan/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalPlan(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var view = await DentalSvc.PlanAsync(ctx, id, DateTime.UtcNow, ct);
			if (view == null) return NotFound();
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanDecide = ViewBag.CanConsult == true || await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			// H179: each phase's informed consent, the patient's signed sessions, and the work done but not yet in a session
			var sessions = ActivatorUtilities.CreateInstance<HospitalDentalSessionService>(HttpContext.RequestServices);
			ViewBag.PhaseConsents = await sessions.PhaseConsentsAsync(ctx, view.Phases.Select(p => p.Phase.ID).ToList(), ct);
			ViewBag.Sessions = await sessions.SessionsAsync(ctx, view.Row.Plan.PatientId, ct);
			ViewBag.Unrecorded = await sessions.UnrecordedAsync(ctx, view.Row.Plan.PatientId, ct);
			ViewBag.SessionAddenda = new Dictionary<int, HospitalAddendaBlock>();
			foreach (var srow in (IReadOnlyList<HospitalDentalSessionRow>)ViewBag.Sessions)
				((Dictionary<int, HospitalAddendaBlock>)ViewBag.SessionAddenda)[srow.Session.ID] = await AddendaSvc.BlockAsync(ctx, CrossBuy.Models.Context.Hospital.HospitalAddendumSubject.DentalSession, srow.Session.ID, ct);
			return View(view);
		}

		// H180: the teeth an image shows
		public sealed class HospitalImageTeethInput { public string? Teeth { get; set; } }

		[HttpPost("Dental/Images/{id:int}/Teeth"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalImageTeeth(int id, [FromBody] HospitalImageTeethInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.SetImageTeethAsync(ctx, id, input.Teeth, ct));
		}

		// H179: the dentist signs the session record of the work done
		[HttpPost("Dental/Patient/{patientId:int}/Session"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult), HospitalIdempotent]
		public async Task<IActionResult> DentalSignSession(int patientId, [FromBody] HospitalDentalSessionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ActivatorUtilities.CreateInstance<HospitalDentalSessionService>(HttpContext.RequestServices).SignAsync(ctx, patientId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Plan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalCreatePlan([FromBody] HospitalDentalPlanInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.CreatePlanAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Plan/{id:int}/Phase"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalAddPhase(int id, [FromBody] HospitalDentalPhaseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.AddPhaseAsync(ctx, id, input.Title, input.TitleEn, ct));
		}

		[HttpPost("Dental/Line"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalAddLine([FromBody] HospitalDentalLineInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.AddLineAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// `read` at the door; the service asks `consult` or `bill` (the desk takes the patient's signature too).
		[HttpPost("Dental/Phase/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DentalDecidePhase(int id, [FromBody] HospitalDentalDecideInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.DecidePhaseAsync(ctx, id, input.Accept, input.Signatory, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Line/{id:int}/Done"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalLineDone(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.CompleteLineAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dental/Line/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> DentalLineCancel(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.CancelLineAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- setup ----------------

		/// A catalogue service's dental profile (the catalogue entry screen), or its removal.
		[HttpPost("Dental/Procedure"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DentalSaveProcedure([FromBody] HospitalDentalProcedureSaveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input?.Profile == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.SaveProcedureAsync(ctx, input.Profile, input.Remove, ct));
		}

		/// A payer's dental benefit (the payers screen).
		[HttpPost("Dental/Benefit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DentalSaveBenefit([FromBody] HospitalDentalBenefitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DentalSvc.SaveBenefitAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
