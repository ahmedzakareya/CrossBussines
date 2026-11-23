using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H98 — THE SPECIALTY CATALOGUE (the approved prototype screen sc-catalog): the specialties built on the
	// engine (their clinic lists), and every other specialty with what it records, its key alert, and — read from the
	// system, never claimed — whether it has a clinic, how many published templates and how many visits. Once the engine
	// exists, a specialty is templates and scores built in the designer; code goes only to the hand-drawn widgets.
	// ==========================================================================================
	public sealed record HospitalSpecialtyEntry(string Key, string Icon, string Dept, string[] TemplateCodes, string[] Items, string Alert, string Kind);
	public sealed record HospitalSpecialtyCard(HospitalSpecialtyEntry Entry, HospitalDepartment? Clinic, int Templates, int Visits);

	public static class HospitalSpecialtyCatalog
	{
		/// The built ones: their clinic list (and dentistry's chart).
		public static readonly IReadOnlyList<(string Icon, string Dept, string Action)> Built = new[]
		{
			("ki-emoji-happy", "DENT", "DentalChart"), ("ki-briefcase", "IM", "SpecialtyClinic"), ("ki-teddy-bear", "PED", "SpecialtyClinic"),
			("ki-eye", "OPH", "SpecialtyClinic"), ("ki-flask", "ALG", "SpecialtyClinic"),
		};

		/// The others, as the prototype describes them (resource keys: "spc:{key}:{n}" for the items, "spc:{key}:alert").
		public static readonly IReadOnlyList<HospitalSpecialtyEntry> Others = new[]
		{
			new HospitalSpecialtyEntry("card", "ki-heart", "CARD", new[] { "CARD-FU" }, new[] { "1", "2", "3", "4" }, "alert", "Template"),
			new HospitalSpecialtyEntry("obg", "ki-heart-circle", "OBG", Array.Empty<string>(), new[] { "1", "2", "3" }, "alert", "Built"),
			new HospitalSpecialtyEntry("derm", "ki-color-swatch", "DERM", Array.Empty<string>(), new[] { "1", "2", "3", "4" }, "alert", "Widget"),
			new HospitalSpecialtyEntry("ent", "ki-abstract-20", "ENT", Array.Empty<string>(), new[] { "1", "2", "3", "4" }, "alert", "Widget"),
			new HospitalSpecialtyEntry("orth", "ki-abstract-14", "ORTH", Array.Empty<string>(), new[] { "1", "2", "3", "4" }, "alert", "Template"),
			new HospitalSpecialtyEntry("psy", "ki-brain", "PSY", new[] { "PSY-SCR" }, new[] { "1", "2", "3", "4" }, "alert", "Template"),
			new HospitalSpecialtyEntry("endo", "ki-chart-line", "ENDO", new[] { "THY-FU", "BONE-DXA" }, new[] { "1", "2", "3", "4" }, "alert", "Template"),   // H189: thyroid (TI-RADS) and bone density
			// H268: the specialty pack (HospitalSpecialtyPack)
			new HospitalSpecialtyEntry("neuro", "ki-abstract-26", "NEURO", new[] { "NEURO-FU" }, new[] { "1", "2", "3" }, "alert", "Template"),
			new HospitalSpecialtyEntry("uro", "ki-drop", "URO", new[] { "URO-LUTS" }, new[] { "1", "2", "3" }, "alert", "Template"),
			new HospitalSpecialtyEntry("gi", "ki-abstract-39", "GI", new[] { "GI-FU" }, new[] { "1", "2", "3" }, "alert", "Template"),
			new HospitalSpecialtyEntry("pulm", "ki-wind", "PULM", new[] { "PULM-COPD" }, new[] { "1", "2", "3" }, "alert", "Template"),
			new HospitalSpecialtyEntry("rheum", "ki-abstract-41", "RHEUM", new[] { "RHEUM-RA" }, new[] { "1", "2" }, "alert", "Template"),
			new HospitalSpecialtyEntry("neph", "ki-filter-search", "NEPH", new[] { "NEPH-CKD" }, new[] { "1", "2", "3" }, "alert", "Template"),
			new HospitalSpecialtyEntry("surg", "ki-abstract-35", "SURG", new[] { "SURG-PO" }, new[] { "1", "2", "3" }, "alert", "Template"),
		};

		/// The cards, with their clinic, published templates (the clinic's, or the listed codes) and visits, read from the system.
		public static async Task<(IReadOnlyList<HospitalSpecialtyCard> Built, IReadOnlyList<HospitalSpecialtyCard> Others)> CardsAsync(CrossDbContext db, BusinessContext ctx, CancellationToken ct)
		{
			var deps = await db.Set<HospitalDepartment>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).ToListAsync(ct);
			List<(int? Dept, string Code)> pub;
			try
			{
				pub = (await (from t in db.Set<HospitalClinicalTemplate>().Where(t => t.CompanyId == ctx.CompanyId && t.IsActive)
							  join v in db.Set<HospitalClinicalTemplateVersion>().Where(v => v.Status == HospitalTemplateStatus.Published) on t.ID equals v.TemplateId
							  select new { t.DepartmentId, t.Code }).ToListAsync(ct)).Select(x => (x.DepartmentId, x.Code)).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { pub = new(); }
			var visits = await (from e in db.Set<HospitalEncounter>().Where(e => e.CompanyId == ctx.CompanyId)
								join p in db.Set<HospitalPractitioner>() on e.PractitionerId equals p.ID
								group e by p.DepartmentId into g
								select new { Dept = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Dept ?? 0, x => x.N, ct);
			HospitalSpecialtyCard Card(HospitalSpecialtyEntry e)
			{
				var dep = deps.FirstOrDefault(d => d.Code == e.Dept);
				int templates = pub.Count(x => (dep != null && x.Dept == dep.ID) || e.TemplateCodes.Contains(x.Code));
				return new HospitalSpecialtyCard(e, dep, templates, dep != null && visits.TryGetValue(dep.ID, out var n) ? n : 0);
			}
			var built = Built.Select(b => Card(new HospitalSpecialtyEntry(b.Dept, b.Icon, b.Dept, Array.Empty<string>(), Array.Empty<string>(), "", b.Action))).ToList();
			return (built, Others.Select(Card).ToList());
		}
	}
}
