using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionPartialInvoiceResult(bool Ok, string Code, string? Version = null,
    int? InvoiceId = null, decimal? Net = null, decimal? Tax = null);

public sealed record ConstructionPartialInvoiceReceiptRow(int ReceiptId, int ReceiptLineId,
    int OrderLineId, string? Description, decimal Received, decimal PreviouslyBilled, decimal Available);
public sealed record ConstructionPartialInvoiceSourcePage(bool Ok, string Code, string? Version = null,
    int? CurrencyId = null, int? CurrencyDecimals = null,
    IReadOnlyList<ConstructionPartialInvoiceReceiptRow>? Receipts = null, string? CurrencyCode = null, string? OrderNumber = null);

public interface IConstructionPartialInvoiceService
{
    Task<ConstructionPartialInvoiceSourcePage> ReadAsync(BusinessContext c, int project, int orderId, CancellationToken ct = default);
    Task<ConstructionPartialInvoiceResult> PreviewAsync(BusinessContext c, int project, int orderId, Guid requestId,
        IReadOnlyList<ConstructionInvoiceQuantityRequest> requested, CancellationToken ct = default);
    Task<ConstructionPartialInvoiceResult> IssueAsync(BusinessContext c, int project, int orderId, DateTime date,
        Guid requestId, string expectedVersion, IReadOnlyList<ConstructionInvoiceQuantityRequest> requested, string reason, CancellationToken ct = default);
}

// The private browser boundary delegates to this permission-checked, locked writer.
// Deployment requires the partial provenance schema and coordinated application activation.
// Receipts stay Posted/unassigned and the PO stays open for later receiving; only PayableService posts.
public sealed class ConstructionPartialInvoiceService(CrossDbContext db, IProjectsAccessService projects,
    IModuleAccessService inventory, IConstructionAuditService audit, ICurrencyRounding rounding, IPayableService payables) : IConstructionPartialInvoiceService
{
    private sealed record Source(PurchaseOrder Order, int Decimals, ConstructionPartialInvoiceHistory History,
        GoodsReceiptLine[] Receipts, string Version);
    private sealed record Planned(int OrderLineId, PurchaseLineInput Input, decimal Net, decimal Tax,
        ConstructionInvoiceQuantityRequest[] Slices);

    // The browser must display the same locked, admitted source that the writer uses.
    // This method supplies quantities only; it does not authorize a later post or quote prices.
    public async Task<ConstructionPartialInvoiceSourcePage> ReadAsync(BusinessContext c, int project,
        int orderId, CancellationToken ct = default)
    {
        if (!await Allowed(c, project, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges())
            return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, project, ct))
                return new(false, "ConcurrencyConflict");
            var source = await ReadSource(c, project, orderId, ct);
            if (source == null) return new(false, "PartialInvoiceSourceNotProven");
            var billed = source.History.Quantities!.GroupBy(x => x.ReceiptLineId)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Billed));
            var orders = source.Order.Lines.ToDictionary(x => x.ID);
            var rows = new List<ConstructionPartialInvoiceReceiptRow>();
            foreach (var receipt in source.Receipts.OrderBy(x => x.GoodsReceiptId).ThenBy(x => x.ID))
            {
                if (receipt.PurchaseOrderLineId is not int lineId || !orders.TryGetValue(lineId, out var line))
                    return new(false, "PartialInvoiceSourceNotProven");
                var previouslyBilled = billed.GetValueOrDefault(receipt.ID);
                var available = receipt.Qty - previouslyBilled;
                if (previouslyBilled < 0 || available < 0) return new(false, "InvoiceQuantityHistoryUnproven");
                rows.Add(new(receipt.GoodsReceiptId, receipt.ID, lineId, line.ItemDescription,
                    receipt.Qty, previouslyBilled, available));
            }
            var currencyCode = await db.Currencies.AsNoTracking().Where(x => x.ID == source.Order.CurrencyId)
                .Select(x => x.Code).SingleOrDefaultAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Ready", source.Version, source.Order.CurrencyId, source.Decimals, rows.AsReadOnly(), currencyCode, source.Order.OrderNo);
        }
        catch (OverflowException) { return new(false, "PartialInvoiceAmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { return new(false, "ConcurrencyConflict"); }
    }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private async Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) =>
        !c.IsExternalPortal && c.IsAuthenticated && c.EmployeeId is > 0 && c.CompanyId > 0 &&
        !db.CompanyScope.AllowsCrossCompany && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        inventory.Scope == EntityRegistry.ScopeInventory &&
        await inventory.CanAsync(c, "purchase", new PermissionTarget { CompanyId = c.CompanyId }, ct) &&
        await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    private async Task<Source?> ReadSource(BusinessContext c, int project, int orderId, CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.ID == orderId, ct);
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == orderId, ct);
        if (order == null || reservation == null || order.Lines.Any(x => x.ItemId is not > 0)) return null;
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.IsBaseline && x.Status == "Approved", ct);
        var functional = await db.Companies.AsNoTracking().Where(x => x.CompanyID == c.CompanyId)
            .Select(x => x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
        if (baseline == null || functional is not > 0 || functional != baseline.CurrencyId || order.CurrencyId != functional || order.ExchangeRate != 1m)
            return null;
        // Rounding services may memoize within a request. A fresh scalar read under this
        // transaction must agree with that cache and the immutable purchase precision.
        var currentDecimals = await db.Currencies.AsNoTracking().Where(x => x.ID == functional)
            .Select(x => (int?)x.DecimalPlaces).SingleOrDefaultAsync(ct);
        if (currentDecimals is not (>= 0 and <= 8)) return null;
        var decimals = await rounding.DecimalsAsync(c.CompanyId, order.CurrencyId);
        if (decimals != currentDecimals || decimals != await rounding.DecimalsAsync(c.CompanyId, null)) return null;
        if (!await db.Set<ConstructionPurchaseDraft>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId &&
            x.ProjectId == project && x.PurchaseOrderId == orderId && x.CurrencyId == functional && x.CurrencyDecimals == decimals, ct)) return null;
        var mapped = await ConstructionUnbilledReceiptWitness.ReadAsync(db, projects, audit, c, project, baseline, order, ct, decimals);
        if (mapped == null) return null;
        var scope = new ConstructionInvoiceScope(c.CompanyId, project, orderId);
        var history = await ConstructionPartialInvoiceHistoryReader.ReadAsync(db, scope, decimals, ct);
        if (!history.Ok) return null;
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == orderId).OrderBy(x => x.ID).ToArrayAsync(ct);
        var ids = receipts.Select(x => x.ID).ToArray();
        var lines = await db.GoodsReceiptLines.AsNoTracking().Where(x => ids.Contains(x.GoodsReceiptId)).OrderBy(x => x.ID).ToArrayAsync(ct);
        var version = Hash(new { Kind = "ConstructionPartialInvoice/v1", order.ID, order.CompanyID, order.ProjectId,
            order.VendorId, order.CurrencyId, order.ExchangeRate, order.Status, order.SubTotal, order.TaxTotal, order.GrandTotal,
            order.OrderDate, order.ExpectedDate, order.Notes, order.WarehouseId,
            Lines = order.Lines.OrderBy(x => x.ID).Select(x => new { x.ID, x.LineNo, x.ItemId, x.UoMId, x.Qty, x.ReceivedQty,
                x.UnitPrice, x.DiscountAmount, x.TaxRate, x.LineTotal, x.ItemDescription }), Receipts = receipts.Select(x => new {
                x.ID, x.Status, x.InvoiceId, x.VendorId, x.CurrencyId, x.ExchangeRate, x.WarehouseId, x.TotalCost, x.ReceiptDate }),
            ReceiptLines = lines, history.Quantities, Values = history.Values!.OrderBy(x => x.Key),
            Baseline = new { baseline.ID, baseline.RowVersion, baseline.CurrencyId }, Reservation = reservation.ID,
            Allocation = mapped.OrderBy(x => x.ID).Select(x => new { x.ID, x.SourceLineId, x.WbsNodeId, x.CostCodeId, x.CostCategory, x.NetDocumentAmount }) });
        return new(order, decimals, history, lines, version);
    }

    private bool Fits<T>(string property, decimal value)
    {
        var metadata = db.Model.FindEntityType(typeof(T))!.FindProperty(property)!;
        var scale = metadata.GetScale(); var precision = metadata.GetPrecision();
        if (scale is null || precision is null || scale is < 0 or > 28 || precision - scale is < 0 or > 28) return false;
        decimal limit = 1; for (var i = 0; i < precision - scale; i++) limit *= 10;
        return value >= 0 && value < limit && decimal.Round(value, scale.Value, MidpointRounding.AwayFromZero) == value;
    }
    private async Task<(string? Error, List<Planned>? Lines)> Plan(Source source, ConstructionInvoiceScope scope,
        Guid request, ConstructionInvoiceQuantityRequest[] input, CancellationToken ct)
    {
        var q = db.Model.FindEntityType(typeof(PurchaseInvoiceLine))!.FindProperty(nameof(PurchaseInvoiceLine.Qty))!;
        if (q.GetPrecision() is not int precision || q.GetScale() is not int scale) return ("InvoiceQuantityPrecisionUnproven", null);
        var receipts = source.Receipts.Select(x => new ConstructionInvoiceReceiptQuantity(scope, x.GoodsReceiptId, x.ID,
            x.PurchaseOrderLineId ?? 0, x.ItemId, x.UoMId, x.Qty, "Posted")).ToArray();
        var check = ConstructionPartialInvoiceQuantityRules.Validate(scope, source.Order.Status, false, request, precision, scale,
            source.Order.Lines.Select(x => new ConstructionInvoiceOrderQuantity(x.ID, x.ItemId!.Value, x.UoMId, x.Qty)).ToArray(),
            receipts, source.History.Quantities!, input);
        if (!check.Ok) return (check.Code, null);
        var result = new List<Planned>();
        foreach (var line in source.Order.Lines.OrderBy(x => x.ID))
        {
            var slices = input.Where(x => receipts.Single(r => r.ReceiptLineId == x.ReceiptLineId).OrderLineId == line.ID).ToArray();
            if (slices.Length == 0) continue;
            var quantity = slices.Sum(x => x.Quantity);
            decimal Round(decimal value) => decimal.Round(value, source.Decimals, MidpointRounding.AwayFromZero);
            var value = ConstructionPartialInvoiceValueRules.Validate(new(line.Qty, line.UnitPrice, line.DiscountAmount,
                line.TaxRate, line.LineTotal, Round(line.LineTotal * line.TaxRate / 100m)), source.History.Values![line.ID].ToArray(), quantity, source.Decimals);
            if (!value.Ok) return (value.Code, null);
            var grni = await (from item in db.Items.AsNoTracking()
                join category in db.ItemCategories.AsNoTracking() on item.ItemCategoryId equals category.ID
                join a in db.Accounts.AsNoTracking() on category.GrniAccountId equals (int?)a.ID
                join t in db.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
                where item.CompanyID == scope.CompanyId && category.CompanyID == scope.CompanyId && a.CompanyID == scope.CompanyId &&
                    item.ID == line.ItemId && item.ItemType == "Stockable" && a.IsActive && a.IsPostable && t.Code == "LIAB" && t.StatementType == "BalanceSheet"
                select a.ID).SingleOrDefaultAsync(ct);
            if (grni <= 0 || !Fits<PurchaseInvoiceLine>(nameof(PurchaseInvoiceLine.Qty), quantity) ||
                !Fits<PurchaseInvoiceLine>(nameof(PurchaseInvoiceLine.UnitPrice), line.UnitPrice) ||
                !Fits<PurchaseInvoiceLine>(nameof(PurchaseInvoiceLine.DiscountAmount), value.Discount) ||
                !Fits<PurchaseInvoiceLine>(nameof(PurchaseInvoiceLine.TaxRate), line.TaxRate) ||
                !Fits<PurchaseInvoiceLine>(nameof(PurchaseInvoiceLine.LineTotal), value.Net) ||
                slices.Any(x => !Fits<ConstructionPartialInvoiceSlice>(nameof(ConstructionPartialInvoiceSlice.Quantity), x.Quantity)))
                return ("PartialInvoicePersistenceNotRepresentable", null);
            result.Add(new(line.ID, new PurchaseLineInput { ItemDescription = line.ItemDescription ?? "Construction receipt clearing",
                Qty = quantity, UnitPrice = line.UnitPrice, DiscountAmount = value.Discount, TaxRate = line.TaxRate,
                ExpenseAccountId = grni }, value.Net, value.Tax, slices));
        }
        var net = result.Sum(x => x.Net); var tax = result.Sum(x => x.Tax);
        if (!Fits<PurchaseInvoice>(nameof(PurchaseInvoice.SubTotal), net) || !Fits<PurchaseInvoice>(nameof(PurchaseInvoice.TaxTotal), tax) ||
            !Fits<PurchaseInvoice>(nameof(PurchaseInvoice.GrandTotal), net + tax)) return ("PartialInvoiceAmountOverflow", null);
        return (null, result);
    }

    public async Task<ConstructionPartialInvoiceResult> PreviewAsync(BusinessContext c, int project, int orderId,
        Guid requestId, IReadOnlyList<ConstructionInvoiceQuantityRequest> requested, CancellationToken ct = default)
    {
        if (requested is not { Count: > 0 and <= 500 } || requested.Any(x => x == null) || requestId == Guid.Empty) return new(false, "InvalidPartialInvoiceRequest");
        var input = requested.OrderBy(x => x.ReceiptLineId).ToArray();
        if (!await Allowed(c, project, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, project, ct)) return new(false, "ConcurrencyConflict");
            var source = await ReadSource(c, project, orderId, ct); if (source == null) return new(false, "PartialInvoiceSourceNotProven");
            var plan = await Plan(source, new(c.CompanyId, project, orderId), requestId, input, ct);
            if (plan.Error != null) return new(false, plan.Error);
            var planned = plan.Lines!;
            await tx.CommitAsync(ct); return new(true, "Ready", source.Version, Net: planned.Sum(x => x.Net), Tax: planned.Sum(x => x.Tax));
        }
        catch (OverflowException) { return new(false, "PartialInvoiceAmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { return new(false, "ConcurrencyConflict"); }
    }

    public async Task<ConstructionPartialInvoiceResult> IssueAsync(BusinessContext c, int project, int orderId, DateTime date,
        Guid requestId, string expectedVersion, IReadOnlyList<ConstructionInvoiceQuantityRequest> requested, string reason, CancellationToken ct = default)
    {
        if (requested is not { Count: > 0 and <= 500 } || requested.Any(x => x == null) || requestId == Guid.Empty ||
            expectedVersion?.Length != 64 || date == default || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return new(false, "InvalidPartialInvoiceRequest");
        var input = requested.OrderBy(x => x.ReceiptLineId).ToArray(); var explanation = reason.Trim();
        var digest = Hash(new { c.CompanyId, project, orderId, Date = date.Date, requestId, expectedVersion, Lines = input, Reason = explanation, Actor = c.EmployeeId });
        if (!await Allowed(c, project, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, project, ct)) return new(false, "ConcurrencyConflict");
            var old = await db.Set<ConstructionPartialInvoice>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (old != null && (old.ProjectId != project || old.PurchaseOrderId != orderId || old.RequestHash != digest)) return new(false, "IdempotencyConflict");
            var source = await ReadSource(c, project, orderId, ct); if (source == null) return new(false, "PartialInvoiceSourceNotProven");
            if (old != null)
            {
                if (!old.IsSealed || !source.History.Quantities!.Any(x => x.RequestId == requestId && x.InvoiceId == old.PurchaseInvoiceId))
                    return new(false, "PartialInvoiceHistoryUnproven");
                await tx.CommitAsync(ct); return new(true, "AlreadyIssued", old.SourceVersion, old.PurchaseInvoiceId);
            }
            if (source.Version != expectedVersion) return new(false, "ConcurrencyConflict");
            var plan = await Plan(source, new(c.CompanyId, project, orderId), requestId, input, ct);
            if (plan.Error != null) return new(false, plan.Error);
            var lines = plan.Lines!;
            var written = await payables.CreatePurchaseInvoiceAsync(c.CompanyId, source.Order.VendorId, date.Date,
                lines.Select(x => x.Input).ToList(), explanation, c.EmployeeId, source.Order.CurrencyId, 1m, project);
            if (!written.ok || written.inv == null) return new(false, written.error ?? "PartialInvoiceWriterRefused");
            var invoice = written.inv;
            if (invoice.CreatedNetLineage.Count != lines.Count || invoice.JournalEntryId is not > 0 ||
                invoice.CreatedNetLineage.Select(x => x.InputOrdinal).Distinct().Count() != lines.Count)
                return new(false, "PartialInvoiceWriterLineageMissing");
            var header = new ConstructionPartialInvoice { CompanyID = c.CompanyId, ProjectId = project, PurchaseOrderId = orderId,
                PurchaseInvoiceId = invoice.ID, JournalEntryId = invoice.JournalEntryId.Value, RequestId = requestId, RequestHash = digest,
                SourceVersion = expectedVersion, CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(header); await db.SaveChangesAsync(ct);
            for (var index = 0; index < lines.Count; index++)
            {
                var lineage = invoice.CreatedNetLineage.SingleOrDefault(x => x.InputOrdinal == index);
                if (lineage == null) return new(false, "PartialInvoiceWriterLineageMissing");
                foreach (var part in lines[index].Slices)
                {
                    var receipt = source.Receipts.Single(x => x.ID == part.ReceiptLineId);
                    db.Add(new ConstructionPartialInvoiceSlice { HeaderId = header.ID, CompanyID = c.CompanyId, ProjectId = project,
                        PurchaseOrderId = orderId, PurchaseOrderLineId = lines[index].OrderLineId, PurchaseInvoiceId = invoice.ID,
                        PurchaseInvoiceLineId = lineage.InvoiceLineId, JournalEntryId = header.JournalEntryId, JournalEntryLineId = lineage.JournalLineId,
                        GoodsReceiptId = receipt.GoodsReceiptId, GoodsReceiptLineId = receipt.ID, Quantity = part.Quantity });
                }
            }
            await db.SaveChangesAsync(ct); header.IsSealed = true; await db.SaveChangesAsync(ct);
            var proof = await ConstructionPartialInvoiceHistoryReader.ReadAsync(db, new(c.CompanyId, project, orderId), source.Decimals, ct);
            if (!proof.Ok) return new(false, proof.Code);
            // Reconcile the newly sealed invoice with the current admitted baseline and
            // receipt graph before committing any payable, GL, provenance or event effect.
            if (await ReadSource(c, project, orderId, ct) == null) return new(false, "PartialInvoiceSourceNotProven");
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionPartialInvoice), header.ID, null,
                "IssuePartialPurchaseInvoice", null, JsonSerializer.Serialize(new { invoice.ID, header.RequestId, header.SourceVersion }),
                ConstructionChangeKind.Created, explanation, c.EmployeeId, nameof(ConstructionPartialInvoiceService), requestId) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "Issued", expectedVersion, invoice.ID, lines.Sum(x => x.Net), lines.Sum(x => x.Tax));
        }
        catch (OverflowException) { return new(false, "PartialInvoiceAmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { return new(false, "ConcurrencyConflict"); }
        finally { db.ChangeTracker.Clear(); }
    }

    private static bool Conflict(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException || current is Microsoft.Data.SqlClient.SqlException sql &&
                sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
