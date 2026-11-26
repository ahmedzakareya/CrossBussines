using System.Text.Json;
using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using J = CrossBuy.BL.CrmIdentity.CrmJourneyRules;

namespace CrossBuy.Controllers
{
	// CRM C7 — dynamic segments and marketing journeys.
	public partial class CrmController
	{
		private ICrmJourneyService JourneySvc => _journeySvc ??= ActivatorUtilities.CreateInstance<CrmJourneyService>(HttpContext.RequestServices);
		private ICrmJourneyService? _journeySvc;
		static readonly JsonSerializerOptions JourneyJs = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

		// ---------------- segments ----------------
		[HttpGet] public async Task<IActionResult> Segments()
		{
			var (ok, _, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View();
		}

		[HttpGet] public async Task<IActionResult> SegmentsData(string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await JourneySvc.SegmentsAsync(cid, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_SegmentRows", rows);
		}

		[HttpGet] public async Task<IActionResult> SegmentEditor(int id = 0)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var seg = id > 0 ? await JourneySvc.SegmentAsync(cid, id) : null;
			if (id > 0 && seg == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(Segments)); }
			ViewBag.CanManage = await _access.CanAsync("manage");
			ViewBag.Industries = await JourneySvc.IndustriesAsync(cid);
			ViewBag.People = (await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && e.IsActive).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", false)).OrderBy(p => p.Name).ToList();
			return View(seg);
		}

		List<J.Rule> RulesFrom(string? json) { try { return JsonSerializer.Deserialize<List<J.Rule>>(json ?? "[]", JourneyJs) ?? new(); } catch (JsonException) { return new(); } }

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SegmentPreview(string rulesJson, bool matchAll)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var rules = RulesFrom(rulesJson);
			var refusal = J.RulesRefusal(rules);
			var p = await JourneySvc.PreviewAsync(cid, rules, matchAll);
			return Json(new { ok = refusal == null, error = refusal == null ? null : CL[refusal].Value, count = p.Count, sample = p.Sample.Select(s => new { id = s.id, name = s.name, account = s.account }) });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveSegment(int id, string name, string? nameEn, bool matchAll, string rulesJson)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e, newId) = await JourneySvc.SaveSegmentAsync(cid, _access.CurrentEmployeeId(), id, name, nameEn, matchAll, RulesFrom(rulesJson));
			return Done(d, e, newId);
		}

		// ---------------- journeys ----------------
		[HttpGet] public async Task<IActionResult> Journeys()
		{
			var (ok, _, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View();
		}

		[HttpGet] public async Task<IActionResult> JourneysData(string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await JourneySvc.JourneysAsync(cid, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_JourneyRows", rows);
		}

		[HttpGet] public async Task<IActionResult> JourneyEditor(int id = 0, int? segment = null)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			if (!await _access.CanAsync("manage")) { this.ToastError(CL["You do not have permission to perform this action"].Value); return RedirectToAction(nameof(Journeys)); }
			var file = id > 0 ? await JourneySvc.JourneyAsync(cid, id) : null;
			if (id > 0 && file == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(Journeys)); }
			ViewBag.Segments = (await JourneySvc.SegmentsAsync(cid, null, 1, 100)).rows;
			ViewBag.Segment = segment;
			ViewBag.Templates = (await InboxSvc.ChannelsAsync(cid)).Where(c => c.Channel.Kind == "WhatsApp" && c.Channel.IsActive)
				.SelectMany(c => (c.Templates ?? "").Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToList();
			return View(file);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveJourney(int id, string name, string? nameEn, int segmentId, bool includeExisting, bool exitOnLeave, string nodesJson)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			List<J.Node> nodes;
			try { nodes = JsonSerializer.Deserialize<List<J.Node>>(nodesJson ?? "[]", JourneyJs) ?? new(); }
			catch (JsonException) { return Done(false, "Could not save. Refresh the page and try again."); }
			var (d, e, newId) = await JourneySvc.SaveJourneyAsync(cid, _access.CurrentEmployeeId(), id, name, nameEn, segmentId, includeExisting, exitOnLeave, nodes);
			return Done(d, e, newId);
		}

		[HttpGet] public async Task<IActionResult> Journey(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var file = await JourneySvc.JourneyAsync(cid, id);
			if (file == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(Journeys)); }
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(file);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> JourneyStatus(int id, string status)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await JourneySvc.SetStatusAsync(cid, id, status, Request.Scheme + "://" + Request.Host);
			return Done(d, e);
		}

		[HttpGet] public async Task<IActionResult> JourneyMembersData(int id, string? status, string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await JourneySvc.MembersAsync(cid, id, status, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_JourneyMemberRows", rows);
		}

		[HttpGet] public async Task<IActionResult> JourneyMemberEvents(int id)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			return PartialView("_JourneyEvents", await JourneySvc.MemberEventsAsync(cid, id));
		}
	}
}
