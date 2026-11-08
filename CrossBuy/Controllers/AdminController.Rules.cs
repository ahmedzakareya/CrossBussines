using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR — COUNTRY RULE PACK screen, in the owner-approved prototype design (design-output/hr-prototype, #/rules).
	// Reading needs the HR read right; saving needs organization-manage, and a save is always the COMPANY's copy
	// (created from the country template the first time), never the template every company shares.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record HrRulesPageModel(CrossBuy.Models.Context.Admin.HrRulePack Pack, CrossBuy.BL.Hr.HrRules Rules, bool IsCompanyCopy,
			IReadOnlyList<CrossBuy.Models.Context.Admin.HrRulePack> Templates, bool CanEdit);

		private CrossBuy.BL.Hr.HrRulePackService RulePacks => new(Db);

		[HttpGet]
		public async Task<IActionResult> HrRules(CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.Read);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var eff = await RulePacks.EffectiveAsync(gate.CompanyId, ct);
			if (eff is not { } e) { TempData["HrErr"] = L["No country rule pack is installed. Apply hr_rule_packs.sql."].Value; return RedirectToAction(nameof(Index)); }
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			bool canEdit = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.OrganizationManage, null);
			return View("HrRules", new HrRulesPageModel(e.Pack, e.Rules, e.IsCompanyCopy, await RulePacks.TemplatesAsync(ct), canEdit));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveHrRules([FromForm] CrossBuy.BL.Hr.HrRules rules, [FromForm] string countryCode, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.OrganizationManage);
			if (!gate.Ok) return HrDenied(nameof(HrRules));
			rules.Weekend = (rules.Weekend ?? new()).Where(d => Enum.TryParse<DayOfWeek>(d, out _)).Distinct().ToList();
			var (ok, err) = await RulePacks.SaveAsync(gate.CompanyId, string.IsNullOrWhiteSpace(countryCode) ? "KW" : countryCode, rules, gate.EmployeeId, ct);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Rules saved"].Value : L[err ?? "The rules could not be saved"].Value;
			return RedirectToAction(nameof(HrRules));
		}

		// START FROM A COUNTRY TEMPLATE: the company's copy is replaced by the chosen template's rules (a new version,
		// so the change is visible and the previous figures are not silently kept). The template itself never changes.
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> ApplyHrTemplate([FromForm] string countryCode, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.OrganizationManage);
			if (!gate.Ok) return HrDenied(nameof(HrRules));
			var template = (await RulePacks.TemplatesAsync(ct)).FirstOrDefault(t => t.CountryCode == countryCode);
			if (template == null) { TempData["HrErr"] = L["Unknown country template"].Value; return RedirectToAction(nameof(HrRules)); }
			var (ok, err) = await RulePacks.ApplyTemplateAsync(gate.CompanyId, template, gate.EmployeeId, ct);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Rules saved"].Value : L[err ?? "The rules could not be saved"].Value;
			return RedirectToAction(nameof(HrRules));
		}
	}
}
