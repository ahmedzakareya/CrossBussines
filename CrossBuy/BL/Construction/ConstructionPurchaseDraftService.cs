using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionPurchaseDraftLine(int? ItemId, string? Description, decimal Quantity,
    int? UnitId, decimal UnitPrice, decimal Discount, decimal TaxRate);
public sealed record ConstructionPurchaseDraftResult(bool Ok, string Code, int? OrderId = null,
    IReadOnlyList<int>? LineIds = null);
public interface IConstructionPurchaseDraftService
{
    Task<ConstructionPurchaseDraftResult> CreateAsync(BusinessContext context, int projectId, int baselineId,
        byte[] baselineVersion, int vendorId, int? warehouseId, DateTime orderDate, DateTime? expectedDate,
        string? notes, IReadOnlyList<ConstructionPurchaseDraftLine> lines, Guid requestId, string reason, CancellationToken ct = default);
}

// Private source-identity foundation. It never marks an order Approved or reserves spending authority.
public sealed class ConstructionPurchaseDraftService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit, ICurrencyRounding rounding) : IConstructionPurchaseDraftService
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static string CreationHash(PurchaseOrder source, IEnumerable<PurchaseOrderLine>? lines = null) => Hash(new { Kind = "ConstructionPurchaseDraft/v1", CreatedAs = "Draft",
        source.ID, source.CompanyID, source.OrderNo, source.ProjectId, source.VendorId, source.WarehouseId, source.CurrencyId,
        source.ExchangeRate, source.OrderDate, source.ExpectedDate, source.Notes, source.SubTotal, source.TaxTotal, source.GrandTotal,
        Lines = (lines ?? source.Lines).OrderBy(x => x.ID) });
    private async Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) => !c.IsExternalPortal && c.IsAuthenticated &&
        c.EmployeeId is > 0 && c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);
    private bool Fits<T>(string property, decimal value)
    {
        var metadata = db.Model.FindEntityType(typeof(T))!.FindProperty(property)!;
        var scale = metadata.GetScale(); var precision = metadata.GetPrecision();
        if (scale is null || precision is null || scale < 0 || scale > 28 || precision - scale is < 0 or > 28) return false;
        decimal limit = 1; for (var i = 0; i < precision - scale; i++) limit *= 10;
        return value >= 0 && value < limit && decimal.Round(value, scale.Value, MidpointRounding.AwayFromZero) == value;
    }
    private async Task<ConstructionPurchaseDraftResult> Receipt(BusinessContext c, int project, int orderId, string code, string sourceVersion, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.ID == orderId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (order == null) return new(false, "DraftSourceMissing");
        if (CreationHash(order) != sourceVersion) return new(false, "DraftSourceChanged");
        var ids = order.Lines.OrderBy(x => x.LineNo).Select(x => x.ID).ToList();
        return ids.Count == 0 ? new(false, "DraftSourceMissing") : new(true, code, orderId, ids.AsReadOnly());
    }

    public async Task<ConstructionPurchaseDraftResult> CreateAsync(BusinessContext c, int projectId, int baselineId,
        byte[] baselineVersion, int vendorId, int? warehouseId, DateTime orderDate, DateTime? expectedDate,
        string? notes, IReadOnlyList<ConstructionPurchaseDraftLine> lines, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000 ||
            baselineVersion is not { Length: > 0 } || lines is not { Count: > 0 and <= 500 } || orderDate == default ||
            expectedDate?.Date < orderDate.Date || notes?.Length > 4000) return new(false, "InvalidDraftRequest");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        var input = lines.ToArray(); var version = baselineVersion.ToArray();
        if (input.Any(x => x == null || x.Quantity <= 0 || (x.ItemId == null && string.IsNullOrWhiteSpace(x.Description)) || x.Description?.Length > 1000 ||
            !Fits<PurchaseOrderLine>(nameof(PurchaseOrderLine.Qty), x.Quantity) ||
            !Fits<PurchaseOrderLine>(nameof(PurchaseOrderLine.UnitPrice), x.UnitPrice) ||
            !Fits<PurchaseOrderLine>(nameof(PurchaseOrderLine.DiscountAmount), x.Discount) ||
            !Fits<PurchaseOrderLine>(nameof(PurchaseOrderLine.TaxRate), x.TaxRate))) return new(false, "InvalidDraftLines");
        var hash = Hash(new { projectId, baselineId, Version = version, vendorId, warehouseId, Date = orderDate.Date,
            Expected = expectedDate?.Date, notes, Lines = input, Actor = c.EmployeeId, Reason = reason.Trim() });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var old = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (old != null)
            {
                if (old.ProjectId != projectId || old.RequestHash != hash) return new(false, "IdempotencyConflict");
                var receipt = await Receipt(c, projectId, old.PurchaseOrderId, "AlreadyCreated", old.InitialSourceVersion, ct);
                await tx.CommitAsync(ct); return receipt; // historical identity, never approval
            }
            var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
            if (policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode)) return new(false, "PolicyNotConfigured");
            var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == baselineId && x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.IsBaseline && x.Status == "Approved", ct);
            if (baseline == null) return new(false, "ApprovedBaselineRequired");
            if (!baseline.RowVersion.SequenceEqual(version)) return new(false, "ConcurrencyConflict");
            // Explicit company identity, not CurrencyService's historical EGP fallback. FX drafts remain unsupported.
            var functional = await db.Companies.AsNoTracking().Where(x => x.CompanyID == c.CompanyId).Select(x => x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
            if (functional is not > 0) return new(false, "FunctionalCurrencyNotProven");
            if (functional != baseline.CurrencyId) return new(false, "BudgetCurrencyMismatch");
            if (!await db.Currencies.AsNoTracking().AnyAsync(x => x.ID == baseline.CurrencyId, ct)) return new(false, "DocumentCurrencyNotProven");
            if (!await db.Vendors.AsNoTracking().AnyAsync(x => x.ID == vendorId && x.CompanyID == c.CompanyId && x.IsActive, ct)) return new(false, "SupplierNotFound");
            if (warehouseId.HasValue && !await db.Warehouses.AsNoTracking().AnyAsync(x => x.ID == warehouseId && x.CompanyID == c.CompanyId && x.IsActive, ct)) return new(false, "WarehouseNotFound");
            var itemIds = input.Where(x => x.ItemId.HasValue).Select(x => x.ItemId!.Value).Distinct().ToArray();
            var items = await db.Items.AsNoTracking().Where(x => itemIds.Contains(x.ID) && x.CompanyID == c.CompanyId && x.IsActive).ToDictionaryAsync(x => x.ID, ct);
            if (items.Count != itemIds.Length) return new(false, "ItemNotFound");
            // Units are company-owned too; a valid unit ID from another company is not a valid source line.
            var effectiveUnits = input.Select(x => x.UnitId ?? (x.ItemId.HasValue ? (int?)items[x.ItemId.Value].BaseUoMId : null)).ToArray();
            var units = effectiveUnits.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            if (await db.UnitsOfMeasure.AsNoTracking().CountAsync(x => units.Contains(x.ID) && x.CompanyID == c.CompanyId && x.IsActive, ct) != units.Length) return new(false, "UnitNotFound");
            var decimals = await rounding.DecimalsAsync(c.CompanyId, baseline.CurrencyId);
            var headerScale = db.Model.FindEntityType(typeof(PurchaseOrder))!.FindProperty(nameof(PurchaseOrder.SubTotal))!.GetScale();
            var lineScale = db.Model.FindEntityType(typeof(PurchaseOrderLine))!.FindProperty(nameof(PurchaseOrderLine.LineTotal))!.GetScale();
            if (decimals < 0 || headerScale == null || lineScale == null || decimals > Math.Min(headerScale.Value, lineScale.Value)) return new(false, "DocumentPrecisionNotSupported");
            decimal R(decimal value) => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
            var order = new PurchaseOrder { CompanyID = c.CompanyId, ProjectId = projectId, VendorId = vendorId,
                WarehouseId = warehouseId, CurrencyId = baseline.CurrencyId, ExchangeRate = 1, Status = "Draft",
                OrderDate = orderDate.Date, ExpectedDate = expectedDate?.Date, Notes = notes,
                CreatedBy = string.IsNullOrEmpty(c.UserId) ? null : c.UserId, CreatedAt = DateTime.UtcNow };
            for (var index = 0; index < input.Length; index++)
            {
                var line = input[index];
                var raw = line.Quantity * line.UnitPrice - line.Discount;
                if (raw < 0) return new(false, "InvalidDraftLines");
                var net = R(raw); var tax = R(net * line.TaxRate / 100m);
                if (!Fits<PurchaseOrderLine>(nameof(PurchaseOrderLine.LineTotal), net)) return new(false, "AmountOverflow");
                order.Lines.Add(new PurchaseOrderLine { LineNo = order.Lines.Count + 1, ItemId = line.ItemId,
                    ItemDescription = line.Description, Qty = line.Quantity, UoMId = effectiveUnits[index],
                    UnitPrice = line.UnitPrice, DiscountAmount = line.Discount, TaxRate = line.TaxRate, LineTotal = net });
                order.SubTotal += net; order.TaxTotal += tax;
            }
            order.GrandTotal = order.SubTotal + order.TaxTotal;
            if (!Fits<PurchaseOrder>(nameof(PurchaseOrder.SubTotal), order.SubTotal) ||
                !Fits<PurchaseOrder>(nameof(PurchaseOrder.TaxTotal), order.TaxTotal) ||
                !Fits<PurchaseOrder>(nameof(PurchaseOrder.GrandTotal), order.GrandTotal)) return new(false, "AmountOverflow");
            db.Add(order); await db.SaveChangesAsync(ct);
            order.OrderNo = FormattableString.Invariant($"PO-{orderDate:yyyy}-{order.ID:D5}"); await db.SaveChangesAsync(ct);
            var source = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ID == order.ID && x.CompanyID == c.CompanyId, ct);
            var fact = new ConstructionPurchaseDraft { CompanyID = c.CompanyId, ProjectId = projectId, PurchaseOrderId = source.ID,
                BudgetVersionId = baseline.ID, CurrencyId = baseline.CurrencyId, CurrencyDecimals = decimals,
                RequestId = requestId, RequestHash = hash,
                InitialSourceVersion = CreationHash(source),
                Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(fact);
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, projectId, nameof(ConstructionPurchaseDraft), source.ID,
                null, "CreateDraftPurchaseOrder", null, JsonSerializer.Serialize(new { source.ID, fact.InitialSourceVersion, fact.CurrencyId, fact.CurrencyDecimals }),
                ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId, nameof(ConstructionPurchaseDraftService), requestId) });
            await db.SaveChangesAsync(ct); var result = await Receipt(c, projectId, source.ID, "DraftCreated", fact.InitialSourceVersion, ct);
            if (!result.Ok) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return result; }
            await tx.CommitAsync(ct); return result;
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }
    private static bool Conflict(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
            if (e is DbUpdateConcurrencyException || e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
