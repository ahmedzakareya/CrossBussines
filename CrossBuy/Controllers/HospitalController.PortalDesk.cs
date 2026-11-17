using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HOSPITAL H24 — the call centre's side of the patient portal: the requests to book or decline, and matching an
	// unverified account to a patient file.
	public partial class HospitalController
	{
		public sealed class HospitalDeclineInput { public string? Reason { get; set; } }
		public sealed class HospitalVerifyInput { public int PatientId { get; set; } }

		private HospitalPortalBookingService? _portalBookingSvc;
		private HospitalPortalBookingService PortalBookingSvc => _portalBookingSvc ??= ActivatorUtilities.CreateInstance<HospitalPortalBookingService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("PortalRequests")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalRequests(string? status, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var st = HospitalPortalRequestStatus.All.Contains(status ?? "") ? status! : HospitalPortalRequestStatus.Open;
			ViewBag.Status = st;
			ViewBag.CanRegister = await _access.CanAsync(ctx, HospitalActions.Register, null, ct);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive).OrderBy(p => p.Name)
				.Select(p => new { p.ID, p.Name, p.NameEn, p.DepartmentId }).ToListAsync(ct);
			ViewBag.Doctors = docs.Select(p => (p.ID, p.Name, p.NameEn, p.DepartmentId)).ToList();
			ViewBag.OpenCount = (await PortalBookingSvc.RequestsAsync(ctx, null, ct)).Count;
			return View(await PortalBookingSvc.RequestsAsync(ctx, st, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("PortalRequests/Slots")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalRequestSlots(int practitionerId, DateTime date, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var slots = await PortalBookingSvc.StaffFreeSlotsAsync(ctx, practitionerId, date, DateTime.Now, ct);
			return Json(new { ok = true, slots = slots.Select(m => new { minute = m, label = (m / 60).ToString("00") + ":" + (m % 60).ToString("00") }) });
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalRequests/{id:int}/Book"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalRequestBook(int id, [FromBody] HospitalRequestBookInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalBookingSvc.BookRequestAsync(ctx, _outpatient, id, input, ctx.EmployeeId, DateTime.Today, DateTime.UtcNow, ct, PracticeSvc));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalRequests/{id:int}/Decline"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalRequestDecline(int id, [FromBody] HospitalDeclineInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalBookingSvc.DeclineRequestAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		public sealed class HospitalFamilyInput { public int PatientId { get; set; } public string? Relation { get; set; } }

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/ForPatient/{patientId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountForPatient(int patientId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalBookingSvc.CreateForPatientAsync(ctx, patientId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Family"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountFamily(int id, [FromBody] HospitalFamilyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalBookingSvc.LinkFamilyAsync(ctx, id, input.PatientId, input.Relation, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		public sealed class HospitalSignInInput { public string? UserName { get; set; } public string? Password { get; set; } }

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/SignIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountSignIn(int id, [FromBody] HospitalSignInInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var svc = ActivatorUtilities.CreateInstance<HospitalPortalIdentityService>(HttpContext.RequestServices);
			return Result(await svc.CreateSignInAsync(ctx, id, input.UserName, input.Password, ct));
		}

		public sealed class HospitalPortalEmailInput { public string? Email { get; set; } }

		/// H46: the address a patient's portal emails go to (a password-reset link, the notice of a change).
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Email"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountEmail(int id, [FromBody] HospitalPortalEmailInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var svc = ActivatorUtilities.CreateInstance<HospitalPortalPasswordService>(HttpContext.RequestServices,
				ActivatorUtilities.CreateInstance<SmtpHospitalPortalMailer>(HttpContext.RequestServices));
			return Result(await svc.SetEmailAsync(ctx, id, input.Email, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Verify"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountVerify(int id, [FromBody] HospitalVerifyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalBookingSvc.VerifyAccountAsync(ctx, id, input.PatientId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
