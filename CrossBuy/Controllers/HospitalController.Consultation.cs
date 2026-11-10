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
	// HOSPITAL — H5: the doctor's worklist and the consultation (SOAP note, vitals, diagnoses,
	// prescription, follow-up). Every rule is HospitalConsultationService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		[HttpGet("Worklist")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Worklist(int? doctor, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var doctors = await _outpatient.PractitionersAsync(ctx, ct);
			var today = ClinicToday();
			// Whose list: the one asked for, else the signed-in doctor's own, else the first doctor working today (H26: by his schedule).
			var sch = await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, doctors.Select(d => d.Row).ToList(), today, today, ct);
			var chosen = doctors.FirstOrDefault(d => d.Row.ID == doctor)
				?? doctors.FirstOrDefault(d => d.Row.EmployeeId != null && d.Row.EmployeeId == ctx.EmployeeId)
				?? doctors.FirstOrDefault(d => sch[d.Row.ID].WorksOn(today))
				?? doctors.FirstOrDefault();
			ViewBag.Doctors = doctors;
			ViewBag.Doctor = chosen;
			ViewBag.Today = today;
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			var rows = chosen == null ? new List<HospitalWorklistRow>() : (await _consult.WorklistAsync(ctx, _outpatient, chosen.Row.ID, today, ct)).ToList();
			return View(rows);
		}

		[HttpPost("Consult/Open/{appointmentId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> OpenConsultation(int appointmentId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _consult.OpenAsync(ctx, appointmentId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Consult/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Consult(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var enc = await _consult.GetAsync(ctx, id, ct);
			if (enc == null) return NotFound();
			var patient = await _patients.OpenAsync(ctx, enc.Encounter.PatientId, ctx.EmployeeId, ct);   // reading a visit opens the file: logged
			if (patient == null) return NotFound();
			Chrome();
			ViewBag.Patient = patient;
			ViewBag.Doctor = await _outpatient.PractitionerAsync(ctx, enc.Encounter.PractitionerId, ct);
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.CanEdit = enc.Encounter.Status == HospitalEncounterStatus.Draft && await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CodeCount = await _consult.CodeCountAsync(ctx, ct);
			ViewBag.Lab = await _lab.HistoryAsync(ctx, patient.ID, true, ct);
			ViewBag.Rad = await _rad.HistoryAsync(ctx, patient.ID, true, ct);
			ViewBag.Formulary = await _pharmacy.FormularyAsync(ctx, ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			// At a glance: the patient's earlier visits, newest first, without this one.
			ViewBag.History = (await _consult.PatientEncountersAsync(ctx, patient.ID, ct)).Where(e => e.Id != id).Take(5).ToList();
			ViewBag.Doctors = (await _outpatient.PractitionersAsync(ctx, ct)).ToDictionary(d => d.Row.ID);
			return View(enc);
		}

		[HttpPost("Consult/{id:int}/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SaveConsultation(int id, [FromBody] HospitalEncounterInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _consult.SaveDraftAsync(ctx, id, input ?? new(), ctx.EmployeeId, ct));
		}

		[HttpPost("Consult/{id:int}/Sign"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SignConsultation(int id, [FromBody] HospitalEncounterInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _consult.SignAsync(ctx, id, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// The diagnosis picker (select2, as the doctor types a code or a word).
		[HttpGet("DiagnosisCodes/Search")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SearchDiagnosisCodes(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var codes = await _consult.SearchCodesAsync(ctx, q, ct);
			return Json(new { ok = true, results = codes.Select(c => new { id = c.Code, text = c.Code + " — " + DisplayName.Of(c.Name, c.NameEn), name = DisplayName.Of(c.Name, c.NameEn) }) });
		}

		[HttpPost("DiagnosisCodes/SeedDefaults"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedDiagnosisCodes(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _consult.SeedDefaultCodesAsync(ctx, ct));
		}
	}
}
