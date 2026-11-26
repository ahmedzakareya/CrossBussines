using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7StructureReadModelTests
{
    private static BusinessContext Actor()=>new(){CompanyId=1,EmployeeId=42,UserId="R7-read-model",Source=BusinessContextSource.Test};
    private static Employee Person(int company,string code)=>new()
    {
        EmpCompanyID=company,FirstName=code,LastName="Test",FullName=code,FullNameEn=code+" EN",
        Address="",PhoneNumber="",Email=code+"@example.invalid",ProfileImage="",Gender="",MaritalStatus="",UserId="R7-"+code,
        DateOfBirth=new(1990,1,1),DateOfJoining=new(2026,1,1),IsActive=true
    };

    [Fact]
    public async Task Workspace_shows_only_permitted_warehouses_and_active_same_company_project_members()
    {
        using var host=new PlatformTestHost(1);
        var own=new Project{CompanyID=1,Code="OWN",Name="Own project"};
        var foreign=new Project{CompanyID=2,Code="FOREIGN",Name="Foreign project"};
        var allowed=new Warehouse{CompanyID=1,Code="ALLOW",Name="Allowed",NameEn="Allowed EN"};
        var denied=new Warehouse{CompanyID=1,Code="DENY",Name="Denied",NameEn="Denied EN"};
        var foreignWh=new Warehouse{CompanyID=2,Code="FOREIGN",Name="Foreign warehouse"};
        var member=Person(1,"Member");var inactiveMember=Person(1,"InactiveMembership");var foreignPerson=Person(2,"ForeignMember");
        host.Seed.AddRange(own,foreign,allowed,denied,foreignWh,member,inactiveMember,foreignPerson);await host.Seed.SaveChangesAsync();
        host.Seed.AddRange(
            new ProjectMember{CompanyID=1,ProjectId=own.ID,EmployeeId=member.ID,RoleOnProject=ProjectMemberRoles.Member,IsActive=true,JoinedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow},
            new ProjectMember{CompanyID=1,ProjectId=own.ID,EmployeeId=inactiveMember.ID,RoleOnProject=ProjectMemberRoles.Member,IsActive=false,JoinedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow},
            // A tampered membership must not expose a foreign employee's name.
            new ProjectMember{CompanyID=1,ProjectId=own.ID,EmployeeId=foreignPerson.ID,RoleOnProject=ProjectMemberRoles.Member,IsActive=true,JoinedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow},
            new ConstructionCostCode{CompanyID=1,Code="OWN",Name="Own category",Category="Material"},
            new ConstructionCostCode{CompanyID=2,Code="HIDDEN",Name="Foreign category",Category="Material"});
        await host.Seed.SaveChangesAsync();
        var service=new ConstructionStructureService(host.Db,new Access(true),new Warehouses(new(){allowed.ID,foreignWh.ID}),new ConstructionAuditService(host.Db));
        var result=await service.GetAsync(Actor(),own.ID);
        Assert.NotNull(result);
        Assert.Equal(allowed.ID,Assert.Single(result.Warehouses).Id);
        var visible=Assert.Single(result.Members);Assert.Equal(member.ID,visible.Id);Assert.Equal("Member EN",visible.NameEn);
        Assert.Equal("OWN",Assert.Single(result.CostCodes).Code);
        Assert.Null(result.Settings); // Reading a workspace must not seed a company choice.
    }

    [Fact]
    public async Task A_foreign_or_missing_project_is_refused_even_if_the_authority_double_grants()
    {
        using var host=new PlatformTestHost(1);
        var foreign=new Project{CompanyID=2,Code="FOREIGN",Name="Foreign project"};host.Seed.Add(foreign);await host.Seed.SaveChangesAsync();
        var service=new ConstructionStructureService(host.Db,new Access(true),new Warehouses(new()),new ConstructionAuditService(host.Db));
        Assert.Null(await service.GetAsync(Actor(),foreign.ID));
        Assert.Null(await service.GetAsync(Actor(),int.MaxValue));
    }

    [Fact]
    public async Task Permission_refusal_returns_no_workspace_data()
    {
        using var host=new PlatformTestHost(1);
        var own=new Project{CompanyID=1,Code="OWN",Name="Own project"};host.Seed.Add(own);await host.Seed.SaveChangesAsync();
        var service=new ConstructionStructureService(host.Db,new Access(false),new Warehouses(new()),new ConstructionAuditService(host.Db));
        Assert.Null(await service.GetAsync(Actor(),own.ID));
    }

    private sealed class Warehouses(HashSet<int> allowed):IConstructionWarehouseAccess
    {public Task<bool> CanBindAsync(BusinessContext context,int warehouseId,CancellationToken ct=default)=>Task.FromResult(allowed.Contains(warehouseId));}
    private sealed class Access(bool allowed):IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext context,string action,PermissionTarget? target=null,CancellationToken cancellationToken=default)=>Task.FromResult(allowed);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context,string action,CancellationToken cancellationToken=default)=>Task.FromResult(allowed?AccessScope.Company(context.CompanyId):AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
}
