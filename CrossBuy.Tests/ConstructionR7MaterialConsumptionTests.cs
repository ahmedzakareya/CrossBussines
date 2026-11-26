using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7MaterialConsumptionTests
{
    private sealed class ViewOnlyAccess : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c,string action,PermissionTarget? target=null,CancellationToken cancellationToken=default) => Task.FromResult(action==ProjectsActions.BudgetView);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c,string action,CancellationToken cancellationToken=default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c,CancellationToken cancellationToken=default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    internal static ConstructionMaterialConsumptionService Service(CrossDbContext db,bool allow=true) =>
        new(db,new ConstructionR7ActualCostTests.Access(allow),new ConstructionAuditService(db));
    internal static async Task<int> Issue(CrossDbContext db,ConstructionR7PurchaseApprovalTests.Setup s,decimal quantity,decimal? cost=null)
    {
        var type=await db.AccountTypes.SingleOrDefaultAsync(x=>x.Code=="EXP");
        if(type==null){type=new AccountType { Code="EXP",Name="Expense",StatementType="IncomeStatement" };db.Add(type);await db.SaveChangesAsync();}
        var account=new Account { CompanyID=1,Code="EXP-"+Guid.NewGuid().ToString("N"),Name="Material expense",AccountTypeId=type.ID,IsActive=true,IsPostable=true };
        db.Add(account);await db.SaveChangesAsync();var amount=cost ?? quantity*10m;
        var movement=new StockMovement { CompanyID=1,ItemId=s.Seed.Item,WarehouseId=s.Seed.Warehouse,Direction=-1,
            QtyBase=quantity,QtyInUoM=quantity,UoMId=s.Seed.Unit,UnitCost=amount/quantity,TotalCost=amount,SourceType="Issue" };
        db.Add(movement);await db.SaveChangesAsync();
        var entry=new JournalEntry { CompanyID=1,Status="Posted",SourceType="Inventory",SourceId=movement.ID,EntryDate=DateTime.Today,
            FunctionalCurrencyId=s.Seed.Currency,FunctionalCurrencyDecimals=3,Lines=new List<JournalEntryLine> {
                new() { LineNo=1,AccountId=account.ID,ProjectId=s.Seed.Project,Debit=amount },
                new() { LineNo=2,AccountId=(await db.Accounts.SingleAsync(x=>x.Code=="110301")).ID,ProjectId=s.Seed.Project,Credit=amount } } };
        db.Add(entry);await db.SaveChangesAsync();movement.JournalEntryId=entry.ID;await db.SaveChangesAsync();
        await ConstructionR7BudgetExposureTests.Attribute(db,s.Seed.Project,entry.Lines.Single(x=>x.LineNo==1).ID,amount);
        return movement.ID;
    }

    [Fact]
    public async Task Record_and_replay_transfer_exact_cost_without_releasing_or_double_counting_the_reservation()
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);var issue=await Issue(f.Db,s,.4m);
        var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;var actor=ConstructionR7OrderAllocationTests.Actor();var request=Guid.NewGuid();
        await using var write=f.NewContext();var service=Service(write);var preview=await service.PreviewAsync(actor,s.Seed.Project,receipt,issue);Assert.True(preview.Ok,preview.Code);Assert.Equal(4m,preview.NetBaseCost);
        var result=await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,preview.Version!,request,"Attribute explicit receipt");Assert.True(result.Ok,result.Code);
        var replay=await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,preview.Version!,request,"Attribute explicit receipt");Assert.Equal("AlreadyRegistered",replay.Code);Assert.Equal(result.Id,replay.Id);
        Assert.Equal("IdempotencyConflict",(await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,preview.Version!,request,"Different reason")).Code);
        Assert.Equal("IssueAlreadyAttributed",(await service.PreviewAsync(actor,s.Seed.Project,receipt,issue)).Code);
        await using var read=f.NewContext();var row=await read.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync();
        Assert.Equal(issue,row.IssueMovementId);Assert.Equal(4m,row.NetBaseCost);Assert.Equal(.4m,row.QuantityBase);Assert.Equal(64,row.SourceVersion.Length);
        Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());Assert.Single(await read.Set<ConstructionCommitmentReservation>().ToListAsync());
        var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Seed.Project);Assert.True(exposure.Ok,exposure.Code);
        var budget=Assert.Single(exposure.Rows!);Assert.Equal(4m,budget.Actual);Assert.Equal(6m,budget.Commitment);Assert.Equal(90m,budget.Remaining);
        Assert.Equal(exposure.Version,(await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Seed.Project)).Version);
    }

    [Fact]
    public async Task New_receipt_consumption_invalidates_a_stale_second_intent_then_fresh_full_graph_reconciles()
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
        var one=await Issue(f.Db,s,.4m);var two=await Issue(f.Db,s,.6m);var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;
        await using var write=f.NewContext();var service=Service(write);var actor=ConstructionR7OrderAllocationTests.Actor();
        var first=await service.PreviewAsync(actor,s.Seed.Project,receipt,one);var old=await service.PreviewAsync(actor,s.Seed.Project,receipt,two);
        Assert.True(first.Ok,first.Code);Assert.True(old.Ok,old.Code);
        Assert.True((await service.RegisterAsync(actor,s.Seed.Project,receipt,one,first.Version!,Guid.NewGuid(),"First split")).Ok);
        Assert.Equal("ConcurrencyConflict",(await service.RegisterAsync(actor,s.Seed.Project,receipt,two,old.Version!,Guid.NewGuid(),"Stale split")).Code);
        var fresh=await service.PreviewAsync(actor,s.Seed.Project,receipt,two);Assert.True(fresh.Ok,fresh.Code);
        var result=await service.RegisterAsync(actor,s.Seed.Project,receipt,two,fresh.Version!,Guid.NewGuid(),"Remaining split");Assert.True(result.Ok,result.Code);
        await using var read=f.NewContext();var rows=await read.Set<ConstructionMaterialConsumption>().AsNoTracking().ToListAsync();
        Assert.Equal(2,rows.Count);Assert.Equal(1m,rows.Sum(x=>x.QuantityBase));Assert.Equal(10m,rows.Sum(x=>x.NetBaseCost));
        var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Seed.Project);Assert.True(exposure.Ok,exposure.Code);
        var budget=Assert.Single(exposure.Rows!);Assert.Equal(10m,budget.Actual);Assert.Equal(0m,budget.Commitment);Assert.Equal(90m,budget.Remaining);
    }

    [Theory]
    [InlineData("issue")][InlineData("receipt")][InlineData("source")][InlineData("dimension")]
    [InlineData("currency")][InlineData("record")][InlineData("reversal")]
    public async Task Stored_consumption_never_releases_commitment_when_current_source_evidence_has_changed(string kind)
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
        var issue=await Issue(f.Db,s,.4m);var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;
        var actor=ConstructionR7OrderAllocationTests.Actor();
        await using(var write=f.NewContext())
        {
            var service=Service(write);var preview=await service.PreviewAsync(actor,s.Seed.Project,receipt,issue);Assert.True(preview.Ok,preview.Code);
            Assert.True((await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,preview.Version!,Guid.NewGuid(),"Registered source")).Ok);
        }
        await using(var tamper=f.NewContext())
        {
            var entry=(await tamper.StockMovements.AsNoTracking().SingleAsync(x=>x.ID==issue)).JournalEntryId;
            if(kind=="issue") await tamper.StockMovements.Where(x=>x.ID==issue).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.QtyBase,.5m));
            if(kind=="receipt") await tamper.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.InvoiceId,(int?)null));
            if(kind=="source") await tamper.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Notes,"Changed after consumption"));
            if(kind=="dimension") await tamper.Set<ConstructionActualCostAllocationLine>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.CostCodeId,99));
            if(kind=="currency") await tamper.JournalEntries.Where(x=>x.ID==entry).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.FunctionalCurrencyId,(int?)99));
            if(kind=="record") await tamper.Set<ConstructionMaterialConsumption>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.SourceVersion,new string('F',64)));
            if(kind=="reversal") await tamper.JournalEntries.Where(x=>x.ID==entry).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Reversed"));
        }
        await using var read=f.NewContext();
        var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Seed.Project);
        Assert.False(exposure.Ok);Assert.Null(exposure.Rows);
        Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());
    }

    [Fact]
    public async Task A_second_unmapped_issue_refuses_the_whole_budget_instead_of_releasing_only_known_consumption()
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
        var issue=await Issue(f.Db,s,.4m);var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;
        var actor=ConstructionR7OrderAllocationTests.Actor();
        await using(var write=f.NewContext())
        {
            var service=Service(write);var p=await service.PreviewAsync(actor,s.Seed.Project,receipt,issue);
            Assert.True((await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,p.Version!,Guid.NewGuid(),"Known issue")).Ok);
        }
        _=await Issue(f.Db,s,.2m);
        await using var read=f.NewContext();var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Seed.Project);
        Assert.False(exposure.Ok);Assert.Null(exposure.Rows);Assert.Contains(exposure.Issues!,x=>x.Code=="InventoryConsumptionCoverageRequired");
    }

    [Fact]
    public async Task Budget_viewer_can_read_transferred_exposure_but_cannot_register_consumption()
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
        var issue=await Issue(f.Db,s,.4m);var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;var actor=ConstructionR7OrderAllocationTests.Actor();
        await using(var write=f.NewContext())
        {
            var service=Service(write);var p=await service.PreviewAsync(actor,s.Seed.Project,receipt,issue);
            Assert.True((await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,p.Version!,Guid.NewGuid(),"Authorized writer")).Ok);
        }
        await using var read=f.NewContext();var access=new ViewOnlyAccess();var audit=new ConstructionAuditService(read);
        var exposure=await new ConstructionBudgetExposureService(read,access,audit).PreviewAsync(actor,s.Seed.Project);
        Assert.True(exposure.Ok,exposure.Code);Assert.Equal(6m,Assert.Single(exposure.Rows!).Commitment);
        Assert.Equal("AccessDenied",(await new ConstructionMaterialConsumptionService(read,access,audit).RegisterAsync(actor,s.Seed.Project,receipt,issue,new string('A',64),Guid.NewGuid(),"Viewer cannot write")).Code);
    }

    [Theory]
    [InlineData(90,true)][InlineData(91,false)]
    public async Task Subsequent_admission_uses_actual_plus_remaining_commitment_without_manufacturing_budget(int amount,bool allowed)
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
        var issue=await Issue(f.Db,s,.4m);var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;var actor=ConstructionR7OrderAllocationTests.Actor();
        await using(var write=f.NewContext())
        {
            var service=Service(write);var p=await service.PreviewAsync(actor,s.Seed.Project,receipt,issue);
            Assert.True((await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,p.Version!,Guid.NewGuid(),"Before next admission")).Ok);
        }
        await using(var proposal=f.NewContext())
        {
            var draft=await ConstructionR7PurchaseDraftTests.Create(proposal,s.Seed,Guid.NewGuid(),new[] { new ConstructionPurchaseDraftLine(s.Seed.Item,"Next purchase",1,s.Seed.Unit,amount,0,20) });
            Assert.True(draft.Ok,draft.Code);var line=await proposal.PurchaseOrderLines.AsNoTracking().SingleAsync(x=>x.PurchaseOrderId==draft.OrderId);
            var editor=new ConstructionOrderAllocationService(proposal,new ConstructionR7OrderAllocationTests.Access(),new ConstructionAuditService(proposal));
            var preview=await editor.PreviewAsync(actor,s.Seed.Project,draft.OrderId!.Value);Assert.True(preview.Ok,preview.Code);
            var mapped=await editor.SaveAsync(actor,s.Seed.Project,draft.OrderId.Value,preview.Version!,Guid.NewGuid(),new[] { new ConstructionCommitmentSplit(line.ID,s.Seed.Node,s.Seed.Cost,amount) },"Map next proposal");Assert.True(mapped.Ok,mapped.Code);
            var admitted=await ConstructionR7ReservationTests.Service(proposal).ReserveAsync(actor,s.Seed.Project,mapped.Id!.Value,preview.Version!,Guid.NewGuid(),"Evaluate after consumption");
            Assert.Equal(allowed,admitted.Ok);
        }
        await using var read=f.NewContext();var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Seed.Project);
        Assert.True(exposure.Ok,exposure.Code);var budget=Assert.Single(exposure.Rows!);
        Assert.Equal(4m,budget.Actual);Assert.Equal(allowed ? 96m : 6m,budget.Commitment);Assert.Equal(allowed ? 0m : 90m,budget.Remaining);
        Assert.Equal(allowed ? 2 : 1,await read.Set<ConstructionCommitmentReservation>().CountAsync());Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());
    }

    [Theory]
    [InlineData("company")][InlineData("project")][InlineData("warehouse")][InlineData("journal")][InlineData("receipt")]
    [InlineData("variance")][InlineData("dimension")][InlineData("currency")][InlineData("source")]
    public async Task Changed_or_foreign_facts_never_create_a_consumption_mapping(string kind)
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);var issue=await Issue(f.Db,s,.4m,kind=="variance" ? 6m : null);
        var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;
        var entry=(await f.Db.StockMovements.AsNoTracking().SingleAsync(x=>x.ID==issue)).JournalEntryId;
        if(kind=="company") await f.Db.StockMovements.Where(x=>x.ID==issue).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.CompanyID,99));
        if(kind=="project") await f.Db.JournalEntryLines.Where(x=>x.JournalEntryId==entry).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ProjectId,(int?)99));
        if(kind=="warehouse") await f.Db.StockMovements.Where(x=>x.ID==issue).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.WarehouseId,99));
        if(kind=="journal") await f.Db.JournalEntries.Where(x=>x.ID==entry).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Reversed"));
        if(kind=="receipt") await f.Db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.InvoiceId,(int?)null));
        if(kind=="dimension") await f.Db.Set<ConstructionActualCostAllocationLine>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.CostCodeId,99));
        if(kind=="currency") await f.Db.JournalEntries.Where(x=>x.ID==entry).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.FunctionalCurrencyId,(int?)99));
        if(kind=="source") await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Notes,"Changed original purchase"));
        await using var write=f.NewContext();var service=Service(write);var actor=ConstructionR7OrderAllocationTests.Actor();
        Assert.False((await service.PreviewAsync(actor,s.Seed.Project,receipt,issue)).Ok);
        Assert.False((await service.RegisterAsync(actor,s.Seed.Project,receipt,issue,new string('A',64),Guid.NewGuid(),"Refuse bad source")).Ok);
        await using var read=f.NewContext();Assert.Empty(await read.Set<ConstructionMaterialConsumption>().ToListAsync());
    }

    [Fact]
    public async Task Prior_graph_bounds_prevent_reusing_receipt_capacity_for_another_posted_issue()
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
        var one=await Issue(f.Db,s,.6m);var two=await Issue(f.Db,s,.6m);var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;
        await using var write=f.NewContext();var service=Service(write);var actor=ConstructionR7OrderAllocationTests.Actor();
        var p=await service.PreviewAsync(actor,s.Seed.Project,receipt,one);Assert.True(p.Ok,p.Code);
        Assert.True((await service.RegisterAsync(actor,s.Seed.Project,receipt,one,p.Version!,Guid.NewGuid(),"First capacity")).Ok);
        var refused=await service.PreviewAsync(actor,s.Seed.Project,receipt,two);Assert.False(refused.Ok);Assert.Equal("ReceiptConsumptionExceeded",refused.Code);
    }

    [Fact]
    public async Task Access_clean_context_and_scope_are_required_before_any_mapping()
    {
        using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);var issue=await Issue(f.Db,s,.4m);
        var receipt=(await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;var actor=ConstructionR7OrderAllocationTests.Actor();
        await using var db=f.NewContext();Assert.Equal("AccessDenied",(await Service(db,false).PreviewAsync(actor,s.Seed.Project,receipt,issue)).Code);
        db.Vendors.Add(new Vendor { CompanyID=1,Name="Unsaved" });Assert.Equal("CleanUnitOfWorkRequired",(await Service(db).PreviewAsync(actor,s.Seed.Project,receipt,issue)).Code);
    }
}
