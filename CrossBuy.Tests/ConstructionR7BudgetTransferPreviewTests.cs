using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7BudgetTransferPreviewTests
{
 [Theory][InlineData(0,1.234,true)][InlineData(100,750,true)][InlineData(100,751,false)][InlineData(-500,1000,true)][InlineData(0,0,false)][InlineData(0,1.2345,false)]
 public void Exact_conservative_balancing_never_creates_extra_budget(int actual,double amount,bool ok)
 {
  var result=ConstructionBudgetTransferRules.Evaluate(1000m,50m,actual,actual==0||actual<0?0:150m,(decimal)amount,3);Assert.Equal(ok,result.Ok);
  if(ok){Assert.Equal(1050m,result.FromAfter+result.ToAfter);Assert.True(result.FromAfter>=0);}
 }
 internal static async Task<(int Project,int From,int To)> Seed(CrossDbContext db)
 {
  var s=await ConstructionR7OrderAllocationTests.Seed(db);
  db.RemoveRange(await db.PurchaseOrderLines.ToListAsync());db.RemoveRange(await db.PurchaseOrders.ToListAsync());await db.SaveChangesAsync();
  var code=new ConstructionCostCode{CompanyID=1,Code="LAB",Name="عمل",NameEn="Labor",Category="Labor",IsActive=true,AllowDirectPosting=true};db.Add(code);await db.SaveChangesAsync();
  var budget=await db.Set<ConstructionBudgetVersion>().SingleAsync();var target=new ConstructionBudgetLine{CompanyID=1,ProjectId=s.Project,BudgetVersionId=budget.ID,WbsNodeId=s.Node,CostCodeId=code.ID,CostCategory="Labor",Amount=50,ChangeReason="Target estimate"};
  db.AddRange(target,new ConstructionCompanyPolicy{CompanyID=1,PolicyKey="BudgetTransfer",Mode="IndependentApproval",Reason="Explicit fixture",UpdatedBy=7,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
  return(s.Project,(await db.Set<ConstructionBudgetLine>().SingleAsync(x=>x.CostCodeId==s.Code)).ID,target.ID);
 }
 internal static ConstructionBudgetTransferPreview Service(CrossDbContext db,bool allowed=true,int company=1)=>new(db,new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7OrderAllocationTests.Actor(company)},new ConstructionR7ActualCostTests.Access(allowed),new ConstructionAuditService(db));
 [Theory][InlineData("ready")][InlineData("missing-policy")][InlineData("disabled")][InlineData("foreign")][InlineData("denied")][InlineData("quantified")][InlineData("precision")][InlineData("too-much")][InlineData("inactive")][InlineData("foreign-line")]
 public async Task Preview_uses_company_policy_and_persisted_baseline_without_mutation(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);
  if(mode=="missing-policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteDeleteAsync();
  if(mode=="disabled")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
  if(mode=="quantified")await f.Db.Set<ConstructionBudgetLine>().Where(x=>x.ID==s.From).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Quantity,1m).SetProperty(p=>p.Rate,1000m));
  if(mode=="inactive")await f.Db.Set<WbsNode>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Archived"));
  await using var db=f.NewContext();var service=Service(db,mode!="denied",mode=="foreign"?2:1);
  var result=await service.PreviewAsync(s.Project,mode=="foreign-line"?s.From+9999:s.From,s.To,mode=="precision"?1.2345m:mode=="too-much"?1001m:1.234m);
  Assert.Equal(mode=="ready",result.Ok);if(result.Ok){Assert.Equal(998.766m,result.FromAfter);Assert.Equal(51.234m,result.ToAfter);Assert.Equal(64,result.Witness!.Length);Assert.Equal(result.Witness,(await service.PreviewAsync(s.Project,s.From,s.To,1.234m)).Witness);}
  await using var verify=f.NewContext();Assert.Single(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync());Assert.Equal(1050m,(await verify.Set<ConstructionBudgetLine>().ToArrayAsync()).Sum(x=>x.Amount));Assert.Empty(await verify.ConstructionAuditEntries.ToListAsync());Assert.Empty(await verify.JournalEntries.ToListAsync());Assert.Empty(await verify.StockMovements.ToListAsync());
 }
 [Fact]
 public void Preview_boundary_has_no_mutation_command_and_language_keys_match()
 {
  Assert.Equal(new[]{"PreviewAsync","ReadAsync"},typeof(IConstructionBudgetTransferPreview).GetMethods().Select(x=>x.Name).OrderBy(x=>x));
  var controller=typeof(CrossBuy.Controllers.ConstructionBudgetTransferController);var method=controller.GetMethod("Preview")!;
  Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute>(method));Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<CrossBuy.Models.ProjectPermAttribute>(method));
  DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);string[]? previous=null;
  foreach(var suffix in new[]{"",".en",".ar",".fr"}){var keys=System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName,"CrossBuy/Resources/SharedResources"+suffix+".resx")).Root!.Elements("data").Where(x=>((string)x.Attribute("name")!).StartsWith("Construction.TransferUi.")).Select(x=>(string)x.Attribute("name")!).OrderBy(x=>x).ToArray();Assert.True(keys.Length>=22);Assert.Equal(keys.Length,keys.Distinct().Count());if(previous!=null)Assert.Equal(previous,keys);previous=keys;}
 }
}
