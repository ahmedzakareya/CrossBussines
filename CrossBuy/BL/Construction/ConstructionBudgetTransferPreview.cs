using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionTransferMath(bool Ok,string Code,decimal? FromAfter=null,decimal? ToAfter=null);
public static class ConstructionBudgetTransferRules
{
 private const decimal Limit=100000000000000000000m;
 public static ConstructionTransferMath Evaluate(decimal from,decimal to,decimal actual,decimal commitment,decimal amount,int decimals)
 {
  if(decimals is <0 or >8)return new(false,"InvalidCurrencyPrecision");
  if(from<0||to<0||commitment<0||from>=Limit||to>=Limit||amount<=0||amount>=Limit)return new(false,"InvalidTransfer");
  if(new[]{from,to,amount}.Any(x=>decimal.Round(x,decimals,MidpointRounding.AwayFromZero)!=x))return new(false,"InvalidCurrencyPrecision");
  try{
   var after=checked(from-amount);var recipient=checked(to+amount);var encumbered=checked(actual+commitment);
   if(after<0||after<Math.Max(0m,encumbered))return new(false,"InsufficientUncommittedBudget");
   if(checked(from+to)>=Limit||recipient>=Limit||checked(after+recipient)!=checked(from+to))return new(false,"AmountOverflow");
   return new(true,"Ready",after,recipient);
  }catch(OverflowException){return new(false,"AmountOverflow");}
 }
}
public sealed record ConstructionTransferChoice(int Id,string WbsCode,string WbsName,string? WbsNameEn,string CostCode,string CostName,string? CostNameEn,decimal Amount);
public sealed record ConstructionTransferQuote(bool Ok,string Code,int? BudgetVersionId=null,string? PolicyToken=null,string? Witness=null,
 decimal? FromBefore=null,decimal? ToBefore=null,decimal? FromAfter=null,decimal? ToAfter=null,decimal? Available=null,string? Currency=null);
public sealed record ConstructionTransferPage(int ProjectId,string Code,string Name,string? NameEn,int? BudgetVersionId,string? PolicyMode,string? PolicyToken,
 IReadOnlyList<ConstructionTransferChoice> Lines,int FromLineId=0,int ToLineId=0,decimal? Amount=null,ConstructionTransferQuote? Quote=null,string? Error=null)
{
 public IReadOnlyList<ConstructionBudgetTransferRequest> Requests {get;init;}=Array.Empty<ConstructionBudgetTransferRequest>();
 public bool RequestSaved {get;init;}
 public bool DecisionSaved {get;init;}
 public bool ApplicationSaved {get;init;}
 public IReadOnlyList<ConstructionBudgetTransferApplication> Applications {get;init;}=Array.Empty<ConstructionBudgetTransferApplication>();
 public int ViewerEmployeeId {get;init;}
 public IReadOnlyList<ConstructionBudgetTransferDecision> Decisions {get;init;}=Array.Empty<ConstructionBudgetTransferDecision>();
}
public interface IConstructionBudgetTransferPreview
{
 Task<ConstructionTransferPage?> ReadAsync(int project,CancellationToken ct=default);
 Task<ConstructionTransferQuote> PreviewAsync(int project,int from,int to,decimal amount,CancellationToken ct=default);
}
// Feasibility only. No transfer request, independent approval, baseline rewrite or financial posting.
public sealed class ConstructionBudgetTransferPreview(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService access,IConstructionAuditService audit):IConstructionBudgetTransferPreview
{
 private async Task<BusinessContext?> Actor(int project,CancellationToken ct)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  return c is {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}&&db.CompanyScope.FilterCompanyId==c.CompanyId&&!db.CompanyScope.AllowsCrossCompany&&
   await access.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct)&&await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==project&&x.CompanyID==c.CompanyId,ct)?c:null;
 }
 public async Task<ConstructionTransferPage?> ReadAsync(int project,CancellationToken ct=default)
 {
  var c=await Actor(project,ct);if(c==null)return null;
  var p=await db.Projects.AsNoTracking().SingleAsync(x=>x.CompanyID==c.CompanyId&&x.ID==project,ct);
  var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="BudgetTransfer",ct);
  var baseline=await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.IsBaseline&&x.Status=="Approved",ct);
  var id=baseline?.ID??0;
  var lines=await (from l in db.Set<ConstructionBudgetLine>().AsNoTracking()
   join n in db.Set<WbsNode>().AsNoTracking() on l.WbsNodeId equals n.ID
   join code in db.Set<ConstructionCostCode>().AsNoTracking() on l.CostCodeId equals code.ID
   where l.CompanyID==c.CompanyId&&l.ProjectId==project&&l.BudgetVersionId==id&&n.CompanyID==c.CompanyId&&n.ProjectId==project&&code.CompanyID==c.CompanyId
   orderby n.Code,code.Code
   select new ConstructionTransferChoice(l.ID,n.Code,n.Name,n.NameEn,code.Code,code.Name,code.NameEn,l.Amount)).ToListAsync(ct);
  var requests=await db.Set<ConstructionBudgetTransferRequest>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project).OrderByDescending(x=>x.ID).Take(50).ToListAsync(ct);
  var requestIds=requests.Select(x=>x.ID).ToArray();
  var decisions=await db.Set<ConstructionBudgetTransferDecision>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&requestIds.Contains(x.TransferRequestId)).ToListAsync(ct);
  var applications=await db.Set<ConstructionBudgetTransferApplication>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&requestIds.Contains(x.TransferRequestId)).ToListAsync(ct);
  return new(project,p.Code,p.Name,p.NameEn,baseline?.ID,policy?.Mode,policy==null?null:Convert.ToBase64String(policy.ConcurrencyToken),lines)
  {Requests=requests,Decisions=decisions,Applications=applications,ViewerEmployeeId=c.EmployeeId!.Value};
 }
 public async Task<ConstructionTransferQuote> PreviewAsync(int project,int from,int to,decimal amount,CancellationToken ct=default)
 {
  var c=await Actor(project,ct);if(c==null)return new(false,"AccessDenied");
  if(from<=0||to<=0||from==to||amount<=0)return new(false,"InvalidTransfer");
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return new(false,"CleanUnitOfWorkRequired");
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project,ct))return new(false,"ConcurrencyConflict");
   var quote=await QuoteLockedAsync(c,project,from,to,amount,ct);
   await tx.CommitAsync(ct);return quote;
  }catch(OverflowException){return new(false,"AmountOverflow");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){return new(false,"ConcurrencyConflict");}
 }
 // Caller owns the serializable transaction and shared project lock. The public command
 // boundary revalidates actor/project access before entering this internal read operation.
 internal async Task<ConstructionTransferQuote> QuoteLockedAsync(BusinessContext c,int project,int from,int to,decimal amount,CancellationToken ct)
 {
   if(from<=0||to<=0||from==to||amount<=0)return new(false,"InvalidTransfer");
   amount=decimal.Parse(amount.ToString("0.############################",System.Globalization.CultureInfo.InvariantCulture),System.Globalization.CultureInfo.InvariantCulture);
   if(db.Database.CurrentTransaction==null||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany)
    return new(false,"CleanUnitOfWorkRequired");
   var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="BudgetTransfer",ct);
   if(policy?.Mode=="Disabled")return new(false,"TransferDisabled");
   if(policy?.Mode!="IndependentApproval")return new(false,"TransferPolicyNotConfigured");
   var baseline=await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.IsBaseline&&x.Status=="Approved",ct);
   if(baseline==null)return new(false,"ApprovedBaselineRequired");
   var lines=await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.BudgetVersionId==baseline.ID).ToListAsync(ct);
   var debit=lines.SingleOrDefault(x=>x.ID==from);var credit=lines.SingleOrDefault(x=>x.ID==to);if(debit==null||credit==null)return new(false,"InvalidTransfer");
   var nodeIds=new[]{debit.WbsNodeId,credit.WbsNodeId}.Distinct().ToArray();var codeIds=new[]{debit.CostCodeId,credit.CostCodeId}.Distinct().ToArray();
   if(await db.Set<WbsNode>().AsNoTracking().CountAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&nodeIds.Contains(x.ID)&&x.OwnsBudget&&x.Status=="Active",ct)!=nodeIds.Length||
    await db.Set<ConstructionCostCode>().AsNoTracking().CountAsync(x=>x.CompanyID==c.CompanyId&&codeIds.Contains(x.ID)&&x.IsActive,ct)!=codeIds.Length)return new(false,"BudgetDimensionCoverageRequired");
   if(lines.Sum(x=>x.Amount)>=100000000000000000000m)return new(false,"AmountOverflow");
   if(debit.Quantity!=null||debit.Rate!=null||credit.Quantity!=null||credit.Rate!=null)return new(false,"QuantifiedTransferNotSupported");
   var decimals=await db.Currencies.AsNoTracking().Where(x=>x.ID==baseline.CurrencyId).Select(x=>(int?)x.DecimalPlaces).SingleOrDefaultAsync(ct);if(decimals==null)return new(false,"InvalidCurrencyPrecision");
   var exposure=await new ConstructionBudgetExposureService(db,access,audit).ReadLocked(c,project,baseline,null,ct);
   if(!exposure.Ok||exposure.Rows==null)return new(false,exposure.Code);
   var used=exposure.Rows.SingleOrDefault(x=>x.WbsNodeId==debit.WbsNodeId&&x.CostCodeId==debit.CostCodeId);
   if(used==null||used.Budget!=debit.Amount)return new(false,"BudgetDimensionCoverageRequired");
   var check=ConstructionBudgetTransferRules.Evaluate(debit.Amount,credit.Amount,used.Actual,used.Commitment,amount,decimals.Value);if(!check.Ok)return new(false,check.Code);
   var witness=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Company=c.CompanyId,Project=project,Baseline=baseline.ID,baseline.RowVersion,Policy=policy.ConcurrencyToken,Exposure=exposure.Version,From=from,To=to,Amount=amount})));
   return new(true,"Ready",baseline.ID,Convert.ToBase64String(policy.ConcurrencyToken),witness,debit.Amount,credit.Amount,check.FromAfter,check.ToAfter,Math.Max(0m,debit.Amount-Math.Max(0m,used.Actual+used.Commitment)),exposure.CurrencyCode);
 }
}
