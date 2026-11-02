using CrossBuy.BL.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using CrossBuy.BL;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public class CrmOwnerWriteGuardTests
{
    private static IBusinessEventService Recorder(CrossBuy.Models.Context.CrossDbContext db) => new BusinessEventService(db, new EntityRegistry(db), new StubContextAccessor(PlatformTestHost.DefaultContext(db.CompanyScope.CompanyId ?? 1)), NullLogger<BusinessEventService>.Instance);

    private sealed class Access : ICrmAccessService
    {
        public int? CurrentEmployeeId() => 31;
        public Task<List<string>> MyRolesAsync() => Task.FromResult(new List<string>());
        public Task<bool> CanAsync(string action) => Task.FromResult(true);
        public Task<HashSet<int>?> VisibleOwnerIdsAsync() => Task.FromResult<HashSet<int>?>(new() {31});
        public Task<HashSet<int>> TeamOwnerIdsAsync(int id) => Task.FromResult(new HashSet<int>());
        public Task<string> RoleLabelAsync(bool ar) => Task.FromResult("test");
    }

    [Theory]
    [InlineData(41, "lead")][InlineData(77, "lead")]
    [InlineData(41, "opportunity")][InlineData(77, "opportunity")]
    [InlineData(41, "account")][InlineData(77, "account")]
    [InlineData(41, "conversion")][InlineData(77, "conversion")]
    [InlineData(41, "stage")][InlineData(77, "stage")]
    public async Task Hidden_record_write_is_refused_and_persisted_state_is_unchanged(int company, string door)
    {
        using var host = new PlatformTestHost(company);
        var lead = new Lead {CompanyID=company, Name="Original lead", Status="New", OwnerEmployeeId=32};
        var opp = new Opportunity {CompanyID=company, Title="Original opportunity", Stage="Prospecting", OwnerEmployeeId=32};
        var account = new CrmAccount {CompanyID=company, Name="Original account", OwnerEmployeeId=32, IsActive=true};
        host.Seed.AddRange(lead, opp, account);
        await host.Seed.SaveChangesAsync();
        using var db=host.Request(company).Db;
        var svc=new CrmService(db,null!,null!,new Access(),null!,Recorder(db));
        bool ok=door switch {
            "lead" => (await svc.SaveLeadAsync(company,new Lead {ID=lead.ID,Name="Forged",Status="Qualified"},null)).ok,
            "opportunity" => (await svc.SaveOpportunityAsync(company,new Opportunity {ID=opp.ID,Title="Forged",Stage="Proposal"},null)).ok,
            "account" => (await svc.SaveAccountAsync(company,new CrmAccount {ID=account.ID,Name="Forged",IsActive=true},null)).ok,
            "conversion" => (await svc.ConvertLeadToAccountAsync(company,lead.ID,null)).ok,
            _ => (await svc.UpdateOpportunityStageAsync(company,opp.ID,"Proposal")).ok
        };
        using var read=host.Request(company).Db;
        Assert.False(ok);
        Assert.Equal("Original lead",(await read.Leads.SingleAsync()).Name);
        Assert.Equal("New",(await read.Leads.SingleAsync()).Status);
        Assert.Null((await read.Leads.SingleAsync()).AccountId);
        Assert.Equal("Original opportunity",(await read.Opportunities.SingleAsync()).Title);
        Assert.Equal("Prospecting",(await read.Opportunities.SingleAsync()).Stage);
        Assert.Equal("Original account",(await read.CrmAccounts.SingleAsync()).Name);
        Assert.Equal(1,await read.CrmAccounts.CountAsync());
    }

    [Theory]
    [InlineData(41,"lead")][InlineData(77,"lead")]
    [InlineData(41,"opportunity")][InlineData(77,"opportunity")]
    [InlineData(41,"account")][InlineData(77,"account")]
    public async Task Visible_editor_write_remains_allowed(int company,string door)
    {
        using var host=new PlatformTestHost(company);
        var lead=new Lead {CompanyID=company,Name="Original",Status="New",OwnerEmployeeId=31};
        var opp=new Opportunity {CompanyID=company,Title="Original",Stage="Prospecting",OwnerEmployeeId=31};
        var account=new CrmAccount {CompanyID=company,Name="Original",OwnerEmployeeId=31,IsActive=true};
        host.Seed.AddRange(lead,opp,account); await host.Seed.SaveChangesAsync();
        using var db=host.Request(company).Db;
        var svc=new CrmService(db,null!,null!,new Access(),null!,Recorder(db));
        bool ok=door switch {
            "lead" => (await svc.SaveLeadAsync(company,new Lead {ID=lead.ID,Name="Edited",Status="New"},null)).ok,
            "opportunity" => (await svc.SaveOpportunityAsync(company,new Opportunity {ID=opp.ID,Title="Edited",Stage="Prospecting"},null)).ok,
            _ => (await svc.SaveAccountAsync(company,new CrmAccount {ID=account.ID,Name="Edited",IsActive=true},null)).ok
        };
        Assert.True(ok);
        using var read=host.Request(company).Db;
        string value=door switch {"lead"=>(await read.Leads.SingleAsync()).Name,"opportunity"=>(await read.Opportunities.SingleAsync()).Title,_=>(await read.CrmAccounts.SingleAsync()).Name};
        Assert.Equal("Edited",value);
    }
}
