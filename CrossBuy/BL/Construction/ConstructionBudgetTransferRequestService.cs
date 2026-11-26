using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionTransferRequestResult(bool Ok,string Code,int? Id=null);
public interface IConstructionBudgetTransferRequests
{
 Task<ConstructionTransferRequestResult> RequestAsync(int project,int from,int to,decimal amount,string witness,Guid requestId,string reason,CancellationToken ct=default);
}
public sealed class ConstructionBudgetTransferRequestService(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService access,IConstructionAuditService audit):IConstructionBudgetTransferRequests
{
 public async Task<ConstructionTransferRequestResult> RequestAsync(int project,int from,int to,decimal amount,string witness,Guid requestId,string reason,CancellationToken ct=default)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  if(c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany||
   !await access.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct)||!await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==project&&x.CompanyID==c.CompanyId,ct))return new(false,"AccessDenied");
  if(from<=0||to<=0||from==to||amount<=0||requestId==Guid.Empty||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>1000||witness is not {Length:64}||witness.Any(x=>!Uri.IsHexDigit(x)))return new(false,"InvalidTransferRequest");
  if(db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return new(false,"CleanUnitOfWorkRequired");
  amount=decimal.Parse(amount.ToString("0.############################",System.Globalization.CultureInfo.InvariantCulture),System.Globalization.CultureInfo.InvariantCulture);
  reason=reason.Trim();witness=witness.ToUpperInvariant();
  var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Company=c.CompanyId,Project=project,From=from,To=to,Amount=amount,Witness=witness,Reason=reason,Actor=c.EmployeeId})));
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project,ct))return new(false,"ConcurrencyConflict");
   var prior=await db.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.RequestId==requestId,ct);
   if(prior!=null)return prior.RequestHash==hash?new(true,"RequestSaved",prior.ID):new(false,"RequestIdConflict");
   var quote=await new ConstructionBudgetTransferPreview(db,contexts,access,audit).QuoteLockedAsync(c,project,from,to,amount,ct);
   if(!quote.Ok)return new(false,quote.Code);
   if(quote.Witness!=witness)return new(false,"ConcurrencyConflict");
   var request=new ConstructionBudgetTransferRequest{CompanyID=c.CompanyId,ProjectId=project,BudgetVersionId=quote.BudgetVersionId!.Value,
    FromLineId=from,ToLineId=to,Amount=amount,FromBefore=quote.FromBefore!.Value,ToBefore=quote.ToBefore!.Value,FromAfter=quote.FromAfter!.Value,ToAfter=quote.ToAfter!.Value,
    RequestId=requestId,RequestHash=hash,SourceVersion=witness,PolicyToken=Convert.FromBase64String(quote.PolicyToken!),Reason=reason,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow};
   db.Add(request);await db.SaveChangesAsync(ct);
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,project,nameof(ConstructionBudgetTransferRequest),request.ID,null,"RequestBudgetTransfer",null,
    JsonSerializer.Serialize(new{request.BudgetVersionId,request.FromLineId,request.ToLineId,request.Amount,request.SourceVersion,request.RequestId}),ConstructionChangeKind.Created,reason,c.EmployeeId,nameof(ConstructionBudgetTransferRequestService),requestId)});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(true,"RequestSaved",request.ID);
  }catch(DbUpdateException){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(OverflowException){db.ChangeTracker.Clear();return new(false,"AmountOverflow");}
 }
}
