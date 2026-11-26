using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ReceiptWitnessTests
{
    internal static async Task<ConstructionR7PurchaseApprovalTests.Setup> Admitted(CrossDbContext db)
    {
        var s = await ConstructionR7PurchaseApprovalTests.Arrange(db, 10m);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
        var request = await ConstructionR7PurchaseApprovalTests.Request(service, s, Guid.NewGuid()); Assert.True(request.Ok, request.Code);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var approved = await service.ApproveAsync(request.ApprovalId!.Value, Guid.NewGuid(), "Admit before fulfillment"); Assert.True(approved.Ok, approved.Code);
        return s;
    }

    internal static async Task ReceiptFixture(CrossDbContext db, ConstructionR7PurchaseApprovalTests.Setup s, decimal quantity)
    {
        var receipt = new GoodsReceipt { CompanyID=1,PurchaseOrderId=s.Order,VendorId=s.Seed.Vendor,WarehouseId=s.Seed.Warehouse,
            CurrencyId=s.Seed.Currency,ExchangeRate=1,Status="Posted",TotalCost=quantity*10,ReceiptDate=DateTime.Today };
        db.Add(receipt); await db.SaveChangesAsync();
        var movement = new StockMovement { CompanyID=1,ItemId=s.Seed.Item,WarehouseId=s.Seed.Warehouse,Direction=1,
            QtyBase=quantity,QtyInUoM=quantity,UoMId=s.Seed.Unit,UnitCost=10,TotalCost=quantity*10,SourceType="Receipt",SourceId=receipt.ID };
        db.Add(movement); await db.SaveChangesAsync();
        var entry = new JournalEntry { CompanyID=1,EntryDate=DateTime.Today,Status="Posted",SourceType="Inventory",SourceId=movement.ID,
            CurrencyId=s.Seed.Currency,FunctionalCurrencyId=s.Seed.Currency,FunctionalCurrencyDecimals=3,
            Lines=new List<JournalEntryLine> { new() { LineNo=1,AccountId=1,Debit=quantity*10 },new() { LineNo=2,AccountId=2,Credit=quantity*10 } } };
        db.Add(entry); await db.SaveChangesAsync();
        movement.JournalEntryId=entry.ID;
        receipt.Lines.Add(new GoodsReceiptLine { ItemId=s.Seed.Item,LineNo=1,Qty=quantity,UoMId=s.Seed.Unit,UnitCost=10,
            LineTotal=quantity*10,PurchaseOrderLineId=s.Lines.Single(),StockMovementId=movement.ID });
        await db.SaveChangesAsync();
        await db.PurchaseOrderLines.Where(x=>x.PurchaseOrderId==s.Order).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ReceivedQty,quantity));
        if(quantity==1) await db.PurchaseOrders.Where(x=>x.ID==s.Order).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Received"));
    }

    [Theory][InlineData("0.4")][InlineData("1")]
    public async Task Proven_receiving_preserves_original_allocation_and_full_commitment_without_rewriting_admission(string text)
    {
        using var f=new ConstructionTestFixture();var s=await Admitted(f.Db);
        var before=await f.Db.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();
        await ReceiptFixture(f.Db,s,decimal.Parse(text,System.Globalization.CultureInfo.InvariantCulture));
        await using var read=f.NewContext();
        var allocation=new ConstructionOrderAllocationService(read,new ConstructionR7OrderAllocationTests.Access(),new ConstructionAuditService(read));
        var preview=await allocation.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project,s.Order); Assert.True(preview.Ok,preview.Code);
        Assert.Equal(decimal.Parse(text,System.Globalization.CultureInfo.InvariantCulture),Assert.Single(preview.Source!.Lines).ReceivedQuantity);
        var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project);
        Assert.True(exposure.Ok,exposure.Code);Assert.Equal(10m,Assert.Single(exposure.Rows!).Commitment);Assert.Equal(0m,Assert.Single(exposure.Rows!).Actual);
        await using var verify=f.NewContext();var after=await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();
        Assert.Equal(before.ContentHash,after.ContentHash);Assert.Equal(before.BeforeVersion,after.BeforeVersion);Assert.Equal(before.ApprovedVersion,after.ApprovedVersion);
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().ToListAsync());
    }

    [Theory]
    [InlineData("unlinked")][InlineData("quantity")][InlineData("over")][InlineData("direction")]
    [InlineData("journal")][InlineData("currency")][InlineData("status")][InlineData("movement")]
    [InlineData("missingJournal")][InlineData("wrongJournal")][InlineData("commercial")][InlineData("cost")]
    public async Task Unproven_receipt_cannot_reconstruct_a_valid_admission(string kind)
    {
        using var f=new ConstructionTestFixture();var s=await Admitted(f.Db);await ReceiptFixture(f.Db,s,0.4m);
        if(kind=="unlinked") await f.Db.GoodsReceiptLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.PurchaseOrderLineId,(int?)null));
        if(kind=="quantity") await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ReceivedQty,0.5m));
        if(kind=="over") await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ReceivedQty,2m));
        if(kind=="direction") await f.Db.StockMovements.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Direction,(short)-1));
        if(kind=="journal") await f.Db.JournalEntries.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Reversed"));
        if(kind=="currency") await f.Db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.CurrencyId,(int?)999));
        if(kind=="status") await f.Db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Cancelled"));
        if(kind=="movement") await f.Db.GoodsReceiptLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.StockMovementId,(int?)null));
        if(kind=="missingJournal") await f.Db.StockMovements.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.JournalEntryId,(int?)null));
        if(kind=="wrongJournal") await f.Db.JournalEntries.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.SourceId,(int?)999));
        if(kind=="commercial") await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Notes,"Changed commercial source"));
        if(kind=="cost")
        {
            await f.Db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.TotalCost,8m));
            await f.Db.GoodsReceiptLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.UnitCost,20m).SetProperty(p=>p.LineTotal,8m));
            await f.Db.StockMovements.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.UnitCost,20m).SetProperty(p=>p.TotalCost,8m));
            await f.Db.JournalEntryLines.Where(x=>x.Debit>0).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Debit,8m));
            await f.Db.JournalEntryLines.Where(x=>x.Credit>0).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Credit,8m));
        }
        await using var read=f.NewContext();var allocation=new ConstructionOrderAllocationService(read,new ConstructionR7OrderAllocationTests.Access(),new ConstructionAuditService(read));
        var preview=await allocation.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project,s.Order);
        Assert.False(preview.Ok);Assert.Equal(kind=="commercial" ? "AdmissionSourceNotProven" : "ReceiptCoverageRequired",preview.Code);
        Assert.Single(await read.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());Assert.Single(await read.Set<ConstructionCommitmentReservation>().ToListAsync());
        Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());
    }

    [Fact]
    public async Task Received_counter_without_receipt_proof_remains_refused()
    {
        using var f=new ConstructionTestFixture();var s=await Admitted(f.Db);
        await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ReceivedQty,0.4m));
        var result=await new ConstructionOrderAllocationService(f.Db,new ConstructionR7OrderAllocationTests.Access(),f.Audit)
            .PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project,s.Order);
        Assert.Equal("ReceiptCoverageRequired",result.Code);
    }
}
