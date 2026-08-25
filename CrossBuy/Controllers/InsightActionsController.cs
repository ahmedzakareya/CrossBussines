using CrossBuy.BL;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Text.Json;

namespace CrossBuy.Controllers
{
    // INSIGHT → TASK. The one governed endpoint the three insight screens share.
    //
    // WHAT THIS IS NOT. It is not a task engine, a second writer, or a place where tasks acquire a
    // special lifecycle. The write is one call to ITaskService.SaveAsync — the same call TasksController
    // makes — so the result is an ORDINARY task: it appears in My Tasks and the board, it goes overdue,
    // it notifies, it raises the normal task business event, and it escalates. Its only distinguishing
    // feature is a line in its description saying where the idea came from.
    //
    // EVERYTHING FROM THE BROWSER IS UNTRUSTED, INCLUDING THE PARTS THIS APPLICATION PUT THERE. The
    // review form is pre-populated by the server, posted back through a user's browser, and then
    // revalidated field by field here. In particular:
    //
    //     company     never read from the request at all - resolved, or refused
    //     source      the row must exist IN THE RESOLVED COMPANY, or it is treated as nonexistent
    //     finding     must be one this source actually offers a task for
    //     assignee    must be an active employee of the resolved company
    //     priority    must be one of the task service's own values
    //     permission  checked on BOTH sides: the source module AND task creation
    //
    // A cross-company source id is answered exactly like a missing one. Telling a caller "that exists
    // but belongs to company 2" is how a probe maps another tenant's data.
    [SessionValidation]
    public class InsightActionsController : Controller
    {
        private readonly CrossDbContext _db;
        private readonly ITaskService _tasks;
        private readonly ICrmAccessService _crmAccess;
        private readonly IInventoryAccessService _invAccess;
        private readonly ITasksAccessService _tasksAccess;
        private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContext;
        private readonly IStringLocalizer<CrossBuy.SharedResources> L;

        public InsightActionsController(
            CrossDbContext db,
            ITaskService tasks,
            ICrmAccessService crmAccess,
            IInventoryAccessService invAccess,
            ITasksAccessService tasksAccess,
            CrossBuy.BL.Platform.IRequestCompanyResolver company,
            CrossBuy.BL.Platform.IBusinessContextAccessor businessContext,
            IStringLocalizer<CrossBuy.SharedResources> localizer)
        {
            _db = db; _tasks = tasks; _crmAccess = crmAccess; _invAccess = invAccess;
            _tasksAccess = tasksAccess; _company = company; _businessContext = businessContext; L = localizer;
        }

        private static bool IsAr =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
        private static string T(string ar, string en) => IsAr ? ar : en;

        private int CurrentEmployeeId()
        {
            var json = HttpContext.Session.GetString("Employee");
            var emp = json != null ? JsonSerializer.Deserialize<ViewModel.EmployeeViewModel>(json) : null;
            return emp?.ID ?? 0;
        }

        // ---- the review form (§12): server-composed, never trusted when it comes back ----
        [HttpGet]
        public async Task<IActionResult> NewTask(string source, int entityId, string finding, string? returnUrl)
        {
            var gate = await AuthoriseAsync(source, entityId, finding);
            if (gate.Refusal != null) return gate.Refusal;

            var proposal = InsightActions.Propose(source, finding)!;
            var vm = new ViewModel.Ai.InsightTaskFormVm
            {
                Source = source,
                EntityId = entityId,
                FindingCode = finding,
                SourceLabel = gate.SourceLabel!,
                CompanyId = gate.CompanyId,
                Title = ComposeTitle(proposal.TitleKey, gate.SourceLabel!),
                Reason = ComposeReason(proposal.ReasonKey, gate.SourceLabel!),
                Priority = proposal.Priority,
                DueDate = DateTime.Today.AddDays(proposal.DueInDays),
                SuggestedAssigneeEmployeeId = gate.SuggestedAssigneeEmployeeId ?? CurrentEmployeeId(),
                Assignees = await _tasks.ActiveEmployeesAsync(gate.CompanyId),
                ReturnUrl = SafeReturnUrl(returnUrl),
                ExistingOpenTaskId = await FindOpenDuplicateAsync(gate.CompanyId, source, entityId, finding),
            };

            return PartialView("_NewTask", vm);
        }

        // ---- the write (§6): one call to the real task service ----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateTask(ViewModel.Ai.InsightTaskFormVm form)
        {
            ArgumentNullException.ThrowIfNull(form);

            // REVALIDATED FROM SCRATCH. The posted source/finding are strings a browser sent; the fact
            // that this server produced them a moment ago proves nothing about what came back.
            var gate = await AuthoriseAsync(form.Source, form.EntityId, form.FindingCode);
            if (gate.Refusal != null) return gate.Refusal;

            var creator = CurrentEmployeeId();
            if (creator <= 0) return Refuse(T("تعذّر تحديد الموظف الحالي", "Could not resolve the current employee"));

            // The assignee must be an active employee OF THE RESOLVED COMPANY. Posting another company's
            // employee id must not route work across a tenant boundary.
            var assignees = await _tasks.ActiveEmployeesAsync(gate.CompanyId);
            if (!assignees.Any(a => a.Id == form.AssigneeEmployeeId))
                return Refuse(T("المسؤول المحدد غير صالح", "The selected assignee is not valid"));

            // Priority is the task service's own vocabulary, not a free string from the form.
            var priority = Priorities.Contains(form.Priority, StringComparer.Ordinal) ? form.Priority : "Normal";

            // DUPLICATE PROTECTION. Bounded by the OPEN task, so a recurrence can raise a new one later.
            var existing = await FindOpenDuplicateAsync(gate.CompanyId, form.Source, form.EntityId, form.FindingCode);
            if (existing.HasValue)
                return Json(new { ok = true, duplicate = true, taskId = existing.Value, url = TaskUrl(existing.Value) });

            var title = string.IsNullOrWhiteSpace(form.Title)
                ? ComposeTitle(InsightActions.Propose(form.Source, form.FindingCode)!.TitleKey, gate.SourceLabel!)
                : form.Title.Trim();

            // The origin line is appended SERVER-SIDE and is not taken from the form: it is the audit
            // trail, and a value the browser could edit would be an audit trail of whatever it liked.
            var description = (form.Reason ?? string.Empty).Trim();
            var origin = InsightActions.OriginLine(form.Source, form.EntityId, form.FindingCode);
            description = string.IsNullOrEmpty(description) ? origin : description + "\n\n" + origin;

            var input = new TaskSaveInput
            {
                Id = 0,
                Title = title,
                Description = description,
                AssigneeEmployeeId = form.AssigneeEmployeeId,
                Priority = priority,
                DueDate = form.DueDate,
                // Structural link only where the frozen registry has a code for it. An invented code
                // would store a link TaskLinkResolver returns null for - a link that looks real and
                // resolves to nothing is worse than none.
                EntityType = InsightActions.RegistryEntityCode(form.Source),
                EntityId = InsightActions.RegistryEntityCode(form.Source) == null ? null : form.EntityId,
            };

            var (ok, err, id) = await _tasks.SaveAsync(gate.CompanyId, input, creator);
            if (!ok) return Refuse(err ?? T("تعذّر إنشاء المهمة", "Could not create the task"));

            // Success is reported only AFTER persistence returned an id.
            return Json(new { ok = true, duplicate = false, taskId = id, url = TaskUrl(id) });
        }

        private static readonly string[] Priorities = { "Low", "Normal", "High", "Urgent" };

        private static string TaskUrl(int id) => $"/Tasks/Detail?id={id}";

        private IActionResult Refuse(string message) =>
            Json(new { ok = false, error = message });

        /// <summary>Only same-site relative paths may be echoed back as a return target.</summary>
        private string? SafeReturnUrl(string? candidate) =>
            !string.IsNullOrWhiteSpace(candidate) && Url.IsLocalUrl(candidate) ? candidate : null;

        private sealed record Gate(
            int CompanyId,
            IActionResult? Refusal,
            string? SourceLabel = null,
            int? SuggestedAssigneeEmployeeId = null);

        /// <summary>
        /// The whole gate: company, source existence within it, both permissions, and that this finding
        /// is one the source actually offers a task for.
        /// </summary>
        private async Task<Gate> AuthoriseAsync(string source, int entityId, string finding)
        {
            var scope = await _company.ResolveAsync();
            if (!scope.Ok)
                return new Gate(0, Refuse(L["You do not have permission to perform this action"].Value));

            if (!InsightActions.Sources.IsKnown(source) || entityId <= 0)
                return new Gate(scope.CompanyId, Refuse(T("مصدر غير معروف", "Unknown source")));

            // An action must exist for THIS finding on THIS source. Dead stock offers no replenishment,
            // and a finding that offers no task cannot be talked into producing one by posting to here.
            if (!InsightActions.AllowsTask(source, finding))
                return new Gate(scope.CompanyId, Refuse(T("لا يوجد إجراء متاح لهذه الملاحظة", "No action is available for this finding")));

            // ---- side one: may this user use the SOURCE module? ----
            var sourceOk = source switch
            {
                InsightActions.Sources.InventoryItem => await _invAccess.CanAsync("read"),
                _ => await _crmAccess.CanAsync("read"),
            };
            if (!sourceOk)
                return new Gate(scope.CompanyId, Refuse(L["You do not have permission to perform this action"].Value));

            // ---- side two: may this user CREATE a task? ----
            var context = await _businessContext.TryGetCurrentAsync();
            if (context == null)
                return new Gate(scope.CompanyId, Refuse(L["You do not have permission to perform this action"].Value));

            if (!await _tasksAccess.CanAsync(context, "create"))
                return new Gate(scope.CompanyId, Refuse(L["You do not have permission to perform this action"].Value));

            // ---- the source row must exist IN THE RESOLVED COMPANY ----
            //
            // A cross-company id is answered exactly like a missing one: same message, same shape. The
            // difference between "does not exist" and "exists elsewhere" is what a probe is looking for.
            var (label, owner) = source switch
            {
                InsightActions.Sources.InventoryItem => await _db.Items.AsNoTracking()
                    .Where(i => i.ID == entityId && i.CompanyID == scope.CompanyId)
                    .Select(i => new ValueTuple<string?, int?>((i.ItemCode ?? "") + " — " + i.Name, null))
                    .FirstOrDefaultAsync(),

                InsightActions.Sources.CrmOpportunity => await _db.Opportunities.AsNoTracking()
                    .Where(o => o.ID == entityId && o.CompanyID == scope.CompanyId)
                    .Select(o => new ValueTuple<string?, int?>(o.Title, o.OwnerEmployeeId))
                    .FirstOrDefaultAsync(),

                _ => await _db.CrmAccounts.AsNoTracking()
                    .Where(a => a.ID == entityId && a.CompanyID == scope.CompanyId)
                    .Select(a => new ValueTuple<string?, int?>(a.Name, a.OwnerEmployeeId))
                    .FirstOrDefaultAsync(),
            };

            if (label == null)
                return new Gate(scope.CompanyId, Refuse(T("السجل غير موجود", "The record was not found")));

            return new Gate(scope.CompanyId, null, label, owner);
        }

        /// <summary>An OPEN task already raised for this exact finding on this exact record, if any.</summary>
        /// <remarks>
        /// Bounded by Status != Done ON PURPOSE. The identity includes the finding, so a different
        /// problem always raises a new task; and once the follow-up is finished, the same problem
        /// recurring may raise another. An identity of "this record forever" would silently block a
        /// legitimate second follow-up months later, which is the failure nobody notices.
        /// </remarks>
        private async Task<int?> FindOpenDuplicateAsync(int companyId, string source, int entityId, string finding)
        {
            var key = InsightActions.DedupKey(source, entityId, finding);
            return await _db.TaskItems.AsNoTracking()
                .Where(t => t.CompanyId == companyId
                            && t.Status != "Done"
                            && t.Description != null
                            && t.Description.Contains(key))
                .OrderByDescending(t => t.ID)
                .Select(t => (int?)t.ID)
                .FirstOrDefaultAsync();
        }

        // ---- suggested wording. Resource-driven, so Arabic is a translation and not a copy ----
        private string ComposeTitle(string key, string subject) => key switch
        {
            "task.title.stockout" => T($"نفاد مخزون — {subject}", $"Out of stock — {subject}"),
            "task.title.reorder" => T($"إعادة طلب — {subject}", $"Reorder — {subject}"),
            "task.title.slowstock" => T($"مراجعة مخزون راكد — {subject}", $"Review slow stock — {subject}"),
            "task.title.oppOverdue" => T($"متابعة فرصة متأخرة — {subject}", $"Follow up overdue opportunity — {subject}"),
            "task.title.oppClosingSoon" => T($"فرصة قرب الإغلاق — {subject}", $"Opportunity closing soon — {subject}"),
            "task.title.oppFollowUp" => T($"متابعة فرصة — {subject}", $"Follow up opportunity — {subject}"),
            "task.title.acctDeclining" => T($"مراجعة تراجع التفاعل — {subject}", $"Review declining engagement — {subject}"),
            "task.title.acctOverdue" => T($"انكشاف صفقات متأخرة — {subject}", $"Overdue deal exposure — {subject}"),
            _ => T($"متابعة — {subject}", $"Follow up — {subject}"),
        };

        private string ComposeReason(string key, string subject) => key switch
        {
            "task.reason.stockout" => T(
                $"أُنشئت من تحليلات مخاطر المخزون: الصنف {subject} نفد مخزونه مع وجود طلب حديث.",
                $"Created from Inventory Risk Insights: {subject} is out of stock with recent demand."),
            "task.reason.reorder" => T(
                $"أُنشئت من تحليلات مخاطر المخزون: الصنف {subject} تحت نقطة إعادة الطلب أو تغطيته منخفضة.",
                $"Created from Inventory Risk Insights: {subject} is below its reorder point or has low days-of-cover."),
            "task.reason.slowstock" => T(
                $"أُنشئت من تحليلات مخاطر المخزون: الصنف {subject} راكد ولم يُصرف خلال النافذة المحددة.",
                $"Created from Inventory Risk Insights: {subject} is slow moving with no issues in the window."),
            "task.reason.oppOverdue" => T(
                $"أُنشئت من تحليلات الفرص: الفرصة {subject} تجاوزت تاريخ الإغلاق المتوقع.",
                $"Created from CRM Opportunity Insights: {subject} is past its expected close date."),
            "task.reason.oppClosingSoon" => T(
                $"أُنشئت من تحليلات الفرص: الفرصة {subject} قرب الإغلاق ولا توجد متابعة مفتوحة.",
                $"Created from CRM Opportunity Insights: {subject} is closing soon with no open follow-up."),
            "task.reason.oppFollowUp" => T(
                $"أُنشئت من تحليلات الفرص: الفرصة {subject} تحتاج متابعة.",
                $"Created from CRM Opportunity Insights: {subject} needs attention."),
            "task.reason.acctDeclining" => T(
                $"أُنشئت من تحليلات صحة الحسابات: تراجع التفاعل مع {subject} مقارنة بالفترة السابقة.",
                $"Created from CRM Account Health: engagement with {subject} declined against the previous window."),
            "task.reason.acctOverdue" => T(
                $"أُنشئت من تحليلات صحة الحسابات: لدى {subject} صفقات تجاوزت تاريخ الإغلاق المتوقع.",
                $"Created from CRM Account Health: {subject} has deals past their expected close date."),
            _ => T(
                $"أُنشئت من تحليلات CRM: {subject} يحتاج متابعة.",
                $"Created from CRM insights: {subject} needs attention."),
        };
    }
}
