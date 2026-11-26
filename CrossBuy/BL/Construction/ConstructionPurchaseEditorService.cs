using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public interface IConstructionPurchaseEditorService
{
    Task<ConstructionPurchaseEditorPage?> ReadAsync(int projectId, int? orderId = null, CancellationToken ct = default);
    Task<ConstructionPurchaseDraftResult> CreateAsync(int projectId, ConstructionPurchaseEditorInput input, CancellationToken ct = default);
    Task<ConstructionAllocationWrite> AllocateAsync(int projectId, int orderId, ConstructionPurchaseEditorInput input, CancellationToken ct = default);
}

// Browser boundary only: company and actor come from the session-free context, financial writes stay in the existing writers.
public sealed class ConstructionPurchaseEditorService(CrossDbContext db, IBusinessContextAccessor contexts,
    IEnumerable<IModuleAccessService> modules, IProjectsAccessService projects, IConstructionWarehouseAccess warehouseAccess,
    IConstructionPurchaseDraftService drafts, IConstructionOrderAllocationService allocations, IConstructionAuditService audit)
    : IConstructionPurchaseEditorService
{
    private async Task<BusinessContext?> Actor(int projectId, CancellationToken ct)
    {
        var c = await contexts.TryGetCurrentAsync(ct);
        if (c == null || !c.IsAuthenticated || c.IsExternalPortal || c.EmployeeId is not > 0 || c.CompanyId <= 0 || db.CompanyScope.FilterCompanyId != c.CompanyId) return null;
        var inventory = modules.FirstOrDefault(x => x.Scope == EntityRegistry.ScopeInventory);
        return inventory != null && await inventory.CanAsync(c, "purchase", new PermissionTarget { CompanyId = c.CompanyId }, ct) &&
            await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct) &&
            await db.Projects.AsNoTracking().AnyAsync(x => x.ID == projectId && x.CompanyID == c.CompanyId, ct) ? c : null;
    }

    public async Task<ConstructionPurchaseEditorPage?> ReadAsync(int projectId, int? orderId = null, CancellationToken ct = default)
    {
        var c = await Actor(projectId, ct); if (c == null || db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return null;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, projectId, ct)) return null;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.ID == projectId && x.CompanyID == c.CompanyId, ct);
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.IsBaseline && x.Status == "Approved", ct);
        var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
        var currency = baseline == null ? null : await db.Currencies.AsNoTracking().SingleOrDefaultAsync(x => x.ID == baseline.CurrencyId, ct);
        var functional = await db.Companies.AsNoTracking().Where(x => x.CompanyID == c.CompanyId).Select(x => x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
        var code = policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode) ? "PolicyNotConfigured"
            : baseline == null ? "ApprovedBaselineRequired"
            : functional is not > 0 ? "FunctionalCurrencyNotProven"
            : currency == null ? "DocumentCurrencyNotProven"
            : functional != currency.ID ? "BudgetCurrencyMismatch"
            : currency.DecimalPlaces > 8 ? "DocumentPrecisionNotSupported" : "Ready";
        var input = new ConstructionPurchaseEditorInput { BaselineId = baseline?.ID ?? 0, BaselineVersion = baseline == null ? "" : Convert.ToBase64String(baseline.RowVersion),
            OrderDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), RequestId = Guid.NewGuid() };
        var vendors = new List<ConstructionReferenceOption>(); var warehouses = new List<ConstructionReferenceOption>();
        var items = new List<ConstructionReferenceOption>(); var units = new List<ConstructionReferenceOption>();
        var lines = new List<ConstructionPurchaseEditorLine>(); var dimensions = new List<ConstructionPurchaseEditorDimension>();
        string? number = null;
        if (!orderId.HasValue)
        {
            vendors = await db.Vendors.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.IsActive).OrderBy(x => x.ID).Select(x => new ConstructionReferenceOption(x.ID, "", x.Name, x.NameEn)).ToListAsync(ct);
            var candidates = await db.Warehouses.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.IsActive).OrderBy(x => x.Code).Select(x => new ConstructionReferenceOption(x.ID, x.Code, x.Name, x.NameEn)).ToListAsync(ct);
            foreach (var warehouse in candidates) if (await warehouseAccess.CanBindAsync(c, warehouse.Id, ct)) warehouses.Add(warehouse);
            items = await db.Items.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.IsActive).OrderBy(x => x.ItemCode).Select(x => new ConstructionReferenceOption(x.ID, x.ItemCode, x.Name, x.NameEn)).ToListAsync(ct);
            units = await db.UnitsOfMeasure.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.IsActive).OrderBy(x => x.Code).Select(x => new ConstructionReferenceOption(x.ID, x.Code, x.Name, x.NameEn)).ToListAsync(ct);
        }
        else
        {
            var fact = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct);
            var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.ID == orderId && x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct);
            if (fact == null || order == null) return null;
            number = order.OrderNo;
            if (order.Status != "Draft") code = "SourceAlreadyProcessed";
            else if (ConstructionPurchaseDraftService.CreationHash(order) != fact.InitialSourceVersion) code = "DraftSourceChanged";
            else if (baseline != null && fact.BudgetVersionId != baseline.ID) code = "ApprovedBaselineRequired";
            else if (currency != null && (fact.CurrencyId != currency.ID || fact.CurrencyDecimals != currency.DecimalPlaces)) code = "DraftCurrencyChanged";
            if (await (from request in db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking()
                       join approval in db.InventoryApprovals.AsNoTracking() on request.ApprovalId equals approval.ID
                       where request.CompanyID == c.CompanyId && request.ProjectId == projectId && request.PurchaseOrderId == orderId && approval.CompanyID == c.CompanyId && approval.Status == "Pending"
                       select request.ID).AnyAsync(ct)) code = "ApprovalAlreadyPending";
            lines = order.Lines.OrderBy(x => x.LineNo).Select(x => new ConstructionPurchaseEditorLine(x.ID, x.LineNo, x.ItemDescription, x.LineTotal)).ToList();
            if (code == "Ready")
            {
                var preview = await new ConstructionOrderAllocationService(db, projects, audit).Load(c, projectId, orderId.Value, false, ct);
                code = preview.Ok ? "Ready" : preview.Code;
                input = input with { SourceVersion = preview.Version ?? "" };
                if (preview.Ok)
                {
                    dimensions = await (from budget in db.Set<ConstructionBudgetLine>().AsNoTracking()
                                        join node in db.Set<WbsNode>().AsNoTracking() on budget.WbsNodeId equals node.ID
                                        join cost in db.Set<ConstructionCostCode>().AsNoTracking() on budget.CostCodeId equals cost.ID
                                        where budget.CompanyID == c.CompanyId && budget.ProjectId == projectId && budget.BudgetVersionId == baseline!.ID &&
                                            node.CompanyID == c.CompanyId && node.ProjectId == projectId && node.Status == "Active" && node.OwnsBudget && node.OwnsCost &&
                                            cost.CompanyID == c.CompanyId && cost.IsActive && cost.AllowDirectPosting && cost.Category == budget.CostCategory
                                        orderby node.Code, cost.Code
                                        select new ConstructionPurchaseEditorDimension(node.ID, cost.ID, node.Code, node.Name, node.NameEn, cost.Code, cost.Name, cost.NameEn)).ToListAsync(ct);
                    var current = await db.Set<ConstructionOrderAllocation>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId && x.IsSealed).OrderByDescending(x => x.RevisionNo).FirstOrDefaultAsync(ct);
                    if (current != null)
                    {
                        var saved = await db.Set<ConstructionOrderAllocationLine>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.AllocationId == current.ID).OrderBy(x => x.ID).ToListAsync(ct);
                        input = input with { RowsJson = JsonSerializer.Serialize(saved.Select(x => new { x.SourceLineId, x.WbsNodeId, x.CostCodeId, NetDocumentAmount = x.NetDocumentAmount.ToString(CultureInfo.InvariantCulture) })) };
                    }
                    if (dimensions.Count == 0) code = "DimensionNotEligible";
                }
            }
        }
        var page = new ConstructionPurchaseEditorPage(projectId, project.Code, project.Name, project.NameEn, orderId, number,
            currency?.Code ?? "", currency?.DecimalPlaces ?? 0, code == "Ready", code, input, vendors, warehouses, items, units, lines, dimensions);
        await tx.CommitAsync(ct); return page;
    }

    public async Task<ConstructionPurchaseDraftResult> CreateAsync(int projectId, ConstructionPurchaseEditorInput input, CancellationToken ct = default)
    {
        var c = await Actor(projectId, ct); if (c == null) return new(false, "AccessDenied");
        if (input.WarehouseId.HasValue && !await warehouseAccess.CanBindAsync(c, input.WarehouseId.Value, ct)) return new(false, "AccessDenied");
        if (!TryRows<ConstructionPurchaseDraftLine>(input.RowsJson, new[] { "ItemId", "Description", "Quantity", "UnitId", "UnitPrice", "Discount", "TaxRate" }, 500, out var rows) ||
            !DateOnly.TryParseExact(input.OrderDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return new(false, "InvalidDraftRequest");
        DateTime? expected = null;
        if (!string.IsNullOrWhiteSpace(input.ExpectedDate))
        {
            if (!DateOnly.TryParseExact(input.ExpectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return new(false, "InvalidDraftRequest");
            expected = parsed.ToDateTime(TimeOnly.MinValue);
        }
        byte[] version;
        if (string.IsNullOrWhiteSpace(input.BaselineVersion) || input.BaselineVersion.Length > 128) return new(false, "InvalidDraftRequest");
        try { version = Convert.FromBase64String(input.BaselineVersion); } catch (FormatException) { return new(false, "InvalidDraftRequest"); }
        return await drafts.CreateAsync(c, projectId, input.BaselineId, version, input.VendorId, input.WarehouseId,
            date.ToDateTime(TimeOnly.MinValue), expected, input.Notes, rows, input.RequestId, input.Reason, ct);
    }

    public async Task<ConstructionAllocationWrite> AllocateAsync(int projectId, int orderId, ConstructionPurchaseEditorInput input, CancellationToken ct = default)
    {
        var c = await Actor(projectId, ct); if (c == null) return new(false, "AccessDenied");
        if (!TryRows<ConstructionCommitmentSplit>(input.RowsJson, new[] { "SourceLineId", "WbsNodeId", "CostCodeId", "NetDocumentAmount" }, 10000, out var rows)) return new(false, "InvalidAllocation");
        if (!await db.Set<ConstructionPurchaseDraft>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId, ct)) return new(false, "PersistedDraftRequired");
        return await allocations.SaveAsync(c, projectId, orderId, input.SourceVersion ?? "", input.RequestId, rows, input.Reason, ct);
    }

    // Explicit invariant decimal strings; no locale guessing, floating-point conversion, duplicate keys or ignored supplied totals.
    private static bool TryRows<T>(string json, string[] fields, int maximum, out List<T> rows)
    {
        rows = new(); if (string.IsNullOrWhiteSpace(json) || json.Length > 1500000) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 || document.RootElement.GetArrayLength() > maximum) return false;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) return false;
                var properties = row.EnumerateObject().ToArray();
                if (properties.Length != fields.Length || !properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields)) return false;
                foreach (var property in properties.Where(x => x.Name is not ("Description" or "ItemId" or "UnitId")))
                {
                    if (property.Name is "Quantity" or "UnitPrice" or "Discount" or "TaxRate" or "NetDocumentAmount" && property.Value.ValueKind != JsonValueKind.String) return false;
                    var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                    if (text == null || !Regex.IsMatch(text, @"\A[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)) return false;
                }
            }
            rows = JsonSerializer.Deserialize<List<T>>(json, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString })!;
            return rows != null && rows.All(x => x != null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException) { return false; }
    }
}
