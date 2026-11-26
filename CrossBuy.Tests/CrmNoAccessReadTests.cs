using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Crm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests;

public sealed class CrmNoAccessReadTests
{
    private sealed class Access(HashSet<int>? owners) : ICrmAccessService
    {
        public int? CurrentEmployeeId()=>7;
        public Task<List<string>> MyRolesAsync()=>Task.FromResult(new List<string>());
        public Task<bool> CanAsync(string action)=>Task.FromResult(owners is null || owners.Count>0);
        public Task<HashSet<int>?> VisibleOwnerIdsAsync()=>Task.FromResult(owners);
        public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId)=>Task.FromResult(new HashSet<int>());
        public Task<string> RoleLabelAsync(bool isAr)=>Task.FromResult("test");
    }
    private static CrmService Service(PlatformTestHost host,HashSet<int>? owners)=>new(host.Db,null!,null!,new Access(owners),null!,
        new BusinessEventService(host.Db,new EntityRegistry(host.Db),new StubContextAccessor(PlatformTestHost.DefaultContext()),NullLogger<BusinessEventService>.Instance));
    [Fact]
    public async Task NoAccess_does_not_expose_an_unassigned_account_or_ticket_through_list_id_or_statistics()
    {
        using var host=new PlatformTestHost();
        var account=new CrmAccount {CompanyID=1,Name="not permitted",IsActive=true,CreatedAt=DateTime.UtcNow};
        host.Seed.CrmAccounts.Add(account);
        host.Seed.CrmTickets.Add(new CrmTicket {CompanyID=1,Subject="not permitted",Status="New",Priority="Normal",CreatedAt=DateTime.UtcNow});
        await host.Seed.SaveChangesAsync();
        var service=Service(host,new HashSet<int>());
        Assert.Null(await service.GetAccountAsync(1,account.ID));
        var tickets=await service.SearchTicketsAsync(1,null,null,null,1,25);
        Assert.Empty(tickets.rows);Assert.Equal(0,tickets.total);
        var statistics=await service.TicketStatsAsync(1);
        Assert.Equal(0,statistics.open);Assert.Equal(0,statistics.breachedFr);Assert.Equal(0,statistics.breachedRes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Permitted_own_and_unrestricted_scopes_keep_the_existing_unassigned_convention(bool unrestricted)
    {
        using var host=new PlatformTestHost();
        var account=new CrmAccount {CompanyID=1,Name="visible",IsActive=true,CreatedAt=DateTime.UtcNow};
        host.Seed.CrmAccounts.Add(account);await host.Seed.SaveChangesAsync();
        Assert.NotNull(await Service(host,unrestricted?null:new HashSet<int>{7}).GetAccountAsync(1,account.ID));
    }
}
