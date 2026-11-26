using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7CompanyPolicyTests
{
 internal sealed class CompanyAdminAccess : CrossBuy.BL.IProjectsAccessService
 {
  public Task<bool> CanAsync(CrossBuy.Models.Platform.BusinessContext c,string action,CrossBuy.Models.Platform.PermissionTarget? target=null,CancellationToken cancellationToken=default)=>Task.FromResult(action==CrossBuy.BL.ProjectsActions.Manage&&target==null);
  public Task<CrossBuy.Models.Platform.AccessScope> ResolveProjectScopeAsync(CrossBuy.Models.Platform.BusinessContext c,string action,CancellationToken cancellationToken=default)=>Task.FromResult(CrossBuy.Models.Platform.AccessScope.None());
  public Task<IReadOnlySet<int>> MemberProjectIdsAsync(CrossBuy.Models.Platform.BusinessContext c,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
 }

 [Theory][InlineData("BudgetTransfer","IndependentApproval")][InlineData("CrossSiteWarehouse","ApprovedException")][InlineData("CostForecast","ManualAuthorOwned")]
 public async Task Choices_are_explicit_scoped_audited_and_stale_writes_refused(string key,string mode)
 {
  using var f=new ConstructionTestFixture();var actors=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7OrderAllocationTests.Actor()};var access=new CompanyAdminAccess();
  await using var db=f.NewContext();var service=new ConstructionCompanyPolicyWorkspace(db,actors,access,new ConstructionAuditService(db));
  Assert.Empty((await service.ReadAsync())!.Policies);Assert.Equal("InvalidPolicy",await service.SaveAsync(key,"",null,"explicit"));Assert.Empty((await service.ReadAsync())!.Policies);
  Assert.Null(await service.SaveAsync(key,mode,null,"Explicit company decision"));var policy=Assert.Single((await service.ReadAsync())!.Policies);Assert.Equal(mode,policy.Mode);
  Assert.Equal("ConcurrencyConflict",await service.SaveAsync(key,mode,null,"Stale missing token"));
  var token=Convert.ToBase64String(policy.ConcurrencyToken);Assert.Null(await service.SaveAsync(key,mode,token,"Reviewed again"));Assert.Equal("ConcurrencyConflict",await service.SaveAsync(key,mode,token,"Stale token"));
  await using var verify=f.NewContext();Assert.Single(await verify.Set<ConstructionCompanyPolicy>().AsNoTracking().ToArrayAsync());Assert.Equal(2,await verify.Set<ConstructionAuditEntry>().CountAsync(x=>x.EntityType==nameof(ConstructionCompanyPolicy)));Assert.Empty(await verify.JournalEntries.ToListAsync());Assert.Empty(await verify.StockMovements.ToListAsync());
  var denied=new ConstructionCompanyPolicyWorkspace(verify,actors,new ConstructionR7ActualCostTests.Access(true),new ConstructionAuditService(verify));Assert.Null(await denied.ReadAsync());Assert.Equal("AccessDenied",await denied.SaveAsync(key,mode,null,"Denied"));
 }
 [Fact]
 public async Task No_cross_company_read_or_write_even_when_access_double_allows()
 {
  using var f=new ConstructionTestFixture();var actors=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=new(){CompanyId=2,EmployeeId=7,UserId="foreign",Source=CrossBuy.Models.Platform.BusinessContextSource.Test}};
  var service=new ConstructionCompanyPolicyWorkspace(f.Db,actors,new CompanyAdminAccess(),f.Audit);Assert.Null(await service.ReadAsync());Assert.Equal("AccessDenied",await service.SaveAsync("BudgetTransfer","Disabled",null,"foreign"));
 }
 [Fact]
 public void Configuration_boundary_and_resource_pairs_are_complete()
 {
  var controller=typeof(CrossBuy.Controllers.ConstructionCompanyPolicyController);Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<CrossBuy.Models.SessionValidationAttribute>(controller));
  var action=controller.GetMethod("Save")!;Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute>(action));Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<CrossBuy.Models.ProjectPermAttribute>(action));
  DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);string[]? keys=null;
  foreach(var suffix in new[]{"",".en",".ar",".fr"}){var rows=System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName,"CrossBuy/Resources/SharedResources"+suffix+".resx")).Root!.Elements("data").Where(x=>((string)x.Attribute("name")!).StartsWith("Construction.PolicyUi.")).ToArray();var actual=rows.Select(x=>(string)x.Attribute("name")!).OrderBy(x=>x).ToArray();Assert.True(actual.Length>=21);Assert.Equal(actual.Length,actual.Distinct().Count());if(keys!=null)Assert.Equal(keys,actual);keys=actual;Assert.All(rows,x=>Assert.False(string.IsNullOrWhiteSpace(x.Element("value")?.Value)));}
 }
}
