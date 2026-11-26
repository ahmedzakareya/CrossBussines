using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7StockInvoiceTests
{
    internal static async Task Accounts(CrossDbContext db, ConstructionR7PurchaseApprovalTests.Setup s)
    {
        var asset=new AccountType { Code="ASSET",Name="Asset",StatementType="BalanceSheet" };
        var liability=new AccountType { Code="LIAB",Name="Liability",StatementType="BalanceSheet" };
        db.AddRange(asset,liability);await db.SaveChangesAsync();
        Account Account(string code,int type)=>new() { CompanyID=1,Code=code,Name=code,AccountTypeId=type,IsActive=true,IsPostable=true };
        var inventory=Account("110301",asset.ID);var grni=Account("210203",liability.ID);
        var payable=Account("2101",liability.ID);var vat=Account("110401",asset.ID);
        db.AddRange(inventory,grni,payable,vat);await db.SaveChangesAsync();
        var item=await db.Items.SingleAsync(x=>x.ID==s.Seed.Item);item.ItemType="Stockable";
        var category=await db.ItemCategories.SingleAsync(x=>x.ID==item.ItemCategoryId);category.InventoryAccountId=inventory.ID;category.GrniAccountId=grni.ID;
        (await db.Vendors.SingleAsync(x=>x.ID==s.Seed.Vendor)).ControlAccountId=payable.ID;
        await db.SaveChangesAsync();
    }
    internal static async Task<ConstructionR7PurchaseApprovalTests.Setup> Arranged(CrossDbContext db)
    {
        var s=await ConstructionR7ReceiptWitnessTests.Admitted(db);await Accounts(db,s);
        await ConstructionR7ReceiptWitnessTests.ReceiptFixture(db,s,1m);
        await Invoice(db,s);
        return s;
    }
    internal static async Task Invoice(CrossDbContext db,ConstructionR7PurchaseApprovalTests.Setup s,bool includeLegacyProvenance=true)
    {
        var grni=await db.Accounts.SingleAsync(x=>x.Code=="210203");var ap=await db.Accounts.SingleAsync(x=>x.Code=="2101");
        var vat=await db.Accounts.SingleAsync(x=>x.Code=="110401");
        var invoice=new PurchaseInvoice { CompanyID=1,VendorId=s.Seed.Vendor,ProjectId=s.Seed.Project,CurrencyId=s.Seed.Currency,
            ExchangeRate=1,Status="Posted",SubTotal=10,TaxTotal=2,GrandTotal=12,SubTotalBase=10,TaxTotalBase=2,GrandTotalBase=12,
            Lines=new List<PurchaseInvoiceLine> { new() { LineNo=1,Qty=1,UnitPrice=10,TaxRate=20,LineTotal=10,ExpenseAccountId=grni.ID } } };
        db.Add(invoice);await db.SaveChangesAsync();
        var entry=new JournalEntry { CompanyID=1,Status="Posted",SourceType="PurchaseInvoice",SourceId=invoice.ID,EntryDate=DateTime.Today,
            CurrencyId=s.Seed.Currency,FunctionalCurrencyId=s.Seed.Currency,FunctionalCurrencyDecimals=3,
            Lines=new List<JournalEntryLine> { new() { LineNo=1,AccountId=grni.ID,ProjectId=s.Seed.Project,Debit=10 },
                new() { LineNo=2,AccountId=vat.ID,ProjectId=s.Seed.Project,Debit=2 },new() { LineNo=3,AccountId=ap.ID,ProjectId=s.Seed.Project,Credit=12 } } };
        db.Add(entry);await db.SaveChangesAsync();invoice.JournalEntryId=entry.ID;
        if(includeLegacyProvenance)
        {
        db.Add(new PurchaseOrderInvoiceLink { CompanyID=1,PurchaseOrderId=s.Order,PurchaseInvoiceId=invoice.ID,CreatedAt=DateTime.UtcNow });
        db.Add(new PurchaseOrderInvoiceLineLink { CompanyID=1,PurchaseOrderId=s.Order,PurchaseOrderLineId=s.Lines.Single(),
            PurchaseInvoiceId=invoice.ID,PurchaseInvoiceLineId=invoice.Lines.Single().ID,JournalEntryId=entry.ID,JournalEntryLineId=entry.Lines.Single(x=>x.LineNo==1).ID });
        }
        await db.SaveChangesAsync();
        await db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.InvoiceId,(int?)invoice.ID));
        await db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Closed"));
    }

    [Fact]
    public async Task Clearing_invoice_preserves_full_commitment_without_counting_VAT_or_GRNI_as_expense()
    {
        using var f=new ConstructionTestFixture();var s=await Arranged(f.Db);
        await using var read=f.NewContext();var reader=ConstructionR7BudgetExposureTests.Reader(read);
        var first=await reader.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project);Assert.True(first.Ok,first.Code);
        var row=Assert.Single(first.Rows!);Assert.Equal(10m,row.Commitment);Assert.Equal(0m,row.Actual);Assert.Equal(90m,row.Remaining);
        Assert.Empty(first.ReplacedOrderIds!);
        Assert.Equal(first.Version,(await reader.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project)).Version);
        var editor=new ConstructionOrderAllocationService(read,new ConstructionR7OrderAllocationTests.Access(),new ConstructionAuditService(read));
        Assert.Equal("SourceNotOpenAndReconciled",(await editor.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project,s.Order)).Code);
        Assert.Single(await read.Set<ConstructionPurchaseAdmission>().ToListAsync());Assert.Single(await read.Set<ConstructionCommitmentReservation>().ToListAsync());
        Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());Assert.Empty(await read.Set<ConstructionActualCostAllocation>().ToListAsync());
    }

    [Theory]
    [InlineData("receipt-link")][InlineData("quantity")][InlineData("price")][InlineData("journal")]
    [InlineData("commercial")][InlineData("balance")][InlineData("vat")][InlineData("net-gl")]
    [InlineData("scope")][InlineData("lineage")][InlineData("receipt-gl")][InlineData("admission")]
    public async Task Unproven_clearing_never_returns_spendable_amounts(string kind)
    {
        using var f=new ConstructionTestFixture();var s=await Arranged(f.Db);
        var invoice=await f.Db.PurchaseInvoices.AsNoTracking().SingleAsync();
        if(kind=="receipt-link") await f.Db.GoodsReceipts.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.InvoiceId,(int?)null));
        if(kind=="quantity") await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ReceivedQty,.5m));
        if(kind=="price") await f.Db.PurchaseInvoiceLines.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.UnitPrice,11m));
        if(kind=="journal") await f.Db.JournalEntries.Where(x=>x.ID==invoice.JournalEntryId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Reversed"));
        if(kind=="commercial") await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Notes,"Changed after admission"));
        if(kind=="balance") await f.Db.JournalEntryLines.Where(x=>x.JournalEntryId==invoice.JournalEntryId && x.Credit>0).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Credit,11m));
        if(kind=="vat") await f.Db.Accounts.Where(x=>x.Code=="110401").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Code,"OTHER-ASSET"));
        if(kind=="net-gl") await f.Db.JournalEntryLines.Where(x=>x.JournalEntryId==invoice.JournalEntryId && x.LineNo==1).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Debit,9m));
        if(kind=="scope") await f.Db.JournalEntryLines.Where(x=>x.JournalEntryId==invoice.JournalEntryId && x.LineNo==1).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ProjectId,(int?)999));
        if(kind=="lineage") await f.Db.Set<PurchaseOrderInvoiceLineLink>().ExecuteDeleteAsync();
        if(kind=="receipt-gl")
        {
            var payable=(await f.Db.Accounts.AsNoTracking().SingleAsync(x=>x.Code=="2101")).ID;
            var receiptEntry=(await f.Db.JournalEntries.AsNoTracking().SingleAsync(x=>x.SourceType=="Inventory")).ID;
            await f.Db.JournalEntryLines.Where(x=>x.JournalEntryId==receiptEntry && x.Credit>0).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.AccountId,payable));
        }
        if(kind=="admission") await f.Db.Set<ConstructionPurchaseAdmission>().ExecuteDeleteAsync();
        await using var read=f.NewContext();var result=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project);
        Assert.False(result.Ok);Assert.Null(result.Rows);Assert.Null(result.Version);Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToListAsync());
    }

    [Fact]
    public async Task Attributed_inventory_expense_still_requires_consumption_lineage_instead_of_double_counting()
    {
        using var f=new ConstructionTestFixture();var s=await Arranged(f.Db);
        var type=new AccountType { Code="EXP",Name="Expense",StatementType="IncomeStatement" };f.Db.Add(type);await f.Db.SaveChangesAsync();
        var account=new Account { CompanyID=1,Code="ISSUE",Name="Material expense",AccountTypeId=type.ID,IsActive=true,IsPostable=true };
        f.Db.Add(account);await f.Db.SaveChangesAsync();
        var entry=new JournalEntry { CompanyID=1,Status="Posted",SourceType="Inventory",SourceId=99,EntryDate=DateTime.Today,
            FunctionalCurrencyId=s.Seed.Currency,FunctionalCurrencyDecimals=3,Lines=new List<JournalEntryLine> {
                new() { LineNo=1,AccountId=account.ID,ProjectId=s.Seed.Project,Debit=4 },
                new() { LineNo=2,AccountId=(await f.Db.Accounts.SingleAsync(x=>x.Code=="110301")).ID,ProjectId=s.Seed.Project,Credit=4 } } };
        f.Db.Add(entry);await f.Db.SaveChangesAsync();
        await ConstructionR7BudgetExposureTests.Attribute(f.Db,s.Seed.Project,entry.Lines.Single(x=>x.LineNo==1).ID,4m);
        await using var read=f.NewContext();var result=await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(),s.Seed.Project);
        Assert.False(result.Ok);Assert.Contains(result.Issues!,x=>x.Code=="InventoryConsumptionCoverageRequired");Assert.Null(result.Rows);
    }
}
