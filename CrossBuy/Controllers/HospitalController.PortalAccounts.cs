using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H47: the desk's patient portal accounts (the prototype's #/portal-accounts). Staff open an account
	// from a patient's file and the patient sets the password from an emailed invitation; staff send reset links,
	// unlock, disable and enable — never seeing or setting a password. Every rule is the services' (`register`).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalPortalAccountsAdminService? _portalAdmin;
		private HospitalPortalAccountsAdminService PortalAdmin => _portalAdmin ??= ActivatorUtilities.CreateInstance<HospitalPortalAccountsAdminService>(HttpContext.RequestServices,
			ActivatorUtilities.CreateInstance<SmtpHospitalPortalMailer>(HttpContext.RequestServices));

		public sealed class HospitalPortalOpenInput
		{
			public int PatientId { get; set; } public string? Email { get; set; } public string? UserName { get; set; }
			public List<HospitalFamilyInput> Family { get; set; } = new();
		}
		public sealed class HospitalPortalInviteInput { public string? UserName { get; set; } }

		private string PortalLink(string token) => Url.Action("Reset", "PatientPortal", new { t = token }, Request.Scheme)!;

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("PortalAccounts")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccounts(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, ready) = await PortalAdmin.ListAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			return View(rows);
		}

		/// The open-account window's patient picker, for the desk that may open accounts (`register`): the same list the
		/// admission desk searches (HospitalPatientService.ListAsync), its photo shown.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("PortalAccounts/Patients")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountPatients(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var page = await _patients.ListAsync(ctx, q, 1, DateTime.UtcNow, ct);
			return Json(new { ok = true, results = page.Rows.Select(p => new { id = p.Id, text = DisplayName.Of(p.Name, p.NameEn) + " — " + p.Mrn }) });
		}

		/// Opens an account for a patient's file: the account, its email, the family it books for, then the invitation.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/Open"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountOpen([FromBody] HospitalPortalOpenInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			if (!HospitalPortalPasswordRules.LooksLikeEmail(input.Email)) return Result(HospitalResult.Fail("This is not an email address"));
			if (HospitalPortalAccountRules.UserNameRefusal(input.UserName) is string why) return Result(HospitalResult.Fail(why));
			var made = await PortalBookingSvc.CreateForPatientAsync(ctx, input.PatientId, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!made.Ok) return Result(made);
			int id = made.Id!.Value;
			var pwd = ActivatorUtilities.CreateInstance<HospitalPortalPasswordService>(HttpContext.RequestServices, ActivatorUtilities.CreateInstance<SmtpHospitalPortalMailer>(HttpContext.RequestServices));
			var email = await pwd.SetEmailAsync(ctx, id, input.Email, ct);
			if (!email.Ok) return Result(email);
			foreach (var f in input.Family.Where(f => f.PatientId > 0 && f.PatientId != input.PatientId))
				await PortalBookingSvc.LinkFamilyAsync(ctx, id, f.PatientId, f.Relation, ctx.EmployeeId, DateTime.UtcNow, ct);
			return Result(await PortalAdmin.InviteAsync(ctx, id, input.UserName, (t, u) => HospitalPortalMails.Invite(_t, PortalLink(t), u), DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Invite"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountInvite(int id, [FromBody] HospitalPortalInviteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalAdmin.InviteAsync(ctx, id, input?.UserName, (t, u) => HospitalPortalMails.Invite(_t, PortalLink(t), u), DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Reset"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountReset(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalAdmin.SendResetAsync(ctx, id, (t, u) => HospitalPortalMails.Reset(_t, PortalLink(t), u), DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Unlock"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountUnlock(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalAdmin.UnlockAsync(ctx, id, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Disable"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountDisable(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalAdmin.SetEnabledAsync(ctx, id, false, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("PortalAccounts/{id:int}/Enable"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> PortalAccountEnable(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalAdmin.SetEnabledAsync(ctx, id, true, ct));
		}
	}
}
