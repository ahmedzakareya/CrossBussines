using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H3: accommodation. Admit a patient into a bed, transfer, discharge; the patient's
	// photo. Every rule is HospitalAdmissionService's; these endpoints resolve the caller and answer.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalTransferInput { public int BedId { get; set; } }

		/// The accommodation window's patient picker (select2, as the desk types).
		[HttpGet("Patients/Search")]
		[HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> SearchPatients(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var page = await _patients.ListAsync(ctx, q, 1, DateTime.UtcNow, ct);
			return Json(new
			{
				ok = true,
				// id + text keep every existing picker working; name, mrn, photo and initials let the shared select2 template
				// (crossbuy-person-option.js) show the patient by face.
				results = page.Rows.Select(p => new { id = p.Id, text = DisplayName.Of(p.Name, p.NameEn) + " — " + p.Mrn, name = DisplayName.Of(p.Name, p.NameEn), mrn = p.Mrn, photo = p.PhotoPath, initials = HospitalUi.Initials(p.NameEn ?? p.Name) }),
			});
		}

		/// One patient, as the accommodation window shows them, and every bed they may take now.
		[HttpGet("Admissions/Suitable")]
		[HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> SuitableBeds(int patientId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var p = await _patients.GetAsync(ctx, patientId, ct);
			if (p == null) return NotFound(new { ok = false, code = "not_found" });
			var current = await _admissions.CurrentAsync(ctx, p.ID, ct);
			var beds = await _admissions.SuitableBedsAsync(ctx, p.ID, ct);
			var currency = await _facility.CurrencyAsync(ctx, ct);
			bool ar = DisplayName.IsArabic;
			return Json(new
			{
				ok = true,
				patient = new
				{
					id = p.ID, mrn = p.Mrn, name = DisplayName.Of(p.Name, p.NameEn), nameEn = p.NameEn, sex = p.Sex, photo = p.PhotoPath,
					age = HospitalPatientService.AgeOn(p.BirthDate, DateTime.UtcNow), allergies = p.Allergies,
					admitted = current == null ? null : new { admissionId = current.Value.Admission.ID, room = current.Value.RoomNumber, bed = current.Value.BedCode },
				},
				currency = currency == null ? null : new { label = currency.Label(ar), decimals = currency.Decimals },
				beds = beds.Select(b => new
				{
					bedId = b.BedId, bed = b.BedCode, roomId = b.RoomId, room = b.RoomNumber,
					ward = b.WardName == null ? null : DisplayName.Of(b.WardName, b.WardNameEn),
					typeId = b.TypeId, type = b.TypeName == null ? null : DisplayName.Of(b.TypeName, b.TypeNameEn), rate = b.DailyRate,
				}),
			});
		}

		[HttpPost("Admissions/Admit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> Admit([FromBody] HospitalAdmitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _admissions.AdmitAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/{id:int}/Transfer"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> Transfer(int id, [FromBody] HospitalTransferInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _admissions.TransferAsync(ctx, id, input?.BedId ?? 0, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/{id:int}/Discharge"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> Discharge(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _admissions.DischargeAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		private HospitalPatientPhotoService PhotoSvc => Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<HospitalPatientPhotoService>(HttpContext.RequestServices);

		/// The patient's photo, read through the document platform (it authorises the reader on this patient).
		[HttpGet("Patient/{id:int}/Photo")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PatientPhotoImage(int id, long d, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return NotFound();
			var file = await PhotoSvc.OpenForStaffAsync(ctx, id, d, ct);
			if (file == null) return NotFound();
			Response.Headers["Cache-Control"] = "private, max-age=600";   // the URL changes with every new photo
			return File(file.Content, file.ContentType);
		}

		[HttpPost("Patient/{id:int}/Photo"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		[RequestSizeLimit(6_000_000)]
		public async Task<IActionResult> PatientPhoto(int id, IFormFile? photo, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return NotFound(new { ok = false, code = "not_found" });
			if (photo == null) return Json(new { ok = false, error = _t["The photo must be an image of up to 5 MB"].Value });
			// Ask before anything reaches disk: a refused caller or a foreign file never leaves an orphan.
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return Result(HospitalResult.Fail("forbidden"));
			if (await _patients.GetAsync(ctx, id, ct) == null) return NotFound(new { ok = false, code = "not_found" });
			// the document platform keeps the bytes (owner ruling: every image goes to the unified attachments)
			await using var bytes = photo.OpenReadStream();
			var (path, error) = await PhotoSvc.StoreAsync(ctx, id, bytes, photo.Length, ct);
			if (path == null) return error == "forbidden" ? Result(HospitalResult.Fail("forbidden")) : Json(new { ok = false, error = _t[error!].Value });
			var r = await _patients.SetPhotoAsync(ctx, id, path, ctx.EmployeeId, ct);
			return r.Ok ? Json(new { ok = true, id, photo = path }) : Result(r);
		}
	}
}
