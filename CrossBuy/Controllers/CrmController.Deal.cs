using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// CRM C3 — the deal workspace (stage path, buying committee, products and discount approval, next step) and its
	// settings (what each stage requires, the discount each role may give). The stage GATE itself is asked from the
	// two existing doors into a stage, MoveOpportunity and SaveOpportunity in CrmController.cs, through StageGateAsync.
	public partial class CrmController
	{
		private ICrmDealService DealSvc => _dealSvc ??= ActivatorUtilities.CreateInstance<CrmDealService>(HttpContext.RequestServices);
		private ICrmDealService? _dealSvc;

		/// The requirement words, localized, for one deal — a custom field is named by its own label.
		private async Task<List<string>> RequirementLabelsAsync(int cid, IEnumerable<CrmDealRules.Requirement> reqs)
		{
			var list = reqs.ToList();
			var ids = list.Where(r => r.FieldId != null).Select(r => r.FieldId!.Value).ToList();
			var labels = await _context.CrmCustomFields.AsNoTracking().Where(f => f.CompanyID == cid && ids.Contains(f.ID))
				.ToDictionaryAsync(f => f.ID, f => !IsAr && !string.IsNullOrWhiteSpace(f.LabelEn) ? f.LabelEn! : f.Label);
			return list.Select(r => r.Kind == "CustomField" && r.FieldId is int f && labels.TryGetValue(f, out var l) ? l : CL["req:" + r.Kind].Value).ToList();
		}

		/// What stops this move, as one sentence and its list, or null when the deal may go into that stage.
		/// FAILS OPEN, on purpose and only here: a stage requirement is a data-completeness rule, so a fault in the CHECK
		/// itself is logged and the move goes ahead — it must never turn the board, which worked before C3, into an
		/// outage. The discount control (QuotationRefusalAsync) is financial and stays closed on any fault.
		internal async Task<(string message, List<string> missing)?> StageGateAsync(int cid, int oppId, string? stageName, int? stageId, int? pipelineId)
		{
			IReadOnlyList<CrmDealRules.Requirement> unmet;
			try { unmet = await DealSvc.GateAsync(cid, oppId, stageName, stageId, pipelineId); }
			catch (Exception ex)
			{
				HttpContext.RequestServices.GetRequiredService<ILogger<CrmController>>().LogError(ex, "CRM stage-requirement check failed for opportunity {Id}; the move was allowed", oppId);
				return null;
			}
			if (unmet.Count == 0) return null;
			var missing = await RequirementLabelsAsync(cid, unmet);
			return (CL["Before this stage the deal still needs:"].Value + " " + string.Join("، ", missing), missing);
		}

		// ---------------- the workspace ----------------
		[HttpGet] public async Task<IActionResult> Deal(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var file = await DealSvc.DealAsync(cid, await _access.VisibleOwnerIdsAsync(), id, _access.CurrentEmployeeId(), await _access.CanAsync("manage"));
			if (file == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(Opportunities)); }
			ViewBag.CanEdit = await _access.CanAsync("edit");
			ViewBag.Unmet = await RequirementLabelsAsync(cid, file.NextStageUnmet);
			ViewBag.Needs = await RequirementLabelsAsync(cid, file.NextStageNeeds);
			ViewBag.MyId = _access.CurrentEmployeeId();
			var cur = await (from co in _context.Companies.AsNoTracking() where co.CompanyID == cid
							 join c in _context.Currencies.AsNoTracking() on co.DefaultCurrencyId equals (int?)c.ID
							 select new { c.Code, c.DecimalPlaces }).FirstOrDefaultAsync();
			ViewBag.CurCode = cur?.Code ?? ""; ViewBag.CurDec = (int)(cur?.DecimalPlaces ?? 2);
			return View(file);
		}

		/// A move made from the workspace's stage path: the same gate, the same transition and automation as the board.
		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DealMove(int id, int stageId)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var stage = await _context.CrmPipelineStages.AsNoTracking().Where(s => s.CompanyID == cid && s.ID == stageId).Select(s => s.Name).FirstOrDefaultAsync();
			if (stage == null) return Json(new { ok = false, error = CL["Record not found"].Value });
			if (await StageGateAsync(cid, id, stage, stageId, null) is { } gate) return Json(new { ok = false, code = "gate", error = gate.message, missing = gate.missing });
			var (done, err) = await _crm.UpdateOpportunityStageAsync(cid, id, stage);
			if (done)
			{
				var owner = await _context.Opportunities.Where(o => o.CompanyID == cid && o.ID == id).Select(o => o.OwnerEmployeeId).FirstOrDefaultAsync();
				await AutoSvc.RunAsync(cid, "OpportunityStageChanged", stage, null, id, owner);
			}
			return Json(new { ok = done, error = done ? null : Say(err) });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DealCommitteeAdd(int id, int contactId, string role)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.AddToCommitteeAsync(cid, await _access.VisibleOwnerIdsAsync(), id, contactId, role, _access.CurrentEmployeeId());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DealCommitteeRemove(int linkId)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.RemoveFromCommitteeAsync(cid, await _access.VisibleOwnerIdsAsync(), linkId);
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		// ---------------- discount approval ----------------
		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DealDiscountRequest(int id, string reason)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.RequestDiscountAsync(cid, await _access.VisibleOwnerIdsAsync(), id, reason, _access.CurrentEmployeeId());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		/// Deciding is guarded twice: CRM "edit" here, and in the service the rule that the decider is not the requester
		/// and that their own limit (or a manager's standing) covers the discount.
		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DealDiscountDecide(int approvalId, bool approve, string? note)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.DecideDiscountAsync(cid, approvalId, approve, note, _access.CurrentEmployeeId(), await _access.CanAsync("manage"));
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DealDiscountWithdraw(int approvalId)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.WithdrawDiscountAsync(cid, approvalId, _access.CurrentEmployeeId());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		[HttpGet] public async Task<IActionResult> DiscountApprovals()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var cur = await (from co in _context.Companies.AsNoTracking() where co.CompanyID == cid
							 join c in _context.Currencies.AsNoTracking() on co.DefaultCurrencyId equals (int?)c.ID
							 select new { c.Code, c.DecimalPlaces }).FirstOrDefaultAsync();
			ViewBag.CurCode = cur?.Code ?? ""; ViewBag.CurDec = (int)(cur?.DecimalPlaces ?? 2);
			return View(await DealSvc.PendingForMeAsync(cid, _access.CurrentEmployeeId(), await _access.CanAsync("manage")));
		}

		// ---------------- settings ----------------
		[HttpGet] public async Task<IActionResult> DealRules()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(await DealSvc.SettingsAsync(cid));
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveStageRequirements(int stageId, string[]? kinds, int[]? fieldIds)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.SaveStageRequirementsAsync(cid, stageId, kinds ?? Array.Empty<string>(), fieldIds ?? Array.Empty<int>());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveDiscountLimits(decimal? salesRep, decimal? salesManager, decimal? marketing, decimal? crmViewer)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await DealSvc.SaveLimitsAsync(cid, new Dictionary<string, decimal?> { ["SalesRep"] = salesRep, ["SalesManager"] = salesManager, ["Marketing"] = marketing, ["CrmViewer"] = crmViewer });
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}
	}
}
