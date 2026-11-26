using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionForecastEstimate(int WbsNodeId,int CostCodeId,decimal RemainingCostIncludingCommitments);
public sealed record ConstructionForecastPreview(bool Ok,string Code,string? Witness=null,int? BudgetVersionId=null,
 int? CurrencyId=null,int? CurrencyDecimals=null,ConstructionForecastCalculation? Calculation=null);
public sealed record ConstructionForecastSave(bool Ok,string Code,int? Id=null);
public interface IConstructionCostForecastService
{
 Task<ConstructionForecastPreview> PreviewAsync(int project,IReadOnlyList<ConstructionForecastEstimate> estimates,CancellationToken ct=default);
 Task<ConstructionForecastSave> SaveAsync(int project,IReadOnlyList<ConstructionForecastEstimate> estimates,string witness,Guid requestId,int? previousSnapshotId,string reason,CancellationToken ct=default);
 Task<IReadOnlyList<ConstructionCostForecastSnapshot>?> HistoryAsync(int project,CancellationToken ct=default);
}
public sealed class ConstructionCostForecastService(CrossDbContext db,IBusinessContextAccessor contexts,
 IProjectsAccessService access,IConstructionAuditService audit):IConstructionCostForecastService
{
 private async Task<BusinessContext?> Actor(int project,string permission,CancellationToken ct)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  return c is {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}&&
   db.CompanyScope.FilterCompanyId==c.CompanyId&&!db.CompanyScope.AllowsCrossCompany&&
   await access.CanAsync(c,permission,new PermissionTarget{ProjectId=project},ct)&&
   await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==project&&x.CompanyID==c.CompanyId,ct)?c:null;
 }
 private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
 private static object[] Canonical(IReadOnlyList<ConstructionForecastEstimate> estimates)=>estimates.OrderBy(x=>x.WbsNodeId).ThenBy(x=>x.CostCodeId)
  .Select(x=>(object)new{x.WbsNodeId,x.CostCodeId,Remaining=x.RemainingCostIncludingCommitments.ToString("G29",CultureInfo.InvariantCulture)}).ToArray();
 private static bool Valid(IReadOnlyList<ConstructionForecastEstimate>? estimates)=>estimates is {Count:>0 and <=2000}&&estimates.All(x=>x!=null)&&
  estimates.Select(x=>(x.WbsNodeId,x.CostCodeId)).Distinct().Count()==estimates.Count;
 public async Task<IReadOnlyList<ConstructionCostForecastSnapshot>?> HistoryAsync(int project,CancellationToken ct=default)
 {
  var c=await Actor(project,ProjectsActions.BudgetView,ct);if(c==null)return null;
  return await db.Set<ConstructionCostForecastSnapshot>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project).OrderByDescending(x=>x.ID).Take(50).ToListAsync(ct);
 }
 public async Task<ConstructionForecastPreview> PreviewAsync(int project,IReadOnlyList<ConstructionForecastEstimate> estimates,CancellationToken ct=default)
 {
  var c=await Actor(project,ProjectsActions.BudgetManage,ct);if(c==null)return new(false,"AccessDenied");
  if(!Valid(estimates))return new(false,"ForecastDimensionsRequired");
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return new(false,"CleanUnitOfWorkRequired");
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project,ct))return new(false,"ConcurrencyConflict");
   var quote=await Quote(c,project,estimates,ct);await tx.CommitAsync(ct);return quote;
  }catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){return new(false,"ConcurrencyConflict");}
 }
 private async Task<ConstructionForecastPreview> Quote(BusinessContext c,int project,IReadOnlyList<ConstructionForecastEstimate> estimates,CancellationToken ct)
 {
  var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="CostForecast",ct);
  if(policy?.Mode!="ManualAuthorOwned")return new(false,policy?.Mode=="Disabled"?"ForecastDisabled":"ForecastPolicyNotConfigured");
  var baseline=await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.IsBaseline&&x.Status=="Approved",ct);
  if(baseline==null)return new(false,"ApprovedBaselineRequired");
  var currency=await db.Currencies.AsNoTracking().SingleOrDefaultAsync(x=>x.ID==baseline.CurrencyId,ct);
  if(currency==null||currency.DecimalPlaces is <0 or >8)return new(false,"InvalidCurrencyPrecision");
  var companyCurrency=await db.Companies.AsNoTracking().Where(x=>x.CompanyID==c.CompanyId).Select(x=>x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
  if(companyCurrency!=baseline.CurrencyId)return new(false,"BudgetCurrencyChanged");
  var exposure=await new ConstructionBudgetExposureService(db,access,audit).ReadLocked(c,project,baseline,null,ct);
  if(!exposure.Ok||exposure.Rows==null||string.IsNullOrWhiteSpace(exposure.Version))return new(false,exposure.Code);
  if(exposure.Rows.Count!=estimates.Count||exposure.Rows.Any(x=>!estimates.Any(y=>y.WbsNodeId==x.WbsNodeId&&y.CostCodeId==x.CostCodeId)))return new(false,"ForecastDimensionsRequired");
  var nodes=exposure.Rows.Select(x=>x.WbsNodeId).Distinct().ToArray();var codes=exposure.Rows.Select(x=>x.CostCodeId).Distinct().ToArray();
  var wbs=await db.Set<WbsNode>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&nodes.Contains(x.ID)).OrderBy(x=>x.ID).ToListAsync(ct);
  var costs=await db.Set<ConstructionCostCode>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&codes.Contains(x.ID)).OrderBy(x=>x.ID).ToListAsync(ct);
  if(wbs.Count!=nodes.Length||costs.Count!=codes.Length||wbs.Any(x=>x.Status!="Active"||!x.OwnsBudget)||costs.Any(x=>!x.IsActive||!x.AllowDirectPosting))return new(false,"BudgetDimensionCoverageRequired");
  var inputs=exposure.Rows.OrderBy(x=>x.WbsNodeId).ThenBy(x=>x.CostCodeId).Select(x=>new ConstructionForecastInput(x.WbsNodeId,x.CostCodeId,x.Budget,x.Actual,x.Commitment,
   estimates.Single(y=>y.WbsNodeId==x.WbsNodeId&&y.CostCodeId==x.CostCodeId).RemainingCostIncludingCommitments)).ToArray();
  var calculation=ConstructionCostForecastRules.Evaluate(policy.Mode,ConstructionCostForecastRules.Method,true,currency.DecimalPlaces,inputs);
  if(!calculation.Ok)return new(false,calculation.Code);
  var witness=Hash(new{Company=c.CompanyId,Project=project,Actor=c.EmployeeId,Baseline=baseline.ID,baseline.RowVersion,Policy=policy.ConcurrencyToken,Exposure=exposure.Version,
   Currency=currency.ID,Precision=currency.DecimalPlaces,Method=ConstructionCostForecastRules.Method,Estimates=Canonical(estimates)});
  return new(true,"Ready",witness,baseline.ID,currency.ID,currency.DecimalPlaces,calculation);
 }
 public async Task<ConstructionForecastSave> SaveAsync(int project,IReadOnlyList<ConstructionForecastEstimate> estimates,string witness,Guid requestId,int? previousSnapshotId,string reason,CancellationToken ct=default)
 {
  var c=await Actor(project,ProjectsActions.BudgetManage,ct);if(c==null)return new(false,"AccessDenied");
  if(!Valid(estimates)||requestId==Guid.Empty||string.IsNullOrWhiteSpace(witness)||witness.Length!=64||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>1000||previousSnapshotId is <=0)return new(false,"InvalidForecastRequest");
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return new(false,"CleanUnitOfWorkRequired");
  reason=reason.Trim();var hash=Hash(new{Company=c.CompanyId,Project=project,Actor=c.EmployeeId,Method=ConstructionCostForecastRules.Method,Estimates=Canonical(estimates),Witness=witness,Previous=previousSnapshotId,Reason=reason});
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project,ct))return new(false,"ConcurrencyConflict");
   var prior=await db.Set<ConstructionCostForecastSnapshot>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.RequestId==requestId,ct);
   if(prior!=null)return prior.RequestHash==hash?new(true,"ForecastSaved",prior.ID):new(false,"ForecastRequestIdConflict");
   var latest=await db.Set<ConstructionCostForecastSnapshot>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project).OrderByDescending(x=>x.ID).Select(x=>(int?)x.ID).FirstOrDefaultAsync(ct);
   if(latest!=previousSnapshotId)return new(false,"ConcurrencyConflict");
   var quote=await Quote(c,project,estimates,ct);if(!quote.Ok)return new(false,quote.Code);
   if(quote.Witness!=witness)return new(false,"ConcurrencyConflict");
   var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="CostForecast",ct);
   var snapshot=new ConstructionCostForecastSnapshot{CompanyID=c.CompanyId,ProjectId=project,BudgetVersionId=quote.BudgetVersionId!.Value,CurrencyId=quote.CurrencyId!.Value,
    CurrencyDecimals=quote.CurrencyDecimals!.Value,PreviousSnapshotId=previousSnapshotId,RequestId=requestId,RequestHash=hash,SourceVersion=witness,PolicyToken=policy.ConcurrencyToken.ToArray(),
    Method=ConstructionCostForecastRules.Method,SnapshotJson=JsonSerializer.Serialize(quote.Calculation!.Lines),EstimateAtCompletion=quote.Calculation.EstimateAtCompletion!.Value,
    BudgetVariance=quote.Calculation.BudgetVariance!.Value,HasCommitmentWarning=quote.Calculation.HasCommitmentWarning,Reason=reason,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow};
   db.Add(snapshot);await db.SaveChangesAsync(ct);
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,project,nameof(ConstructionCostForecastSnapshot),snapshot.ID,null,"AuthorCostForecast",null,
    JsonSerializer.Serialize(new{snapshot.RequestId,snapshot.SourceVersion,snapshot.PreviousSnapshotId,snapshot.Method}),ConstructionChangeKind.Created,reason,c.EmployeeId,nameof(ConstructionCostForecastService),requestId)});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(true,"ForecastSaved",snapshot.ID);
  }catch(DbUpdateException){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch{db.ChangeTracker.Clear();throw;}
 }
}
