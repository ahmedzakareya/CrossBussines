using System.Globalization;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7UnbilledConsumptionTests
{
    private static async Task<(ConstructionR7PurchaseApprovalTests.Setup Setup,int Receipt,int Issue)> Arrange(CrossDbContext db,decimal received,decimal consumed)
    {
        var s=await ConstructionR7ReceiptWitnessTests.Admitted(db);await ConstructionR7StockInvoiceTests.Accounts(db,s);
        await ConstructionR7ReceiptWitnessTests.ReceiptFixture(db,s,received);
        var receipt=(await db.GoodsReceiptLines.AsNoTracking().SingleAsync()).ID;
        var issue=await ConstructionR7MaterialConsumptionTests.Issue(db,s,consumed);
        return(s,receipt,issue);
    }

    [Theory][InlineData("0.4","0.2")][InlineData("1","0.4")]
    public async Task Proven_unbilled_consumption_transfers_only_consumed_cost_from_the_original_full_commitment(string received,string consumed)
    {
        using var f=new ConstructionTestFixture();var amount=decimal.Parse(consumed,CultureInfo.InvariantCulture)*10;
        var s=await Arrange(f.Db,decimal.Parse(received,CultureInfo.InvariantCulture),amount/10);
        var actor=ConstructionR7OrderAllocationTests.Actor();
        await using(var write=f.NewContext())
        {
            var service=ConstructionR7MaterialConsumptionTests.Service(write);var p=await service.PreviewAsync(actor,s.Setup.Seed.Project,s.Receipt,s.Issue);Assert.True(p.Ok,p.Code);
            var result=await service.RegisterAsync(actor,s.Setup.Seed.Project,s.Receipt,s.Issue,p.Version!,Guid.NewGuid(),"Proven unbilled consumption");Assert.True(result.Ok,result.Code);
        }
        await using var read=f.NewContext();var exposure=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Setup.Seed.Project);
        Assert.True(exposure.Ok,exposure.Code);var row=Assert.Single(exposure.Rows!);
        Assert.Equal(amount,row.Actual);Assert.Equal(10-amount,row.Commitment);Assert.Equal(90m,row.Remaining);
        Assert.Empty(await read.PurchaseInvoices.ToListAsync());Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());
        Assert.Single(await read.Set<ConstructionCommitmentReservation>().ToListAsync());
    }

    [Fact]
    public async Task Later_remaining_receipt_and_invoice_preserve_immutable_consumption_and_do_not_create_more_budget()
    {
        using var f=new ConstructionTestFixture();var s=await Arrange(f.Db,.4m,.2m);var actor=ConstructionR7OrderAllocationTests.Actor();
        string recorded;
        await using(var write=f.NewContext())
        {
            var service=ConstructionR7MaterialConsumptionTests.Service(write);var p=await service.PreviewAsync(actor,s.Setup.Seed.Project,s.Receipt,s.Issue);
            Assert.True(p.Ok,p.Code);Assert.True((await service.RegisterAsync(actor,s.Setup.Seed.Project,s.Receipt,s.Issue,p.Version!,Guid.NewGuid(),"Before invoice")).Ok);
            recorded=(await write.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion;
        }
        await ConstructionR7ReceiptWitnessTests.ReceiptFixture(f.Db,s.Setup,.6m);
        await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ReceivedQty,1m));
        await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Received"));
        await using(var before=f.NewContext())
        {
            var exposure=await ConstructionR7BudgetExposureTests.Reader(before).PreviewAsync(actor,s.Setup.Seed.Project);Assert.True(exposure.Ok,exposure.Code);
            Assert.Equal(8m,Assert.Single(exposure.Rows!).Commitment);
        }
        await ConstructionR7StockInvoiceTests.Invoice(f.Db,s.Setup);
        await using var read=f.NewContext();var final=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(actor,s.Setup.Seed.Project);
        Assert.True(final.Ok,final.Code);var row=Assert.Single(final.Rows!);Assert.Equal(2m,row.Actual);Assert.Equal(8m,row.Commitment);Assert.Equal(90m,row.Remaining);
        Assert.Equal(recorded,(await read.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion);
        Assert.Single(await read.PurchaseInvoices.ToListAsync());Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());
    }

    [Theory][InlineData("classification")][InlineData("receipt-link")][InlineData("quantity")][InlineData("premature-invoice")]
    public async Task Invalid_unbilled_sources_and_unsupported_partial_invoices_do_not_register_consumption(string kind)
    {
        using var f=new ConstructionTestFixture();var s=await Arrange(f.Db,.4m,kind=="quantity" ? .5m : .2m);
        if(kind=="classification")
        {
            var grni=await f.Db.Accounts.SingleAsync(x=>x.Code=="210203");
            var asset=(await f.Db.AccountTypes.SingleAsync(x=>x.Code=="ASSET")).ID;grni.AccountTypeId=asset;await f.Db.SaveChangesAsync();
        }
        if(kind=="receipt-link") await f.Db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.InvoiceId,(int?)999));
        if(kind=="premature-invoice") await ConstructionR7StockInvoiceTests.Invoice(f.Db,s.Setup);
        await using var write=f.NewContext();var service=ConstructionR7MaterialConsumptionTests.Service(write);
        Assert.False((await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Setup.Seed.Project,s.Receipt,s.Issue)).Ok);
        await using var read=f.NewContext();Assert.Empty(await read.Set<ConstructionMaterialConsumption>().ToListAsync());
    }
}
