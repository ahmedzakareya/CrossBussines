using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H60: informed consent with an electronic signature. The forms are the administrator's (`manage`);
	// a clinician (`consult`) takes a consent on its own signing page (a tablet-sized pad); the patient's file lists
	// them, shows the signature, prints through Report Studio (Hospital.Consent) and withdraws with a reason.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalConsentService? _consentSvc;
		private HospitalConsentService ConsentSvc => _consentSvc ??= ActivatorUtilities.CreateInstance<HospitalConsentService>(HttpContext.RequestServices);

		// ---------------- forms (settings) ----------------

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Settings/Consents")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ConsentForms(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (forms, ready) = await ConsentSvc.FormsAsync(ctx, false, ct);
			ViewBag.Ready = ready;
			return View(forms);
		}

		/// The forms list (cbServerList): every search term matches the code or a title, a kind narrows it; X-Total/X-Page/X-Pages.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Settings/Consents/Data")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ConsentFormsData(string? q, string? kind, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			var (forms, _) = await ConsentSvc.FormsAsync(ctx, false, ct);
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var hit = forms.Where(f => (string.IsNullOrEmpty(kind) || f.Kind == kind)
				&& terms.All(t => f.Code.Contains(t, StringComparison.OrdinalIgnoreCase) || f.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || f.TitleEn.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
			int total = hit.Count, pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
			page = Math.Clamp(page, 1, pages);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = pages.ToString();
			return PartialView("_ConsentFormRows", hit.Skip((page - 1) * pageSize).Take(pageSize).ToList());
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Settings/Consents/Forms"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveConsentForm(int? id, [FromBody] HospitalConsentFormInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsentSvc.SaveFormAsync(ctx, id is > 0 ? id : null, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Settings/Consents/Standard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AddStandardConsentForms(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await ConsentSvc.AddStandardAsync(ctx, ctx.EmployeeId, DateTime.UtcNow, ct);
			return r.Ok ? Json(new { ok = true, added = r.Args?.FirstOrDefault() ?? 0 }) : Result(r);
		}

		// ---------------- signing ----------------

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Patient/{id:int}/Consents/New")]
		[HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> NewConsent(int id, int? surgeryId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.MergedIntoPatientId == null, ct);
			if (p == null) return NotFound();
			Chrome();
			var (forms, ready) = await ConsentSvc.FormsAsync(ctx, true, ct);
			ViewBag.Ready = ready;
			ViewBag.Forms = forms;
			ViewBag.Cases = ready ? await ConsentSvc.CasesAsync(ctx, id, ct) : new List<HospitalConsentCase>();
			ViewBag.SurgeryId = surgeryId;
			ViewBag.Age = HospitalPatientService.AgeOn(p.BirthDate, DateTime.UtcNow);
			// the clinician who explains is the signed-in employee: their practitioner name when they have one (the text's {doctor})
			var doc = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.EmployeeId == ctx.EmployeeId && x.IsActive).Select(x => new { x.Name, x.NameEn }).FirstOrDefaultAsync(ct);
			var me = await _db.Employee.AsNoTracking().Where(e => e.ID == ctx.EmployeeId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			ViewBag.Doctor = doc?.Name ?? me?.FullName ?? "";
			ViewBag.DoctorEn = doc?.NameEn ?? me?.FullNameEn ?? me?.FullName ?? "";
			var staff = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive && e.ID != ctx.EmployeeId)
				.OrderBy(e => e.FullName).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, staff.Select(s => s.ID).ToList(), ct);
			ViewBag.Staff = staff.Select(s => (s.ID, Name: s.FullName ?? "", NameEn: s.FullNameEn ?? s.FullName ?? "", Photo: photos.GetValueOrDefault(s.ID))).ToList();
			return View("ConsentSign", p);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Patient/{id:int}/Consents"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		[RequestSizeLimit(1024 * 1024)]
		public async Task<IActionResult> SignConsent(int id, [FromBody] HospitalConsentInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsentSvc.SignAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Consents/{id:int}/Withdraw"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> WithdrawConsent(int id, [FromBody] HospitalVoidInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ConsentSvc.WithdrawAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// The signature picture (never cached by the browser: it is a clinical record).
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Consents/{id:int}/Signature")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConsentSignature(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var f = await ConsentSvc.SignatureAsync(ctx, id, ct);
			if (f == null) return NotFound();
			Response.Headers.CacheControl = "no-store";
			return File(f.Content, f.ContentType);
		}

		/// A consent's own address (the entity registry's route): its patient's file, on the consents tab.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Consents/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Consent(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var pid = await _db.Set<HospitalConsent>().AsNoTracking().Where(c => c.ID == id && c.CompanyId == ctx.CompanyId).Select(c => (int?)c.PatientId).FirstOrDefaultAsync(ct);
			return pid is int p ? Redirect(Url.Action(nameof(Patient), new { id = p }) + "#pfConsents") : NotFound();
		}
	}
}
