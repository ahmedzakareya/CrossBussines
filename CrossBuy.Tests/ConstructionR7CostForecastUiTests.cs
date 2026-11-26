using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context.Construction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7CostForecastUiTests
{
 private sealed class ReadAccess:IProjectsAccessService
 {
  public Task<bool> CanAsync(BusinessContext c,string action,PermissionTarget? target=null,CancellationToken cancellationToken=default)=>Task.FromResult(action==ProjectsActions.BudgetView);
  public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c,string action,CancellationToken cancellationToken=default)=>Task.FromResult(AccessScope.None());
  public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
 }
 private sealed class TempProvider:ITempDataProvider
 {
  public IDictionary<string,object> LoadTempData(HttpContext context)=>new Dictionary<string,object>();
  public void SaveTempData(HttpContext context,IDictionary<string,object> values){}
 }
 private static ConstructionCostForecastController Controller(ConstructionTestFixture f)=>new(new ConstructionCostForecastWorkspace(f.Db,ConstructionR7BudgetTransferDecisionTests.Actor(7),new ConstructionR7ActualCostTests.Access()),ConstructionR7CostForecastTests.Service(f.Db))
 {ControllerContext=new(){HttpContext=new DefaultHttpContext()},TempData=new TempDataDictionary(new DefaultHttpContext(),new TempProvider())};
 [Fact]
 public async Task Read_only_viewer_can_read_history_but_cannot_author_and_no_estimate_is_defaulted()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7CostForecastTests.Seed(f.Db);var actor=ConstructionR7BudgetTransferDecisionTests.Actor(7);var workspace=new ConstructionCostForecastWorkspace(f.Db,actor,new ReadAccess());
  var page=await workspace.ReadAsync(s.Project);Assert.NotNull(page);Assert.False(page.CanAuthor);Assert.Empty(page.Estimates);Assert.Null(page.Preview);Assert.Equal(2,page.Choices.Count);
  var denied=new ConstructionCostForecastService(f.Db,actor,new ReadAccess(),new ConstructionAuditService(f.Db));Assert.Equal("AccessDenied",(await denied.PreviewAsync(s.Project,s.Estimates)).Code);
  Assert.Null(await new ConstructionCostForecastWorkspace(f.Db,ConstructionR7BudgetTransferDecisionTests.Actor(7,2),new ConstructionR7ActualCostTests.Access()).ReadAsync(s.Project));
 }
 [Fact]
 public async Task Controller_preview_then_save_creates_one_snapshot_and_redirects_preserving_culture()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7CostForecastTests.Seed(f.Db);var controller=Controller(f);var key=Guid.NewGuid();
  var view=Assert.IsType<ViewResult>(await controller.Preview(s.Project,s.Estimates.ToList(),key));var page=Assert.IsType<ConstructionForecastPage>(view.Model);Assert.True(page.Preview!.Ok);Assert.Equal(key,page.RequestId);Assert.Empty(await f.Db.Set<ConstructionCostForecastSnapshot>().AsNoTracking().ToArrayAsync());
  var redirect=Assert.IsType<RedirectToActionResult>(await controller.Save(s.Project,s.Estimates.ToList(),page.Preview.Witness!,key,null,"Reviewed forecast"));Assert.Equal("Index",redirect.ActionName);Assert.Equal(s.Project,redirect.RouteValues!["projectId"]);Assert.True(redirect.RouteValues.ContainsKey("ui-culture"));
  await using var verify=f.NewContext();Assert.Single(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
 }
 [Fact]
 public async Task Malformed_form_is_refused_without_calculation_or_partial_save()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7CostForecastTests.Seed(f.Db);var controller=Controller(f);controller.ModelState.AddModelError("estimates","Invalid binding");
  var view=Assert.IsType<ViewResult>(await controller.Preview(s.Project,s.Estimates.ToList(),Guid.NewGuid()));var page=Assert.IsType<ConstructionForecastPage>(view.Model);Assert.Null(page.Preview);Assert.Equal("InvalidForecastRequest",page.Error);Assert.Equal(409,controller.Response.StatusCode);
  Assert.IsType<ViewResult>(await controller.Save(s.Project,s.Estimates.ToList(),new string('A',64),Guid.NewGuid(),null,"Invalid form"));
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());
 }
 [Fact]
 public async Task Changed_policy_after_preview_hides_authoring_state_and_save_is_refused()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7CostForecastTests.Seed(f.Db);var controller=Controller(f);var key=Guid.NewGuid();var view=Assert.IsType<ViewResult>(await controller.Preview(s.Project,s.Estimates.ToList(),key));var page=Assert.IsType<ConstructionForecastPage>(view.Model);
  await f.Db.Set<ConstructionCompanyPolicy>().Where(x=>x.PolicyKey=="CostForecast").ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Disabled"));
  var failure=Assert.IsType<ViewResult>(await controller.Save(s.Project,s.Estimates.ToList(),page.Preview!.Witness!,key,null,"Disabled after review"));var current=Assert.IsType<ConstructionForecastPage>(failure.Model);Assert.Equal("Disabled",current.PolicyMode);Assert.Equal("ForecastDisabled",current.Error);Assert.Null(current.Preview);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());
 }
 [Fact]
 public void Write_endpoints_have_forgery_permission_and_payload_guards_and_resource_sets_match()
 {
  var type=typeof(ConstructionCostForecastController);Assert.NotNull(Attribute.GetCustomAttribute(type,typeof(CrossBuy.Models.SessionValidationAttribute)));
  foreach(var name in new[]{"Preview","Save"}){var method=type.GetMethod(name)!;Assert.NotNull(Attribute.GetCustomAttribute(method,typeof(ValidateAntiForgeryTokenAttribute)));Assert.NotNull(Attribute.GetCustomAttribute(method,typeof(RequestSizeLimitAttribute)));Assert.NotNull(Attribute.GetCustomAttribute(method,typeof(CrossBuy.Models.ProjectPermAttribute)));}
  DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);string[]? previous=null;
  foreach(var suffix in new[]{"",".en",".ar",".fr"}){var doc=System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName,"CrossBuy/Resources/SharedResources"+suffix+".resx"));var rows=doc.Root!.Elements("data").Where(x=>((string)x.Attribute("name")!).StartsWith("Construction.ForecastUi.")).ToArray();var keys=rows.Select(x=>(string)x.Attribute("name")!).OrderBy(x=>x).ToArray();Assert.True(keys.Length>=40);Assert.Equal(keys.Length,keys.Distinct().Count());Assert.All(rows,x=>Assert.False(string.IsNullOrWhiteSpace(x.Element("value")!.Value)));if(previous!=null)Assert.Equal(previous,keys);previous=keys;}
 }
}
