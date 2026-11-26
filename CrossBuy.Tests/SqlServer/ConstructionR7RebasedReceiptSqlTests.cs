using System.Reflection;
using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebasedReceiptSqlTests(UatSqlProbeFixture sql)
{
    internal sealed class Inventory : IInventoryAccessService
    {
        public bool Allowed = true, WarehouseAllowed = true;
        public int? CurrentEmployeeId() => 8;
        public Task<List<string>> MyRolesAsync() => Task.FromResult(new List<string> { "WarehouseKeeper" });
        public Task<bool> CanAsync(string action) => Task.FromResult(Allowed && action == "doc");
        public Task<bool> CanUseWarehouseAsync(int warehouseId) => Task.FromResult(Allowed && WarehouseAllowed);
        public Task<string> RoleLabelAsync(bool isAr) => Task.FromResult("WarehouseKeeper");
    }

    private sealed class ProjectRead(bool allowed) : IProjectsAccessService
    {
        // Receiving needs project visibility, not permission to view confidential budgets.
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(allowed && action == ProjectsActions.Read);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }

    internal static async Task Apply(CrossDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "construction_r7_purchase_approvals.sql", "construction_r7_rebase_requests.sql",
            "construction_r7_rebase_decisions.sql", "construction_r7_exposure_rebases.sql", "construction_r7_purchase_rebase_requests.sql",
            "construction_r7_purchase_rebase_decisions.sql" })
            foreach (var batch in Regex.Split(await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", name)), @"(?im)^\s*GO\s*\r?$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
    }

    internal static async Task<ConstructionR7PurchaseApprovalTests.Setup> Prepare(CrossDbContext db,
        bool stopBeforeRebase = false, bool withWarehouseConsent = false)
    {
        // Real purchase creation/allocation and original reservation. Only the approved revised
        // budget is fixture data; no fabricated creation/admission/stock/journal witnesses.
        var s = await ConstructionR7PurchaseApprovalTests.Arrange(db, 10m);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var reservation = await ConstructionR7ReservationTests.Service(db).ReserveAsync(actor, s.Seed.Project,
            s.Allocation, s.Version, Guid.NewGuid(), "Original source before budget revision"); Assert.True(reservation.Ok, reservation.Code);
        var original = await db.Set<ConstructionBudgetVersion>().SingleAsync(x => x.ID == s.Seed.Baseline);
        original.IsBaseline = false; original.Status = "Superseded";
        var revised = new ConstructionBudgetVersion { CompanyID = 1, ProjectId = s.Seed.Project, CurrencyId = s.Seed.Currency,
            PreviousVersionId = original.ID, VersionNo = 2, Kind = "Revised", Status = "Approved", IsBaseline = true,
            CreatedBy = 7, ApprovedBy = 9, ApprovedAt = DateTime.UtcNow, Reason = "Approved test revision" };
        db.Add(revised); await db.SaveChangesAsync();
        var lines = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x => x.BudgetVersionId == original.ID).ToListAsync();
        db.AddRange(lines.Select(x => new ConstructionBudgetLine { CompanyID = 1, ProjectId = s.Seed.Project,
            BudgetVersionId = revised.ID, WbsNodeId = x.WbsNodeId, CostCodeId = x.CostCodeId, CostCategory = x.CostCategory,
            Amount = x.Amount, ChangeReason = "Preserve test dimensions" })); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (stopBeforeRebase) return s;
        var rebase = new ConstructionReservationRebaseRequestService(db, new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(db));
        var preview = await rebase.PreviewAsync(actor, s.Seed.Project, reservation.Id!.Value, revised.ID); Assert.True(preview.Ok, preview.Code);
        var request = await rebase.RequestAsync(actor, s.Seed.Project, reservation.Id.Value, revised.ID, preview.Version!, Guid.NewGuid(), "Rebase unchanged purchase"); Assert.True(request.Ok, request.Code);
        var review = await rebase.DecideAsync(ConstructionR7RebasedPurchaseDecisionTests.Actor(9), s.Seed.Project, request.Id!.Value, preview.Version!, true, Guid.NewGuid(), "Independent rebase review"); Assert.True(review.Ok, review.Code);
        var ready = await rebase.ReadSourceReadinessAsync(actor, s.Seed.Project, request.Id.Value); Assert.True(ready.Ok, ready.Code);
        Assert.True((await rebase.ActivateExposureAsync(actor, s.Seed.Project, request.Id.Value, ready.Version!, Guid.NewGuid(), "Activate exposure")).Ok);
        IConstructionWarehouseAccess? warehouseAccess = null;
        int? consentDecisionId = null;
        if (withWarehouseConsent)
        {
            var other = new Project { CompanyID = 1, Code = "R7-RECEIPT-CONSENT-OTHER", Name = "Consent source project", IsActive = true };
            var destinationWarehouse = new Warehouse { CompanyID = 1, Code = "R7-RECEIPT-CONSENT-DEST", Name = "Consent destination", IsActive = true };
            db.AddRange(other, destinationWarehouse, new ConstructionCompanyPolicy { CompanyID = 1,
                PolicyKey = "CrossSiteWarehouse", Mode = "ApprovedException", Reason = "Explicit test policy",
                UpdatedBy = 7, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var sourceSite = new ConstructionSite { CompanyID = 1, ProjectId = other.ID, WarehouseId = s.Seed.Warehouse,
                Code = "R7-RECEIPT-CONSENT-SOURCE", Name = "Consent source site", IsActive = true };
            var destinationSite = new ConstructionSite { CompanyID = 1, ProjectId = s.Seed.Project, WarehouseId = destinationWarehouse.ID,
                Code = "R7-RECEIPT-CONSENT-TARGET", Name = "Consent destination site", IsActive = true };
            db.AddRange(sourceSite, destinationSite);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            warehouseAccess = new ConstructionR7WarehouseClassificationTests.WarehouseAccess(s.Seed.Warehouse, destinationWarehouse.ID);
            var consentActor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = actor };
            var consentService = new ConstructionRebasedWarehouseConsentService(db, consentActor,
                new ConstructionR7ActualCostTests.Access(), new[] { new ConstructionR7PurchaseApprovalTests.InventoryAuthority() },
                warehouseAccess, new ConstructionAuditService(db));
            var consentQuote = await consentService.PreviewAsync(s.Seed.Project, s.Order, destinationSite.ID);
            Assert.True(consentQuote.Ok, consentQuote.Code);
            var repeatedConsentQuote = await consentService.PreviewAsync(s.Seed.Project, s.Order, destinationSite.ID);
            Assert.Equal(consentQuote.Version, repeatedConsentQuote.Version);
            var consentRequest = await consentService.RequestAsync(s.Seed.Project, s.Order, destinationSite.ID,
                consentQuote.Version!, Guid.NewGuid(), "Approve exact rebased receipt destination");
            Assert.True(consentRequest.Ok, consentRequest.Code);
            consentActor.Current = ConstructionR7BudgetExceptionTests.Approver;
            var consentDecision = await consentService.DecideAsync(s.Seed.Project, consentRequest.Id!.Value,
                "Approved", Guid.NewGuid(), "Independent destination approval");
            Assert.True(consentDecision.Ok, consentDecision.Code);
            consentDecisionId = consentDecision.Id;
        }

        var accessor = new ConstructionR7PurchaseApprovalTests.ActorAccessor
            { Current = withWarehouseConsent ? actor : ConstructionR7RebasedPurchaseDecisionTests.Actor(9) };
        var approval = ConstructionR7PurchaseApprovalTests.Service(db, accessor, warehouses: warehouseAccess,
            projectAccess: new ConstructionR7ActualCostTests.Access());
        var quote = consentDecisionId.HasValue
            ? await approval.PreviewRebasedRequestWithWarehouseConsentAsync(s.Seed.Project, s.Order, consentDecisionId.Value)
            : await approval.PreviewRebasedRequestAsync(s.Seed.Project, s.Order);
        Assert.True(quote.Ok, quote.Code);
        var pending = consentDecisionId.HasValue
            ? await approval.RequestRebasedWithWarehouseConsentAsync(s.Seed.Project, s.Order, consentDecisionId.Value,
                quote.Version!, Guid.NewGuid(), "Review purchase with approved destination")
            : await approval.RequestRebasedAsync(s.Seed.Project, s.Order, quote.Version!, Guid.NewGuid(), "Review purchase");
        Assert.True(pending.Ok, pending.Code);
        accessor.Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(10);
        var admitted = await approval.ApproveAsync(pending.ApprovalId!.Value, Guid.NewGuid(), "Independent purchase admission"); Assert.True(admitted.Ok, admitted.Code);
        await ConstructionR7StockInvoiceTests.Accounts(db, s);
        var year = new CrossBuy.Models.Context.Accounting.FiscalYear { CompanyID = 1, Name = "Rebased receipts",
            StartDate = DateTime.Today.AddDays(-10), EndDate = DateTime.Today.AddDays(10), Status = "Open" };
        db.Add(year); await db.SaveChangesAsync(); db.FiscalPeriods.Add(new() { FiscalYearId = year.ID, PeriodNo = 1,
            StartDate = year.StartDate, EndDate = year.EndDate, Status = "Open" }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return s;
    }

    internal ServiceProvider Graph(UatSqlProbeFixture.ProbeDatabase probe, Inventory inventory, bool guard = true, bool projectRead = true)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddLocalization();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()); services.AddScoped(_ => sql.ContextFor(probe, 1));
        services.AddScoped<ICurrencyService, CurrencyService>(); services.AddScoped<ICurrencyRounding, CurrencyRounding>();
        services.AddScoped<IFiscalPeriodService, FiscalPeriodService>(); services.AddScoped<IJournalEntryService, JournalEntryService>();
        services.AddScoped<IStockService, StockService>(); services.AddScoped<IPayableService, PayableService>();
        services.AddScoped<IFixedAssetService, FixedAssetService>(); services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
        services.AddScoped<IProcurementService, ProcurementService>(); services.AddScoped<IEntityRegistry, EntityRegistry>();
        services.AddScoped<IBusinessContextAccessor>(_ => new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(8) });
        services.AddScoped<IBusinessEventService, BusinessEventService>();
        services.AddScoped<IInventoryAccessService>(_ => inventory); services.AddScoped<IProjectsAccessService>(_ => new ProjectRead(projectRead));
        services.AddScoped<IConstructionAuditService, ConstructionAuditService>();
        if (guard) services.AddScoped<IConstructionRebasedReceiptService, ConstructionRebasedReceiptService>();
        services.AddScoped<INotificationService>(_ => DispatchProxy.Create<INotificationService, ConstructionR7ProcurementWriterSqlTests.SilentNotifications>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = false });
    }

    [SkippableTheory]
    [InlineData("partial")]
    [InlineData("split")]
    [InlineData("full")]
    [InlineData("variance")]
    [InlineData("overreceive")]
    [InlineData("denied")]
    [InlineData("warehouse-denied")]
    [InlineData("project-denied")]
    [InlineData("missing-guard")]
    [InlineData("stale-policy")]
    [InlineData("foreign-mapping")]
    public async Task Native_receipt_preserves_rebased_commitment_and_rolls_back_invalid_effects(string mode)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBGRN");
        try
        {
            ConstructionR7PurchaseApprovalTests.Setup s;
            using (var db = sql.ContextFor(probe, 1)) { await Apply(db); s = await Prepare(db); }
            if (mode == "stale-policy")
            { using var stale = sql.ContextFor(probe, 1); await stale.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "Warning")); }
            if (mode == "foreign-mapping")
            {
                using var mapping = sql.ContextFor(probe, 1);
                var warehouse = await mapping.PurchaseOrders.AsNoTracking().Where(x => x.ID == s.Order).Select(x => x.WarehouseId).SingleAsync();
                var other = new Project { CompanyID = 1, Code = "R7-RECEIPT-WH-OTHER", Name = "Other project", IsActive = true };
                mapping.Add(other); await mapping.SaveChangesAsync();
                mapping.Add(new ConstructionSite { CompanyID = 1, ProjectId = other.ID, WarehouseId = warehouse,
                    Code = "R7-RECEIPT-WH-SITE", Name = "Foreign warehouse mapping", IsActive = true });
                await mapping.SaveChangesAsync();
            }
            using var graph = Graph(probe, new Inventory { Allowed = mode != "denied", WarehouseAllowed = mode != "warehouse-denied" }, mode != "missing-guard", mode != "project-denied");
            var qty = mode is "partial" or "split" ? .4m : mode == "overreceive" ? 1.1m : 1m;
            Task<(bool ok, string? error, CrossBuy.Models.Context.Inventory.GoodsReceipt? gr)> Receive(decimal amount, decimal cost = 10m) =>
                graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(1, s.Seed.Vendor, s.Seed.Warehouse, s.Order,
                    DateTime.Today, "Rebased real writer", new() { new() { ItemId = s.Seed.Item, Qty = amount,
                        UoMId = s.Seed.Unit, UnitCost = cost, PurchaseOrderLineId = s.Lines.Single() } }, "rebased-native", s.Seed.Currency, 1m);
            var result = await Receive(qty, mode == "variance" ? 20m : 10m);
            var expected = mode is "partial" or "split" or "full";
            Assert.Equal(expected, result.ok);
            if (mode == "foreign-mapping") Assert.Equal("WarehouseExceptionContinuationUnavailable", result.error);
            var receipts = expected ? 1 : 0;
            if (mode == "split") { var second = await Receive(.6m); Assert.True(second.ok, second.error); qty = 1m; receipts = 2; }
            using var verify = sql.ContextFor(probe, 1);
            Assert.Equal(receipts, await verify.GoodsReceipts.AsNoTracking().CountAsync());
            Assert.Equal(receipts, await verify.StockMovements.AsNoTracking().CountAsync());
            Assert.Equal(receipts, await verify.JournalEntries.AsNoTracking().CountAsync());
            Assert.Equal(expected ? qty : 0m, (await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(x => x.PurchaseOrderId == s.Order)).ReceivedQty);
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            Assert.Single(await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().ToListAsync());
            Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
            if (expected)
            {
                var exposure = await new ConstructionBudgetExposureService(verify, new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(verify))
                    .PreviewAsync(ConstructionR7RebasedPurchaseDecisionTests.Actor(8), s.Seed.Project);
                Assert.True(exposure.Ok, exposure.Code); var row = Assert.Single(exposure.Rows!);
                Assert.Equal(10m, row.Commitment); Assert.Equal(0m, row.Actual); Assert.Equal(row.Budget - 10m, row.Remaining);
                Assert.Equal(qty * 10m, await verify.JournalEntryLines.AsNoTracking().SumAsync(x => x.Debit));
                Assert.Equal(qty * 10m, await verify.JournalEntryLines.AsNoTracking().SumAsync(x => x.Credit));
                if (qty == 1m)
                {
                    var blockedInvoice = await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1, s.Order, "rebased-unsupported-invoice");
                    Assert.False(blockedInvoice.ok);
                    var retry = await Receive(1m); Assert.False(retry.ok);
                    using var after = sql.ContextFor(probe, 1);
                    Assert.Equal(receipts, await after.GoodsReceipts.AsNoTracking().CountAsync());
                    Assert.Equal(receipts, await after.StockMovements.AsNoTracking().CountAsync());
                }
            }
            Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebased_receipt_requires_the_approved_consent_mapping_to_remain_current(bool staleMapping)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBWHRECEIPT");
        try
        {
            ConstructionR7PurchaseApprovalTests.Setup s;
            using (var db = sql.ContextFor(probe, 1))
            {
                await Apply(db);
                await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionRebasedWarehouseConsentApplications; DROP TABLE dbo.ConstructionRebasedWarehouseConsentBindings; DROP TABLE dbo.ConstructionRebasedWarehouseConsentDecisions; DROP TABLE dbo.ConstructionRebasedWarehouseConsentRequests;");
                DirectoryInfo? root = new(AppContext.BaseDirectory);
                while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
                Assert.NotNull(root);
                var path = Path.Combine(root!.FullName, "CrossBuy", "deploy", "sql", "construction_r7_rebased_warehouse_consent.sql");
                foreach (var batch in Regex.Split(await File.ReadAllTextAsync(path), @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
                s = await Prepare(db, withWarehouseConsent: true);
            }

            int destinationWarehouse;
            using (var mapping = sql.ContextFor(probe, 1))
            {
                destinationWarehouse = await mapping.Set<ConstructionSite>().AsNoTracking()
                    .Where(x => x.CompanyID == 1 && x.Code == "R7-RECEIPT-CONSENT-TARGET")
                    .Select(x => x.WarehouseId!.Value).SingleAsync();
                if (staleMapping)
                    await mapping.Set<ConstructionSite>().Where(x => x.CompanyID == 1 && x.Code == "R7-RECEIPT-CONSENT-SOURCE")
                        .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsActive, false));
            }

            using var graph = Graph(probe, new Inventory());
            var result = await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(1, s.Seed.Vendor,
                destinationWarehouse, s.Order, DateTime.Today, "Rebased consent receipt",
                new() { new() { ItemId = s.Seed.Item, Qty = 1m, UoMId = s.Seed.Unit, UnitCost = 10m,
                    PurchaseOrderLineId = s.Lines.Single() } }, "rebased-consent-receipt", s.Seed.Currency, 1m);
            Assert.True(result.ok == !staleMapping, $"Expected success={!staleMapping}, got {result.ok}: {result.error}");
            if (staleMapping) Assert.Equal("WarehouseExceptionContinuationUnavailable", result.error);
            using var verify = sql.ContextFor(probe, 1);
            Assert.Equal(staleMapping ? 0 : 1, await verify.GoodsReceipts.AsNoTracking().CountAsync());
            Assert.Equal(staleMapping ? 0 : 1, await verify.StockMovements.AsNoTracking().CountAsync());
            Assert.Equal(staleMapping ? 0 : 1, await verify.JournalEntries.AsNoTracking().CountAsync());
            Assert.Single(await verify.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking().ToListAsync());
            Assert.Single(await verify.Set<ConstructionRebasedWarehouseConsentApplication>().AsNoTracking().ToListAsync());
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
