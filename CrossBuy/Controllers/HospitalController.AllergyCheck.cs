using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.BL.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H269: the allergy check while the doctor writes the prescription. The consultation showed the recorded
	// allergy only as a banner, and nothing looked at the drug as it was chosen — the owner prescribed amoxicillin to a
	// patient allergic to penicillin and saw no warning. Each drug picked on the visit is checked here against the
	// visit's own patient: its written name, the stocked item's names in both languages, and the drug record's generic
	// names and class, all matched by family (HospitalAllergyMatch). A warning, never a stop.
	// ==========================================================================================
	public partial class HospitalController
	{
		[HttpGet("Consult/{id:int}/AllergyCheck")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConsultAllergyCheck(int id, string? drug, int? itemId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var patientId = await _db.HospitalEncounters.AsNoTracking().Where(e => e.ID == id && e.CompanyId == ctx.CompanyId).Select(e => (int?)e.PatientId).FirstOrDefaultAsync(ct);
			if (patientId == null) return NotFound(new { ok = false, code = "not_found" });
			var allergies = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == patientId && p.CompanyId == ctx.CompanyId).Select(p => p.Allergies).FirstOrDefaultAsync(ct);

			var words = new List<string?> { drug };
			if (itemId is int item && (await _pharmacy.MedicineItemIdsAsync(ctx, ct)).Contains(item))
			{
				var names = await _db.Items.AsNoTracking().Where(i => i.ID == item).Select(i => new { i.Name, i.NameEn }).FirstOrDefaultAsync(ct);
				words.Add(names?.Name); words.Add(names?.NameEn);
				var master = (await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, new[] { item }, ct)).GetValueOrDefault(item);
				words.Add(HospitalDrugRules.MatchText("", master)); words.Add(master?.TherapeuticClass);
			}
			var hits = HospitalAllergyMatch.Hits(string.Join(" ", words.Where(w => !string.IsNullOrWhiteSpace(w))), allergies);
			return Json(new { ok = true, hits });
		}
	}
}
