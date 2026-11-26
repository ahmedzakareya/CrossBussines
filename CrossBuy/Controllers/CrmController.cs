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
	// PARTIAL: the Client Portal contacts screen lives in CrmController.PortalUsers.cs. It is a partial
// rather than a controller of its own because a portal contact is a customer's person, so it answers
// to the CRM authority this file already wires - and because the platform's controller count is frozen.
public partial class CrmController : Controller
	{
		private readonly ICrmService _crm;
		private readonly CrossDbContext _context;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private readonly ICrmAccessService _access;
		// A SERVICE REFUSAL IS A SENTENCE THE USER READS.
		// CrmService returns its refusals as English text. That was invisible while they landed
		// in an inline alert nobody looked at; a toast puts them in front of an Arabic reader at
		// the moment a save fails. The English sentence IS the resource key, so an untranslated
		// one still shows exactly what it shows today rather than a blank toast.
		private string Say(string? serviceMessage) => string.IsNullOrWhiteSpace(serviceMessage)
			? L["Operation failed"].Value
			: L[serviceMessage].Value;

		// THE SAME RECORD PICKER THE TASKS MODULE USES. ITaskLinkResolver is a thin company-scoped
		// wrapper over IEntityRegistry - the frozen entity vocabulary, its search and its deep
		// links. A second picker for CRM would be a second vocabulary, which is exactly what the
		// registry exists to prevent.
		private readonly CrossBuy.BL.ITaskLinkResolver _links;

		public CrmController(ICrmService crm, CrossDbContext context, CrossBuy.BL.Platform.IRequestCompanyResolver company, ICrmAccessService access, IStringLocalizer<CrossBuy.SharedResources> localizer, CrossBuy.BL.ITaskLinkResolver links) { _crm = crm; _context = context; _company = company; _access = access; L = localizer; _links = links; }


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
			if (scope.Ok && scope.CompanyId > 0 && await _access.CanAsync("read")) return (true, scope.CompanyId, null!);
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
			this.ToastResult(ok, ok ? L["Custom field saved"].Value : Say(err));
			return RedirectToAction(nameof(CustomFields), new { entityType });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> DeleteCustomField(int id, string entityType)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await CfSvc.DeleteFieldAsync(cid, id);
			this.ToastSuccess(L["Field deleted"].Value);
			return RedirectToAction(nameof(CustomFields), new { entityType });
		}

		// ---------------- The tag VOCABULARY ----------------
		//
		// Four CRM screens already let you ATTACH a tag, and the Tags table was empty with no way to
		// put anything in it: ITagService had Create/Rename/Retire from the start and nothing in the
		// application ever called them, so every tag picker rendered "No results found" permanently.
		// This is the missing half - the place a tag is born, renamed and retired.
		//
		// WHY TagSvc IS WRITTEN TO HERE, when the note above says it is read-only in this controller.
		// That rule is about ASSIGNMENTS: a tag attached to a record must be written inside that
		// record’s own save transaction, or a half-saved record keeps tags it never had. The
		// VOCABULARY is not part of any record’s save - it has no transaction to join - so it is
		// written through the platform service that owns it, which is also where the normalization,
		// the clash rules and the archive-vs-delete decision live.
		
		[HttpGet] public async Task<IActionResult> Tags()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
		
			// ARCHIVED TAGS ARE SHOWN, unlike every picker: this is the screen where you bring one
			// back, and a tag you cannot see is one you cannot revive.
			var tags = await TagSvc.GetTagsAsync(cid, includeArchived: true);
		
			// HOW MANY RECORDS CARRY EACH TAG, in one grouped query. The count is not decoration:
			// RetireTagAsync DELETES an unused tag and ARCHIVES a used one, and the reader is
			// entitled to know which of the two the button in front of them will do.
			var ids = tags.Select(t => t.ID).ToList();
			ViewBag.TagUsage = ids.Count == 0
				? new Dictionary<int, int>()
				: await _context.TagAssignments.AsNoTracking()
					.Where(a => a.CompanyID == cid && ids.Contains(a.TagId))
					.GroupBy(a => a.TagId)
					.Select(g => new { TagId = g.Key, N = g.Count() })
					.ToDictionaryAsync(x => x.TagId, x => x.N);
		
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(tags);
		}
		
		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> CreateTag(string name)
		{
			var scope = await _company.ResolveAsync();
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
		
			// A name that matches an ARCHIVED tag revives it rather than failing or making a twin -
			// the service’s own rule, and the reason the message below says "restored" in that case.
			var (ok, err, tag) = await TagSvc.CreateTagAsync(cid, name ?? "", scope.EmployeeId);
			this.ToastResult(ok, ok ? L["Tag saved"].Value : Say(err));
			return RedirectToAction(nameof(Tags));
		}
		
		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> RenameTag(int id, string name)
		{
			var scope = await _company.ResolveAsync();
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
		
			// A rename does NOT touch the assignments: every record keeps the same tag, now spelled
			// correctly. That is the whole reason a tag is an id on the wire and never a name.
			var (ok, err) = await TagSvc.RenameTagAsync(cid, id, name ?? "", scope.EmployeeId);
			this.ToastResult(ok, ok ? L["Tag saved"].Value : Say(err));
			return RedirectToAction(nameof(Tags));
		}
		
		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> RetireTag(int id)
		{
			var scope = await _company.ResolveAsync();
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
		
			// The service decides: ARCHIVE when the tag has history, DELETE when it has none. The
			// answer tells the user which happened, because "archived" and "deleted" are not the
			// same promise and the screen must not report one as the other.
			var (ok, err, archived) = await TagSvc.RetireTagAsync(cid, id, scope.EmployeeId);
			this.ToastResult(ok, ok
				? (archived ? L["Tag archived"].Value : L["Tag deleted"].Value)
				: Say(err));
			return RedirectToAction(nameof(Tags));
		}
		
		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> ReviveTag(int id)
		{
			var scope = await _company.ResolveAsync();
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
		
			// Reviving IS creating by the same name: CreateTagAsync finds the archived row by its
			// normalized key and reactivates it. Going through that one path keeps a single set of
			// rules for what a tag name may be, instead of a second flag-flip that skips them.
			var existing = (await TagSvc.GetTagsAsync(cid, includeArchived: true))
				.FirstOrDefault(t => t.ID == id);
			if (existing == null) { this.ToastResult(false, Say(null)); return RedirectToAction(nameof(Tags)); }
		
			var (ok, err, _) = await TagSvc.CreateTagAsync(cid, existing.Name, scope.EmployeeId);
			this.ToastResult(ok, ok ? L["Tag restored"].Value : Say(err));
			return RedirectToAction(nameof(Tags));
		}
		
		[HttpGet] public async Task<IActionResult> EntityCustomValues(string entityType, int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			return Json((await CfSvc.GetForEntityAsync(cid, entityType, id)).Select(x => new { fieldId = x.Field.ID, value = x.Value }));
		}

		// ---------------- CRM automation rules (3-7b-ii) ----------------
		private ICrmAutomationService AutoSvc => (HttpContext.RequestServices.GetService(typeof(ICrmAutomationService)) as ICrmAutomationService)!;

		// The PLATFORM tag service, resolved the same way this controller already resolves AutoSvc and
		// CfSvc. It is used here for READS only - the company's tag list for a picker, and one batch of
		// chips per rendered page. Every tag WRITE goes through CrmService, inside the save's own
		// transaction, because a tag that saved while its record did not is the failure this increment
		// exists to avoid.
		private CrossBuy.BL.Platform.ITagService TagSvc =>
			(HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.ITagService)) as CrossBuy.BL.Platform.ITagService)!;

		/// The tag set a form OWNS. An emptied multi-select posts no key at all, which binds to the same
		/// null a caller with no tag control produces - so the form sends a sentinel to say "these tags
		/// are mine", and an absent id list then means CLEARED rather than UNTOUCHED.
		private int[]? TagsFromForm(int[]? tagIds)
		{
			bool managed = Request.HasFormContentType && Request.Form.ContainsKey("TagsManaged");
			if (!managed) return null;
			return tagIds ?? Array.Empty<int>();
		}

		/// One batch read per rendered page - never one call per row.
		private async Task LoadRowTagsAsync(int companyId, string entityCode, IEnumerable<int> ids)
		{
			var list = ids.Where(i => i > 0).Distinct().ToList();
			ViewBag.RowTags = list.Count == 0
				? new Dictionary<int, List<CrossBuy.Models.Context.Platform.Tag>>()
				: (await TagSvc.GetTagsForEntitiesAsync(companyId, entityCode, list))
					.ToDictionary(p => p.Key, p => p.Value);
		}

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
			this.ToastResult(ok, ok ? L["Automation rule saved"].Value : Say(err));
			return RedirectToAction(nameof(AutomationRules));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> DeleteAutomationRule(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await AutoSvc.DeleteRuleAsync(cid, id);
			this.ToastSuccess(L["Rule deleted"].Value);
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
            var permittedOwners = await _access.VisibleOwnerIdsAsync();
            var leadSource = _context.Leads.AsNoTracking().Where(x => x.CompanyID == cid);
            var opportunitySource = _context.Opportunities.AsNoTracking().Where(x => x.CompanyID == cid);
            var activitySource = _context.Activities.AsNoTracking().Where(x => x.CompanyID == cid);
            var accountSource = _context.CrmAccounts.AsNoTracking().Where(x => x.CompanyID == cid);
            var campaignSource = _context.Campaigns.AsNoTracking().Where(x => x.CompanyID == cid);
            var contactSource = _context.CrmContacts.AsNoTracking().Where(x => x.CompanyID == cid);
            if(permittedOwners != null) {
                campaignSource = campaignSource.Where(x => permittedOwners.Count > 0 && (x.OwnerEmployeeId == null || permittedOwners.Contains(x.OwnerEmployeeId.Value)));
                leadSource = leadSource.Where(x => permittedOwners.Count > 0 && (x.OwnerEmployeeId == null || permittedOwners.Contains(x.OwnerEmployeeId.Value)));
                opportunitySource = opportunitySource.Where(x => permittedOwners.Count > 0 && (x.OwnerEmployeeId == null || permittedOwners.Contains(x.OwnerEmployeeId.Value)));
                activitySource = activitySource.Where(x => permittedOwners.Count > 0 && (x.OwnerEmployeeId == null || permittedOwners.Contains(x.OwnerEmployeeId.Value)));
                accountSource = accountSource.Where(x => permittedOwners.Count > 0 && (x.OwnerEmployeeId == null || permittedOwners.Contains(x.OwnerEmployeeId.Value)));
                contactSource = contactSource.Where(x => permittedOwners.Count > 0 && (x.OwnerEmployeeId == null || permittedOwners.Contains(x.OwnerEmployeeId.Value)));
            }
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var dto = new CrmDashboardDto();
			dto.Pipeline = await _crm.PipelineSummaryAsync(cid);

			var opps = await opportunitySource.AsNoTracking()
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
			var accNames = await accountSource.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID))
				.Select(a => new { a.ID, a.Name, a.NameEn }).ToListAsync();
			decimal maxAcc = topAcc.Count > 0 ? topAcc.Max(x => x.Value) : 0;
			foreach (var t in topAcc)
			{
				var an = accNames.FirstOrDefault(a => a.ID == t.AccountId);
				dto.TopAccounts.Add(new CrmTopAccount {
					Name = an == null ? ("#" + t.AccountId) : ((isEn && !string.IsNullOrWhiteSpace(an.NameEn)) ? an.NameEn! : an.Name),
					Value = t.Value, Pct = maxAcc > 0 ? (int)Math.Round(100m * t.Value / maxAcc) : 0 });
			}

			dto.LeadCount = await leadSource.CountAsync(l => l.CompanyID == cid && l.Status != "Converted" && l.Status != "Lost");
			dto.RecentLeads = (await leadSource.AsNoTracking().Where(l => l.CompanyID == cid)
				.OrderByDescending(l => l.ID).Take(6)
				.Select(l => new { l.Name, l.NameEn, l.Status, l.Score, l.CreatedAt }).ToListAsync())
				.Select(l => new CrmRecentLead { Name = (isEn && !string.IsNullOrWhiteSpace(l.NameEn)) ? l.NameEn! : l.Name, Status = l.Status, Score = l.Score, CreatedAt = l.CreatedAt }).ToList();

			dto.OpenActivities = await activitySource.CountAsync(a => a.CompanyID == cid && !a.Done);
			dto.OverdueActivities = await activitySource.CountAsync(a => a.CompanyID == cid && !a.Done && a.DueDate != null && a.DueDate < now);
			var ts = await _crm.TicketStatsAsync(cid);
			dto.OpenTickets = ts.open;
			dto.AccountCount = await accountSource.CountAsync(a => a.CompanyID == cid);
			dto.ContactCount = await contactSource.CountAsync(c => c.CompanyID == cid);
			dto.ActiveCampaigns = await campaignSource.CountAsync(c => c.CompanyID == cid && c.Status == "Active");
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
		[HttpGet] public async Task<IActionResult> Accounts()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			ViewBag.AllTags = await TagSvc.GetTagsAsync(cid);
			return View();
		}

		[HttpGet] public async Task<IActionResult> AccountsData(string? q, int page = 1, int pageSize = 25,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassigned = false)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchAccountsAsync(cid, q, page, pageSize, tagIds, tagMatchAll, unassigned);
			await LoadRowTagsAsync(cid, CrossBuy.BL.Platform.EntityRegistry.CrmAccount, rows.Select(r => r.Id));
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
			ViewBag.AllTags = await TagSvc.GetTagsAsync(cid);
			ViewBag.EntityTagIds = model.ID > 0
				? (await TagSvc.GetEntityTagsAsync(cid, CrossBuy.BL.Platform.EntityRegistry.CrmAccount, model.ID)).Select(t => t.ID).ToArray()
				: Array.Empty<int>();
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveAccount(int id, string name, string? nameEn, string? industry, string? industryEn, string? phone, string? email, string? website, string? address, string? source, string? segment, bool isActive, string? notes, int[]? tagIds = null)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, newId) = await _crm.SaveAccountAsync(cid, new Models.Context.Crm.CrmAccount { ID = id, Name = name ?? "", NameEn = nameEn, Industry = industry, IndustryEn = industryEn, Phone = phone, Email = email, Website = website, Address = address, Source = source, Segment = segment, IsActive = isActive, Notes = notes }, User?.Identity?.Name, TagsFromForm(tagIds));
			this.ToastResult(ok, ok ? (id > 0 ? L["Account updated"].Value : L["Account created"].Value) : Say(err));
			return ok ? RedirectToAction(nameof(AccountEditor), new { id = newId }) : RedirectToAction(nameof(Accounts));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveContact(int id, int accountId, string name, string? nameEn, string? title, string? titleEn, string? phone, string? email, bool isPrimary, string? notes)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err) = await _crm.SaveContactAsync(cid, new Models.Context.Crm.CrmContact { ID = id, AccountId = accountId, Name = name ?? "", NameEn = nameEn, Title = title, TitleEn = titleEn, Phone = phone, Email = email, IsPrimary = isPrimary, Notes = notes });
			this.ToastResult(ok, ok ? L["Contact saved"].Value : Say(err));
			return RedirectToAction(nameof(AccountEditor), new { id = accountId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> DeleteContact(int id, int accountId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.DeleteContactAsync(cid, id);
			this.ToastSuccess(L["Contact deleted"].Value);
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
			this.ToastResult(ok, ok ? (id > 0 ? L["Campaign updated"].Value : L["Campaign added"].Value) : Say(err));
			return RedirectToAction(nameof(Campaigns));
		}

		// ---------------- Campaign detail: members + ROI (3-5) ----------------
		[HttpGet] public async Task<IActionResult> CampaignDetail(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var detail = await _crm.GetCampaignDetailAsync(cid, id);
			if (detail == null) { this.ToastError(L["Campaign not found"].Value); return RedirectToAction(nameof(Campaigns)); }
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
			this.ToastResult(n > 0, n > 0 ? L["Member added"].Value : L["Member already exists"].Value);
			return RedirectToAction(nameof(CampaignDetail), new { id = campaignId });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> AddListToCampaign(int campaignId, int listId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.AddListToCampaignAsync(cid, campaignId, listId);
			this.ToastSuccess(L["Added {0} members from the list", n].Value);
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
			this.ToastResult(ok, ok ? (id > 0 ? L["List updated"].Value : L["List created"].Value) : Say(err));
			return ok ? RedirectToAction(nameof(ListEditor), new { id = newId }) : RedirectToAction(nameof(MarketingLists));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> AddListMember(int listId, string entityType, int entityId, string? memberName)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.AddListMembersAsync(cid, listId, new[] { (entityType, entityId, memberName) });
			this.ToastResult(n > 0, n > 0 ? L["Member added"].Value : L["Member already exists"].Value);
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

		[HttpGet] public async Task<IActionResult> TicketsData(string? q, string? status, string? priority, bool myUnit = false, int page = 1, int pageSize = 25)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchTicketsAsync(cid, q, status, priority, page, pageSize, myUnit);
			// The unit NAMES for the rows on this page - one read, not one per row, and it covers
			// units that have since been deactivated so history stays readable.
			ViewBag.DeptNames = await _crm.DepartmentNamesAsync(rows.Select(r => r.DepartmentId ?? 0));
			SetPaging(total, page, pageSize);
			return PartialView("_TicketRows", rows);
		}

		[HttpGet] public async Task<IActionResult> TicketEditor(int? id)
		{
			// The calls on this ticket. A support record whose history of phone calls lives
			// somewhere else is a record nobody can read in one place.
			if (id is > 0) ViewBag.TicketCalls = await _crm.GetCallsAsync(
				(await _company.ResolveAsync()).CompanyId, 50, id);
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
			// The same chart the call screen offers, so a problem routed on the phone and one routed
			// from its own screen are picked from one structure.
			ViewBag.OrgTree = await _crm.OrgTreeAsync(cid);
			// The call rows on this page are the SAME partial the call log draws, and it names the
			// agent (with their photo) and the unit each call went to - so this page has to supply
			// the two lookups that partial reads, exactly as the call screen does.
			var tcalls = ViewBag.TicketCalls as List<Models.Context.Crm.CrmCall> ?? new();
			ViewBag.Employees = await _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == cid).ToListAsync();
			ViewBag.DeptNames = await _crm.DepartmentNamesAsync(tcalls.Select(c => c.DepartmentId ?? 0));
			ViewBag.RecordTypes = _links.Types();
			// Resolved rather than stored: a label copied onto the ticket at creation would still say
				// "Order #118 - Ahmed" after the order was renamed or cancelled.
			ViewBag.SourceLink = await _links.ResolveAsync(cid, model.SourceType, model.SourceId);
			if (model.ID > 0) { ViewBag.Timeline
 = await _crm.GetTimelineAsync(cid, "Ticket", model.ID); ViewBag.EntityType = "Ticket"; ViewBag.EntityId = model.ID; }
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveTicket(int id, string subject, string? subjectEn, string? description, string? descriptionEn, int? accountId, string? category, string? categoryEn, string priority, int? ownerEmployeeId, string? channel, string? callerPhone, int? departmentId, string? sourceType, int? sourceId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, newId) = await _crm.SaveTicketAsync(cid, new Models.Context.Crm.CrmTicket {
				ID = id, Subject = subject ?? "", SubjectEn = subjectEn, Description = description, DescriptionEn = descriptionEn, AccountId = accountId, Category = category, CategoryEn = categoryEn, Priority = priority, OwnerEmployeeId = ownerEmployeeId, Channel = channel, CallerPhone = callerPhone, DepartmentId = departmentId, SourceType = sourceType, SourceId = sourceId }, User?.Identity?.Name);
			this.ToastResult(ok, ok ? (id > 0 ? L["Ticket updated"].Value : L["Ticket created"].Value) : Say(err));
			return ok ? RedirectToAction(nameof(TicketEditor), new { id = newId }) : RedirectToAction(nameof(Tickets));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> UpdateTicketStatus(int id, string status)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.UpdateTicketStatusAsync(cid, id, status);
			// CRM C8: a resolved request gets its one satisfaction survey now, while the request knows the site's address
			if (status is "Resolved" or "Closed") await DeskSvc.SurveyForAsync(cid, id, Origin);
			this.ToastSuccess(L["Ticket status updated"].Value);
			return RedirectToAction(nameof(TicketEditor), new { id });
		}

		// ============================================================================================
		// CALL CENTRE
		//
		// Actions on the EXISTING CRM controller, not a new one: the controller count is frozen, and
		// a call belongs to the same bounded context as the ticket it is about anyway.
		// ============================================================================================

		[HttpGet] public async Task<IActionResult> Calls(string? q, int? ticketId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var calls = await _crm.GetCallsAsync(cid, 100);
			ViewBag.Calls = calls;
			// The picker draws the chart as it is NOW; the log has to name the node each call
			// actually went to, which may since have been deactivated or moved. Two reads, because
			// they are two different questions and one list cannot answer both.
			ViewBag.OrgTree = await _crm.OrgTreeAsync(cid);
			ViewBag.DeptNames = await _crm.DepartmentNamesAsync(calls.Select(c => c.DepartmentId ?? 0));
			ViewBag.RecordTypes = _links.Types();

			// ARRIVED FROM A TICKET. The form opens attached to it, with the number and the caller
			// filled in from the ticket itself - both are facts the ticket already knows.
			//
			// The ROUTING is deliberately NOT pre-filled, even when the ticket is already routed:
			// setting a department is what sends the notification, so carrying it over would ping
			// the unit again for every call logged against the ticket.
			if (ticketId is > 0)
			{
				var t = await _crm.GetTicketAsync(cid, ticketId.Value);
				// Resolved through the service, so a ticket the caller may not see simply does not
				// attach - the query string cannot be used to confirm one exists.
				if (t != null)
				{
					ViewBag.ForTicketId = t.ID;
					ViewBag.ForTicketSubject = CrossBuy.BL.DisplayName.Of(t.Subject, t.SubjectEn);
					ViewBag.ForTicketPhone = t.CallerPhone;
					// WHAT THE TICKET IS ABOUT, resolved live for the same read-only treatment as the
					// routing below: both are properties of the TICKET, both are edited on its screen,
					// and a second place to set them is a second answer to the same question.
					ViewBag.ForTicketSource = await _links.ResolveAsync(cid, t.SourceType, t.SourceId);

					// Where the TICKET stands, shown read-only. Routing belongs to the ticket, so the
					// call screen reports it and does not offer to change it.
					if (t.DepartmentId is > 0)
					{
						var dn = await _crm.DepartmentNamesAsync(new[] { t.DepartmentId.Value });
						ViewBag.ForTicketDept = dn.TryGetValue(t.DepartmentId.Value, out var dd)
							? CrossBuy.BL.DisplayName.Of(dd.NameAr, dd.NameEn)
							: "#" + t.DepartmentId.Value;
					}
				}
			}
			ViewBag.Breached = await _crm.BreachedTicketsAsync(cid);
			ViewBag.Stats = await _crm.TicketStatsAsync(cid);
			// THE ENTITY, NOT AN ANONYMOUS PROJECTION.
			//
			// An anonymous type is internal to the assembly that declares it, and Razor RUNTIME
			// compilation builds views into a different one - so `@e.ID` on a dynamic threw
			// RuntimeBinderException: 'object' does not contain a definition for 'ID'. It compiles
			// clean and fails only when the page is requested, which is why it has to be caught by
			// loading the page rather than by building.
			ViewBag.Employees = await _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == cid).OrderBy(e => e.FullName).ToListAsync();
			return View();
		}

		// WHAT IS IT ABOUT. Feeds the "بخصوص" picker: the caller names an order, an invoice or an
		// item, and the agent attaches the actual record rather than retyping its number into a
		// subject line where nothing can ever join on it.
		[HttpGet] public async Task<IActionResult> RecordSearch(string? entityType, string? term)
		{
			if (string.IsNullOrWhiteSpace(entityType)) return Json(new { results = Array.Empty<object>() });
			var (okCo, cid, _) = await ResolveCompanyAsync();
			// A picker with no resolved company offers NOTHING - the same empty answer an unknown
			// type gets, so a caller cannot tell the two apart by probing.
			if (!okCo || cid <= 0) return Json(new { results = Array.Empty<object>() });
			var rows = await _links.SearchAsync(cid, entityType, term);
			return Json(new { results = rows.Select(r => new { id = r.Id, text = r.Label }) });
		}

		// WHO IS THIS. The lookup an agent needs in the seconds before answering: every earlier call
		// from the same number, and whatever each of them turned out to be about.
		[HttpGet] public async Task<IActionResult> CallerLookup(string? phone)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var calls = await _crm.HistoryForNumberAsync(cid, phone ?? "");
			var ticketIds = calls.Where(c => c.TicketId != null).Select(c => c.TicketId!.Value).Distinct().ToList();
			var subjects = await _context.CrmTickets.AsNoTracking()
				.Where(t => t.CompanyID == cid && ticketIds.Contains(t.ID))
				.Select(t => new { t.ID, t.Subject, t.Status }).ToListAsync();
			return Json(new
			{
				count = calls.Count,
				calls = calls.Select(c => new { c.ID, c.Direction, c.Outcome, c.StartedAt, c.DurationSeconds, c.TicketId }),
				tickets = subjects,
			});
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> LogCall(string direction, string? phoneNumber, int? ticketId,
			int? accountId, int? agentEmployeeId, int? departmentId, DateTime? startedAt,
			int? durationSeconds, string outcome, string? notes, string? ticketSubject,
			string? sourceType, int? sourceId)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var res = await _crm.LogCallAsync(cid, new Models.Context.Crm.CrmCall
			{
				Direction = direction, PhoneNumber = phoneNumber, TicketId = ticketId, AccountId = accountId,
				AgentEmployeeId = agentEmployeeId, DepartmentId = departmentId,
				SourceType = sourceType, SourceId = sourceId,
				StartedAt = startedAt ?? DateTime.UtcNow,
				DurationSeconds = durationSeconds, Outcome = outcome, Notes = notes,
			}, User?.Identity?.Name, ticketSubject);

			// SAY WHAT ACTUALLY HAPPENED TO THE ROUTING. "Call logged" alone would hide the one
			// outcome the agent must act on: the department exists, the call was routed to it, and
			// nobody is assigned to it - so no notification went anywhere and the agent is still the
			// only person who knows. That is a warning, not a success.
			if (!res.Ok)
			{
				this.ToastError(Say(res.Error));
			}
			else
			{
				// The ticket, if one was opened, leads - it is the thing the agent will be asked
				// about later, and its number is the only part of this they need to carry away.
				var opened = res.TicketId is > 0 ? L["Ticket #{0} opened", res.TicketId!.Value].Value + " · " : "";
				if (departmentId is > 0)
				{
					var names = await _crm.DepartmentNamesAsync(new[] { departmentId.Value });
					var dept = names.TryGetValue(departmentId.Value, out var d)
						? DisplayName.Of(d.NameAr, d.NameEn)
						: "#" + departmentId.Value;
					if (res.Notified > 0) this.ToastSuccess(opened + L["Call logged and routed to {0}; {1} notified", dept, res.Notified].Value);
					else this.ToastWarning(opened + L["Call logged and routed to {0}, but no employee is assigned to that department yet", dept].Value);
				}
				else
				{
					this.ToastSuccess(opened + L["Call logged"].Value);
				}
			}
			// An agent who TYPED an existing ticket number was already heading for that ticket; one
			// who opened a new one from here is still on the phones, and the new row links to it.
			return ticketId is > 0
				? RedirectToAction(nameof(TicketEditor), new { id = ticketId })
				: RedirectToAction(nameof(Calls));
		}

		// ESCALATE ONE TICKET, UP THE CHART. Different from EscalateBreaches below, which sweeps
		// everything past its deadline and tells the OWNER: this is a human deciding that one
		// problem has outgrown the unit holding it, and it moves the ticket to the unit above.
		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> EscalateTicket(int id, string? reason)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, toName, notified) = await _crm.EscalateTicketAsync(cid, id, reason, User?.Identity?.Name);
			if (!ok) this.ToastError(Say(err));
			else if (notified > 0) this.ToastSuccess(L["Escalated to {0}; {1} notified", toName ?? "", notified].Value);
			else this.ToastWarning(L["Escalated to {0}, but no employee is assigned to that unit yet", toName ?? ""].Value);
			return RedirectToAction(nameof(TicketEditor), new { id });
		}

		// ESCALATE WHAT IS ALREADY LATE.
		//
		// `crm_ticket` has been a declared notification type with a mapped route since the module
		// was written, and NOTHING in the codebase ever raised it - so a breach was discovered only
		// by a human who happened to open the screen. Measured on this database at the time: 4 of 6
		// tickets were past their resolution deadline and nobody had been told.
		//
		// Deliberately an ACTION a supervisor runs, not a background worker: a worker that notifies
		// on every pass would send the same breach every few minutes forever. The dedup key below
		// makes a repeat run idempotent per ticket per day.
		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> EscalateBreaches([FromServices] CrossBuy.BL.INotificationService notify)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			// CRM C8: the same sweep the CRM worker now runs, in one place
			var (sent, unowned) = await DeskSvc.SweepBreachesAsync(cid);
			this.ToastSuccess(string.Format(L["Escalated {0} breached ticket(s); {1} have no owner"].Value, sent, unowned));
			return RedirectToAction(nameof(Calls));
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
			this.ToastResult(ok, ok ? L["SLA policy saved"].Value : Say(err));
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
			this.ToastResult(ok, ok ? L["Rule saved"].Value : Say(err));
			return RedirectToAction(nameof(ScoringRules));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> SaveCrmSettings(bool autoRouteLeads, int hotScore, int warmScore)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			await _crm.SaveCrmSettingsAsync(cid, new Models.Context.Crm.CrmSettings { AutoRouteLeads = autoRouteLeads, HotScore = hotScore, WarmScore = warmScore });
			this.ToastSuccess(L["Settings saved"].Value);
			return RedirectToAction(nameof(ScoringRules));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> RecomputeScores()
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var n = await _crm.RecomputeAllLeadScoresAsync(cid);
			this.ToastSuccess(L["Recomputed scores for {0} leads", n].Value);
			return RedirectToAction(nameof(ScoringRules));
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
			// The company's OWN vocabulary, and only the live tags: an archived tag can still be read on
			// a record that carries it, but it is not offered for new classification.
			ViewBag.AllTags = await TagSvc.GetTagsAsync(cid);
			return View();
		}

		[HttpGet] public async Task<IActionResult> LeadsData(string? q, string? status, int page = 1, int pageSize = 25,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassigned = false, bool overdueFollowUp = false)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchLeadsAsync(cid, q, status, page, pageSize, tagIds, tagMatchAll, unassigned, overdueFollowUp);
			await LoadRowTagsAsync(cid, CrossBuy.BL.Platform.EntityRegistry.Lead, rows.Select(r => r.ID));
			var st = await _crm.GetCrmSettingsAsync(cid);
			ViewBag.Hot = st.HotScore; ViewBag.Warm = st.WarmScore;
			SetPaging(total, page, pageSize);
			return PartialView("_LeadRows", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveLead(int id, string name, string? nameEn, string? company, string? phone, string? email, string? source, string? segment, decimal estimatedValue, string status, string? notes, int? campaignId, string? customFieldsJson, int[]? tagIds = null)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var lead = new Lead { ID = id, Name = name ?? "", NameEn = nameEn, Company = company, Phone = phone, Email = email, Source = source, Segment = segment, EstimatedValue = estimatedValue, Status = status, Notes = notes, CampaignId = campaignId };
			bool isNew = id <= 0;
			var (ok, err) = await _crm.SaveLeadAsync(cid, lead, User?.Identity?.Name, TagsFromForm(tagIds));
			if (ok && lead.ID > 0) await CfSvc.SaveValuesAsync(cid, "Lead", lead.ID, ParseCustomFields(customFieldsJson));
			if (ok && isNew && lead.ID > 0)
			{
				var owner = await _context.Leads.Where(l => l.CompanyID == cid && l.ID == lead.ID).Select(l => l.OwnerEmployeeId).FirstOrDefaultAsync();
				await AutoSvc.RunAsync(cid, "LeadCreated", null, lead.ID, null, owner);   // CRM 3-7b(ii)
			}
			this.ToastResult(ok, ok ? (id > 0 ? L["Lead updated"].Value : L["Lead added"].Value) : Say(err));
			return RedirectToAction(nameof(Leads));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> ConvertLead(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (ok, err, _) = await _crm.ConvertLeadToAccountAsync(cid, id, User?.Identity?.Name);
			this.ToastResult(ok, ok ? L["Lead converted to account"].Value : Say(err));
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
				this.ToastError(L["You do not have permission to perform this action"].Value);
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
					// THE COMPANY BY NAME, for the provenance strip. All four insight screens printed the
					// integer primary key as "Company 1", which is an internal handle: it tells a reader
					// nothing and looks identical on every installation. ViewBag.CompanyName is the
					// convention the Workspace layout already uses, and the partial falls back to the id.
					ViewBag.CompanyName = await CrossBuy.BL.CompanyDisplay.NameAsync(_context, vm.CompanyId);
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

					ViewBag.CompanyName = await CrossBuy.BL.CompanyDisplay.NameAsync(_context, vm.CompanyId);
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
				this.ToastError(L["You do not have permission to perform this action"].Value);
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
					// THE COMPANY BY NAME, for the provenance strip. All four insight screens printed the
					// integer primary key as "Company 1", which is an internal handle: it tells a reader
					// nothing and looks identical on every installation. ViewBag.CompanyName is the
					// convention the Workspace layout already uses, and the partial falls back to the id.
					ViewBag.CompanyName = await CrossBuy.BL.CompanyDisplay.NameAsync(_context, vm.CompanyId);
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

				// THE OWNER’S PHOTO RIDES ON THE QUERY THAT WAS ALREADY FETCHING THEIR NAME. One extra
				// column on a dictionary this screen builds anyway - no second round trip, and no lookup
				// per row.
				var owners = await _context.Employee.AsNoTracking()
					.Where(e => e.EmpCompanyID == scope.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
					.ToDictionaryAsync(e => e.ID, e => new
					{
						Name = CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn),
						e.ProfileImage,
					});

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
					OwnerName = r.OwnerEmployeeId != null && owners.TryGetValue(r.OwnerEmployeeId.Value, out var on) ? on.Name : null,
					OwnerAvatarUrl = r.OwnerEmployeeId != null && owners.TryGetValue(r.OwnerEmployeeId.Value, out var op) ? op.ProfileImage : null,
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

					ViewBag.CompanyName = await CrossBuy.BL.CompanyDisplay.NameAsync(_context, vm.CompanyId);
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
			ViewBag.AllTags = await TagSvc.GetTagsAsync(cid);
			return View();
		}

		[HttpGet] public async Task<IActionResult> OpportunitiesData(string? q, string? stage, int page = 1, int pageSize = 25,
			int[]? tagIds = null, bool tagMatchAll = false, bool unassigned = false, bool overdueFollowUp = false)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var (rows, total) = await _crm.SearchOpportunitiesAsync(cid, q, stage, page, pageSize, tagIds, tagMatchAll, unassigned, overdueFollowUp);
			await LoadRowTagsAsync(cid, CrossBuy.BL.Platform.EntityRegistry.Opportunity, rows.Select(r => r.Id));
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
			this.ToastResult(ok, ok ? (id > 0 ? L["Pipeline updated"].Value : L["Pipeline created"].Value) : Say(err));
			return ok ? RedirectToAction(nameof(PipelineEditor), new { id = newId }) : RedirectToAction(nameof(Pipelines));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> MoveOpportunity(int id, string stage)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			// CRM C3: a stage may require things before a deal ENTERS it; the board is one of its two doors.
			if (await StageGateAsync(cid, id, stage, null, null) is { } gate)
				return Json(new { ok = false, code = "gate", error = gate.message, missing = gate.missing, url = Url.Action("Deal", new { id }) });
			var (ok, err) = await _crm.UpdateOpportunityStageAsync(cid, id, stage);
			if (ok)
			{
				var owner = await _context.Opportunities.Where(o => o.CompanyID == cid && o.ID == id).Select(o => o.OwnerEmployeeId).FirstOrDefaultAsync();
				await AutoSvc.RunAsync(cid, "OpportunityStageChanged", stage, null, id, owner);   // CRM 3-7b(ii)
			}
			return Json(new { ok, error = err });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> SaveOpportunity(int id, string title, string? titleEn, int? accountId, int? pipelineId, string stage, decimal amount, DateTime? expectedCloseDate, string? notes, int? campaignId, string? winLossReason, string? customFieldsJson, int[]? tagIds = null)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			// CRM C3: the form is the other door into a stage. Only an existing deal changes stage here.
			if (id > 0 && await StageGateAsync(cid, id, stage, null, pipelineId) is { } gate)
			{
				this.ToastError(gate.message);
				return RedirectToAction("Deal", new { id });
			}
			var opp = new Opportunity { ID = id, Title = title ?? "", TitleEn = titleEn, AccountId = accountId, PipelineId = pipelineId, Stage = stage, Amount = amount, ExpectedCloseDate = expectedCloseDate, Notes = notes, CampaignId = campaignId, WinLossReason = winLossReason };
			var (ok, err) = await _crm.SaveOpportunityAsync(cid, opp, User?.Identity?.Name, TagsFromForm(tagIds));
			if (ok && opp.ID > 0) await CfSvc.SaveValuesAsync(cid, "Opportunity", opp.ID, ParseCustomFields(customFieldsJson));
			this.ToastResult(ok, ok ? (id > 0 ? L["Opportunity updated"].Value : L["Opportunity added"].Value) : Say(err));
			return RedirectToAction(nameof(Opportunities));
		}

		// ---------------- Opportunity products + O2C (3-3b) ----------------
		[HttpGet] public async Task<IActionResult> OpportunityProducts(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var opp = await _crm.GetOpportunityAsync(cid, id);
			if (opp == null) return RedirectToAction(nameof(Opportunities));
			var lines = await _crm.GetOpportunityProductsAsync(cid, id);
			ViewBag.Products = lines;

			// THE ITEM’S OWN NAME, which this screen did not have.
			//
			// An OpportunityProduct row stores an ItemId and a free-text ItemDescription, and nothing
			// else - the name lives in Items. The view had no second source for it, so it labelled the
			// item picker with the DESCRIPTION: type "delivered in March" into the description, save,
			// reload, and the item column read "delivered in March" too. The row was never corrupted;
			// the screen was showing one field twice.
			//
			// Code and name both travel, because that is exactly what /Inventory/ItemPickData returns
			// and what cbItemSelectSet renders ("CODE — Name"). A reloaded line has to look identical
			// to one the user just picked, or the screen tells them the item changed when it did not.
			var lineItemIds = lines.Where(p => p.ItemId != null).Select(p => p.ItemId!.Value).Distinct().ToList();
			ViewBag.LineItems = lineItemIds.Count == 0
				? new Dictionary<int, (string Code, string Name)>()
				: (await _context.Items.AsNoTracking()
					.Where(i => i.CompanyID == cid && lineItemIds.Contains(i.ID))
					.Select(i => new { i.ID, i.ItemCode, i.Name })
					.ToListAsync())
					.ToDictionary(i => i.ID, i => (i.ItemCode ?? "", i.Name ?? ""));
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
			this.ToastResult(ok, ok ? L["Opportunity lines saved"].Value : Say(err));
			return RedirectToAction(nameof(OpportunityProducts), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("edit")]
		public async Task<IActionResult> ConvertOpportunityToQuotation(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			// CRM C3: a quotation leaves the CRM for the customer, so the discount on it must be within the limit of the
			// person sending it, or approved by someone else for these exact lines.
			if (await DealSvc.QuotationRefusalAsync(cid, id, _access.CurrentEmployeeId()) is { } refusal)
			{
				this.ToastError(CL[refusal].Value);
				return RedirectToAction("Deal", new { id });
			}
			var (ok, err, qid) = await _crm.ConvertOpportunityToQuotationAsync(cid, id, User?.Identity?.Name);
			this.ToastResult(ok, ok ? L["Quotation #{0} created", qid].Value : Say(err));
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
			this.ToastResult(ok, ok ? (id > 0 ? L["Activity updated"].Value : L["Activity added"].Value) : Say(err));
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
			if (employeeId <= 0 || !allowed.Contains(role)) { this.ToastError(L["Invalid data"].Value); return RedirectToAction(nameof(CrmRoles)); }
			// The employee must belong to the resolved company. Without this a manager could grant CRM
			// authority over their own tenant to a stranger from another one, and the role row would look
			// perfectly ordinary afterwards. An outsider id is answered exactly like a nonexistent one.
			bool ours = await _context.Employee.AsNoTracking().AnyAsync(e => e.ID == employeeId && e.EmpCompanyID == cid);
			if (!ours) { this.ToastError(L["Invalid data"].Value); return RedirectToAction(nameof(CrmRoles)); }
			bool exists = await _context.CrmUserRoles.AnyAsync(r => r.CompanyID == cid && r.EmployeeId == employeeId && r.Role == role);
			if (!exists) { _context.CrmUserRoles.Add(new Models.Context.Crm.CrmUserRole { CompanyID = cid, EmployeeId = employeeId, Role = role, CreatedAt = DateTime.UtcNow }); await _context.SaveChangesAsync(); }
			this.ToastSuccess(L["Role assigned"].Value);
			return RedirectToAction(nameof(CrmRoles));
		}

		[HttpPost][ValidateAntiForgeryToken][CrossBuy.Models.CrmPerm("manage")]
		public async Task<IActionResult> RemoveCrmRole(int id)
		{
			var (okCo, cid, denyCo) = await ResolveCompanyAsync();
			if (!okCo) return denyCo;
			var r = await _context.CrmUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == cid);
			if (r != null) { _context.CrmUserRoles.Remove(r); await _context.SaveChangesAsync(); }
			this.ToastSuccess(L["Role deleted"].Value);
			return RedirectToAction(nameof(CrmRoles));
		}
	}
}
