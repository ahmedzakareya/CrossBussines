using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// CRM C9 — the assistant on the CRM screens: My day, the deal's assistant panel, the board's hints and the leads'
	// behavioural scores. Everything is computed from rules and the record; no model sits on these requests.
	public partial class CrmController
	{
		private ICrmAssistService AssistSvc => _assistSvc ??= ActivatorUtilities.CreateInstance<CrmAssistService>(HttpContext.RequestServices);
		private ICrmAssistService? _assistSvc;

		[HttpGet] public async Task<IActionResult> MyDay()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var me = _access.CurrentEmployeeId();
			// the scores this screen shows are recomputed for this rep's leads on the spot, not left to the worker's clock
			if (me is int m) await AssistSvc.RefreshLeadsAsync(cid, await _context.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.OwnerEmployeeId == m).Select(l => l.ID).ToListAsync());
			return View(await AssistSvc.MyDayAsync(cid, me, await _access.VisibleOwnerIdsAsync()));
		}

		/// The deal page's assistant panel: summary, next actions, draft reply, risks.
		[HttpGet] public async Task<IActionResult> DealAssist(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var file = await DealSvc.DealAsync(cid, await _access.VisibleOwnerIdsAsync(), id, _access.CurrentEmployeeId(), await _access.CanAsync("manage"));
			if (file == null) return NotFound();
			return PartialView("_DealAssist", await AssistSvc.DealAsync(cid, file, await RequirementLabelsAsync(cid, file.NextStageUnmet)));
		}

		/// For the board: each card's most urgent action and its worst risk.
		[HttpGet] public async Task<IActionResult> PipelineHints(string ids)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(300).ToList();
			var visible = await _access.VisibleOwnerIdsAsync();
			var allowed = await _context.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && list.Contains(o.ID) && (visible == null || (o.OwnerEmployeeId != null && visible.Contains(o.OwnerEmployeeId.Value)))).Select(o => o.ID).ToListAsync();
			var hints = await AssistSvc.PipelineHintsAsync(cid, allowed);
			// only what needs attention: a hint on every card is a hint nobody reads
			return Json(hints.Where(h => h.Value.top != null && (h.Value.urgency >= 60 || h.Value.risk == CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Severity.High)).ToDictionary(h => h.Key.ToString(), h => new
			{
				action = NbaText(h.Value.top!.Code, h.Value.top.Param),
				urgent = h.Value.urgency >= 80, risk = h.Value.risk?.ToString(),
			}));
		}

		/// One next-best-action in words, its number carrying a unit where it is a count of days.
		internal string NbaText(string code, string? param) => CL["nba:" + code].Value + (param == null ? "" : " — " + param + (code is "fixCloseDate" or "reconnect" ? " " + CL["days"].Value : ""));

		/// For the leads list: each lead's behavioural score, band and its top reasons, worded.
		[HttpGet] public async Task<IActionResult> LeadInsights(string ids)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(300).ToList();
			var visible = await _access.VisibleOwnerIdsAsync();
			var allowed = await _context.Leads.AsNoTracking().Where(l => l.CompanyID == cid && list.Contains(l.ID) && (visible == null || l.OwnerEmployeeId == null || visible.Contains(l.OwnerEmployeeId.Value))).Select(l => l.ID).ToListAsync();
			var ins = await AssistSvc.LeadInsightsAsync(cid, allowed);
			return Json(ins.ToDictionary(i => i.Key.ToString(), i => new
			{
				score = i.Value.Score, band = i.Value.Band, bandText = CL["After behaviour"].Value + ": " + CL["band:" + i.Value.Band].Value,
				why = i.Value.Reasons.Take(3).Select(r => (r.Points > 0 ? "+" : "") + r.Points + " " + CL["why:" + r.Code].Value + (r.Evidence != null && r.Code != "fit" ? " (" + r.Evidence + ")" : "")).ToList(),
			}));
		}
	}
}
