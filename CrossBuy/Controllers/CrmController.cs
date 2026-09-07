using CrossBuy.BL;
using CrossBuy.ViewModel.Ai;
using Rules = CrossBuy.BL.Platform.Ai.CrmAccountHealthRules;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// CRM dashboard view model (mirrors the Inventory dashboard richness)
	public class CrmDashboardDto
	{
		public List<PipelineStage> Pipeline { get; set; } = new();
		public List<CrmMonthPoint> Months { get; set; } = new();
		public List<CrmTopAccount> TopAccounts { get; set; } = new();
		public List<CrmRecentLead> RecentLeads { get; set; } = new();
		public int LeadCount, OpenOppCount, WonCount, OpenActivities, OverdueActivities, OpenTickets, AccountCount, ContactCount, ActiveCampaigns;
		public decimal OpenValue, WeightedValue, WonValue, WinRate;
	}
	public class CrmMonthPoint { public int Year, Month; public decimal WonValue, CreatedValue; }
	public class CrmTopAccount { public string Name { get; set; } = ""; public decimal Value; public int Pct; }
	public class CrmRecentLead { public string Name { get; set; } = ""; public string Status { get; set; } = ""; public int Score; public DateTime? CreatedAt; }
	// public DTO for the CrmRoles ViewBag (anonymous types crash dynamic binding in the runtime-compiled view)
	public class CrmRoleRow { public int ID { get; set; } public int EmployeeId { get; set; } public string EmployeeName { get; set; } = ""; public string Role { get; set; } = ""; }

	[SessionValidation]
	public class CrmController : Controller
	{
		private readonly ICrmService _crm;
		private readonly CrossDbContext _context;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private readonly ICrmAccessService _access;
		public CrmController(ICrmService crm, CrossDbContext context, CrossBuy.BL.Platform.IRequestCompanyResolver company, ICrmAccessService access, IStringLocalizer<CrossBuy.SharedResources> localizer) { _crm = crm; _context = context; _company = company; _access = access; L = localizer; }

		// ---------------- CRM custom fields (3-7b-i) ----------------
		// ---------------- Fail-closed company resolution ----------------
		//
		// Every CRM path in this controller used to read `DefaultCompanyId = 1`. In an installation
		// with fourteen companies that is not a default, it is a hardcoded tenant: Lead, Opportunity
		// and CrmAccount carry a global query filter, so companies 2..14 saw an EMPTY grid and their
		// writes were refused by CompanyWriteGuardInterceptor — while the seventeen CRM tables that
		// carry no filter took the write and stored it under company 1.
		//
		// This resolves the caller's company from the session-backed BusinessContext and REFUSES the
		// request when it cannot. It never falls back to a constant, never reads a company off the
		// query string or form, and answers every failure with the same Forbid() — so "your company
		// could not be resolved" and "that row belongs to another company" are indistinguishable from
		// outside, which is what stops the error itself becoming an existence oracle.
		private async Task<(bool ok, int cid, IActionResult deny)> ResolveCompanyAsync()
		{
			var scope = await _company.ResolveAsync();
			if (scope.Ok && scope.CompanyId > 0) return (true, scope.CompanyId, null!);
			return (false, 0, Forbid());
		}

		private ICrmCustomFieldService CfSvc => (HttpContext.RequestServices.GetService(typeof(ICrmCustomFieldService)) as ICrmCustomFieldService)!;

		private static Dictionary<int, string?> ParseCustomFields(string? json)
		{
			var d = new Dictionary<int, string?>();
			if (string.IsNullOrWhiteSpace(json)) return d;
			try
			{
				using var doc = System.Text.Json.JsonDocument.Parse(json);
				foreach (var el in doc.RootElement.EnumerateArray())
				{
					int fid = el.GetProperty("fieldId").GetInt32();
					string? val = el.TryGetProperty("value", out var v) ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString()) : null;
					d[fid] = val;
				}
			}
			catch { }
			return d;
		}

		[HttpGet] public async Task<IActionResult> CustomFields(string? entityType)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.EntityType = string.IsNullOrWhiteSpace(entityType) ? "Lead" : entityType;
			return View(await CfSvc.GetFieldsAsync(cid, null, false));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SaveCustomField(int id, string entityType, string label, string? labelEn, string fieldType, string? options, string? optionsEn, bool required, int sortOrder, bool isActive)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, _) = await CfSvc.SaveFieldAsync(new Models.Context.Crm.CrmCustomField { ID = id, CompanyID = cid, EntityType = entityType, Label = label ?? "", LabelEn = labelEn, FieldType = fieldType, Options = options, OptionsEn = optionsEn, Required = required, SortOrder = sortOrder, IsActive = isActive });
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Custom field saved"].Value : err;
			return RedirectToAction(nameof(CustomFields), new { entityType });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> DeleteCustomField(int id, string entityType)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await CfSvc.DeleteFieldAsync(cid, id);
			TempData["CrmMsg"] = L["Field deleted"].Value;
			return RedirectToAction(nameof(CustomFields), new { entityType });
		}

		[HttpGet] public async Task<IActionResult> EntityCustomValues(string entityType, int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			return Json((await CfSvc.GetForEntityAsync(cid, entityType, id)).Select(x => new { fieldId = x.Field.ID, value = x.Value }));
		}

		// ---------------- CRM automation rules (3-7b-ii) ----------------
		private ICrmAutomationService AutoSvc => (HttpContext.RequestServices.GetService(typeof(ICrmAutomationService)) as ICrmAutomationService)!;

		[HttpGet] public async Task<IActionResult> AutomationRules()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			return View(await AutoSvc.GetRulesAsync(cid));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SaveAutomationRule(int id, string name, string? nameEn, string triggerType, string? stageFilter, string actionType, string? activityType, string? subject, string? subjectEn, int dueInDays, string? notifyTitle, string? notifyBody, bool isActive, int sortOrder)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, _) = await AutoSvc.SaveRuleAsync(new Models.Context.Crm.CrmAutomationRule { ID = id, CompanyID = cid, Name = name ?? "", NameEn = nameEn, TriggerType = triggerType, StageFilter = stageFilter, ActionType = actionType, ActivityType = activityType, Subject = subject, SubjectEn = subjectEn, DueInDays = dueInDays, NotifyTitle = notifyTitle, NotifyBody = notifyBody, IsActive = isActive, SortOrder = sortOrder });
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Automation rule saved"].Value : err;
			return RedirectToAction(nameof(AutomationRules));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> DeleteAutomationRule(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await AutoSvc.DeleteRuleAsync(cid, id);
			TempData["CrmMsg"] = L["Rule deleted"].Value;
			return RedirectToAction(nameof(AutomationRules));
		}

		private void SetPaging(int total, int page, int pageSize)
		{
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
		}

		[HttpGet] public async Task<IActionResult> Index()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var dto = new CrmDashboardDto();
			dto.Pipeline = await _crm.PipelineSummaryAsync(cid);

			var opps = await _context.Opportunities.AsNoTracking()
				.Where(o => o.CompanyID == cid)
				.Select(o => new { o.Stage, o.Amount, o.Probability, o.AccountId, o.CreatedAt })
				.ToListAsync();
			bool IsOpen(string s) => s != "Won" && s != "Lost";
			dto.OpenOppCount = opps.Count(o => IsOpen(o.Stage));
			dto.OpenValue = opps.Where(o => IsOpen(o.Stage)).Sum(o => o.Amount);
			dto.WeightedValue = opps.Where(o => IsOpen(o.Stage)).Sum(o => o.Amount * o.Probability / 100m);
			int wonCount = opps.Count(o => o.Stage == "Won"), lostCount = opps.Count(o => o.Stage == "Lost");
			dto.WonCount = wonCount;
			dto.WonValue = opps.Where(o => o.Stage == "Won").Sum(o => o.Amount);
			dto.WinRate = (wonCount + lostCount) > 0 ? Math.Round(100m * wonCount / (wonCount + lostCount), 0) : 0;

			// last 6 months trend (created value vs won value) by CreatedAt
			var now = DateTime.Today;
			var start = new DateTime(now.Year, now.Month, 1).AddMonths(-5);
			for (int i = 0; i < 6; i++)
			{
				var mStart = start.AddMonths(i); var mEnd = mStart.AddMonths(1);
				var inMonth = opps.Where(o => o.CreatedAt.HasValue && o.CreatedAt >= mStart && o.CreatedAt < mEnd).ToList();
				dto.Months.Add(new CrmMonthPoint { Year = mStart.Year, Month = mStart.Month,
					WonValue = inMonth.Where(o => o.Stage == "Won").Sum(o => o.Amount), CreatedValue = inMonth.Sum(o => o.Amount) });
			}

			// top accounts by open opportunity value
			var topAcc = opps.Where(o => IsOpen(o.Stage) && o.AccountId.HasValue)
				.GroupBy(o => o.AccountId!.Value)
				.Select(g => new { AccountId = g.Key, Value = g.Sum(x => x.Amount) })
				.OrderByDescending(x => x.Value).Take(5).ToList();
			var accIds = topAcc.Select(t => t.AccountId).ToList();
			var accNames = await _context.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID))
				.Select(a => new { a.ID, a.Name, a.NameEn }).ToListAsync();
			decimal maxAcc = topAcc.Count > 0 ? topAcc.Max(x => x.Value) : 0;
			foreach (var t in topAcc)
			{
				var an = accNames.FirstOrDefault(a => a.ID == t.AccountId);
				dto.TopAccounts.Add(new CrmTopAccount {
					Name = an == null ? ("#" + t.AccountId) : ((isEn && !string.IsNullOrWhiteSpace(an.NameEn)) ? an.NameEn! : an.Name),
					Value = t.Value, Pct = maxAcc > 0 ? (int)Math.Round(100m * t.Value / maxAcc) : 0 });
			}

			dto.LeadCount = await _context.Leads.CountAsync(l => l.CompanyID == cid && l.Status != "Converted" && l.Status != "Lost");
			dto.RecentLeads = (await _context.Leads.AsNoTracking().Where(l => l.CompanyID == cid)
				.OrderByDescending(l => l.ID).Take(6)
				.Select(l => new { l.Name, l.NameEn, l.Status, l.Score, l.CreatedAt }).ToListAsync())
				.Select(l => new CrmRecentLead { Name = (isEn && !string.IsNullOrWhiteSpace(l.NameEn)) ? l.NameEn! : l.Name, Status = l.Status, Score = l.Score, CreatedAt = l.CreatedAt }).ToList();

			dto.OpenActivities = await _context.Activities.CountAsync(a => a.CompanyID == cid && !a.Done);
			dto.OverdueActivities = await _context.Activities.CountAsync(a => a.CompanyID == cid && !a.Done && a.DueDate != null && a.DueDate < now);
			var ts = await _crm.TicketStatsAsync(cid);
			dto.OpenTickets = ts.open;
			dto.AccountCount = await _context.CrmAccounts.CountAsync(a => a.CompanyID == cid);
			dto.ContactCount = await _context.CrmContacts.CountAsync(c => c.CompanyID == cid);
			dto.ActiveCampaigns = await _context.Campaigns.CountAsync(c => c.CompanyID == cid && c.Status == "Active");
			return View(dto);
		}

		// customer picker for select2-ajax (opportunity link)
		[HttpGet] public async Task<IActionResult> CustomerPickData(string? term)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var t = (term ?? "").Trim();
			var query = _context.Customers.AsNoTracking().Where(c => c.CompanyID == cid && c.IsActive);
			if (t.Length > 0) query = query.Where(c => c.Name.Contains(t) || (c.NameEn != null && c.NameEn.Contains(t)) || (c.Phone != null && c.Phone.Contains(t)));
			var rows = await query.OrderBy(c => c.Name).Take(20).Select(c => new { id = c.ID, text = c.Name }).ToListAsync();
			return Json(new { results = rows });
		}

		// account picker for select2-ajax (opportunity link) — CRM 3-2
		[HttpGet] public async Task<IActionResult> AccountPickData(string? term)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var rows = await _crm.GetAccountsForPickAsync(cid, term);
			return Json(new { results = rows.Select(r => new { id = r.id, text = r.name }) });
		}

		// ---------------- Accounts + Contacts (3-2) ----------------
		[HttpGet] public IActionResult Accounts() => View();

		[HttpGet] public async Task<IActionResult> AccountsData(string? q, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchAccountsAsync(cid, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_AccountRows", rows);
		}

		[HttpGet] public async Task<IActionResult> AccountEditor(int? id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var model = (id.HasValue && id.Value > 0)
				? await _crm.GetAccountAsync(cid, id.Value) ?? new Models.Context.Crm.CrmAccount { IsActive = true }
				: new Models.Context.Crm.CrmAccount { IsActive = true };
			if (model.CustomerId.HasValue)
			{
				var acIsAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				var cust = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == cid && c.ID == model.CustomerId.Value).Select(c => new { c.Name, c.NameEn }).FirstOrDefaultAsync();
				ViewBag.CustomerName = cust == null ? null : (!acIsAr && !string.IsNullOrWhiteSpace(cust.NameEn) ? cust.NameEn : cust.Name);
			}
			if (model.ID > 0) { ViewBag.Timeline = await _crm.GetTimelineAsync(cid, "Account", model.ID); ViewBag.EntityType = "Account"; ViewBag.EntityId = model.ID; }
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveAccount(int id, string name, string? nameEn, string? industry, string? industryEn, string? phone, string? email, string? website, string? address, string? source, string? segment, bool isActive, string? notes)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, newId) = await _crm.SaveAccountAsync(cid, new Models.Context.Crm.CrmAccount { ID = id, Name = name ?? "", NameEn = nameEn, Industry = industry, IndustryEn = industryEn, Phone = phone, Email = email, Website = website, Address = address, Source = source, Segment = segment, IsActive = isActive, Notes = notes }, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Account updated"].Value : L["Account created"].Value) : err;
			return ok ? RedirectToAction(nameof(AccountEditor), new { id = newId }) : RedirectToAction(nameof(Accounts));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveContact(int id, int accountId, string name, string? nameEn, string? title, string? titleEn, string? phone, string? email, bool isPrimary, string? notes)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.SaveContactAsync(cid, new Models.Context.Crm.CrmContact { ID = id, AccountId = accountId, Name = name ?? "", NameEn = nameEn, Title = title, TitleEn = titleEn, Phone = phone, Email = email, IsPrimary = isPrimary, Notes = notes });
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Contact saved"].Value : err;
			return RedirectToAction(nameof(AccountEditor), new { id = accountId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> DeleteContact(int id, int accountId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.DeleteContactAsync(cid, id);
			TempData["CrmMsg"] = L["Contact deleted"].Value;
			return RedirectToAction(nameof(AccountEditor), new { id = accountId });
		}

		// ---------------- Campaigns ----------------
		[HttpGet] public IActionResult Campaigns() => View();

		[HttpGet] public async Task<IActionResult> CampaignsData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchCampaignsAsync(cid, q, status, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_CampaignRows", rows);
		}

		[HttpGet] public async Task<IActionResult> CampaignsExport(string? q, string? status)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, _) = await _crm.SearchCampaignsAsync(cid, q, status, 1, 100000);
			var headers = new[] { "Campaign", "Channel", "Status", "Budget", "Leads", "Opportunities", "Won", "Won value", "ROI %" };
			var data = rows.Select(c => (IReadOnlyList<object?>)new object?[] { c.Name, c.Channel, c.Status, c.Budget, c.Leads, c.Opps, c.Won, c.WonValue, c.RoiPct });
			return File(CrossBuy.BL.ExcelExporter.Build("Campaigns", headers, data, "Campaigns — CrossBuy"), CrossBuy.BL.ExcelExporter.ContentType, "campaigns.xlsx");
		}

		[HttpGet] public async Task<IActionResult> LeadsExport(string? q, string? status)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, _) = await _crm.SearchLeadsAsync(cid, q, status, 1, 100000);
			var headers = new[] { "Name", "Company", "Phone", "Email", "Source", "Segment", "Expected value", "Status" };
			var data = rows.Select(l => (IReadOnlyList<object?>)new object?[] { l.Name, l.Company, l.Phone, l.Email, l.Source, l.Segment, l.EstimatedValue, l.Status });
			return File(CrossBuy.BL.ExcelExporter.Build("Leads", headers, data, "Leads — CrossBuy"), CrossBuy.BL.ExcelExporter.ContentType, "leads.xlsx");
		}

		[HttpGet] public async Task<IActionResult> OpportunitiesExport(string? q, string? stage)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, _) = await _crm.SearchOpportunitiesAsync(cid, q, stage, 1, 100000);
			var headers = new[] { "Title", "Customer", "Stage", "Value", "Probability %", "Expected close" };
			var data = rows.Select(o => (IReadOnlyList<object?>)new object?[] { o.Title, o.CustomerName, o.Stage, o.Amount, o.Probability, o.ExpectedCloseDate });
			return File(CrossBuy.BL.ExcelExporter.Build("Opportunities", headers, data, "Opportunities — CrossBuy"), CrossBuy.BL.ExcelExporter.ContentType, "opportunities.xlsx");
		}

		[HttpGet] public async Task<IActionResult> ActivitiesExport(string? q, bool? done)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, _) = await _crm.SearchActivitiesAsync(cid, q, done, 1, 100000);
			var headers = new[] { "Type", "Subject", "Due", "Status", "Notes" };
			var data = rows.Select(a => (IReadOnlyList<object?>)new object?[] { a.Type, a.Subject, a.DueDate, a.Done ? "منجز" : "معلّق", a.Notes });
			return File(CrossBuy.BL.ExcelExporter.Build("Activities", headers, data, "Activities and tasks — CrossBuy"), CrossBuy.BL.ExcelExporter.ContentType, "activities.xlsx");
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveCampaign(int id, string name, string? nameEn, string? channel, string status, DateTime? startDate, DateTime? endDate, decimal budget, string? notes)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.SaveCampaignAsync(cid, new Campaign { ID = id, Name = name ?? "", NameEn = nameEn, Channel = channel, Status = status, StartDate = startDate, EndDate = endDate, Budget = budget, Notes = notes }, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Campaign updated"].Value : L["Campaign added"].Value) : err;
			return RedirectToAction(nameof(Campaigns));
		}

		// ---------------- Campaign detail: members + ROI (3-5) ----------------
		[HttpGet] public async Task<IActionResult> CampaignDetail(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var detail = await _crm.GetCampaignDetailAsync(cid, id);
			if (detail == null) { TempData["CrmErr"] = L["Campaign not found"].Value; return RedirectToAction(nameof(Campaigns)); }
			ViewBag.Members = await _crm.GetCampaignMembersAsync(cid, id);
			ViewBag.Lists = await _crm.GetListsForPickAsync(cid);
			return View(detail);
		}

		[HttpGet] public async Task<IActionResult> EntityPickData(string type, string? term)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var rows = await _crm.PickEntitiesAsync(cid, type ?? "Account", term);
			return Json(new { results = rows.Select(r => new { id = r.id, text = r.name }) });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> AddCampaignMember(int campaignId, string entityType, int entityId, string? memberName)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.AddCampaignMembersAsync(cid, campaignId, new[] { (entityType, entityId, memberName) });
			TempData[n > 0 ? "CrmMsg" : "CrmErr"] = n > 0 ? L["Member added"].Value : L["Member already exists"].Value;
			return RedirectToAction(nameof(CampaignDetail), new { id = campaignId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> AddListToCampaign(int campaignId, int listId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.AddListToCampaignAsync(cid, campaignId, listId);
			TempData["CrmMsg"] = L["Added {0} members from the list", n].Value;
			return RedirectToAction(nameof(CampaignDetail), new { id = campaignId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> UpdateMemberStatus(int id, string status, int campaignId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.UpdateMemberStatusAsync(cid, id, status);
			return RedirectToAction(nameof(CampaignDetail), new { id = campaignId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> RemoveCampaignMember(int id, int campaignId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.RemoveCampaignMemberAsync(cid, id);
			return RedirectToAction(nameof(CampaignDetail), new { id = campaignId });
		}

		// ---------------- Marketing lists (3-5) ----------------
		[HttpGet] public IActionResult MarketingLists() => View();

		[HttpGet] public async Task<IActionResult> MarketingListsData(string? q, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchListsAsync(cid, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_ListRows", rows);
		}

		[HttpGet] public async Task<IActionResult> ListEditor(int? id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var model = (id.HasValue && id.Value > 0)
				? await _crm.GetListAsync(cid, id.Value) ?? new Models.Context.Crm.CrmMarketingList { IsActive = true }
				: new Models.Context.Crm.CrmMarketingList { IsActive = true };
			// resolve each member to its actual entity name, culture-aware (English NameEn when UI is not Arabic)
			if (model.ID > 0 && model.Members.Count > 0)
			{
				var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
				string Pick(string? ar, string? en) => (isEn && !string.IsNullOrWhiteSpace(en)) ? en! : (ar ?? "");
				var accIds = model.Members.Where(m => m.EntityType == "Account").Select(m => m.EntityId).ToList();
				var leadIds = model.Members.Where(m => m.EntityType == "Lead").Select(m => m.EntityId).ToList();
				var conIds = model.Members.Where(m => m.EntityType == "Contact").Select(m => m.EntityId).ToList();
				var accs = await _context.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID)).Select(a => new { a.ID, a.Name, a.NameEn }).ToDictionaryAsync(a => a.ID);
				var leads = await _context.Leads.AsNoTracking().Where(l => l.CompanyID == cid && leadIds.Contains(l.ID)).Select(l => new { l.ID, l.Name, l.NameEn }).ToDictionaryAsync(l => l.ID);
				var cons = await _context.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && conIds.Contains(c.ID)).Select(c => new { c.ID, c.Name, c.NameEn }).ToDictionaryAsync(c => c.ID);
				var names = new Dictionary<int, string>();
				foreach (var m in model.Members)
				{
					string? nm = m.EntityType switch
					{
						"Account" => accs.TryGetValue(m.EntityId, out var a) ? Pick(a.Name, a.NameEn) : null,
						"Lead" => leads.TryGetValue(m.EntityId, out var l) ? Pick(l.Name, l.NameEn) : null,
						"Contact" => cons.TryGetValue(m.EntityId, out var c) ? Pick(c.Name, c.NameEn) : null,
						_ => null
					};
					names[m.ID] = !string.IsNullOrWhiteSpace(nm) ? nm! : (m.MemberName ?? ("#" + m.EntityId));
				}
				ViewBag.MemberNames = names;
			}
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveList(int id, string name, string? nameEn, string? description, string? descriptionEn, bool isActive)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, newId) = await _crm.SaveListAsync(cid, new Models.Context.Crm.CrmMarketingList { ID = id, Name = name ?? "", NameEn = nameEn, Description = description, DescriptionEn = descriptionEn, IsActive = isActive }, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["List updated"].Value : L["List created"].Value) : err;
			return ok ? RedirectToAction(nameof(ListEditor), new { id = newId }) : RedirectToAction(nameof(MarketingLists));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> AddListMember(int listId, string entityType, int entityId, string? memberName)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.AddListMembersAsync(cid, listId, new[] { (entityType, entityId, memberName) });
			TempData[n > 0 ? "CrmMsg" : "CrmErr"] = n > 0 ? L["Member added"].Value : L["Member already exists"].Value;
			return RedirectToAction(nameof(ListEditor), new { id = listId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> RemoveListMember(int id, int listId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.RemoveListMemberAsync(cid, id);
			return RedirectToAction(nameof(ListEditor), new { id = listId });
		}

		// ---------------- Tickets + SLA (3-6) ----------------
		[HttpGet] public async Task<IActionResult> Tickets()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Stats = await _crm.TicketStatsAsync(cid);
			return View();
		}

		[HttpGet] public async Task<IActionResult> TicketsData(string? q, string? status, string? priority, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchTicketsAsync(cid, q, status, priority, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_TicketRows", rows);
		}

		[HttpGet] public async Task<IActionResult> TicketEditor(int? id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var model = (id.HasValue && id.Value > 0)
				? await _crm.GetTicketAsync(cid, id.Value) ?? new Models.Context.Crm.CrmTicket()
				: new Models.Context.Crm.CrmTicket();
			if (model.AccountId.HasValue)
			{
				var tkIsAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				var acc = await _context.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.ID == model.AccountId.Value).Select(a => new { a.Name, a.NameEn }).FirstOrDefaultAsync();
				ViewBag.AccountName = acc == null ? null : (!tkIsAr && !string.IsNullOrWhiteSpace(acc.NameEn) ? acc.NameEn : acc.Name);
			}
			if (model.ID > 0) { ViewBag.Timeline = await _crm.GetTimelineAsync(cid, "Ticket", model.ID); ViewBag.EntityType = "Ticket"; ViewBag.EntityId = model.ID; }
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveTicket(int id, string subject, string? subjectEn, string? description, string? descriptionEn, int? accountId, string? category, string? categoryEn, string priority, int? ownerEmployeeId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, newId) = await _crm.SaveTicketAsync(cid, new Models.Context.Crm.CrmTicket {
				ID = id, Subject = subject ?? "", SubjectEn = subjectEn, Description = description, DescriptionEn = descriptionEn, AccountId = accountId, Category = category, CategoryEn = categoryEn, Priority = priority, OwnerEmployeeId = ownerEmployeeId }, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Ticket updated"].Value : L["Ticket created"].Value) : err;
			return ok ? RedirectToAction(nameof(TicketEditor), new { id = newId }) : RedirectToAction(nameof(Tickets));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> UpdateTicketStatus(int id, string status)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.UpdateTicketStatusAsync(cid, id, status);
			TempData["CrmMsg"] = L["Ticket status updated"].Value;
			return RedirectToAction(nameof(TicketEditor), new { id });
		}

		[HttpGet][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SlaPolicies()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Policies = await _crm.GetSlaPoliciesAsync(cid);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SaveSlaPolicy(int id, string name, string priority, int firstResponseMins, int resolutionMins, bool isActive)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.SaveSlaPolicyAsync(cid, new Models.Context.Crm.CrmSlaPolicy {
				ID = id, Name = name ?? "", Priority = priority, FirstResponseMins = firstResponseMins, ResolutionMins = resolutionMins, IsActive = isActive });
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["SLA policy saved"].Value : err;
			return RedirectToAction(nameof(SlaPolicies));
		}

		// ---------------- Scoring + routing + forecast (3-7) ----------------
		[HttpGet][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> ScoringRules()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Rules = await _crm.GetScoringRulesAsync(cid);
			ViewBag.Settings = await _crm.GetCrmSettingsAsync(cid);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SaveScoringRule(int id, string name, string? nameEn, string field, string @operator, string? value, int points, bool isActive)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.SaveScoringRuleAsync(cid, new Models.Context.Crm.CrmScoringRule {
				ID = id, Name = name ?? "", NameEn = nameEn, Field = field, Operator = @operator, Value = value, Points = points, IsActive = isActive });
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Rule saved"].Value : err;
			return RedirectToAction(nameof(ScoringRules));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SaveCrmSettings(bool autoRouteLeads, int hotScore, int warmScore)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.SaveCrmSettingsAsync(cid, new Models.Context.Crm.CrmSettings { AutoRouteLeads = autoRouteLeads, HotScore = hotScore, WarmScore = warmScore });
			TempData["CrmMsg"] = L["Settings saved"].Value;
			return RedirectToAction(nameof(ScoringRules));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> RecomputeScores()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.RecomputeAllLeadScoresAsync(cid);
			TempData["CrmMsg"] = L["Recomputed scores for {0} leads", n].Value;
			return RedirectToAction(nameof(ScoringRules));
		}

		[HttpGet] public async Task<IActionResult> Forecast()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Forecast = await _crm.GetForecastAsync(cid);
			return View();
		}

		// ---------------- 360° report (3-8) ----------------
		[HttpGet] public async Task<IActionResult> Reports()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			return View(await _crm.GetCrmReportAsync(cid));
		}

		// ---------------- Leads ----------------
		[HttpGet] public async Task<IActionResult> Leads()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Campaigns = await _crm.GetCampaignsForPickAsync(cid);
			ViewBag.CustomFields = await CfSvc.GetFieldsAsync(cid, "Lead", true);
			return View();
		}

		[HttpGet] public async Task<IActionResult> LeadsData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchLeadsAsync(cid, q, status, page, pageSize);
			var st = await _crm.GetCrmSettingsAsync(cid);
			ViewBag.Hot = st.HotScore; ViewBag.Warm = st.WarmScore;
			SetPaging(total, page, pageSize);
			return PartialView("_LeadRows", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveLead(int id, string name, string? nameEn, string? company, string? phone, string? email, string? source, string? segment, decimal estimatedValue, string status, string? notes, int? campaignId, string? customFieldsJson)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var lead = new Lead { ID = id, Name = name ?? "", NameEn = nameEn, Company = company, Phone = phone, Email = email, Source = source, Segment = segment, EstimatedValue = estimatedValue, Status = status, Notes = notes, CampaignId = campaignId };
			bool isNew = id <= 0;
			var (ok, err) = await _crm.SaveLeadAsync(cid, lead, User?.Identity?.Name);
			if (ok && lead.ID > 0) await CfSvc.SaveValuesAsync(cid, "Lead", lead.ID, ParseCustomFields(customFieldsJson));
			if (ok && isNew && lead.ID > 0)
			{
				var owner = await _context.Leads.Where(l => l.CompanyID == cid && l.ID == lead.ID).Select(l => l.OwnerEmployeeId).FirstOrDefaultAsync();
				await AutoSvc.RunAsync(cid, "LeadCreated", null, lead.ID, null, owner);   // CRM 3-7b(ii)
			}
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Lead updated"].Value : L["Lead added"].Value) : err;
			return RedirectToAction(nameof(Leads));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> ConvertLead(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, _) = await _crm.ConvertLeadToAccountAsync(cid, id, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Lead converted to account"].Value : err;
			return RedirectToAction(nameof(Leads));
		}

		// ---------------- Opportunities ----------------
		// ===== CRM Account Health Insights (rules-based decision support) =====
		//
		// A DIFFERENT QUESTION FROM OpportunityInsights, not a second rendering of it. That screen asks
		// "which deals need attention"; this one asks "which relationships are weakening". An account can
		// hold three healthy-looking deals and still have gone silent for two months, and no per-opportunity
		// rule can see that.
		//
		// STILL NO CRM MODEL. Re-audited this increment: crossbuy_ai ships journal-anomaly, cashflow and
		// inventory and nothing that takes an account. So this performs NO AI egress and claims no
		// probability, score or prediction - it is arithmetic over counts the reader can check.
		//
		// BOUNDED QUERIES, NOT ONE PER ACCOUNT. Five aggregate queries serve any number of accounts:
		// accounts, direct account activity, opportunity-linked activity, open opportunities, owner names.
		// Every count is computed by SQL; no activity row is materialised in memory.
		[HttpGet]
		[CrossBuy.Models.CrmPerm("read")]
		public async Task<IActionResult> AccountInsights()
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				TempData["CrmErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var nowUtc = DateTime.UtcNow;
			var vm = new CrossBuy.ViewModel.Ai.CrmAccountHealthVm
			{
				CompanyId = scope.CompanyId,
				RecentWindowDays = Rules.RecentWindowDays,
				PreviousWindowDays = Rules.PreviousWindowDays,
			};

			try
			{
				var recentFrom = nowUtc.Date.AddDays(-Rules.RecentWindowDays);
				var previousFrom = recentFrom.AddDays(-Rules.PreviousWindowDays);

				// Owner narrowing from the module's own contract, applied ON TOP of the company predicate.
				// It can only narrow; the company filter below is ours and independent of it.
				var visibleOwners = await _access.VisibleOwnerIdsAsync();

				var accountsQuery = _context.CrmAccounts.AsNoTracking()
					.Where(a => a.CompanyID == scope.CompanyId);
				if (visibleOwners != null)
					accountsQuery = accountsQuery.Where(a => a.OwnerEmployeeId != null && visibleOwners.Contains(a.OwnerEmployeeId.Value));

				var accounts = await accountsQuery
					.Select(a => new { a.ID, a.Name, a.NameEn, a.OwnerEmployeeId })
					.ToListAsync();

				vm.Analysed = accounts.Count;
				if (accounts.Count == 0)
				{
					// Nothing examined is NOT a healthy customer base. Map(200, 0) yields InsufficientData.
					vm.Panel = AiInsightMapper.Map(200, 0, scope.CompanyId, nowUtc);
					return View(vm);
				}

				var ids = accounts.Select(a => a.ID).ToList();

				// ---- activity, aggregated in SQL, from BOTH authoritative links to an account ----
				//
				// 1. an activity recorded directly against the account (EntityType/EntityId), and
				// 2. an activity on one of the account's opportunities.
				// Both are real engagement with that customer. Each is company-predicated in its own right
				// rather than trusted to the join - a filter that is only correct because of another table's
				// predicate is one refactor away from being wrong.
				var direct = await _context.Activities.AsNoTracking()
					.Where(x => x.CompanyID == scope.CompanyId
						&& x.EntityType == "Account" && x.EntityId != null && ids.Contains(x.EntityId.Value))
					.GroupBy(x => x.EntityId!.Value)
					.Select(g => new
					{
						AccountId = g.Key,
						Recent = g.Count(x => x.CreatedAt != null && x.CreatedAt >= recentFrom),
						Previous = g.Count(x => x.CreatedAt != null && x.CreatedAt >= previousFrom && x.CreatedAt < recentFrom),
						LastAt = g.Max(x => x.CreatedAt),
						Open = g.Count(x => !x.Done),
					})
					.ToListAsync();

				var viaOpp = await (
					from act in _context.Activities.AsNoTracking()
						.Where(x => x.CompanyID == scope.CompanyId && x.OpportunityId != null)
					join opp in _context.Opportunities.AsNoTracking()
						.Where(o => o.CompanyID == scope.CompanyId && o.AccountId != null)
						on act.OpportunityId equals opp.ID
					where ids.Contains(opp.AccountId!.Value)
					group act by opp.AccountId!.Value into g
					select new
					{
						AccountId = g.Key,
						Recent = g.Count(x => x.CreatedAt != null && x.CreatedAt >= recentFrom),
						Previous = g.Count(x => x.CreatedAt != null && x.CreatedAt >= previousFrom && x.CreatedAt < recentFrom),
						LastAt = g.Max(x => x.CreatedAt),
						Open = g.Count(x => !x.Done),
					}).ToListAsync();

				// ---- open opportunities and overdue exposure, aggregated in SQL ----
				var closedStages = Rules.ClosedStagesForExposure;
				var today = nowUtc.Date;
				var exposure = await _context.Opportunities.AsNoTracking()
					.Where(o => o.CompanyID == scope.CompanyId
						&& o.AccountId != null && ids.Contains(o.AccountId.Value)
						&& !closedStages.Contains(o.Stage))
					.GroupBy(o => o.AccountId!.Value)
					.Select(g => new
					{
						AccountId = g.Key,
						OpenCount = g.Count(),
						OpenValue = g.Sum(x => (decimal?)x.Amount) ?? 0m,
						PastDueCount = g.Count(x => x.ExpectedCloseDate != null && x.ExpectedCloseDate < today),
						PastDueValue = g.Sum(x => x.ExpectedCloseDate != null && x.ExpectedCloseDate < today
							? (decimal?)x.Amount : 0m) ?? 0m,
					})
					.ToListAsync();

				var ownerNames = await _context.Employee.AsNoTracking()
					.Where(e => e.EmpCompanyID == scope.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn })
					.ToDictionaryAsync(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn));

				var directById = direct.ToDictionary(x => x.AccountId);
				var viaOppById = viaOpp.ToDictionary(x => x.AccountId);
				var exposureById = exposure.ToDictionary(x => x.AccountId);

				var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				var signals = accounts.Select(a =>
				{
					directById.TryGetValue(a.ID, out var d);
					viaOppById.TryGetValue(a.ID, out var v);
					exposureById.TryGetValue(a.ID, out var x);

					// Two sources of the same fact, combined. MAX for the date, SUM for the counts.
					DateTime? lastAt = (d?.LastAt, v?.LastAt) switch
					{
						(null, null) => null,
						(var p, null) => p,
						(null, var q) => q,
						var (p, q) => p > q ? p : q,
					};

					return new Rules.Signal
					{
						AccountId = a.ID,
						Name = (!isAr && !string.IsNullOrWhiteSpace(a.NameEn)) ? a.NameEn! : a.Name,
						OwnerEmployeeId = a.OwnerEmployeeId,
						OwnerName = a.OwnerEmployeeId != null && ownerNames.TryGetValue(a.OwnerEmployeeId.Value, out var on) ? on : null,
						RecentActivityCount = (d?.Recent ?? 0) + (v?.Recent ?? 0),
						PreviousActivityCount = (d?.Previous ?? 0) + (v?.Previous ?? 0),
						LastActivityAt = lastAt,
						OpenFollowUpCount = (d?.Open ?? 0) + (v?.Open ?? 0),
						OpenOpportunityCount = x?.OpenCount ?? 0,
						OpenOpportunityValue = x?.OpenValue ?? 0m,
						PastDueOpportunityCount = x?.PastDueCount ?? 0,
						PastDueOpportunityValue = x?.PastDueValue ?? 0m,
					};
				});

				vm.Insights = Rules.Analyse(signals, nowUtc);
				vm.Panel = AiInsightMapper.Map(200, accounts.Count, scope.CompanyId, nowUtc);
			}
			catch (Microsoft.Data.SqlClient.SqlException)
			{
				vm.Panel = AiInsightMapper.Unavailable("crm-account:data-unavailable", scope.CompanyId, nowUtc);
			}
			catch (InvalidOperationException)
			{
				vm.Panel = AiInsightMapper.Failed("crm-account:unreadable-data", scope.CompanyId, nowUtc);
			}

			return View(vm);
		}

		// ===== CRM Opportunity Insights (rules-based decision support) =====
		//
		// NOT AI, AND THE SCREEN SAYS SO. There is no CRM model in this product: crossbuy_ai ships
		// journal-anomaly, cashflow and inventory models and nothing that takes an opportunity. This action
		// therefore performs NO AI egress at all - it reads the company's own CRM rows and applies the
		// deterministic rules in CrmOpportunityRiskRules. Calling that "AI" on the page would be inventing a
		// model, which is the one thing this surface must not do.
		//
		// READ-ONLY. Nothing here writes, and every link it renders is a GET to a screen that already exists.
		[HttpGet]
		[CrossBuy.Models.CrmPerm("read")]
		public async Task<IActionResult> OpportunityInsights()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			// COMPANY IS RESOLVED, NEVER SUPPLIED. The action takes no parameters, so there is nothing a
			// caller could offer. The rest of this controller still uses its cid constant; that
			// is pre-existing debt this screen does not inherit and does not fix.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				// The resolver's reason is deliberately not rendered: it names companies, and "your company is
				// 2, the record is 1" tells a caller a record exists where they cannot see it.
				TempData["CrmErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var vm = new CrossBuy.ViewModel.Ai.CrmOpportunityInsightsVm { CompanyId = scope.CompanyId };
			var nowUtc = DateTime.UtcNow;

			try
			{
				// OWNER SCOPE comes from the CRM module's own contract, not from logic copied to here. Null
				// means "no owner narrowing for this user"; a set means they see only those owners.
				//
				// IT IS APPLIED ON TOP OF THE COMPANY PREDICATE, never instead of it. At HEAD that service
				// still derives its roles from a hardcoded company 1 (another workstream is repairing it), so
				// a wrong owner set could narrow this list oddly - but it can never widen it across companies,
				// because the company filter below is ours and independent of it.
				var visibleOwners = await _access.VisibleOwnerIdsAsync();

				var opps = _context.Opportunities.AsNoTracking()
					.Where(o => o.CompanyID == scope.CompanyId);
				if (visibleOwners != null)
					opps = opps.Where(o => o.OwnerEmployeeId != null && visibleOwners.Contains(o.OwnerEmployeeId.Value));

				// Only OPEN opportunities are examined. A Won or Lost deal with no recent activity is
				// finished, not neglected, and putting it on a work list trains the reader to ignore the list.
				var closed = CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.ClosedStages.ToList();
				var rows = await opps
					.Where(o => !closed.Contains(o.Stage))
					.Select(o => new
					{
						o.ID, o.Title, o.TitleEn, o.AccountId, o.Stage, o.Amount, o.Probability,
						o.ExpectedCloseDate, o.CreatedAt, o.OwnerEmployeeId,
					})
					.ToListAsync();

				vm.Analysed = rows.Count;

				if (rows.Count == 0)
				{
					// Nothing examined is NOT a clean pipeline. Map(200, 0) yields InsufficientData.
					vm.Panel = AiInsightMapper.Map(200, 0, scope.CompanyId, nowUtc);
					return View(vm);
				}

				var ids = rows.Select(r => r.ID).ToList();

				// Last activity and open-follow-up counts, company-scoped in their own right rather than
				// trusted to the join: a filter that is only correct because of another table's predicate is
				// one refactor away from being wrong.
				var activity = await _context.Activities.AsNoTracking()
					.Where(a => a.CompanyID == scope.CompanyId && a.OpportunityId != null && ids.Contains(a.OpportunityId.Value))
					.GroupBy(a => a.OpportunityId!.Value)
					.Select(g => new
					{
						OpportunityId = g.Key,
						LastAt = g.Max(x => x.CreatedAt),
						Open = g.Count(x => !x.Done),
					})
					.ToListAsync();
				var activityById = activity.ToDictionary(a => a.OpportunityId);

				var accountNames = await _context.CrmAccounts.AsNoTracking()
					.Where(a => a.CompanyID == scope.CompanyId)
					.Select(a => new { a.ID, a.Name })
					.ToDictionaryAsync(a => a.ID, a => a.Name);

				var ownerNames = await _context.Employee.AsNoTracking()
					.Where(e => e.EmpCompanyID == scope.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn })
					.ToDictionaryAsync(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn));

				var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				var signals = rows.Select(r => new CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Signal
				{
					OpportunityId = r.ID,
					Title = (!isAr && !string.IsNullOrWhiteSpace(r.TitleEn)) ? r.TitleEn! : r.Title,
					AccountId = r.AccountId,
					AccountName = r.AccountId != null && accountNames.TryGetValue(r.AccountId.Value, out var an) ? an : null,
					Stage = r.Stage,
					Amount = r.Amount,
					Probability = r.Probability,
					ExpectedCloseDate = r.ExpectedCloseDate,
					CreatedAt = r.CreatedAt,
					OwnerEmployeeId = r.OwnerEmployeeId,
					OwnerName = r.OwnerEmployeeId != null && ownerNames.TryGetValue(r.OwnerEmployeeId.Value, out var on) ? on : null,
					LastActivityAt = activityById.TryGetValue(r.ID, out var ac) ? ac.LastAt : null,
					OpenActivityCount = activityById.TryGetValue(r.ID, out var ac2) ? ac2.Open : 0,
				});

				vm.Insights = CrossBuy.BL.Platform.Ai.CrmOpportunityRiskRules.Analyse(signals, nowUtc);
				vm.Panel = AiInsightMapper.Map(200, rows.Count, scope.CompanyId, nowUtc);
			}
			catch (Microsoft.Data.SqlClient.SqlException)
			{
				// The data this screen reads is unreachable. Recoverable, and honestly "we could not look",
				// not "there is nothing wrong".
				vm.Panel = AiInsightMapper.Unavailable("crm-insight:data-unavailable", scope.CompanyId, nowUtc);
			}
			catch (InvalidOperationException)
			{
				vm.Panel = AiInsightMapper.Failed("crm-insight:unreadable-data", scope.CompanyId, nowUtc);
			}

			return View(vm);
		}

		[HttpGet] public async Task<IActionResult> Opportunities()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Pipeline = await _crm.PipelineSummaryAsync(cid);
			ViewBag.Campaigns = await _crm.GetCampaignsForPickAsync(cid);
			var pid = await _crm.GetDefaultPipelineIdAsync(cid);
			ViewBag.DefaultPipelineId = pid;
			ViewBag.Stages = pid > 0 ? await _crm.GetStagesAsync(cid, pid) : new List<Models.Context.Crm.CrmPipelineStage>();
			ViewBag.CustomFields = await CfSvc.GetFieldsAsync(cid, "Opportunity", true);
			return View();
		}

		[HttpGet] public async Task<IActionResult> OpportunitiesData(string? q, string? stage, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchOpportunitiesAsync(cid, q, stage, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_OppRows", rows);
		}

		[HttpGet] public async Task<IActionResult> Pipeline()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Pipeline = await _crm.PipelineSummaryAsync(cid);
			var pid = await _crm.GetDefaultPipelineIdAsync(cid);
			ViewBag.Stages = pid > 0 ? await _crm.GetStagesAsync(cid, pid) : new List<Models.Context.Crm.CrmPipelineStage>();
			return View(await _crm.PipelineBoardAsync(cid));
		}

		// pipeline stages JSON (cascade) — CRM 3-3
		[HttpGet] public async Task<IActionResult> StagesData(int pipelineId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var stages = await _crm.GetStagesAsync(cid, pipelineId);
			return Json(stages.Select(s => new { s.ID, s.Name, s.NameEn, s.Probability, s.IsWon, s.IsLost }));
		}

		// ---------------- Pipelines config (3-3) ----------------
		[HttpGet][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> Pipelines()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.Pipelines = await _crm.GetPipelinesAsync(cid);
			return View();
		}

		[HttpGet][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> PipelineEditor(int? id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var model = (id.HasValue && id.Value > 0)
				? await _crm.GetPipelineWithStagesAsync(cid, id.Value) ?? new Models.Context.Crm.CrmPipeline { IsActive = true }
				: new Models.Context.Crm.CrmPipeline { IsActive = true };
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SavePipeline(int id, string name, string? nameEn, bool isDefault, bool isActive, string? stagesJson)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			List<Models.Context.Crm.CrmPipelineStage> stages;
			try { stages = System.Text.Json.JsonSerializer.Deserialize<List<Models.Context.Crm.CrmPipelineStage>>(stagesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { stages = new(); }
			var (ok, err, newId) = await _crm.SavePipelineAsync(cid, new Models.Context.Crm.CrmPipeline { ID = id, Name = name ?? "", NameEn = nameEn, IsDefault = isDefault, IsActive = isActive }, stages);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Pipeline updated"].Value : L["Pipeline created"].Value) : err;
			return ok ? RedirectToAction(nameof(PipelineEditor), new { id = newId }) : RedirectToAction(nameof(Pipelines));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> MoveOpportunity(int id, string stage)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.UpdateOpportunityStageAsync(cid, id, stage);
			if (ok)
			{
				var owner = await _context.Opportunities.Where(o => o.CompanyID == cid && o.ID == id).Select(o => o.OwnerEmployeeId).FirstOrDefaultAsync();
				await AutoSvc.RunAsync(cid, "OpportunityStageChanged", stage, null, id, owner);   // CRM 3-7b(ii)
			}
			return Json(new { ok, error = err });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveOpportunity(int id, string title, string? titleEn, int? accountId, int? pipelineId, string stage, decimal amount, DateTime? expectedCloseDate, string? notes, int? campaignId, string? winLossReason, string? customFieldsJson)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var opp = new Opportunity { ID = id, Title = title ?? "", TitleEn = titleEn, AccountId = accountId, PipelineId = pipelineId, Stage = stage, Amount = amount, ExpectedCloseDate = expectedCloseDate, Notes = notes, CampaignId = campaignId, WinLossReason = winLossReason };
			var (ok, err) = await _crm.SaveOpportunityAsync(cid, opp, User?.Identity?.Name);
			if (ok && opp.ID > 0) await CfSvc.SaveValuesAsync(cid, "Opportunity", opp.ID, ParseCustomFields(customFieldsJson));
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Opportunity updated"].Value : L["Opportunity added"].Value) : err;
			return RedirectToAction(nameof(Opportunities));
		}

		// ---------------- Opportunity products + O2C (3-3b) ----------------
		[HttpGet] public async Task<IActionResult> OpportunityProducts(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var opp = await _crm.GetOpportunityAsync(cid, id);
			if (opp == null) return RedirectToAction(nameof(Opportunities));
			ViewBag.Products = await _crm.GetOpportunityProductsAsync(cid, id);
			if (opp.AccountId.HasValue) ViewBag.AccountName = await _context.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.ID == opp.AccountId.Value).Select(a => a.Name).FirstOrDefaultAsync();
			return View(opp);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveOpportunityProducts(int id, string? linesJson)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			List<Models.Context.Crm.OpportunityProduct> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<Models.Context.Crm.OpportunityProduct>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err) = await _crm.SaveOpportunityProductsAsync(cid, id, lines);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Opportunity lines saved"].Value : err;
			return RedirectToAction(nameof(OpportunityProducts), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> ConvertOpportunityToQuotation(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, qid) = await _crm.ConvertOpportunityToQuotationAsync(cid, id, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? L["Quotation #{0} created", qid].Value : err;
			return RedirectToAction(nameof(OpportunityProducts), new { id });
		}

		// ---------------- Activities ----------------
		[HttpGet] public IActionResult Activities() => View();

		[HttpGet] public async Task<IActionResult> ActivitiesData(string? q, bool? done, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchActivitiesAsync(cid, q, done, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_ActivityRows", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveActivity(int id, string type, string subject, string? subjectEn, DateTime? dueDate, string? notes,
			string? entityType = null, int? entityId = null, DateTime? reminderAt = null, string? returnUrl = null)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.SaveActivityAsync(cid, new Activity {
				ID = id, Type = type, Subject = subject ?? "", SubjectEn = subjectEn, DueDate = dueDate, Notes = notes,
				EntityType = entityType, EntityId = entityId, ReminderAt = reminderAt }, User?.Identity?.Name);
			TempData[ok ? "CrmMsg" : "CrmErr"] = ok ? (id > 0 ? L["Activity updated"].Value : L["Activity added"].Value) : err;
			if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
			return RedirectToAction(nameof(Activities));
		}

		// CRM 3-4: timeline of any entity (Lead/Opportunity/Account/Customer/Contact) — reusable partial.
		[HttpGet] public async Task<IActionResult> TimelineData(string entityType, int entityId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var rows = await _crm.GetTimelineAsync(cid, entityType, entityId);
			ViewBag.EntityType = entityType; ViewBag.EntityId = entityId;
			return PartialView("_Timeline", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> ToggleActivity(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.ToggleActivityAsync(cid, id);
			return RedirectToAction(nameof(Activities));
		}

		// ---------------- CRM roles (3-1) ----------------
		[HttpGet][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> CrmRoles()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			ViewBag.Employees = await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid).OrderBy(e => e.FullName).Select(e => new CrossBuy.ViewModel.EmployeeViewModel { ID = e.ID, FullName = isEn ? (e.FullNameEn ?? e.FullName) : e.FullName }).ToListAsync();
			ViewBag.Assignments = await (from r in _context.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == cid)
										 join e in _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid) on r.EmployeeId equals e.ID into ej
										 from e in ej.DefaultIfEmpty()
										 orderby r.ID descending
										 select new CrmRoleRow { ID = r.ID, EmployeeId = r.EmployeeId, EmployeeName = e != null ? (isEn ? (e.FullNameEn ?? e.FullName) : e.FullName) : ("#" + r.EmployeeId), Role = r.Role }).ToListAsync();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> AssignCrmRole(int employeeId, string role)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var allowed = new[] { "SalesManager", "SalesRep", "Marketing", "CrmViewer" };
			if (employeeId <= 0 || !allowed.Contains(role)) { TempData["CrmErr"] = L["Invalid data"].Value; return RedirectToAction(nameof(CrmRoles)); }
			// The employee must belong to the resolved company. Without this a manager could grant CRM
			// authority over their own tenant to a stranger from another one, and the role row would look
			// perfectly ordinary afterwards. An outsider id is answered exactly like a nonexistent one.
			bool ours = await _context.Employee.AsNoTracking().AnyAsync(e => e.ID == employeeId && e.EmpCompanyID == cid);
			if (!ours) { TempData["CrmErr"] = L["Invalid data"].Value; return RedirectToAction(nameof(CrmRoles)); }
			bool exists = await _context.CrmUserRoles.AnyAsync(r => r.CompanyID == cid && r.EmployeeId == employeeId && r.Role == role);
			if (!exists) { _context.CrmUserRoles.Add(new Models.Context.Crm.CrmUserRole { CompanyID = cid, EmployeeId = employeeId, Role = role, CreatedAt = DateTime.UtcNow }); await _context.SaveChangesAsync(); }
			TempData["CrmMsg"] = L["Role assigned"].Value;
			return RedirectToAction(nameof(CrmRoles));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> RemoveCrmRole(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var r = await _context.CrmUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == cid);
			if (r != null) { _context.CrmUserRoles.Remove(r); await _context.SaveChangesAsync(); }
			TempData["CrmMsg"] = L["Role deleted"].Value;
			return RedirectToAction(nameof(CrmRoles));
		}
	}
}
