using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;

public interface IConstructionBudgetTransferApplications
{
 Task<ConstructionTransferRequestResult> ApplyAsync(int project,int transferDecisionId,Guid applicationId,string reason,CancellationToken ct=default);
}
public sealed class ConstructionBudgetTransferApplicationService(CrossDbContext db,IBusinessContextAccessor contexts,
 IProjectsAccessService access,IConstructionAuditService audit,ICurrencyRounding rounding):IConstructionBudgetTransferApplications
{
 private void Rotate(ConstructionBudgetVersion version)
 {
  if(!db.Database.IsSqlServer())version.RowVersion=Guid.NewGuid().ToByteArray();
 }
 // ONE definition of the request hash. The losing-racer recovery below must compute a byte-identical
 // hash to the one the winner stored, so the formula cannot be allowed to exist twice.
 private static string RequestHashOf(int company,int project,int decision,int request,string sourceVersion,int? actor,string reason)
  =>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
    new{Company=company,Project=project,Decision=decision,Request=request,SourceVersion=sourceVersion,Actor=actor,Reason=reason})));

 // A racer that cannot take the project lock, or that loses on deadlock/lock-timeout, used to return
 // ConcurrencyConflict without ever reaching the idempotency re-read below - so a duplicate submission
 // was refused instead of being handed the application the winner had already committed.
 // This runs ONLY after the caller has rolled back: with no ambient transaction a plain READ COMMITTED
 // read cannot see uncommitted work, so a row returned here PROVES the winner committed. Returns null
 // when nothing is proven, leaving the caller's original refusal in place.
 private async Task<ConstructionTransferRequestResult?> ResolveCommittedApplicationAsync(
  BusinessContext c,int project,int transferDecisionId,Guid applicationId,string reason,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction!=null)return null;
  db.ChangeTracker.Clear();
  var prior=await db.Set<ConstructionBudgetTransferApplication>().AsNoTracking()
   .SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ApplicationId==applicationId,ct);
  if(prior==null)return null;
  var decision=await db.Set<ConstructionBudgetTransferDecision>().AsNoTracking()
   .SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==transferDecisionId,ct);
  if(decision==null)return null;
  var request=await db.Set<ConstructionBudgetTransferRequest>().AsNoTracking()
   .SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==decision.TransferRequestId,ct);
  if(request==null)return null;
  // Same key, same payload -> the winner's result. Same key, different payload -> still a conflict,
  // exactly as the in-transaction check does; a reused key must never silently adopt a new payload.
  return prior.RequestHash==RequestHashOf(c.CompanyId,project,decision.ID,request.ID,request.SourceVersion,c.EmployeeId,reason)
   ?new ConstructionTransferRequestResult(true,"ApplicationSaved",prior.ID)
   :new ConstructionTransferRequestResult(false,"ApplicationIdConflict");
 }
 public async Task<ConstructionTransferRequestResult> ApplyAsync(int project,int transferDecisionId,Guid applicationId,string reason,CancellationToken ct=default)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  if(c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}
   ||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany
   ||!await access.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct)
   ||!await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==project&&x.CompanyID==c.CompanyId,ct))return new(false,"AccessDenied");
  if(transferDecisionId<=0||applicationId==Guid.Empty||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>1000)return new(false,"InvalidTransferApplication");
  if(db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return new(false,"CleanUnitOfWorkRequired");
  reason=reason.Trim();
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project,ct))
   {
    await tx.RollbackAsync(ct);
    return await ResolveCommittedApplicationAsync(c,project,transferDecisionId,applicationId,reason,ct)??new(false,"ConcurrencyConflict");
   }
   var decision=await db.Set<ConstructionBudgetTransferDecision>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==transferDecisionId,ct);
   if(decision?.Outcome!="Approved")return new(false,"ApprovedTransferDecisionRequired");
   // The actual executor creates/submits the resulting version. The stored independent
   // decision supplies its approver; never attribute creation to the absent requester.
   // Existing native budget invariants forbid the creator being their own approver.
   if(decision.CreatedBy==c.EmployeeId)return new(false,"SeparateExecutorRequired");
   var request=await db.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==decision.TransferRequestId,ct);
   if(request==null||request.CreatedBy==decision.CreatedBy||decision.SourceVersion!=request.SourceVersion)return new(false,"ApprovedTransferDecisionRequired");
   var hash=RequestHashOf(c.CompanyId,project,decision.ID,request.ID,request.SourceVersion,c.EmployeeId,reason);
   var prior=await db.Set<ConstructionBudgetTransferApplication>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ApplicationId==applicationId,ct);
   if(prior!=null)return prior.RequestHash==hash?new(true,"ApplicationSaved",prior.ID):new(false,"ApplicationIdConflict");
   if(await db.Set<ConstructionBudgetTransferApplication>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&(x.TransferDecisionId==decision.ID||x.TransferRequestId==request.ID),ct))return new(false,"TransferAlreadyApplied");
   var quote=await new ConstructionBudgetTransferPreview(db,contexts,access,audit).QuoteLockedAsync(c,project,request.FromLineId,request.ToLineId,request.Amount,ct);
   if(!quote.Ok)return new(false,quote.Code);
   if(quote.Witness!=request.SourceVersion||quote.BudgetVersionId!=request.BudgetVersionId||quote.PolicyToken!=Convert.ToBase64String(request.PolicyToken)
    ||quote.FromBefore!=request.FromBefore||quote.ToBefore!=request.ToBefore||quote.FromAfter!=request.FromAfter||quote.ToAfter!=request.ToAfter)return new(false,"ConcurrencyConflict");
   var baseline=await db.Set<ConstructionBudgetVersion>().SingleAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==request.BudgetVersionId&&x.IsBaseline&&x.Status=="Approved",ct);
   var companyCurrency=await db.Companies.AsNoTracking().Where(x=>x.CompanyID==c.CompanyId).Select(x=>x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
   if(companyCurrency!=baseline.CurrencyId)return new(false,"BudgetCurrencyChanged");
   var invalid=await new ConstructionBudgetVersionService(db,access,audit,rounding).ValidateStoredLines(c,baseline,ct);
   if(invalid!=null)return new(false,invalid);
   var lines=await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.BudgetVersionId==baseline.ID).OrderBy(x=>x.ID).ToListAsync(ct);
   var nextNo=checked((await db.Set<ConstructionBudgetVersion>().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project).MaxAsync(x=>(int?)x.VersionNo,ct)??0)+1);
   var now=DateTime.UtcNow;
   var result=new ConstructionBudgetVersion{CompanyID=c.CompanyId,ProjectId=project,VersionNo=nextNo,PreviousVersionId=baseline.ID,CurrencyId=baseline.CurrencyId,
    Kind="Revised",Status="Draft",IsBaseline=false,Reason=reason,CreatedBy=c.EmployeeId!.Value,CreatedAt=now};
   db.Add(result);await db.SaveChangesAsync(ct);
   var cloned=lines.Select(x=>new ConstructionBudgetLine{CompanyID=c.CompanyId,ProjectId=project,BudgetVersionId=result.ID,WbsNodeId=x.WbsNodeId,CostCodeId=x.CostCodeId,
    CostCategory=x.CostCategory,Amount=x.ID==request.FromLineId?request.FromAfter:x.ID==request.ToLineId?request.ToAfter:x.Amount,
    PreviousAmount=x.Amount,Quantity=x.Quantity,Rate=x.Rate,IsContingency=x.IsContingency,ChangeReason=reason}).ToList();
   if(cloned.Sum(x=>x.Amount)!=lines.Sum(x=>x.Amount))throw new InvalidOperationException("Transfer must preserve total budget.");
   db.AddRange(cloned);await db.SaveChangesAsync(ct);
   // Native line triggers rotate the draft token. Refresh it before advancing
   // through the real Draft -> Submitted -> Approved history transitions.
   if(db.Database.IsSqlServer())await db.Entry(result).ReloadAsync(ct);
   result.Status="Submitted";result.SubmittedBy=c.EmployeeId;result.SubmittedAt=now;Rotate(result);await db.SaveChangesAsync(ct);
   baseline.Status="Superseded";baseline.IsBaseline=false;Rotate(baseline);await db.SaveChangesAsync(ct);
   result.Status="Approved";result.IsBaseline=true;result.ApprovedBy=decision.CreatedBy;result.ApprovedAt=now;Rotate(result);await db.SaveChangesAsync(ct);
   var applied=new ConstructionBudgetTransferApplication{CompanyID=c.CompanyId,ProjectId=project,TransferRequestId=request.ID,TransferDecisionId=decision.ID,
    OriginalBudgetVersionId=baseline.ID,ResultBudgetVersionId=result.ID,ApplicationId=applicationId,RequestHash=hash,SourceVersion=request.SourceVersion,
    Reason=reason,AppliedBy=c.EmployeeId.Value,AppliedAt=now};
   db.Add(applied);await db.SaveChangesAsync(ct);
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,project,nameof(ConstructionBudgetTransferApplication),applied.ID,null,"ApplyBudgetTransfer",null,
    JsonSerializer.Serialize(new{applied.TransferRequestId,applied.TransferDecisionId,applied.OriginalBudgetVersionId,applied.ResultBudgetVersionId,applied.SourceVersion,applied.ApplicationId,IndependentApprover=decision.CreatedBy}),
    ConstructionChangeKind.Created,reason,c.EmployeeId,nameof(ConstructionBudgetTransferApplicationService),applicationId)});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(true,"ApplicationSaved",applied.ID);
  }catch(DbUpdateException){db.ChangeTracker.Clear();
   return await ResolveCommittedApplicationAsync(c,project,transferDecisionId,applicationId,reason,ct)??new(false,"ConcurrencyConflict");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){db.ChangeTracker.Clear();
   return await ResolveCommittedApplicationAsync(c,project,transferDecisionId,applicationId,reason,ct)??new(false,"ConcurrencyConflict");}
  catch(OverflowException){db.ChangeTracker.Clear();return new(false,"AmountOverflow");}
 }
}
