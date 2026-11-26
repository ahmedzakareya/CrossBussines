using System.Reflection;
using System.Xml.Linq;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7WarehouseExceptionUiTests
{
 internal static ConstructionWarehouseExceptionWorkspace Workspace(CrossDbContext db,ConstructionR7WarehouseExceptionRequestTests.Setup s,int actor=7,bool inventory=true,bool project=true,bool source=true,bool destination=true,int company=1)=>new(db,
  ConstructionR7BudgetTransferDecisionTests.Actor(actor,company),new ConstructionR7ActualCostTests.Access(project),new[]{new ConstructionR7PurchaseApprovalTests.InventoryAuthority{Purchase=inventory}},
  new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{source?s.SourceWarehouse:0,destination?s.DestinationWarehouse:0}),new ConstructionAuditService(db));
 internal static ConstructionRebasedWarehouseConsentWorkspace RebasedConsentWorkspace(CrossDbContext db,ConstructionR7PurchaseApprovalTests.ActorAccessor actor)=>new(db,actor,
  new ConstructionR7ActualCostTests.Access(),new[]{new ConstructionR7PurchaseApprovalTests.InventoryAuthority()});
 [Fact]
 public async Task Rebased_consent_panel_hides_self_decision_and_offers_pending_request_to_independent_manager()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7RebasedWarehouseConsentTests.Seed(f.Db);var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();
  var service=new ConstructionRebasedWarehouseConsentService(f.Db,actor,new ConstructionR7ActualCostTests.Access(),new[]{new ConstructionR7PurchaseApprovalTests.InventoryAuthority()},
   new ConstructionR7WarehouseClassificationTests.WarehouseAccess(s.SourceWarehouse,s.DestinationWarehouse),new ConstructionAuditService(f.Db));
  var quote=await service.PreviewAsync(s.Project,s.Order,s.DestinationSite);Assert.True(quote.Ok,quote.Code);
  var request=await service.RequestAsync(s.Project,s.Order,s.DestinationSite,quote.Version!,Guid.NewGuid(),"Screen request");Assert.True(request.Ok,request.Code);
  var workspace=RebasedConsentWorkspace(f.Db,actor);var requester=await workspace.ReadAsync(s.Project,s.Order,Array.Empty<WarehouseSiteOption>(),s.DestinationSite);
  Assert.NotNull(requester);Assert.False(Assert.Single(requester.History).CanDecide);
  actor.Current=ConstructionR7BudgetExceptionTests.Approver;
  var reviewer=await workspace.ReadAsync(s.Project,s.Order,Array.Empty<WarehouseSiteOption>(),s.DestinationSite);
  Assert.True(Assert.Single(reviewer!.History).CanDecide);
 }
 [Fact]
 public async Task Own_project_sites_and_actor_bound_request_are_read_only_and_exclude_other_project_site()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionRequestTests.Seed(f.Db);var before=await f.Db.ConstructionAuditEntries.CountAsync();var page=await Workspace(f.Db,s).ReadAsync(s.Project,s.Order,s.DestinationSite);Assert.NotNull(page);Assert.True(page.CanCreate);Assert.Single(page.Sites);Assert.Equal(s.DestinationSite,page.Sites[0].Id);Assert.DoesNotContain(page.Sites,x=>x.Id==s.OriginSite);Assert.Empty(page.History);Assert.True(page.RequiresBinding);
  var repeated=await Workspace(f.Db,s).ReadAsync(s.Project,s.Order,s.DestinationSite);Assert.Equal(page.Witness,repeated!.Witness);
  await using var verify=f.NewContext();Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());
 }
 [Theory][InlineData("project")][InlineData("inventory")][InlineData("company")][InlineData("source")][InlineData("destination")]
 public async Task Denied_scope_or_warehouse_grants_do_not_produce_actionable_options(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionRequestTests.Seed(f.Db);var page=await Workspace(f.Db,s,inventory:mode!="inventory",project:mode!="project",source:mode!="source",destination:mode!="destination",company:mode=="company"?2:1).ReadAsync(s.Project,s.Order,s.DestinationSite);
  if(mode is "project" or "inventory" or "company")Assert.Null(page);else{Assert.NotNull(page);Assert.False(page.CanCreate);Assert.Empty(page.Sites);Assert.Empty(page.ReadyDecisions);}
 }
 [Fact]
 public async Task Independent_controls_and_policy_restriction_are_derived_from_fresh_facts()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionRequestTests.Seed(f.Db);var service=ConstructionR7WarehouseExceptionRequestTests.Service(f.Db,s);var quote=await service.PreviewAsync(s.Order,s.DestinationSite);Assert.True((await service.RequestAsync(s.Order,s.DestinationSite,quote.Version!,Guid.NewGuid(),"Visible request")).Ok);
  var self=await Workspace(f.Db,s).ReadAsync(s.Project,s.Order,s.DestinationSite);Assert.False(self!.CanCreate);Assert.False(Assert.Single(self.History).CanApprove);Assert.False(Assert.Single(self.History).CanReject);
  var independent=await Workspace(f.Db,s,actor:8).ReadAsync(s.Project,s.Order);Assert.True(Assert.Single(independent!.History).CanApprove);Assert.True(Assert.Single(independent.History).CanReject);
  await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));var stale=await Workspace(f.Db,s,actor:8).ReadAsync(s.Project,s.Order);Assert.False(Assert.Single(stale!.History).CanApprove);Assert.True(Assert.Single(stale.History).CanReject);
 }
 [Fact]
 public async Task Bound_pending_consent_is_not_offered_for_a_second_purchase_request()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionConsumptionTests.Seed(f.Db);var page=await Workspace(f.Db,s.Scope).ReadAsync(s.Scope.Project,s.Scope.Order);Assert.True(page!.RequiresBinding);Assert.Empty(page.ReadyDecisions);Assert.Equal("Approved",Assert.Single(page.History).Status);
 }
 [Fact]
 public async Task Controller_refuses_wrong_parent_and_preserves_stale_command_without_fresh_resubmission()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionRequestTests.Seed(f.Db);var workspace=Workspace(f.Db,s);var page=(await workspace.ReadAsync(s.Project,s.Order,s.DestinationSite))!;var controller=ConstructionR7PurchaseRequestUiTests.Controller(f.Db);var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();var purchases=ConstructionR7PurchaseApprovalTests.Service(f.Db,actor);var requests=ConstructionR7WarehouseExceptionRequestTests.Service(f.Db,s);var key=Guid.NewGuid();
  Assert.IsType<ForbidResult>(await controller.RequestConstructionWarehouseException(s.OtherProject,s.Order,s.DestinationSite,page.Witness!,key,"Wrong parent",requests,workspace,purchases));
  await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Stale after review"));
  Assert.IsType<ViewResult>(await controller.RequestConstructionWarehouseException(s.Project,s.Order,s.DestinationSite,page.Witness!,key,"Retain my reason",requests,workspace,purchases));
  var retained=Assert.IsType<ConstructionWarehouseExceptionPage>(controller.ViewBag.WarehouseException);Assert.Equal(key,retained.RequestId);Assert.Equal(page.Witness,retained.Witness);Assert.Equal("Retain my reason",retained.Reason);Assert.False(retained.CanCreate);Assert.True(retained.Refusal);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());
 }
 [Fact]
 public void Mutation_endpoints_require_post_antiforgery_and_inventory_permission()
 {
  foreach(var name in new[]{nameof(InventoryController.RequestConstructionWarehouseException),nameof(InventoryController.DecideConstructionWarehouseException),nameof(InventoryController.RequestConstructionRebasedWarehouseConsent),nameof(InventoryController.DecideConstructionRebasedWarehouseConsent)}){
   var method=typeof(InventoryController).GetMethod(name)!;Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());Assert.NotNull(method.GetCustomAttribute<InvPermAttribute>());
  }
 }
 [Fact]
 public void All_warehouse_labels_have_arabic_english_and_neutral_resources()
 {
  var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);
  var partial=File.ReadAllText(Path.Combine(root!.FullName,"CrossBuy/Views/Inventory/_ConstructionWarehouseException.cshtml"));
  var keys=System.Text.RegularExpressions.Regex.Matches(partial,"Construction\\.Warehouse\\.[A-Za-z.]+").Select(x=>x.Value).Where(x=>!x.EndsWith("State.")).Concat(new[]{"Construction.Warehouse.State.Pending","Construction.Warehouse.State.Approved","Construction.Warehouse.State.Rejected","Construction.Warehouse.SelectDecision","Construction.Warehouse.ChooseDecision","Construction.Warehouse.RequestSaved","Construction.Warehouse.DecisionSaved"}).Distinct().ToArray();
  foreach(var file in new[]{"SharedResources.ar.resx","SharedResources.en.resx","SharedResources.fr.resx","SharedResources.resx"}){
   var resources=XDocument.Load(Path.Combine(root.FullName,"CrossBuy/Resources",file));foreach(var key in keys){var entry=Assert.Single(resources.Root!.Elements("data"),x=>(string?)x.Attribute("name")==key);Assert.False(string.IsNullOrWhiteSpace(entry.Element("value")!.Value));}
  }
  var consentPartial=File.ReadAllText(Path.Combine(root.FullName,"CrossBuy/Views/Inventory/_ConstructionRebasedWarehouseConsent.cshtml"));
  var consentKeys=System.Text.RegularExpressions.Regex.Matches(consentPartial,"Construction\\.RebasedWarehouseConsent\\.[A-Za-z.]+[A-Za-z]").Select(x=>x.Value).Where(x=>!x.EndsWith(".State")).Concat(new[]{"Construction.RebasedWarehouseConsent.State.Pending","Construction.RebasedWarehouseConsent.State.Approved","Construction.RebasedWarehouseConsent.State.Rejected"}).Distinct().ToArray();
  foreach(var file in new[]{"SharedResources.ar.resx","SharedResources.en.resx","SharedResources.fr.resx","SharedResources.resx"}){
   var resources=XDocument.Load(Path.Combine(root.FullName,"CrossBuy/Resources",file));foreach(var key in consentKeys){var entry=Assert.Single(resources.Root!.Elements("data"),x=>(string?)x.Attribute("name")==key);Assert.False(string.IsNullOrWhiteSpace(entry.Element("value")!.Value));}
  }
 }
}
