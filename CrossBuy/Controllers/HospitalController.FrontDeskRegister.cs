using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H142: registering a patient without leaving the desk — the window the approved prototype shows (quick or
	// full, the ID-card reader, the duplicate warning, the camera). The window only gathers: the file is saved by the
	// patient service's own Patients/Save (its rules: names in both languages, one file per ID number), the duplicate
	// check is its Patients/Similar, and the photo goes to the document platform through Patient/{id}/Photo.
	// What the desk had typed in the one search fills the window: digits become the ID or the mobile, letters the name.
	// ==========================================================================================
	public partial class HospitalController
	{
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Register")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskRegister(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			ViewBag.Countries = await CountriesAsync(ct);
			ViewBag.DefaultNationality = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => (int?)c.CountryID).FirstOrDefaultAsync(ct);
			ViewBag.NextMrn = await _patients.NextMrnAsync(ctx, ct);
			ViewBag.Today = DeskClock(DateTime.UtcNow).Today;
			ViewBag.CanClinical = await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct);   // H156
			return PartialView("_DeskRegister", HospitalDeskRegisterRules.Prefill(q));
		}
	}
}
