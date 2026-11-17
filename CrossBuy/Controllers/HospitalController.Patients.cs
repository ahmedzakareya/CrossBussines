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
	// HOSPITAL — H2: the patient registry. Same controller as the facility (the controller count is
	// frozen), its own file.
	//
	// Screens: the registry (search, paging, the four counters), registration / edit with a live
	// duplicate check, and the patient's file — banner, allergy alert, details, the record-access
	// log, and the care team's conversation through the Communication Platform.
	//
	// Opening the file writes the access log (HospitalPatientService.OpenAsync); the page does not.
	// ==========================================================================================
	public partial class HospitalController
	{
		/// One country list for the form and the file. The table's nationality columns cannot be trusted
		/// by name: most rows hold the English word in NationalityName and the Arabic one in
		/// NationalityNameEn, but some (Kuwait) the other way round. So the Arabic label is whichever
		/// column is written in Arabic script, and the English one whichever is not.
		private async Task<List<(int Id, string Label)>> CountriesAsync(CancellationToken ct)
		{
			bool ar = DisplayName.IsArabic;
			var rows = await _db.CountriesLookup.AsNoTracking()
				.Select(c => new { c.ID, c.CountryName, c.CountryNameAr, c.NationalityName, c.NationalityNameEn }).ToListAsync(ct);
			static bool Arabic(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim()[0] is >= '؀' and <= 'ۿ';
			return rows.Select(c =>
			{
				string? arabic = Arabic(c.NationalityNameEn) ? c.NationalityNameEn : Arabic(c.NationalityName) ? c.NationalityName : null;
				string? english = !Arabic(c.NationalityName) ? c.NationalityName : !Arabic(c.NationalityNameEn) ? c.NationalityNameEn : null;
				return (c.ID, ar ? DisplayName.Or(arabic, c.CountryNameAr) : DisplayName.Or(english, c.CountryName));
			}).OrderBy(c => c.Item2).ToList();
		}

		[HttpGet("Patients")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Patients(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Query = q;
			ViewBag.CanRegister = await _access.CanAsync(ctx, HospitalActions.Register, null, ct);
			return View(await _patients.ListAsync(ctx, q, page, DateTime.UtcNow, ct));
		}

		[HttpGet("Patients/Register")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> RegisterPatient(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Countries = await CountriesAsync(ct);
			// A new file starts with the company's own country as the nationality.
			ViewBag.DefaultNationality = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => (int?)c.CountryID).FirstOrDefaultAsync(ct);
			ViewBag.NextMrn = await _patients.NextMrnAsync(ctx, ct);
			ViewBag.PriceLists = await ApprovedPriceListsAsync(ctx, ct);
			return View("PatientEdit", null);
		}

		[HttpGet("Patient/{id:int}/Edit")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> EditPatient(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var p = await _patients.GetAsync(ctx, id, ct);
			if (p == null) return NotFound();
			Chrome();
			ViewBag.Countries = await CountriesAsync(ct);
			ViewBag.PriceLists = await ApprovedPriceListsAsync(ctx, ct);
			return View("PatientEdit", p);
		}

		[HttpPost("Patients/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> SavePatient([FromBody] HospitalPatientInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _patients.SaveAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// The registration form's duplicate check, as the desk types.
		[HttpGet("Patients/Similar")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> SimilarPatients(string? nationalId, string? phone, string? name, string? nameEn, int? exclude, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var hits = await _patients.SimilarAsync(ctx, nationalId, phone, name, nameEn, exclude, ct);
			return Json(new { ok = true, items = hits.Select(h => new { id = h.Id, mrn = h.Mrn, name = DisplayName.Of(h.Name, h.NameEn), nationalId = h.NationalId, phone = h.Phone, reason = h.Reason, url = Url.Action("Patient", new { id = h.Id }) }) });
		}

		[HttpGet("Patient/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Patient(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			// H64: a restricted file opens only for its care team and the privileged — anyone else meets the gate (and
			// is not logged as having opened it); a break-glass opens it for its window
			var (fileAccess, glassUntil) = await RestrictedSvc.AccessAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!HospitalFileAccess.Allows(fileAccess))
			{
				var gated = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (gated == null) return NotFound();
				Chrome();
				return View("BreakGlassGate", gated);
			}
			ViewBag.FileAccess = fileAccess; ViewBag.GlassUntil = glassUntil;
			ViewBag.Restriction = await RestrictedSvc.RestrictionAsync(ctx, id, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanClinical = await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct);   // H155: reception reads the administrative file
			var p = await _patients.OpenAsync(ctx, id, ctx.EmployeeId, ct);   // records the open
			if (p == null)
			{
				// H57: a merged file opens the file it was merged into
				var into = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.MergedIntoPatientId != null).Select(x => x.MergedIntoPatientId).FirstOrDefaultAsync(ct);
				return into is int survivor ? RedirectToAction(nameof(Patient), new { id = survivor }) : NotFound();
			}
			Chrome();
			ViewBag.CanRegister = await _access.CanAsync(ctx, HospitalActions.Register, null, ct);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.Current = await _admissions.CurrentAsync(ctx, p.ID, ct);
			ViewBag.Now = (await WhereSvc.WhereAsync(ctx, new[] { p.ID }, DateTime.Now, ct)).GetValueOrDefault(p.ID);   // H114: where they are now
			ViewBag.Stays = await _admissions.StaysAsync(ctx, p.ID, ct);
			var (sick, sickReady) = await SickSvc.ListAsync(ctx, p.ID, ct);   // H56
			ViewBag.SickLeaves = sick; ViewBag.SickReady = sickReady;
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			var (consents, consentsReady) = await ConsentSvc.ListAsync(ctx, p.ID, ct);   // H60
			ViewBag.Consents = consents; ViewBag.ConsentsReady = consentsReady;
			ViewBag.Appointments = await _outpatient.PatientAppointmentsAsync(ctx, p.ID, ct);
			ViewBag.Visits = await _consult.PatientEncountersAsync(ctx, p.ID, ct);
			ViewBag.Lab = await _lab.HistoryAsync(ctx, p.ID, false, ct);
			ViewBag.Rad = await _rad.HistoryAsync(ctx, p.ID, false, ct);
			ViewBag.Bills = await _billing.BillsAsync(ctx, null, p.ID, DateTime.UtcNow, ct);
			// printing from the patient's file: the latest bill (invoice / receipt), the latest visit that prescribed
			// something (prescription) and the latest dental plan (claim form) — all Report Studio layouts
			ViewBag.PrintBillId = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.PatientId == p.ID
				&& _db.HospitalBillLines.Any(l => l.CompanyId == ctx.CompanyId && l.BillId == b.ID)).OrderByDescending(b => b.OpenedAt).Select(b => (int?)b.ID).FirstOrDefaultAsync(ct);
			ViewBag.PrintEncounterId = await _db.Set<HospitalEncounter>().AsNoTracking()
				.Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == p.ID && _db.Set<HospitalPrescriptionLine>().Any(l => l.CompanyId == ctx.CompanyId && l.EncounterId == e.ID))
				.OrderByDescending(e => e.StartedAt).Select(e => (int?)e.ID).FirstOrDefaultAsync(ct);
			ViewBag.PrintPlanId = await _db.Set<HospitalDentalPlan>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PatientId == p.ID)
				.OrderByDescending(x => x.CreatedAt).Select(x => (int?)x.ID).FirstOrDefaultAsync(ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			ViewBag.Doctors = (await _outpatient.PractitionersAsync(ctx, ct)).ToDictionary(d => d.Row.ID);
			ViewBag.Nationality = p.NationalityId is int n ? (await CountriesAsync(ct)).Where(c => c.Id == n).Select(c => c.Label).FirstOrDefault() : null;

			// The access log, with the person who opened the file — their name and their photo.
			var log = await _patients.AccessLogAsync(ctx, id, 50, ct);
			var ids = log.Where(a => a.EmployeeId is > 0).Select(a => a.EmployeeId!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			ViewBag.AccessLog = log;
			ViewBag.People = people.ToDictionary(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn));
			ViewBag.Photos = await EmployeePhotos.ResolveAsync(_db, ctx, ids, ct);

			return View(p);
		}

		// ---- the patient's conversation: hospital read + the file in the caller's company ----------

		private async Task<BusinessContext?> PatientGateAsync(int id, CancellationToken ct)
		{
			if (id <= 0) return null;
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return null;
			if (!await _access.CanAsync(ctx, HospitalActions.Read,
					CrossBuy.Models.Platform.PermissionTarget.ForEntity(EntityRegistry.HospitalPatient, id), ct)) return null;
			bool exists = await _db.HospitalPatients.AsNoTracking().AnyAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			return exists ? ctx : null;
		}

		[HttpGet("Patient/{id:int}/Conversation")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PatientConversation(int id, CancellationToken ct = default)
		{
			var ctx = await PatientGateAsync(id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationListAsync(ctx, EntityRegistry.HospitalPatient, id, ct);
		}

		[HttpPost("Patient/{id:int}/ConversationAdd"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		[RequestSizeLimit(21_000_000)]
		public async Task<IActionResult> PatientConversationAdd(int id, string? body, long? parentCommentId, IFormFile? file, CancellationToken ct = default)
		{
			var ctx = await PatientGateAsync(id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationAddAsync(ctx, EntityRegistry.HospitalPatient, id, body, parentCommentId, file, ct);
		}

		[HttpPost("Patient/{id:int}/ConversationReact"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PatientConversationReact(int id, long commentId, string? key, bool on, CancellationToken ct = default)
		{
			var ctx = await PatientGateAsync(id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationReactAsync(ctx, commentId, key, on, ct);
		}
	}
}
