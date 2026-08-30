using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Hr;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
    // ============================================================================================
    // EMPLOYEE ONBOARDING — the service.
    //
    // Three rules shape every method here, and they are the reason this file is not thinner:
    //
    //  1. THE COMPANY COMES FROM BusinessContext, AND THE SUBJECT'S COMPANY COMES FROM THE ROW.
    //     Never from a parameter. A caller hands ids; ids are lookup keys, not claims. Every read
    //     carries `CompanyID == ctx.CompanyId` in the WHERE clause rather than as a check after the
    //     load, so a foreign id never materialises a row at all.
    //
    //  2. AUTHORIZATION IS HrAccessService, NOT A SECOND ENGINE. Managing onboarding is
    //     EmployeeManage; waiving a mandatory requirement is a stronger right; an employee acting on
    //     their own plan is EmployeeRequest, which HrAccessService answers self-only. No new
    //     vocabulary is introduced.
    //
    //  3. COMPLETION IS COMPUTED, NEVER ASSERTED. `CanCompleteAsync` walks the mandatory items and
    //     asks the document platform about each requirement. A UI that thinks a plan is finished is
    //     an opinion; this is the answer.
    //
    // WHAT IS NOT HERE, ON PURPOSE: no Task creation, no Workspace attention card, no business
    // event, no expiry worker. Each of those is a separate platform with its own owner, and this
    // batch integrates with the document platform only. The hooks they would need are named in the
    // report rather than half-built here.
    // ============================================================================================

    public sealed class OnboardingRequirement
    {
        public required EmployeeOnboardingItem Item { get; init; }

        // Null when the item requires no document. Otherwise the type it demands.
        public PlatformDocumentType? RequiredType { get; init; }

        // The document that satisfies the requirement right now, or null. "Right now" matters: an
        // expired document is not a satisfying document, so this goes null the day it lapses without
        // anybody editing anything.
        public PlatformDocument? SatisfyingDocument { get; init; }

        public bool RequiresDocument => Item.RequiredDocumentTypeID is > 0;
        public bool DocumentSatisfied => !RequiresDocument || SatisfyingDocument != null;

        // The item is settled AND, if it demanded a document, that document is actually there and
        // valid. A checkbox alone cannot make this true — which is the whole point of §9.
        public bool IsSatisfied => OnboardingItemStatus.IsSettled(Item.Status) && DocumentSatisfied;

        public bool IsOverdue(DateTime today) =>
            !OnboardingItemStatus.IsSettled(Item.Status)
            && Item.DueDate.HasValue && Item.DueDate.Value.Date < today.Date;
    }

    public sealed class OnboardingView
    {
        public required EmployeeOnboarding Plan { get; init; }
        public required IReadOnlyList<OnboardingRequirement> Requirements { get; init; }

        public int MandatoryTotal => Requirements.Count(r => r.Item.IsMandatory);
        public int MandatoryDone => Requirements.Count(r => r.Item.IsMandatory && r.IsSatisfied);
        public int DocumentsRequired => Requirements.Count(r => r.RequiresDocument);
        public int DocumentsPresent => Requirements.Count(r => r.RequiresDocument && r.SatisfyingDocument != null);
        public int OverdueCount(DateTime today) => Requirements.Count(r => r.IsOverdue(today));

        // READY means every mandatory requirement is satisfied — not "the status column says
        // Completed". The status column follows this; it never leads it.
        public bool IsReady => Requirements.Where(r => r.Item.IsMandatory).All(r => r.IsSatisfied);
    }

    public sealed record OnboardingOutcome(bool Ok, string? Error = null, EmployeeOnboarding? Plan = null)
    {
        // One refusal for "not yours", "does not exist" and "not allowed". Distinguishing them would
        // let a caller map the company's employees by watching which ids answer differently.
        public static OnboardingOutcome Denied() => new(false, "not_authorized");
        public static OnboardingOutcome Fail(string code) => new(false, code);
    }

    public interface IEmployeeOnboardingService
    {
        Task<OnboardingView?> GetAsync(int employeeId, CancellationToken ct = default);
        Task<OnboardingOutcome> StartAsync(int employeeId, int? templateId, DateTime? target, CancellationToken ct = default);
        Task<OnboardingOutcome> CompleteItemAsync(int itemId, string? notes, CancellationToken ct = default);
        Task<OnboardingOutcome> WaiveItemAsync(int itemId, string reason, CancellationToken ct = default);
        Task<OnboardingOutcome> CompleteAsync(int onboardingId, CancellationToken ct = default);
    }

    public sealed class EmployeeOnboardingService : IEmployeeOnboardingService
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;

        // The document platform exposes its tables through Set<T>() rather than named DbSet
        // properties (PlatformDocumentService does the same), so this follows its convention instead
        // of adding a second way to reach the same tables.
        private DbSet<PlatformDocument> Documents => _db.Set<PlatformDocument>();
        private DbSet<PlatformDocumentType> DocumentTypes => _db.Set<PlatformDocumentType>();

        private readonly IHrAccessService _hr;
        private readonly IReportClockShim _clock;

        public EmployeeOnboardingService(CrossDbContext db, IBusinessContextAccessor contexts,
            IHrAccessService hr, IReportClockShim clock)
        {
            _db = db;
            _contexts = contexts;
            _hr = hr;
            _clock = clock;
        }

        // ----------------------------------------------------------------------------------------
        // The gate. Returns the context only when the caller may act on THIS employee, so a method
        // that has a context has already proved both halves: the right, and the subject.
        // ----------------------------------------------------------------------------------------
        private async Task<BusinessContext?> GateAsync(int employeeId, string action, CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return null;

            // The subject's company is read from the Employee ROW. HrAccessService does this itself
            // for the target, and doing it here too is what lets the query below be written with the
            // company already known rather than discovered afterwards.
            var owning = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId)
                .Select(e => (int?)e.EmpCompanyID)
                .FirstOrDefaultAsync(ct);

            if (owning is null || owning.Value != ctx.CompanyId) return null;

            bool may = await _hr.CanAsync(ctx, action,
                PermissionTarget.ForSubjectEmployee(employeeId, ctx.CompanyId), ct);

            return may ? ctx : null;
        }

        // ----------------------------------------------------------------------------------------
        // READ. EmployeeView is the ordinary right; an employee reading their OWN plan is covered by
        // HrAccessService's aboutMe rule, so self-service needs no separate branch here.
        // ----------------------------------------------------------------------------------------
        public async Task<OnboardingView?> GetAsync(int employeeId, CancellationToken ct = default)
        {
            var ctx = await GateAsync(employeeId, HrActions.EmployeeView, ct);
            if (ctx == null) return null;

            var plan = await _db.EmployeeOnboardings.AsNoTracking()
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.EmployeeID == employeeId && o.CompanyID == ctx.CompanyId, ct);

            if (plan == null) return null;

            var requirements = await ResolveAsync(plan, ctx, ct);
            return new OnboardingView { Plan = plan, Requirements = requirements };
        }

        // ----------------------------------------------------------------------------------------
        // THE DOCUMENT REQUIREMENT RESOLVER — the heart of §9.
        //
        // For each item that demands a type, it looks for a PlatformDocument that is:
        //   this company's, attached to THIS employee, of THAT type, Active, and not expired.
        //
        // Every one of those five is load-bearing, and the tests remove them one at a time.
        // Note what is absent: no file name, no StorageKey, no path. Possession of a storage key
        // grants nothing because nothing here reads one.
        // ----------------------------------------------------------------------------------------
        private async Task<IReadOnlyList<OnboardingRequirement>> ResolveAsync(
            EmployeeOnboarding plan, BusinessContext ctx, CancellationToken ct)
        {
            var items = plan.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.ID).ToList();

            var typeIds = items.Where(i => i.RequiredDocumentTypeID is > 0)
                               .Select(i => i.RequiredDocumentTypeID!.Value)
                               .Distinct().ToList();

            if (typeIds.Count == 0)
                return items.Select(i => new OnboardingRequirement { Item = i }).ToList();

            var types = await DocumentTypes.AsNoTracking()
                .Where(t => typeIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);

            var today = _clock.Today;

            // ONE query for every requirement rather than one per item — an onboarding plan with
            // twelve document items would otherwise issue twelve round trips per page render.
            var docs = await Documents.AsNoTracking()
                .Where(d => d.CompanyID == ctx.CompanyId
                            && d.EntityType == EntityRegistry.Employee
                            && d.EntityId == plan.EmployeeID
                            && d.DocumentTypeId != null
                            && typeIds.Contains(d.DocumentTypeId!.Value)
                            && d.Status == "Active"
                            && (d.ExpiryDate == null || d.ExpiryDate >= today))
                .ToListAsync(ct);

            var byType = docs
                .GroupBy(d => d.DocumentTypeId!.Value)
                // Newest wins when several exist: a replaced document is normal for anything that
                // expires, and the current one is the one that answers.
                .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Id).First());

            return items.Select(i => new OnboardingRequirement
            {
                Item = i,
                RequiredType = i.RequiredDocumentTypeID is > 0 && types.TryGetValue(i.RequiredDocumentTypeID.Value, out var t) ? t : null,
                SatisfyingDocument = i.RequiredDocumentTypeID is > 0 && byType.TryGetValue(i.RequiredDocumentTypeID.Value, out var d) ? d : null,
            }).ToList();
        }

        // ----------------------------------------------------------------------------------------
        // START — idempotent by construction.
        // ----------------------------------------------------------------------------------------
        public async Task<OnboardingOutcome> StartAsync(int employeeId, int? templateId,
            DateTime? target, CancellationToken ct = default)
        {
            var ctx = await GateAsync(employeeId, HrActions.EmployeeManage, ct);
            if (ctx == null) return OnboardingOutcome.Denied();

            // ONE PLAN PER EMPLOYEE. A retry, a double-clicked button or a replayed request returns
            // the existing plan instead of creating a second one. The database enforces it too — a
            // unique index on (CompanyID, EmployeeID) — because this check alone is a race.
            var existing = await _db.EmployeeOnboardings
                .FirstOrDefaultAsync(o => o.EmployeeID == employeeId && o.CompanyID == ctx.CompanyId, ct);
            if (existing != null) return new OnboardingOutcome(true, null, existing);

            var employee = await _db.Employee.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ID == employeeId && e.EmpCompanyID == ctx.CompanyId, ct);
            if (employee == null) return OnboardingOutcome.Denied();

            // The template must be this company's. A template id from another tenant resolves to
            // nothing, which is the same answer a missing one gives.
            var template = templateId is > 0
                ? await _db.OnboardingTemplates.AsNoTracking().Include(t => t.Items)
                    .FirstOrDefaultAsync(t => t.ID == templateId && t.CompanyID == ctx.CompanyId, ct)
                : await _db.OnboardingTemplates.AsNoTracking().Include(t => t.Items)
                    .FirstOrDefaultAsync(t => t.CompanyID == ctx.CompanyId && t.IsDefault && t.IsActive, ct);

            if (templateId is > 0 && template == null) return OnboardingOutcome.Fail("template_not_found");

            var now = _clock.Now;
            var basis = employee.DateOfJoining == default ? now.Date : employee.DateOfJoining.Date;

            var plan = new EmployeeOnboarding
            {
                CompanyID = ctx.CompanyId,
                EmployeeID = employeeId,
                TemplateID = template?.ID,
                Status = OnboardingStatus.InProgress,
                StartedAt = now,
                TargetCompletionDate = target?.Date
                    ?? (template?.DefaultDurationDays is int d ? basis.AddDays(d) : null),
                CreatedBy = ctx.EmployeeId,
                CreatedAt = now,
            };

            foreach (var ti in (template?.Items ?? new List<OnboardingTemplateItem>()).OrderBy(x => x.SortOrder))
            {
                plan.Items.Add(new EmployeeOnboardingItem
                {
                    CompanyID = ctx.CompanyId,
                    ItemKey = ti.ItemKey,
                    TitleAr = ti.TitleAr,
                    TitleEn = ti.TitleEn,
                    DescriptionAr = ti.DescriptionAr,
                    DescriptionEn = ti.DescriptionEn,
                    Responsibility = ti.Responsibility,
                    IsMandatory = ti.IsMandatory,
                    // The offset becomes a REAL DATE here, once. Editing the template afterwards must
                    // not move a date somebody is already working to.
                    DueDate = ti.DueOffsetDays is int off ? basis.AddDays(off) : null,
                    SortOrder = ti.SortOrder,
                    RequiredDocumentTypeID = ti.RequiredDocumentTypeID,
                    Status = OnboardingItemStatus.Pending,
                    CreatedBy = ctx.EmployeeId,
                    CreatedAt = now,
                });
            }

            _db.EmployeeOnboardings.Add(plan);
            await _db.SaveChangesAsync(ct);
            return new OnboardingOutcome(true, null, plan);
        }

        // ----------------------------------------------------------------------------------------
        // COMPLETE ONE ITEM.
        // ----------------------------------------------------------------------------------------
        public async Task<OnboardingOutcome> CompleteItemAsync(int itemId, string? notes, CancellationToken ct = default)
        {
            var (ctx, item) = await LoadItemAsync(itemId, HrActions.EmployeeManage, ct);
            if (ctx == null || item == null) return OnboardingOutcome.Denied();

            // A DOCUMENT ITEM CANNOT BE TICKED. If the item demands a type, a valid document of that
            // type must already exist — completion reports reality, it does not create it. This is the
            // line between a checklist and a control.
            if (item.RequiredDocumentTypeID is > 0)
            {
                bool present = await HasValidDocumentAsync(
                    ctx.CompanyId, item.Onboarding!.EmployeeID, item.RequiredDocumentTypeID.Value, ct);
                if (!present) return OnboardingOutcome.Fail("required_document_missing");
            }

            var now = _clock.Now;
            item.Status = OnboardingItemStatus.Completed;
            item.CompletedAt = now;
            item.CompletedBy = ctx.EmployeeId;      // server identity, never a posted actor id
            if (!string.IsNullOrWhiteSpace(notes)) item.Notes = notes.Trim();
            item.UpdatedBy = ctx.EmployeeId;
            item.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return new OnboardingOutcome(true);
        }

        // ----------------------------------------------------------------------------------------
        // WAIVE — the stronger right, and the one that must leave evidence.
        // ----------------------------------------------------------------------------------------
        public async Task<OnboardingOutcome> WaiveItemAsync(int itemId, string reason, CancellationToken ct = default)
        {
            // A REASON IS PART OF THE AUTHORITY, not a nicety. Checked before the gate so an empty
            // reason cannot even reach the permission check and appear in a log as an attempt.
            if (string.IsNullOrWhiteSpace(reason)) return OnboardingOutcome.Fail("waiver_reason_required");

            // PerformanceManage is HrManager-only, where EmployeeManage is also held by HrOfficer.
            // Excusing a mandatory requirement is a management decision, so it takes the narrower
            // right — chosen from the existing vocabulary rather than by inventing "waive".
            var (ctx, item) = await LoadItemAsync(itemId, HrActions.PerformanceManage, ct);
            if (ctx == null || item == null) return OnboardingOutcome.Denied();

            var now = _clock.Now;
            item.Status = OnboardingItemStatus.Waived;
            item.WaivedAt = now;
            item.WaivedBy = ctx.EmployeeId;
            item.WaiverReason = reason.Trim();
            item.UpdatedBy = ctx.EmployeeId;
            item.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return new OnboardingOutcome(true);
        }

        // ----------------------------------------------------------------------------------------
        // COMPLETE THE PLAN — refused while anything mandatory is outstanding.
        // ----------------------------------------------------------------------------------------
        public async Task<OnboardingOutcome> CompleteAsync(int onboardingId, CancellationToken ct = default)
        {
            var ctxProbe = await _contexts.TryGetCurrentAsync(ct);
            if (ctxProbe is not { CompanyId: > 0 }) return OnboardingOutcome.Denied();

            var plan = await _db.EmployeeOnboardings.Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.ID == onboardingId && o.CompanyID == ctxProbe.CompanyId, ct);
            if (plan == null) return OnboardingOutcome.Denied();

            var ctx = await GateAsync(plan.EmployeeID, HrActions.EmployeeManage, ct);
            if (ctx == null) return OnboardingOutcome.Denied();

            // THE SERVER DECIDES. The view model computes the same thing for display, but this is the
            // authority: a caller that believes the plan is finished does not get to say so.
            var requirements = await ResolveAsync(plan, ctx, ct);
            var outstanding = requirements.Where(r => r.Item.IsMandatory && !r.IsSatisfied).ToList();
            if (outstanding.Count > 0) return OnboardingOutcome.Fail("mandatory_items_outstanding");

            var now = _clock.Now;
            plan.Status = OnboardingStatus.Completed;
            plan.CompletedAt = now;
            plan.CompletedBy = ctx.EmployeeId;
            plan.UpdatedBy = ctx.EmployeeId;
            plan.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return new OnboardingOutcome(true, null, plan);
        }

        // ----------------------------------------------------------------------------------------
        private async Task<(BusinessContext? Ctx, EmployeeOnboardingItem? Item)> LoadItemAsync(
            int itemId, string action, CancellationToken ct)
        {
            var probe = await _contexts.TryGetCurrentAsync(ct);
            if (probe is not { CompanyId: > 0 }) return (null, null);

            // The company predicate is in the WHERE clause. A foreign item id never loads.
            var item = await _db.EmployeeOnboardingItems
                .Include(i => i.Onboarding)
                .FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == probe.CompanyId, ct);

            if (item?.Onboarding == null) return (null, null);

            var ctx = await GateAsync(item.Onboarding.EmployeeID, action, ct);
            return ctx == null ? (null, null) : (ctx, item);
        }

        private Task<bool> HasValidDocumentAsync(int companyId, int employeeId, long typeId, CancellationToken ct)
        {
            var today = _clock.Today;
            return Documents.AsNoTracking().AnyAsync(d =>
                d.CompanyID == companyId
                && d.EntityType == EntityRegistry.Employee
                && d.EntityId == employeeId
                && d.DocumentTypeId == typeId
                && d.Status == "Active"
                && (d.ExpiryDate == null || d.ExpiryDate >= today), ct);
        }
    }

    // A one-method clock seam so the expiry comparisons above are testable without waiting for a
    // document to lapse. Deliberately tiny: the platform already has clocks, and this exists only so
    // this service does not call DateTime.Now inline where a test cannot reach it.
    public interface IReportClockShim
    {
        DateTime Now { get; }
        DateTime Today => Now.Date;
    }

    public sealed class SystemOnboardingClock : IReportClockShim
    {
        public DateTime Now => DateTime.Now;
    }
}
