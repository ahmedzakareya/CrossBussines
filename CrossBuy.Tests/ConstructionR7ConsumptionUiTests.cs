using System.Reflection;
using System.Xml.Linq;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context.Construction;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7ConsumptionUiTests
{
 [Theory]
 [InlineData("ready")][InlineData("denied")][InlineData("foreign-company")][InlineData("invalid")][InlineData("foreign-source")][InlineData("stale")]
 public async Task Workspace_preserves_scope_and_financial_sources(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);
  var issue=await ConstructionR7MaterialConsumptionTests.Issue(f.Db,s,.4m);
  var receipt=await f.Db.GoodsReceiptLines.AsNoTracking().SingleAsync();
  var beforeStock=await f.Db.StockMovements.CountAsync();var beforeGl=await f.Db.JournalEntries.CountAsync();
  await using var db=f.NewContext();
  var actors=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=mode=="foreign-company"?new(){CompanyId=2,EmployeeId=7,UserId="other",Source=CrossBuy.Models.Platform.BusinessContextSource.Test}:ConstructionR7OrderAllocationTests.Actor()};
  var access=new ConstructionR7ActualCostTests.Access(mode!="denied");
  var workspace=new ConstructionConsumptionWorkspace(db,actors,access,new ConstructionMaterialConsumptionService(db,access,new ConstructionAuditService(db)));
  var page=await workspace.ReadAsync(s.Seed.Project);
  if(mode is "denied" or "foreign-company")Assert.Null(page);else {Assert.NotNull(page);Assert.Contains(page.Sources!.Receipts,x=>x.Id==receipt.ID);Assert.Contains(page.Sources.Issues,x=>x.Id==issue);}
  var input=new ConstructionConsumptionInput{ReceiptLineId=mode=="foreign-source"?receipt.ID+9999:receipt.ID,IssueMovementId=issue,RequestId=Guid.NewGuid(),Reason=mode=="invalid"?"":"Explicit receipt"};
  var preview=await workspace.PreviewAsync(s.Seed.Project,input);
  Assert.Equal(mode is "ready" or "stale",preview.Ok);
  input.Version=mode=="stale"?new string('A',64):preview.Version;
  var written=await workspace.RegisterAsync(s.Seed.Project,input);Assert.Equal(mode=="ready",written.Ok);
  if(written.Ok){Assert.Equal("AlreadyRegistered",(await workspace.RegisterAsync(s.Seed.Project,input)).Code);var after=await workspace.ReadAsync(s.Seed.Project);Assert.Single(after!.History);Assert.DoesNotContain(after.Sources!.Issues,x=>x.Id==issue);}
  await using var verify=f.NewContext();Assert.Equal(mode=="ready"?1:0,await verify.Set<ConstructionMaterialConsumption>().AsNoTracking().CountAsync());
  Assert.Equal(beforeStock,await verify.StockMovements.CountAsync());Assert.Equal(beforeGl,await verify.JournalEntries.CountAsync());
 }
 [Fact]
 public async Task Sources_do_not_leak_a_receipt_or_issue_after_its_project_changes()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7StockInvoiceTests.Arranged(f.Db);var issue=await ConstructionR7MaterialConsumptionTests.Issue(f.Db,s,.4m);
  await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ProjectId,(int?)null));
  await f.Db.JournalEntryLines.Where(x=>x.JournalEntryId==f.Db.StockMovements.Where(m=>m.ID==issue).Select(m=>m.JournalEntryId).First()).ExecuteUpdateAsync(x=>x.SetProperty(l=>l.ProjectId,(int?)null));
  await using var db=f.NewContext();var access=new ConstructionR7ActualCostTests.Access(true);var actors=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7OrderAllocationTests.Actor()};
  var workspace=new ConstructionConsumptionWorkspace(db,actors,access,ConstructionR7MaterialConsumptionTests.Service(db));var page=await workspace.ReadAsync(s.Seed.Project);
  Assert.NotNull(page);Assert.Empty(page.Sources!.Receipts);Assert.Empty(page.Sources.Issues);
 }
 [Fact]
 public void Browser_boundary_and_localization_are_complete()
 {
  Assert.NotNull(typeof(ConstructionConsumptionController).GetCustomAttribute<SessionValidationAttribute>());
  foreach(var name in new[]{"Preview","Register"}){var method=typeof(ConstructionConsumptionController).GetMethod(name)!;Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());Assert.NotNull(method.GetCustomAttribute<ProjectPermAttribute>());Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());}
  DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);string[]? previous=null;
  foreach(var suffix in new[]{"",".en",".ar",".fr"}){var rows=XDocument.Load(Path.Combine(root!.FullName,"CrossBuy/Resources/SharedResources"+suffix+".resx")).Root!.Elements("data").Where(x=>((string)x.Attribute("name")!).StartsWith("Construction.ConsumptionUi.")).ToArray();var keys=rows.Select(x=>(string)x.Attribute("name")!).OrderBy(x=>x).ToArray();Assert.True(keys.Length>20);Assert.Equal(keys.Length,keys.Distinct().Count());if(previous!=null)Assert.Equal(previous,keys);previous=keys;Assert.All(rows,x=>Assert.False(string.IsNullOrWhiteSpace(x.Element("value")?.Value)));}
 }
}
