using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// CRM C8 — the service desk overview, the knowledge base, and what the ticket screen gains: suggested articles, the
	// public thread with the customer, and the satisfaction result.
	public partial class CrmController
	{
		private ICrmServiceDeskService DeskSvc => _deskSvc ??= ActivatorUtilities.CreateInstance<CrmServiceDeskService>(HttpContext.RequestServices);
		private ICrmServiceDeskService? _deskSvc;
		private string Origin => Request.Scheme + "://" + Request.Host;

		[HttpGet] public async Task<IActionResult> ServiceDesk()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(await DeskSvc.OverviewAsync(cid));
		}

		[HttpGet] public async Task<IActionResult> KnowledgeBase()
		{
			var (ok, _, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanEdit = await _access.CanAsync("edit");
			return View();
		}

		[HttpGet] public async Task<IActionResult> KnowledgeBaseData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await DeskSvc.KbListAsync(cid, q, status, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_KbRows", rows);
		}

		[HttpGet] public async Task<IActionResult> KbEditor(int id = 0)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var a = id > 0 ? await DeskSvc.KbAsync(cid, id) : null;
			if (id > 0 && a == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(KnowledgeBase)); }
			ViewBag.CanEdit = await _access.CanAsync("edit");
			return View(a);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> SaveKb(int id, string title, string? titleEn, string body, string? bodyEn, string? category, string? keywords, bool isPublic, string status)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e, newId) = await DeskSvc.SaveKbAsync(cid, _access.CurrentEmployeeId(), new Models.Context.Crm.CrmKbArticle
				{ ID = id, Title = title, TitleEn = titleEn, Body = body, BodyEn = bodyEn, Category = category, Keywords = keywords, IsPublic = isPublic, Status = status });
			return Done(d, e, newId);
		}

		/// The panel the ticket screen loads: the public thread, the articles that fit this ticket, and its rating.
		[HttpGet] public async Task<IActionResult> TicketDesk(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var t = await _context.CrmTickets.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == cid && x.ID == id);
			if (t == null) return NotFound();
			ViewBag.Ticket = t;
			ViewBag.Thread = await DeskSvc.ThreadAsync(cid, id);
			ViewBag.Suggested = await DeskSvc.SuggestAsync(cid, t.Subject + " " + t.SubjectEn + " " + t.Description);
			ViewBag.Survey = await DeskSvc.SurveyOfAsync(cid, id);
			ViewBag.CanEdit = await _access.CanAsync("edit");
			return PartialView("_TicketDesk");
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> TicketReply(int id, string? body, int? articleId)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await DeskSvc.ReplyAsync(cid, _access.CurrentEmployeeId(), id, body ?? "", articleId, Origin);
			return Done(d, e);
		}
	}
}
