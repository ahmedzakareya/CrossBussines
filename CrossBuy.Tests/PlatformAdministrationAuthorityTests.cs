using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests;

public sealed class PlatformAdministrationAuthorityTests
{
    private sealed class Identity(params string[] held) : IPlatformAdminIdentity
    {
        public int Calls;
        public Task<IReadOnlyList<string>> RolesAsync(string? userId,CancellationToken cancellationToken=default)
        {Calls++;return Task.FromResult<IReadOnlyList<string>>(held);}
    }
    private static BusinessContext Context(int company=1,int? employee=7,BusinessContextSource source=BusinessContextSource.Http,string user="user")
        =>new(){CompanyId=company,EmployeeId=employee,Source=source,UserId=user,Roles=new[]{"SuperAdmin"}};
    [Theory]
    [InlineData("SuperAdmin",true)]
    [InlineData("PlatformOps",true)]
    [InlineData("Admin",false)]
    [InlineData("Administrator",false)]
    [InlineData("SalesManager",false)]
    public async Task Console_uses_the_existing_platform_security_identity_tier(string role,bool allowed)
        =>Assert.Equal(allowed,await PlatformAdministrationReader.CanReadAsync(Context(),new Identity(role)));
    [Fact]
    public async Task A_stale_or_forged_context_role_does_not_replace_the_identity_store()
        =>Assert.False(await PlatformAdministrationReader.CanReadAsync(Context(),new Identity()));
    [Theory]
    [InlineData(0,7,BusinessContextSource.Http,"user")]
    [InlineData(1,0,BusinessContextSource.Http,"user")]
    [InlineData(1,7,BusinessContextSource.ExternalPortal,"user")]
    [InlineData(1,7,BusinessContextSource.Http,"")]
    public async Task Unresolved_or_external_context_reaches_no_administration_lookup(int company,int employee,BusinessContextSource source,string user)
    {
        var identity=new Identity("SuperAdmin");
        Assert.False(await PlatformAdministrationReader.CanReadAsync(Context(company,employee,source,user),identity));
        Assert.Equal(0,identity.Calls);
    }
}
