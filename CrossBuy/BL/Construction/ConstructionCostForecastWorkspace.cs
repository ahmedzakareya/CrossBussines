using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionForecastChoice(int WbsNodeId,int CostCodeId,string WbsCode,string WbsName,string? WbsNameEn,string CostCode,string CostName,string? CostNameEn,decimal Budget);
public sealed record ConstructionForecastPage(int ProjectId,string Code,string Name,string? NameEn,bool CanAuthor,string? PolicyMode,int? BaselineId,string? CurrencyCode,
 IReadOnlyList<ConstructionForecastChoice> Choices,IReadOnlyList<ConstructionCostForecastSnapshot> History)
{
 public IReadOnlyList<ConstructionForecastEstimate> Estimates {get;init;}=Array.Empty<ConstructionForecastEstimate>();
 public ConstructionForecastPreview? Preview {get;init;}
 public string? Error {get;init;}
 public bool Saved {get;init;}
 public int? PreviousSnapshotId=>History.FirstOrDefault()?.ID;
 public Guid RequestId {get;init;}=Guid.NewGuid();
}
public interface IConstructionCostForecastWorkspace {Task<ConstructionForecastPage?> ReadAsync(int project,CancellationToken ct=default);}
public sealed class ConstructionCostForecastWorkspace(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService access):IConstructionCostForecastWorkspace
{
 public async Task<ConstructionForecastPage?> ReadAsync(int project,CancellationToken ct=default)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  if(c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany||
   !await access.CanAsync(c,ProjectsActions.BudgetView,new PermissionTarget{ProjectId=project},ct))return null;
  var p=await db.Projects.AsNoTracking().SingleOrDefaultAsync(x=>x.ID==project&&x.CompanyID==c.CompanyId,ct);if(p==null)return null;
  var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="CostForecast",ct);
  var baseline=await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.IsBaseline&&x.Status=="Approved",ct);
  var id=baseline?.ID??0;
  var choices=await(from b in db.Set<ConstructionBudgetLine>().AsNoTracking()
   join w in db.Set<WbsNode>().AsNoTracking() on b.WbsNodeId equals w.ID
   join code in db.Set<ConstructionCostCode>().AsNoTracking() on b.CostCodeId equals code.ID
   where b.CompanyID==c.CompanyId&&b.ProjectId==project&&b.BudgetVersionId==id&&w.CompanyID==c.CompanyId&&w.ProjectId==project&&code.CompanyID==c.CompanyId
   orderby w.Code,code.Code
   select new ConstructionForecastChoice(w.ID,code.ID,w.Code,w.Name,w.NameEn,code.Code,code.Name,code.NameEn,b.Amount)).ToListAsync(ct);
  var history=await db.Set<ConstructionCostForecastSnapshot>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project).OrderByDescending(x=>x.ID).Take(50).ToListAsync(ct);
  var currency=baseline==null?null:await db.Currencies.AsNoTracking().Where(x=>x.ID==baseline.CurrencyId).Select(x=>x.Code).SingleOrDefaultAsync(ct);
  return new(project,p.Code,p.Name,p.NameEn,await access.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct),policy?.Mode,baseline?.ID,currency,choices,history);
 }
}
