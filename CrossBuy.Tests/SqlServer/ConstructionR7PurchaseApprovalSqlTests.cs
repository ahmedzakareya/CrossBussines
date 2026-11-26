using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7PurchaseApprovalSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private string approvalScript = "";
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7POAPP");
        try
        {
            using var db = sql.ContextFor(probe, 1); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionMaterialConsumptions; DROP TABLE dbo.ConstructionPurchaseAdmissions; DROP TABLE dbo.ConstructionPurchaseApprovalRequests; DROP TABLE dbo.ConstructionPurchaseDrafts; DROP TABLE dbo.ConstructionBudgetExceptionConsumptions; DROP TABLE dbo.ConstructionBudgetExceptionDecisions; DROP TABLE dbo.ConstructionBudgetExceptionRequests; DROP TABLE dbo.ConstructionCommitmentReleases; DROP TABLE dbo.ConstructionCommitmentReservations; DROP TABLE dbo.ConstructionOrderAllocationLines; DROP TABLE dbo.ConstructionOrderAllocations;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var name in new[] { "construction_r7_order_allocations.sql", "construction_r7_commitment_reservations.sql", "construction_r7_budget_exceptions.sql", "construction_r7_purchase_drafts.sql", "construction_r7_purchase_approvals.sql", "construction_r7_material_consumptions.sql", "construction_r7_rebase_requests.sql", "construction_r7_rebase_decisions.sql", "construction_r7_exposure_rebases.sql", "construction_r7_purchase_rebase_requests.sql", "construction_r7_purchase_rebase_decisions.sql" })
            {
                var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", name));
                if (name == "construction_r7_purchase_approvals.sql")
                {
                    approvalScript = script;
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.InventoryApprovals ALTER COLUMN Amount decimal(18,2) NOT NULL; INSERT dbo.InventoryApprovals(CompanyID,DocType,Status,Amount,RequestedByEmployeeId,RequestedAt,PayloadJson) VALUES(1,N'PurchaseOrder',N'Pending',12.34,7,SYSUTCDATETIME(),N'{{}}');");
                }
                await Apply(db, script); await Apply(db, script);
            }
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    private static async Task Apply(CrossDbContext db, string script)
    {
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
            if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }

    [SkippableTheory][InlineData("0.4")][InlineData("1")][InlineData("split")][InlineData("variance")][InlineData("invoice")][InlineData("invoice-retry")]
    [InlineData("invoice-consume-partial")][InlineData("invoice-consume-full")][InlineData("invoice-consume-variance")]
    [InlineData("invoice-consume-register-partial")][InlineData("invoice-consume-register-full")][InlineData("invoice-consume-register-variance")]
    [InlineData("invoice-consume-register-race")][InlineData("invoice-consume-register-audit")][InlineData("invoice-consume-register-ddl")][InlineData("invoice-consume-register-two")]
    [InlineData("unbilled-consume-partial")][InlineData("unbilled-consume-full")]
    [InlineData("invoice-short-95")][InlineData("invoice-short-99")][InlineData("invoice-short-counter")]
    public async Task Native_real_receipt_writers_preserve_admission_and_full_budget_commitment(string text)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        ConstructionR7PurchaseApprovalTests.Setup setup; string content; string before;
        using (var db=sql.ContextFor(probe!,1))
        {
            if(text.StartsWith("invoice",StringComparison.Ordinal))
            {
                await db.Database.OpenConnectionAsync(); // authored GO batches share their owner transaction
                DirectoryInfo? root=new(AppContext.BaseDirectory);
                while(root!=null && !File.Exists(Path.Combine(root.FullName,"CrossBuy.sln"))) root=root.Parent;
                Assert.NotNull(root);
                foreach(var name in new[] { "construction_r7_procurement_provenance.sql","construction_r7_invoice_line_provenance.sql" })
                {
                    var script=await File.ReadAllTextAsync(Path.Combine(root!.FullName,"CrossBuy/deploy/sql",name));
                    await Apply(db,script);await Apply(db,script);
                }
            }
            setup=await ConstructionR7ReceiptWitnessTests.Admitted(db);
            var admission=await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();content=admission.ContentHash;before=admission.BeforeVersion;
            var year=new CrossBuy.Models.Context.Accounting.FiscalYear { CompanyID=1,Name="C4 receipt year",StartDate=DateTime.Today.AddDays(-10),EndDate=DateTime.Today.AddDays(10),Status="Open" };
            db.Add(year);await db.SaveChangesAsync();
            db.FiscalPeriods.Add(new() { FiscalYearId=year.ID,PeriodNo=1,StartDate=year.StartDate,EndDate=year.EndDate,Status="Open" });
            if(text.StartsWith("invoice",StringComparison.Ordinal) || text.StartsWith("unbilled",StringComparison.Ordinal)) await ConstructionR7StockInvoiceTests.Accounts(db,setup);
            else
            {
            var inventory=new CrossBuy.Models.Context.Accounting.Account { CompanyID=1,Code="110301",Name="Inventory",AccountTypeId=1,IsPostable=true,IsActive=true };
            var grni=new CrossBuy.Models.Context.Accounting.Account { CompanyID=1,Code="210203",Name="GRNI",AccountTypeId=1,IsPostable=true,IsActive=true };
            db.AddRange(inventory,grni);await db.SaveChangesAsync();
            var item=await db.Items.SingleAsync(x=>x.ID==setup.Seed.Item);item.ItemType="Stockable";
            var category=await db.ItemCategories.SingleAsync(x=>x.ID==item.ItemCategoryId);category.InventoryAccountId=inventory.ID;category.GrniAccountId=grni.ID;
            await db.SaveChangesAsync();
            }
        }
        var services=new ServiceCollection();services.AddLogging();services.AddLocalization();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());services.AddScoped(_=>sql.ContextFor(probe!,1));
        services.AddScoped<ICurrencyService,CurrencyService>();services.AddScoped<ICurrencyRounding,CurrencyRounding>();
        services.AddScoped<IFiscalPeriodService,FiscalPeriodService>();services.AddScoped<IJournalEntryService,JournalEntryService>();
        services.AddScoped<IStockService,StockService>();services.AddScoped<IPayableService,PayableService>();
        services.AddScoped<IFixedAssetService,FixedAssetService>();services.AddScoped<IThreeWayMatchService,ThreeWayMatchService>();
        services.AddScoped<IProcurementService,ProcurementService>();services.AddScoped<IEntityRegistry,EntityRegistry>();
        services.AddScoped<IBusinessContextAccessor>(_=>new ConstructionR7PurchaseApprovalTests.ActorAccessor());
        services.AddScoped<IBusinessEventService,BusinessEventService>();
        services.AddScoped<INotificationService>(_=>DispatchProxy.Create<INotificationService,ConstructionR7ProcurementWriterSqlTests.SilentNotifications>());
        using var graph=services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild=true,ValidateScopes=false });
        var quantity=text.StartsWith("invoice",StringComparison.Ordinal) || text=="unbilled-consume-full" ? 1m : text is "split" or "variance" or "unbilled-consume-partial" ? 0.4m : decimal.Parse(text,System.Globalization.CultureInfo.InvariantCulture);
        if(text.StartsWith("invoice-short",StringComparison.Ordinal)) quantity=text=="invoice-short-95" ? .95m : .99m;
        var received=await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(1,setup.Seed.Vendor,setup.Seed.Warehouse,
            setup.Order,DateTime.Today,"C4 real writer receipt",new() { new() { ItemId=setup.Seed.Item,Qty=quantity,UoMId=setup.Seed.Unit,
                UnitCost=text=="variance" ? 20m : 10m,PurchaseOrderLineId=setup.Lines.Single() } },"r7-c4-native",setup.Seed.Currency,1m);
        Assert.True(received.ok,received.error);
        if(text.StartsWith("invoice-short",StringComparison.Ordinal))
        {
            using(var counter=sql.ContextFor(probe!,1))
            {
                if(text=="invoice-short-counter")
                    await counter.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(l=>l.ReceivedQty,1m));
                Assert.True((await new ThreeWayMatchService(counter).CheckPoAsync(1,setup.Order)).Ok);
            }
            var refused=await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1,setup.Order,"r7-short-receipt");
            Assert.False(refused.ok);Assert.Null(refused.invoiceId);Assert.Contains("exact receipt coverage",refused.error);
            using var untouched=sql.ContextFor(probe!,1);
            Assert.Empty(await untouched.PurchaseInvoices.AsNoTracking().ToListAsync());
            Assert.Empty(await untouched.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
            Assert.Empty(await untouched.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().ToListAsync());
            Assert.Empty(await untouched.BusinessEvents.Where(x=>x.EventType==PurchaseInvoiceEvents.Created).AsNoTracking().ToListAsync());
            Assert.Empty(await untouched.JournalEntries.Where(x=>x.SourceType=="PurchaseInvoice").AsNoTracking().ToListAsync());
            Assert.Equal(quantity,(await untouched.StockBalances.AsNoTracking().SingleAsync()).QtyOnHand);
            Assert.Equal("Approved",(await untouched.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
            Assert.All(await untouched.GoodsReceipts.AsNoTracking().ToListAsync(),x=>Assert.Null(x.InvoiceId));
            var unchanged=await untouched.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();
            Assert.Equal(content,unchanged.ContentHash);Assert.Equal(before,unchanged.BeforeVersion);
            Assert.Empty(await untouched.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
            var budget=await ConstructionR7BudgetExposureTests.Reader(untouched).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),setup.Seed.Project);
            if(text=="invoice-short-counter") { Assert.False(budget.Ok);Assert.Null(budget.Rows); }
            else { Assert.True(budget.Ok,budget.Code);Assert.Equal(10m,Assert.Single(budget.Rows!).Commitment); }
            return;
        }
        if(text=="split")
        {
            var remainder=await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(1,setup.Seed.Vendor,setup.Seed.Warehouse,
                setup.Order,DateTime.Today,"C4 remaining receipt",new() { new() { ItemId=setup.Seed.Item,Qty=0.6m,UoMId=setup.Seed.Unit,
                    UnitCost=10,PurchaseOrderLineId=setup.Lines.Single() } },"r7-c4-native",setup.Seed.Currency,1m);
            Assert.True(remainder.ok,remainder.error);quantity=1m;
        }
        if(text.StartsWith("invoice",StringComparison.Ordinal))
        {
            var converted=await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1,setup.Order,"r7-c4-invoice");
            Assert.True(converted.ok,converted.error);
            if(text=="invoice-retry")
            {
                var retry=await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1,setup.Order,"r7-c4-retry");
                Assert.True(retry.ok,retry.error);Assert.Equal(converted.invoiceId,retry.invoiceId);
            }
        }
        using var read=sql.ContextFor(probe!,1);
        var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),setup.Seed.Project);
        if(text=="variance") Assert.False(exposure.Ok);
        else { Assert.True(exposure.Ok,exposure.Code);Assert.Equal(10m,Assert.Single(exposure.Rows!).Commitment);Assert.Equal(0m,Assert.Single(exposure.Rows!).Actual); }
        var allocation=new ConstructionOrderAllocationService(read,new ConstructionR7OrderAllocationTests.Access(),new ConstructionAuditService(read));
        var preview=await allocation.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),setup.Seed.Project,setup.Order);
        if(text.StartsWith("invoice",StringComparison.Ordinal)) { Assert.False(preview.Ok);Assert.Equal("SourceNotOpenAndReconciled",preview.Code); }
        else if(text=="variance") { Assert.False(preview.Ok);Assert.Equal("ReceiptCoverageRequired",preview.Code); }
        else { Assert.True(preview.Ok,preview.Code);Assert.Equal(quantity,Assert.Single(preview.Source!.Lines).ReceivedQuantity); }
        using var verify=sql.ContextFor(probe!,1);var frozen=await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();
        Assert.Equal(content,frozen.ContentHash);Assert.Equal(before,frozen.BeforeVersion);
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToListAsync());Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().ToListAsync());
        Assert.Equal(text=="split" ? 2 : 1,await verify.StockMovements.CountAsync());
        Assert.Equal(text=="split" || text.StartsWith("invoice",StringComparison.Ordinal) ? 2 : 1,await verify.JournalEntries.CountAsync(x=>x.Status=="Posted"));
        if(text.StartsWith("invoice",StringComparison.Ordinal))
        {
            Assert.Single(await verify.PurchaseInvoices.ToListAsync());
            Assert.Single(await verify.Set<PurchaseOrderInvoiceLink>().ToListAsync());Assert.Single(await verify.Set<PurchaseOrderInvoiceLineLink>().ToListAsync());
            Assert.Single(await verify.BusinessEvents.Where(x=>x.EventType==PurchaseInvoiceEvents.Created).ToListAsync());
            Assert.Equal(1m,(await verify.StockBalances.AsNoTracking().SingleAsync()).QtyOnHand);
            Assert.Empty(await verify.Set<ConstructionActualCostAllocation>().ToListAsync());
            if(text.StartsWith("invoice-consume",StringComparison.Ordinal))
                await ProveConsumptionKernel(graph,setup,text,content,before);
        }
        else
        {
            Assert.Empty(await verify.PurchaseInvoices.ToListAsync());
            if(text.StartsWith("unbilled",StringComparison.Ordinal)) await ProveUnbilledConsumption(graph,setup,text);
        }
    }

    private async Task ProveUnbilledConsumption(ServiceProvider graph,ConstructionR7PurchaseApprovalTests.Setup setup,string mode)
    {
        using(var ddl=sql.ContextFor(probe!,1))
        {
            await ddl.Database.OpenConnectionAsync();DirectoryInfo? root=new(AppContext.BaseDirectory);
            while(root!=null && !File.Exists(Path.Combine(root.FullName,"CrossBuy.sln"))) root=root.Parent;Assert.NotNull(root);
            foreach(var name in new[] { "construction_r7_actual_cost_allocations.sql","construction_r7_material_consumptions.sql" })
            {
                var script=await File.ReadAllTextAsync(Path.Combine(root!.FullName,"CrossBuy/deploy/sql",name));await Apply(ddl,script);await Apply(ddl,script);
            }
        }
        using var read=sql.ContextFor(probe!,1);
        var expenseType=new CrossBuy.Models.Context.Accounting.AccountType { Code="EXP",Name="Expense",StatementType="IncomeStatement" };
        read.Add(expenseType);await read.SaveChangesAsync();
        var expense=new CrossBuy.Models.Context.Accounting.Account { CompanyID=1,Code="510199",Name="Unbilled consumption",AccountTypeId=expenseType.ID,IsPostable=true,IsActive=true };
        read.Add(expense);await read.SaveChangesAsync();
        var quantity=mode.EndsWith("partial",StringComparison.Ordinal) ? .2m : .4m;
        var issue=await graph.GetRequiredService<IStockService>().PostMovementAsync(1,new MovementRequest { ItemId=setup.Seed.Item,WarehouseId=setup.Seed.Warehouse,
            Direction=-1,Qty=quantity,UoMId=setup.Seed.Unit,SourceType="Issue",ProjectId=setup.Seed.Project,CounterAccountOverride=expense.ID,PostToGl=true },"r7-unbilled-issue");
        Assert.True(issue.ok,issue.error);
        var movement=await read.StockMovements.AsNoTracking().SingleAsync(x=>x.ID==issue.movement!.ID);
        var debit=await read.JournalEntryLines.AsNoTracking().SingleAsync(x=>x.JournalEntryId==movement.JournalEntryId && x.Debit>0);
        await ConstructionR7BudgetExposureTests.Attribute(read,setup.Seed.Project,debit.ID,movement.TotalCost);
        var receiptLine=(await read.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;var actor=ConstructionR7OrderAllocationTests.Actor();
        string recorded;string financialVersion;
        using(var write=sql.ContextFor(probe!,1))
        {
            var service=ConstructionR7MaterialConsumptionTests.Service(write);var p=await service.PreviewAsync(actor,setup.Seed.Project,receiptLine,movement.ID);Assert.True(p.Ok,p.Code);
            var result=await service.RegisterAsync(actor,setup.Seed.Project,receiptLine,movement.ID,p.Version!,Guid.NewGuid(),"Native unbilled receipt");Assert.True(result.Ok,result.Code);
        }
        using(var before=sql.ContextFor(probe!,1))
        {
            var exposure=await ConstructionR7BudgetExposureTests.Reader(before).PreviewAsync(actor,setup.Seed.Project);Assert.True(exposure.Ok,exposure.Code);
            var row=Assert.Single(exposure.Rows!);Assert.Equal(quantity*10,row.Actual);Assert.Equal(10-quantity*10,row.Commitment);Assert.Equal(90m,row.Remaining);
            recorded=(await before.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion;financialVersion=exposure.Version!;
        }
        if(mode.EndsWith("partial",StringComparison.Ordinal))
        {
            var remaining=await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(1,setup.Seed.Vendor,setup.Seed.Warehouse,setup.Order,DateTime.Today,
                "Remaining native unbilled receipt",new() { new() { ItemId=setup.Seed.Item,Qty=.6m,UoMId=setup.Seed.Unit,UnitCost=10,PurchaseOrderLineId=setup.Lines.Single() } },"r7-unbilled-remainder",setup.Seed.Currency,1m);
            Assert.True(remaining.ok,remaining.error);
        }
        var invoice=await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1,setup.Order,"r7-unbilled-invoice");Assert.True(invoice.ok,invoice.error);
        using var final=sql.ContextFor(probe!,1);var complete=await ConstructionR7BudgetExposureTests.Reader(final).PreviewAsync(actor,setup.Seed.Project);
        Assert.True(complete.Ok,complete.Code);var budget=Assert.Single(complete.Rows!);
        Assert.Equal(quantity*10,budget.Actual);Assert.Equal(10-quantity*10,budget.Commitment);Assert.Equal(90m,budget.Remaining);
        Assert.NotEqual(financialVersion,complete.Version);Assert.Equal(recorded,(await final.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion);
        Assert.Single(await final.Set<ConstructionCommitmentReservation>().ToListAsync());Assert.Empty(await final.Set<ConstructionCommitmentRelease>().ToListAsync());
        Assert.Single(await final.Set<ConstructionActualCostAllocation>().ToListAsync());Assert.Single(await final.PurchaseInvoices.ToListAsync());
    }

    private async Task ProveConsumptionKernel(ServiceProvider graph,ConstructionR7PurchaseApprovalTests.Setup setup,
        string mode,string content,string before)
    {
        if(mode.Contains("register",StringComparison.Ordinal))
        {
            using var ddl=sql.ContextFor(probe!,1);await ddl.Database.OpenConnectionAsync();
            await ddl.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionMaterialConsumptions;");
            DirectoryInfo? root=new(AppContext.BaseDirectory);
            while(root!=null && !File.Exists(Path.Combine(root.FullName,"CrossBuy.sln"))) root=root.Parent;
            Assert.NotNull(root);
            foreach(var name in new[] { "construction_r7_actual_cost_allocations.sql","construction_r7_material_consumptions.sql" })
            {
                var script=await File.ReadAllTextAsync(Path.Combine(root!.FullName,"CrossBuy/deploy/sql",name));
                await Apply(ddl,script);await Apply(ddl,script);
            }
        }
        int expense;
        using(var db=sql.ContextFor(probe!,1))
        {
            var type=new CrossBuy.Models.Context.Accounting.AccountType { Code="EXP",Name="Project expense",StatementType="IncomeStatement" };
            db.Add(type);await db.SaveChangesAsync();
            var account=new CrossBuy.Models.Context.Accounting.Account { CompanyID=1,Code="510104",Name="Material consumption",AccountTypeId=type.ID,IsActive=true,IsPostable=true };
            db.Add(account);await db.SaveChangesAsync();expense=account.ID;
        }
        var stock=graph.GetRequiredService<IStockService>();
        if(mode.Contains("variance",StringComparison.Ordinal))
        {
            using var accountRead=sql.ContextFor(probe!,1);var grni=(await accountRead.Accounts.AsNoTracking().SingleAsync(x=>x.Code=="210203")).ID;
            var opening=await stock.PostMovementAsync(1,new MovementRequest { ItemId=setup.Seed.Item,WarehouseId=setup.Seed.Warehouse,
                Direction=1,Qty=1,UoMId=setup.Seed.Unit,UnitCostInBase=20m,SourceType="Opening",CounterAccountOverride=grni,PostToGl=true },"r7-consumption-variance");
            Assert.True(opening.ok,opening.error);
        }
        var quantity=mode.Contains("full",StringComparison.Ordinal) ? 1m : .4m;
        var issued=await stock.PostMovementAsync(1,new MovementRequest { ItemId=setup.Seed.Item,WarehouseId=setup.Seed.Warehouse,
            Direction=-1,Qty=quantity,UoMId=setup.Seed.Unit,SourceType="Issue",ProjectId=setup.Seed.Project,
            CounterAccountOverride=expense,PostToGl=true },"r7-consumption-proof");
        Assert.True(issued.ok,issued.error);Assert.NotNull(issued.movement);
        using(var read=sql.ContextFor(probe!,1))
        {
            var receipt=await read.GoodsReceipts.AsNoTracking().Include(x=>x.Lines).SingleAsync(x=>x.PurchaseOrderId==setup.Order);
            var receiptLine=Assert.Single(receipt.Lines);var incoming=await read.StockMovements.AsNoTracking().SingleAsync(x=>x.ID==receiptLine.StockMovementId);
            var receiptEntry=await read.JournalEntries.AsNoTracking().Include(x=>x.Lines).SingleAsync(x=>x.ID==incoming.JournalEntryId);
            Assert.Equal("Posted",receiptEntry.Status);Assert.Equal("Inventory",receiptEntry.SourceType);Assert.Equal(incoming.ID,receiptEntry.SourceId);
            Assert.Equal((short)1,incoming.Direction);Assert.Equal("Receipt",incoming.SourceType);
            Assert.Equal(incoming.TotalCost,receiptEntry.Lines.Sum(x=>x.Debit));Assert.Equal(incoming.TotalCost,receiptEntry.Lines.Sum(x=>x.Credit));
            var outgoing=await read.StockMovements.AsNoTracking().SingleAsync(x=>x.ID==issued.movement!.ID);
            var entry=await read.JournalEntries.AsNoTracking().Include(x=>x.Lines).SingleAsync(x=>x.ID==outgoing.JournalEntryId);
            Assert.Equal("Posted",entry.Status);Assert.Equal("Inventory",entry.SourceType);Assert.Equal(outgoing.ID,entry.SourceId);
            Assert.Equal((short)-1,outgoing.Direction);Assert.Equal("Issue",outgoing.SourceType);
            Assert.All(entry.Lines,x=>Assert.Equal(setup.Seed.Project,x.ProjectId));
            Assert.Equal(outgoing.TotalCost,entry.Lines.Sum(x=>x.Debit));Assert.Equal(outgoing.TotalCost,entry.Lines.Sum(x=>x.Credit));
            var net=Assert.Single(entry.Lines,x=>x.AccountId==expense && x.Debit>0);
            var capturedCurrency=Assert.IsType<int>(entry.FunctionalCurrencyId);
            var capturedDecimals=Assert.IsType<int>(entry.FunctionalCurrencyDecimals);
            var r=new ConstructionConsumptionReceipt(1,setup.Seed.Project,setup.Order,receiptLine.PurchaseOrderLineId!.Value,
                receipt.ID,receiptLine.ID,incoming.ID,receiptEntry.FunctionalCurrencyId!.Value,receiptEntry.FunctionalCurrencyDecimals!.Value,
                incoming.ItemId,incoming.WarehouseId,incoming.QtyBase,incoming.TotalCost);
            var i=new ConstructionConsumptionIssue(1,net.ProjectId!.Value,outgoing.ID,entry.ID,net.ID,capturedCurrency,
                capturedDecimals,outgoing.ItemId,outgoing.WarehouseId,outgoing.QtyBase,outgoing.TotalCost);
            var link=new ConstructionConsumptionLink(1,setup.Seed.Project,setup.Order,r.OrderLineId,r.ReceiptLineId,r.MovementId,i.MovementId,i.JournalEntryId,i.ExpenseJournalLineId);
            var check=ConstructionConsumptionLineageRules.Validate(1,setup.Seed.Project,setup.Seed.Currency,new[] { r },new[] { i },new[] { link });
            Assert.Equal(!mode.Contains("variance",StringComparison.Ordinal),check.Ok);
            if(mode.Contains("variance",StringComparison.Ordinal)) { Assert.Equal(6m,outgoing.TotalCost);Assert.Equal("ConsumptionVarianceCoverageRequired",check.Code);Assert.Null(check.CandidateNetBaseCost); }
            else Assert.Equal(outgoing.TotalCost,check.CandidateNetBaseCost);
            // Existing analytical attribution is real; a pure graph check still cannot release a reservation.
            await ConstructionR7BudgetExposureTests.Attribute(read,setup.Seed.Project,net.ID,outgoing.TotalCost);
            if(mode.Contains("register",StringComparison.Ordinal))
                await ProveConsumptionRegistration(graph,setup,mode,receiptLine.ID,outgoing.ID,expense);
        }
        using var verify=sql.ContextFor(probe!,1);
        var exposure=await ConstructionR7BudgetExposureTests.Reader(verify).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),setup.Seed.Project);
        if(mode.Contains("register",StringComparison.Ordinal) && !mode.Contains("variance",StringComparison.Ordinal) && !mode.Contains("ddl",StringComparison.Ordinal))
        {
            Assert.True(exposure.Ok,exposure.Code);var budget=Assert.Single(exposure.Rows!);
            var actual=mode.Contains("two",StringComparison.Ordinal) ? 10m : quantity*10m;
            Assert.Equal(actual,budget.Actual);Assert.Equal(10m-actual,budget.Commitment);Assert.Equal(90m,budget.Remaining);
            Assert.Equal(exposure.Version,(await ConstructionR7BudgetExposureTests.Reader(verify).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),setup.Seed.Project)).Version);
        }
        else { Assert.False(exposure.Ok);Assert.Null(exposure.Rows);Assert.Contains(exposure.Issues!,x=>x.Code=="InventoryConsumptionCoverageRequired"); }
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToListAsync());Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().ToListAsync());
        var admission=await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();Assert.Equal(content,admission.ContentHash);Assert.Equal(before,admission.BeforeVersion);
        Assert.Equal(mode.Contains("ddl",StringComparison.Ordinal) || mode.Contains("two",StringComparison.Ordinal) ? 2 : 1,await verify.Set<ConstructionActualCostAllocation>().CountAsync());
        Assert.Single(await verify.PurchaseInvoices.ToListAsync());
        var remaining=mode.Contains("variance",StringComparison.Ordinal) ? 1.6m : mode.Contains("ddl",StringComparison.Ordinal) ? .9m : mode.Contains("two",StringComparison.Ordinal) ? 0m : 1m-quantity;
        Assert.Equal(remaining,(await verify.StockBalances.AsNoTracking().SingleAsync()).QtyOnHand);
    }

    private async Task ProveConsumptionRegistration(ServiceProvider graph,ConstructionR7PurchaseApprovalTests.Setup setup,string mode,
        int receiptLine,int issue,int expense)
    {
        var actor=ConstructionR7OrderAllocationTests.Actor();var request=Guid.NewGuid();
        using var read=sql.ContextFor(probe!,1);var preview=await ConstructionR7MaterialConsumptionTests.Service(read).PreviewAsync(actor,setup.Seed.Project,receiptLine,issue);
        if(mode.Contains("variance",StringComparison.Ordinal))
        {
            Assert.False(preview.Ok);Assert.Equal("ConsumptionVarianceCoverageRequired",preview.Code);
            Assert.False((await ConstructionR7MaterialConsumptionTests.Service(read).RegisterAsync(actor,setup.Seed.Project,receiptLine,issue,new string('A',64),request,"Refuse variance")).Ok);
            Assert.Empty(await read.Set<ConstructionMaterialConsumption>().ToListAsync());return;
        }
        Assert.True(preview.Ok,preview.Code);
        async Task<ConstructionConsumptionWrite> Record(Guid id)
        {
            using var db=sql.ContextFor(probe!,1);
            return await ConstructionR7MaterialConsumptionTests.Service(db).RegisterAsync(actor,setup.Seed.Project,receiptLine,issue,preview.Version!,id,"Native explicit receipt attribution");
        }
        if(mode.Contains("race",StringComparison.Ordinal))
        {
            var results=await Task.WhenAll(Task.Run(()=>Record(Guid.NewGuid())),Task.Run(()=>Record(Guid.NewGuid())));
            Assert.Single(results,x=>x.Code=="Registered");Assert.Single(results,x=>!x.Ok);
        }
        else if(mode.Contains("audit",StringComparison.Ordinal))
        {
            var holder=new CompanyScopeHolder();holder.Set(1,null);var failure=new FailConsumptionAudit();
            using var db=new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options,holder);
            var service=ConstructionR7MaterialConsumptionTests.Service(db);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RegisterAsync(actor,setup.Seed.Project,receiptLine,issue,preview.Version!,request,"Native explicit receipt attribution"));
            using(var check=sql.ContextFor(probe!,1)) Assert.Empty(await check.Set<ConstructionMaterialConsumption>().ToListAsync());
            failure.Enabled=false;var result=await service.RegisterAsync(actor,setup.Seed.Project,receiptLine,issue,preview.Version!,request,"Native explicit receipt attribution");Assert.True(result.Ok,result.Code);
        }
        else
        {
            var result=await Record(request);Assert.True(result.Ok,result.Code);
            var replay=await Record(request);Assert.Equal("AlreadyRegistered",replay.Code);Assert.Equal(result.Id,replay.Id);
        }
        using(var check=sql.ContextFor(probe!,1))
        {
            var row=await check.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync();Assert.Equal(issue,row.IssueMovementId);
            Assert.Single(await check.Set<ConstructionAuditEntry>().Where(x=>x.EntityType==nameof(ConstructionMaterialConsumption) && x.EntityId==row.ID).ToListAsync());
            Assert.Empty(await check.Set<ConstructionCommitmentRelease>().ToListAsync());
            if(mode.Contains("ddl",StringComparison.Ordinal))
            {
                var update=await Assert.ThrowsAsync<SqlException>(()=>check.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ConstructionMaterialConsumptions SET Reason=N'changed' WHERE ID={row.ID}"));Assert.Equal(51903,update.Number);
                var delete=await Assert.ThrowsAsync<SqlException>(()=>check.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM dbo.ConstructionMaterialConsumptions WHERE ID={row.ID}"));Assert.Equal(51903,delete.Number);
                DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null && !File.Exists(Path.Combine(root.FullName,"CrossBuy.sln"))) root=root.Parent;
                var script=await File.ReadAllTextAsync(Path.Combine(root!.FullName,"CrossBuy/deploy/sql/construction_r7_material_consumptions.sql"));
                await check.Database.OpenConnectionAsync();await Apply(check,script);await Apply(check,script);
                Assert.Equal(row.SourceVersion,(await check.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion);
            }
        }
        if(mode.Contains("two",StringComparison.Ordinal) || mode.Contains("ddl",StringComparison.Ordinal))
        {
            var stock=graph.GetRequiredService<IStockService>();
            if(mode.Contains("ddl",StringComparison.Ordinal))
            {
                var grni=(await read.Accounts.AsNoTracking().SingleAsync(x=>x.Code=="210203")).ID;
                var added=await stock.PostMovementAsync(1,new MovementRequest { ItemId=setup.Seed.Item,WarehouseId=setup.Seed.Warehouse,Direction=1,Qty=1,
                    UoMId=setup.Seed.Unit,UnitCostInBase=10,SourceType="Opening",CounterAccountOverride=grni,PostToGl=true },"r7-receipt-capacity");Assert.True(added.ok,added.error);
            }
            var quantity=mode.Contains("ddl",StringComparison.Ordinal) ? .7m : .6m;
            var next=await stock.PostMovementAsync(1,new MovementRequest { ItemId=setup.Seed.Item,WarehouseId=setup.Seed.Warehouse,Direction=-1,Qty=quantity,
                UoMId=setup.Seed.Unit,SourceType="Issue",ProjectId=setup.Seed.Project,CounterAccountOverride=expense,PostToGl=true },"r7-next-consumption");Assert.True(next.ok,next.error);
            using var nextDb=sql.ContextFor(probe!,1);
            var movement=await nextDb.StockMovements.AsNoTracking().SingleAsync(x=>x.ID==next.movement!.ID);
            var net=await nextDb.JournalEntryLines.AsNoTracking().SingleAsync(x=>x.JournalEntryId==movement.JournalEntryId && x.Debit>0);
            await ConstructionR7BudgetExposureTests.Attribute(nextDb,setup.Seed.Project,net.ID,movement.TotalCost);
            var service=ConstructionR7MaterialConsumptionTests.Service(nextDb);var nextPreview=await service.PreviewAsync(actor,setup.Seed.Project,receiptLine,movement.ID);
            if(mode.Contains("two",StringComparison.Ordinal))
            {
                Assert.True(nextPreview.Ok,nextPreview.Code);var result=await service.RegisterAsync(actor,setup.Seed.Project,receiptLine,movement.ID,nextPreview.Version!,Guid.NewGuid(),"Remaining quantity");Assert.True(result.Ok,result.Code);
                Assert.Equal(10m,(await nextDb.Set<ConstructionMaterialConsumption>().AsNoTracking().ToListAsync()).Sum(x=>x.NetBaseCost));
            }
            else
            {
                Assert.False(nextPreview.Ok);Assert.Equal("ReceiptConsumptionExceeded",nextPreview.Code);
                var raw=await nextDb.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync();raw.ID=0;raw.RequestId=Guid.NewGuid();raw.IssueMovementId=movement.ID;
                raw.IssueJournalEntryId=net.JournalEntryId;raw.ExpenseJournalLineId=net.ID;raw.ActualAllocationId=(await nextDb.Set<ConstructionActualCostAllocation>().AsNoTracking().SingleAsync(x=>x.JournalEntryLineId==net.ID)).ID;
                raw.QuantityBase=quantity;raw.NetBaseCost=movement.TotalCost;nextDb.Add(raw);
                var failed=await Assert.ThrowsAsync<DbUpdateException>(()=>nextDb.SaveChangesAsync());Assert.Equal(51902,Assert.IsType<SqlException>(failed.InnerException).Number);
            }
        }
    }

    private sealed class FailConsumptionAudit : SaveChangesInterceptor
    {
        public bool Enabled=true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x=>x.State==EntityState.Added && x.Entity.EntityType==nameof(ConstructionMaterialConsumption)))
                throw new InvalidOperationException("Injected consumption audit failure");
            return base.SavingChangesAsync(data,result,ct);
        }
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native_finance_preview_to_purchase_admission_preserves_original_source_and_refuses_changed_basis(bool changeBeforeDecision)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1);
        var seed = await ConstructionR7PurchaseDraftTests.Seed(db);
        await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "ApprovalRequired"));
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var editor = ConstructionR7PurchaseEditorTests.Service(db, actor);
        var draftInput = ConstructionR7PurchaseEditorTests.Draft(seed) with { RowsJson = System.Text.Json.JsonSerializer.Serialize(new[] {
            new { ItemId = seed.Item, Description = "Exact exception journey", Quantity = "1", UnitId = seed.Unit,
                UnitPrice = "200.1234", Discount = "0", TaxRate = "20" } }) };
        var created = await editor.CreateAsync(seed.Project, draftInput); Assert.True(created.Ok, created.Code);
        var orderId = created.OrderId!.Value;
        var allocationPage = (await editor.ReadAsync(seed.Project, orderId))!;
        var allocationInput = ConstructionR7PurchaseEditorTests.Allocate(allocationPage, seed);
        var allocation = await editor.AllocateAsync(seed.Project, orderId, allocationInput); Assert.True(allocation.Ok, allocation.Code);
        var purchase = ConstructionR7PurchaseApprovalTests.Service(db, actor);
        var blocked = (await purchase.ReadRequestAsync(seed.Project, orderId))!;
        Assert.False(blocked.CanSubmit); Assert.Equal("MatchingBudgetExceptionRequired", blocked.ReadinessCode);
        var finance = ConstructionR7BudgetExceptionTests.Service(db);
        var page = (await finance.ReadPageAsync(actor.Current, seed.Project, orderId))!;
        Assert.True(page.Preview!.Ok, page.Preview.Code);
        Assert.Equal(200.123m, page.Preview.ProposedAmount); Assert.Equal(100.123m, page.Preview.Overrun);
        var requested = await finance.RequestCurrentAsync(actor.Current, seed.Project, page.AllocationId!.Value,
            page.SourceVersion!, page.Preview.Version!, Guid.NewGuid(), "Approve displayed overrun");
        Assert.True(requested.Ok, requested.Code);
        Assert.True((await finance.ApproveAsync(ConstructionR7BudgetExceptionTests.Approver, seed.Project,
            requested.Id!.Value, requested.Version!, Guid.NewGuid(), "Independent finance decision")).Ok);
        using (var beforePurchase = sql.ContextFor(probe!, 1))
        {
            Assert.Empty(await beforePurchase.Set<ConstructionCommitmentReservation>().ToListAsync());
            Assert.Empty(await beforePurchase.Set<ConstructionBudgetExceptionConsumption>().ToListAsync());
            Assert.Equal("Draft", await beforePurchase.PurchaseOrders.Where(x => x.ID == orderId).Select(x => x.Status).SingleAsync());
        }
        var ready = (await purchase.ReadRequestAsync(seed.Project, orderId))!;
        Assert.True(ready.CanSubmit, ready.ReadinessCode); Assert.Equal(requested.Id, Assert.Single(ready.Exceptions).Id);
        var purchaseRequest = await purchase.RequestAsync(seed.Project, orderId, ready.AllocationId!.Value,
            ready.SourceVersion!, requested.Id, ready.RequestId, "Submit original purchase with finance approval");
        Assert.True(purchaseRequest.Ok, purchaseRequest.Code);
        actor.Current = new CrossBuy.Models.Platform.BusinessContext { CompanyId = 1, EmployeeId = 9,
            UserId = "independent-purchase-reviewer", Source = CrossBuy.Models.Platform.BusinessContextSource.Test };
        if (changeBeforeDecision)
        {
            var token = Guid.NewGuid().ToByteArray();
            await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
            var denied = await purchase.ApproveAsync(purchaseRequest.ApprovalId!.Value, Guid.NewGuid(), "Refuse stale financial basis");
            Assert.False(denied.Ok); Assert.Contains(denied.Code, new[] { "AllocationSourceChanged", "ExceptionSourceChanged" });
        }
        else
        {
            var approved = await ConstructionR7PurchaseApprovalTests.Gateway(db, actor, purchase)
                .ApproveAsync(purchaseRequest.ApprovalId!.Value, 9, "Approve original source once");
            Assert.True(approved.ok, approved.error);
            Assert.Equal("ExceptionAlreadyConsumed", (await finance.ConsumeAsync(actor.Current, seed.Project,
                requested.Id!.Value, Guid.NewGuid(), "Cannot spend twice")).Code);
        }
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Single(await verify.PurchaseOrders.ToListAsync());
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(); Assert.Equal(orderId, order.ID);
        Assert.Equal(200.123m, order.SubTotal); Assert.Equal(changeBeforeDecision ? "Draft" : "Approved", order.Status);
        Assert.Equal(changeBeforeDecision ? 0 : 1, await verify.Set<ConstructionPurchaseAdmission>().CountAsync());
        Assert.Equal(changeBeforeDecision ? 0 : 1, await verify.Set<ConstructionCommitmentReservation>().CountAsync());
        Assert.Equal(changeBeforeDecision ? 0 : 1, await verify.Set<ConstructionBudgetExceptionConsumption>().CountAsync());
        Assert.Single(await verify.Set<ConstructionBudgetExceptionRequest>().ToListAsync());
        Assert.Single(await verify.Set<ConstructionBudgetExceptionDecision>().ToListAsync());
        Assert.Empty(await verify.JournalEntries.ToListAsync()); Assert.Empty(await verify.StockMovements.ToListAsync());
    }

    [SkippableFact]
    public async Task Native_original_source_exact_amount_trusted_constraints_and_populated_rerun()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var setup = await ConstructionR7PurchaseApprovalTests.Arrange(db); var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
        var request = await ConstructionR7PurchaseApprovalTests.Request(service, setup, Guid.NewGuid()); Assert.True(request.Ok, request.Code);
        var bypass = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PurchaseOrders SET Status=N'Approved' WHERE ID={setup.Order}")); Assert.Equal(51903, bypass.Number);
        var payload = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.InventoryApprovals SET PayloadJson=PayloadJson+N' ' WHERE ID={request.ApprovalId}")); Assert.Equal(52007, payload.Number);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        Assert.True((await ConstructionR7PurchaseApprovalTests.Gateway(db, actor, service).ApproveAsync(request.ApprovalId!.Value, 8, "Native independent approval")).ok);
        using var verify = sql.ContextFor(probe!, 1); await verify.Database.OpenConnectionAsync(); await Apply(verify, approvalScript);
        Assert.Equal(12.34m, await verify.InventoryApprovals.Where(x => x.DocType == "PurchaseOrder").Select(x => x.Amount).SingleAsync());
        Assert.Equal(1.482m, await verify.InventoryApprovals.Where(x => x.ID == request.ApprovalId).Select(x => x.Amount).SingleAsync());
        Assert.Equal("Approved", await verify.PurchaseOrders.Where(x => x.ID == setup.Order).Select(x => x.Status).SingleAsync());
        Assert.Equal(6, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionPurchaseAdmissions'),OBJECT_ID('dbo.ConstructionPurchaseApprovalRequests')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
        var frozen = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PurchaseOrderLines SET ItemDescription=ItemDescription+N' ' WHERE PurchaseOrderId={setup.Order}")); Assert.Equal(52006, frozen.Number);
        Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Equal(2, await verify.BusinessEvents.CountAsync()); Assert.Empty(await verify.StockMovements.ToListAsync()); Assert.Empty(await verify.JournalEntries.ToListAsync());
        var exposure = await ConstructionR7BudgetExposureTests.Reader(verify).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Seed.Project); Assert.True(exposure.Ok, exposure.Code);
    }

    [SkippableTheory][InlineData(false)][InlineData(true)]
    public async Task Native_competing_decisions_admit_one_original_purchase(bool sameRequest)
    {
        Skip.If(!sql.Available, sql.SkipReason); ConstructionPurchaseApprovalResult request;
        using (var seed = sql.ContextFor(probe!, 1))
        {
            var setup = await ConstructionR7PurchaseApprovalTests.Arrange(seed);
            request = await ConstructionR7PurchaseApprovalTests.Request(ConstructionR7PurchaseApprovalTests.Service(seed, new()), setup, Guid.NewGuid()); Assert.True(request.Ok, request.Code);
        }
        using var a = sql.ContextFor(probe!, 1); using var b = sql.ContextFor(probe!, 1); var id = Guid.NewGuid();
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7BudgetExceptionTests.Approver };
        var results = await Task.WhenAll(ConstructionR7PurchaseApprovalTests.Service(a, actor).ApproveAsync(request.ApprovalId!.Value, id, "Concurrent native review"),
            ConstructionR7PurchaseApprovalTests.Service(b, actor).ApproveAsync(request.ApprovalId.Value, sameRequest ? id : Guid.NewGuid(), "Concurrent native review"));
        Assert.Single(results, x => x.Code == "Approved"); Assert.Single(results, x => x.Code == (sameRequest ? "AlreadyApproved" : "ApprovalAlreadyDecided"));
        using var verify = sql.ContextFor(probe!, 1); Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().ToListAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToListAsync()); Assert.Equal(2, await verify.BusinessEvents.CountAsync());
    }

    [SkippableTheory][InlineData(false)][InlineData(true)]
    public async Task Native_late_audit_failure_restores_borrowed_owner_and_budget_then_retries(bool exception)
    {
        Skip.If(!sql.Available, sql.SkipReason); ConstructionR7PurchaseApprovalTests.Setup setup; ConstructionPurchaseApprovalResult request;
        using (var seed = sql.ContextFor(probe!, 1))
        {
            setup = await ConstructionR7PurchaseApprovalTests.Arrange(seed, exception ? 200m : null, exception ? "ApprovalRequired" : "HardBlock");
            int? exceptionId = null;
            if (exception)
            {
                var finance = ConstructionR7BudgetExceptionTests.Service(seed);
                var raised = await finance.RequestAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Seed.Project, setup.Allocation, setup.Version, Guid.NewGuid(), "Native finance review"); Assert.True(raised.Ok, raised.Code);
                Assert.True((await finance.ApproveAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Seed.Project, raised.Id!.Value, raised.Version!, Guid.NewGuid(), "Independent finance decision")).Ok); exceptionId = raised.Id;
            }
            request = await ConstructionR7PurchaseApprovalTests.Request(ConstructionR7PurchaseApprovalTests.Service(seed, new()), setup, Guid.NewGuid(), exceptionId); Assert.True(request.Ok, request.Code);
        }
        var scope = new CompanyScopeHolder(); scope.Set(1, null); var fail = new FailDecisionAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(fail).Options, scope);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7BudgetExceptionTests.Approver }; var service = ConstructionR7PurchaseApprovalTests.Service(db, actor); var id = Guid.NewGuid();
        await using (var owner = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var project = await db.Projects.SingleAsync(); project.Name = "Saved owner work"; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(request.ApprovalId!.Value, id, "Native final decision"));
            Assert.Same(owner, db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges()); await owner.CommitAsync();
        }
        using (var verify = sql.ContextFor(probe!, 1))
        {
            Assert.Equal("Saved owner work", await verify.Projects.Select(x => x.Name).SingleAsync());
            Assert.Equal("Draft", await verify.PurchaseOrders.Select(x => x.Status).SingleAsync());
            Assert.Equal("Pending", await verify.InventoryApprovals.Where(x => x.ID == request.ApprovalId).Select(x => x.Status).SingleAsync());
            Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToListAsync()); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().ToListAsync()); Assert.Single(await verify.BusinessEvents.ToListAsync());
        }
        fail.Enabled = false; var result = await service.ApproveAsync(request.ApprovalId!.Value, id, "Native final decision"); Assert.True(result.Ok, result.Code);
        using var final = sql.ContextFor(probe!, 1); Assert.Single(await final.Set<ConstructionPurchaseAdmission>().ToListAsync());
        Assert.Equal(exception ? 1 : 0, await final.Set<ConstructionBudgetExceptionConsumption>().CountAsync()); Assert.Equal(2, await final.BusinessEvents.CountAsync());
    }
    [SkippableTheory][InlineData(false)][InlineData(true)]
    public async Task Native_browser_request_roundtrip_is_read_only_until_post_and_replay_preserves_original_purchase(bool withException)
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var setup = await ConstructionR7PurchaseApprovalTests.Arrange(db, withException ? 200m : null, withException ? "ApprovalRequired" : "HardBlock");
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
        int? exceptionId = null;
        if (withException)
        {
            var finance = ConstructionR7BudgetExceptionTests.Service(db);
            var raised = await finance.RequestAsync(actor.Current, setup.Seed.Project, setup.Allocation, setup.Version, Guid.NewGuid(), "Native UI exception request"); Assert.True(raised.Ok, raised.Code);
            var before = await service.ReadRequestAsync(setup.Seed.Project, setup.Order); Assert.NotNull(before); Assert.False(before.CanSubmit); Assert.Empty(before.Exceptions);
            var approved = await finance.ApproveAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Seed.Project, raised.Id!.Value, raised.Version!, Guid.NewGuid(), "Independent UI finance decision"); Assert.True(approved.Ok, approved.Code); exceptionId = raised.Id;
        }
        var page = await service.ReadRequestAsync(setup.Seed.Project, setup.Order); Assert.NotNull(page); Assert.True(page.CanSubmit, page.ReadinessCode);
        Assert.Equal(withException ? 240m : 1.482m, page.TotalAmount); Assert.Equal(3, page.CurrencyDecimals); Assert.Equal(withException ? 200m : 1.235m, Assert.Single(page.Splits).Amount);
        if (withException) Assert.Equal(exceptionId, Assert.Single(page.Exceptions).Id);
        Assert.Single(await db.InventoryApprovals.ToListAsync()); // legacy migration witness only; preview has not submitted anything
        var controller = ConstructionR7PurchaseRequestUiTests.Controller(db);
        await ConstructionR7PurchaseRequestUiTests.AssertSubmission(controller, service, page, exceptionId);
        await ConstructionR7PurchaseRequestUiTests.AssertSubmission(controller, service, page, exceptionId);
        var pending = await service.ReadRequestAsync(setup.Seed.Project, setup.Order); Assert.NotNull(pending); Assert.False(pending.CanSubmit);
        var entry = Assert.Single(pending.History); actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var decision = await service.ApproveAsync(entry.ApprovalId, Guid.NewGuid(), "Independent UI source review"); Assert.True(decision.Ok, decision.Code);
        using var verify = sql.ContextFor(probe!, 1); Assert.Equal(setup.Order, (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).ID);
        Assert.Single(await verify.Set<ConstructionPurchaseApprovalRequest>().ToListAsync()); Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().ToListAsync());
        Assert.Equal(2, await verify.BusinessEvents.CountAsync());
        actor.Current = ConstructionR7OrderAllocationTests.Actor(); var approvedPage = await service.ReadRequestAsync(setup.Seed.Project, setup.Order);
        Assert.NotNull(approvedPage); Assert.False(approvedPage.CanSubmit); Assert.Equal("Approved", Assert.Single(approvedPage.History).Status);
    }

    [SkippableFact]
    public async Task Native_stale_browser_post_retains_original_reason_and_witness_without_creating_approval()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var setup = await ConstructionR7PurchaseApprovalTests.Arrange(db); var service = ConstructionR7PurchaseApprovalTests.Service(db, new());
        var page = (await service.ReadRequestAsync(setup.Seed.Project, setup.Order))!; Assert.True(page.CanSubmit, page.ReadinessCode);
        await db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == setup.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.ItemDescription, "Concurrent native update"));
        await ConstructionR7PurchaseRequestUiTests.AssertConflict(ConstructionR7PurchaseRequestUiTests.Controller(db), service, page);
        using var verify = sql.ContextFor(probe!, 1); Assert.Empty(await verify.Set<ConstructionPurchaseApprovalRequest>().ToListAsync());
        Assert.Single(await verify.InventoryApprovals.ToListAsync()); Assert.Empty(await verify.BusinessEvents.ToListAsync());
    }

    [SkippableFact]
    public async Task Native_editor_creates_allocates_requests_and_approves_one_original_source_without_initial_spending()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var seed = await ConstructionR7PurchaseDraftTests.Seed(db); var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var editor = ConstructionR7PurchaseEditorTests.Service(db, actor); var controller = ConstructionR7PurchaseRequestUiTests.Controller(db);
        var input = ConstructionR7PurchaseEditorTests.Draft(seed);
        var created = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(await controller.CreateConstructionPurchaseDraft(seed.Project, input, editor));
        var retry = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(await controller.CreateConstructionPurchaseDraft(seed.Project, input, editor));
        var orderId = Assert.IsType<int>(created.RouteValues!["orderId"]); Assert.Equal(orderId, retry.RouteValues!["orderId"]);
        var page = (await editor.ReadAsync(seed.Project, orderId))!; Assert.True(page.CanSave, page.Code);
        var allocation = ConstructionR7PurchaseEditorTests.Allocate(page, seed);
        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(await controller.SaveConstructionPurchaseAllocation(seed.Project, orderId, allocation, editor));
        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(await controller.SaveConstructionPurchaseAllocation(seed.Project, orderId, allocation, editor));
        Assert.Empty(await db.Set<ConstructionCommitmentReservation>().ToListAsync()); Assert.Empty(await db.BusinessEvents.ToListAsync());
        var approvals = ConstructionR7PurchaseApprovalTests.Service(db, actor); var request = (await approvals.ReadRequestAsync(seed.Project, orderId))!;
        Assert.True(request.CanSubmit, request.ReadinessCode); Assert.Equal(1.482m, request.TotalAmount);
        await ConstructionR7PurchaseRequestUiTests.AssertSubmission(controller, approvals, request);
        var pending = (await approvals.ReadRequestAsync(seed.Project, orderId))!; var approvalId = Assert.Single(pending.History).ApprovalId;
        actor.Current = ConstructionR7BudgetExceptionTests.Approver; Assert.True((await approvals.ApproveAsync(approvalId, Guid.NewGuid(), "Independent complete editor review")).Ok);
        using var verify = sql.ContextFor(probe!, 1); Assert.Equal(orderId, (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).ID);
        Assert.Single(await verify.Set<ConstructionOrderAllocation>().ToListAsync()); Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().ToListAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToListAsync()); Assert.Empty(await verify.StockMovements.ToListAsync()); Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    [SkippableFact]
    public async Task Native_changed_draft_cannot_be_reallocated_and_stale_browser_intent_is_retained()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var seed = await ConstructionR7PurchaseDraftTests.Seed(db); var editor = ConstructionR7PurchaseEditorTests.Service(db, new());
        var created = await editor.CreateAsync(seed.Project, ConstructionR7PurchaseEditorTests.Draft(seed)); Assert.True(created.Ok, created.Code);
        var page = (await editor.ReadAsync(seed.Project, created.OrderId))!; var input = ConstructionR7PurchaseEditorTests.Allocate(page, seed);
        await db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.ItemDescription, "Concurrent native identity change"));
        var controller = ConstructionR7PurchaseRequestUiTests.Controller(db);
        var rejected = Assert.IsType<CrossBuy.ViewModel.ConstructionPurchaseEditorPage>(Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(
            await controller.SaveConstructionPurchaseAllocation(seed.Project, created.OrderId!.Value, input, editor)).Model);
        Assert.Equal(409, controller.Response.StatusCode); Assert.False(rejected.CanSave); Assert.Equal(input, rejected.Input); Assert.Equal("DraftSourceChanged", rejected.RefusalCode);
        using var verify = sql.ContextFor(probe!, 1); Assert.Empty(await verify.Set<ConstructionOrderAllocation>().ToListAsync()); Assert.Empty(await verify.BusinessEvents.ToListAsync());
    }

    [SkippableFact]
    public async Task Native_allocation_revision_racing_approval_request_cannot_change_a_pending_request_basis()
    {
        Skip.If(!sql.Available, sql.SkipReason); int orderId; ConstructionR7PurchaseDraftTests.SeedData seed;
        CrossBuy.ViewModel.ConstructionPurchaseEditorInput first; CrossBuy.ViewModel.ConstructionPurchaseEditorInput revision; int allocationId;
        using (var db = sql.ContextFor(probe!, 1))
        {
            seed = await ConstructionR7PurchaseDraftTests.Seed(db); var editor = ConstructionR7PurchaseEditorTests.Service(db, new());
            var created = await editor.CreateAsync(seed.Project, ConstructionR7PurchaseEditorTests.Draft(seed)); Assert.True(created.Ok, created.Code); orderId = created.OrderId!.Value;
            first = ConstructionR7PurchaseEditorTests.Allocate((await editor.ReadAsync(seed.Project, orderId))!, seed);
            var allocated = await editor.AllocateAsync(seed.Project, orderId, first); Assert.True(allocated.Ok, allocated.Code); allocationId = allocated.Id!.Value;
            revision = ConstructionR7PurchaseEditorTests.Allocate((await editor.ReadAsync(seed.Project, orderId))!, seed);
        }
        using var a = sql.ContextFor(probe!, 1); using var b = sql.ContextFor(probe!, 1);
        var write = ConstructionR7PurchaseEditorTests.Service(a, new()).AllocateAsync(seed.Project, orderId, revision);
        var request = ConstructionR7PurchaseApprovalTests.Service(b, new()).RequestAsync(seed.Project, orderId, allocationId, first.SourceVersion!, null, Guid.NewGuid(), "Concurrent independent request");
        await Task.WhenAll(write, request); var changed = await write; var submitted = await request;
        Assert.True(changed.Ok != submitted.Ok, $"allocation={changed.Code}; request={submitted.Code}");
        if (!changed.Ok) Assert.Contains(changed.Code, new[] { "ApprovalAlreadyPending", "ConcurrencyConflict" });
        if (!submitted.Ok) Assert.Contains(submitted.Code, new[] { "AllocationNotCurrent", "AllocationSourceChanged", "ConcurrencyConflict" });
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Equal(changed.Ok ? 2 : 1, await verify.Set<ConstructionOrderAllocation>().CountAsync());
        Assert.Equal(submitted.Ok ? 1 : 0, await verify.Set<ConstructionPurchaseApprovalRequest>().CountAsync());
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToListAsync()); Assert.Equal("Draft", await verify.PurchaseOrders.Select(x => x.Status).SingleAsync());
    }

    private sealed class FailDecisionAudit : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x => x.State == EntityState.Added && x.Entity.FieldName == "ApproveConstructionPurchase")) throw new InvalidOperationException("Injected final purchase audit failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}



