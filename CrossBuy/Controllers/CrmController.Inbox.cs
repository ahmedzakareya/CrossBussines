using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// CRM C4 — the unified inbox (staff side) and its channels. The outside world (website chat, Meta's webhook) is in
	// HomeController.Inbox.cs on the public surface, and writes nothing but inbox rows.
	public partial class CrmController
	{
		private ICrmInboxService InboxSvc => _inboxSvc ??= ActivatorUtilities.CreateInstance<CrmInboxService>(HttpContext.RequestServices);
		private ICrmInboxService? _inboxSvc;

		private async Task<(bool ok, int cid, HashSet<int>? visible)> InboxScopeAsync()
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			return ok ? (true, cid, await _access.VisibleOwnerIdsAsync()) : (false, 0, null);
		}
		private JsonResult Refused() => Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
		private JsonResult Done(bool ok, string? err, object? extra = null) => Json(new { ok, error = ok ? null : CL[err ?? "Operation failed"].Value, extra });

		[HttpGet] public async Task<IActionResult> Inbox(int? id, string? view)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			await InboxSvc.LinkPendingAsync(cid);   // anything that arrived since the worker's last pass is matched now
			var (rows, counts) = await InboxSvc.InboxAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), view, null);
			ViewBag.Counts = counts; ViewBag.View = view ?? "all"; ViewBag.Selected = id ?? rows.FirstOrDefault()?.C.ID;
			ViewBag.Channels = await InboxSvc.ChannelsAsync(cid);
			ViewBag.CanEdit = await _access.CanAsync("edit");
			return View(rows);
		}

		[HttpGet] public async Task<IActionResult> InboxList(string? view, string? q)
		{
			var (ok, cid, visible) = await InboxScopeAsync();
			if (!ok) return Forbid();
			await InboxSvc.LinkPendingAsync(cid);
			var (rows, counts) = await InboxSvc.InboxAsync(cid, visible, _access.CurrentEmployeeId(), view, q);
			Response.Headers["X-Counts"] = System.Text.Json.JsonSerializer.Serialize(new { counts.All, counts.Mine, counts.Unassigned, counts.Unread, counts.ByChannel });
			return PartialView("_InboxList", rows);
		}

		[HttpGet] public async Task<IActionResult> InboxThread(int id)
		{
			var (ok, cid, visible) = await InboxScopeAsync();
			if (!ok) return Forbid();
			var t = await InboxSvc.ThreadAsync(cid, visible, id, markRead: true);
			if (t == null) return NotFound();
			ViewBag.CanEdit = await _access.CanAsync("edit");
			ViewBag.People = (await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && e.IsActive).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", false)).OrderBy(p => p.Name).ToList();
			return PartialView("_InboxThread", t);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxSend(int id, string body)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e) = await InboxSvc.SendAsync(cid, v, id, body, _access.CurrentEmployeeId()); return Done(d, e); }

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxTemplate(int id, string template)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e) = await InboxSvc.SendTemplateAsync(cid, v, id, template, _access.CurrentEmployeeId()); return Done(d, e); }

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxNote(int id, string body)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e) = await InboxSvc.NoteAsync(cid, v, id, body, _access.CurrentEmployeeId()); return Done(d, e); }

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxAssign(int id, int? owner)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e) = await InboxSvc.AssignAsync(cid, v, id, owner, _access.CurrentEmployeeId()); return Done(d, e); }

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxStatus(int id, string status)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e) = await InboxSvc.SetStatusAsync(cid, v, id, status, _access.CurrentEmployeeId()); return Done(d, e); }

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxCreateLead(int id)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e, lid) = await InboxSvc.CreateLeadAsync(cid, v, id, _access.CurrentEmployeeId()); return Done(d, e, lid); }

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> InboxCreateTicket(int id, string? subject)
		{ var (ok, cid, v) = await InboxScopeAsync(); if (!ok) return Refused(); var (d, e, tid) = await InboxSvc.CreateTicketAsync(cid, v, id, subject ?? "", _access.CurrentEmployeeId()); return Done(d, e, tid); }

		// ---------------- channels ----------------
		[HttpGet] public async Task<IActionResult> Channels()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(await InboxSvc.ChannelsAsync(cid));
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveChannel(int id, string kind, string name, bool isActive, string? greeting, string? phoneNumberId, string? displayPhone,
			string? templates, string? accessToken, string? appSecret)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e, newId) = await InboxSvc.SaveChannelAsync(cid, id, kind, name, isActive, greeting, phoneNumberId, displayPhone, templates, accessToken, appSecret);
			return Done(d, e, newId);
		}
	}
}
