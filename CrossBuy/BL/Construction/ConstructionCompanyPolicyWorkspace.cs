using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionCompanyPolicyPage(IReadOnlyList<ConstructionCompanyPolicy> Policies,string? Error=null);
public interface IConstructionCompanyPolicyWorkspace
{
 Task<ConstructionCompanyPolicyPage?> ReadAsync(CancellationToken ct=default);
 Task<string?> SaveAsync(string key,string mode,string? token,string reason,CancellationToken ct=default);
}
// Configuration foundation only. No transfer, exception or forecast is executed by choosing a mode.
public sealed class ConstructionCompanyPolicyWorkspace(CrossDbContext db,IBusinessContextAccessor contexts,
 IProjectsAccessService access,IConstructionAuditService audit):IConstructionCompanyPolicyWorkspace
{
 private async Task<BusinessContext?> Actor(CancellationToken ct)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  return c is {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}&&
   db.CompanyScope.FilterCompanyId==c.CompanyId&&!db.CompanyScope.AllowsCrossCompany&&
   await access.CanAsync(c,ProjectsActions.Manage,cancellationToken:ct)?c:null;
 }
 public async Task<ConstructionCompanyPolicyPage?> ReadAsync(CancellationToken ct=default)
 {
  var c=await Actor(ct);return c==null?null:new(await db.Set<ConstructionCompanyPolicy>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId).OrderBy(x=>x.PolicyKey).ToListAsync(ct));
 }
 public async Task<string?> SaveAsync(string key,string mode,string? token,string reason,CancellationToken ct=default)
 {
  var c=await Actor(ct);if(c==null)return "AccessDenied";
  if(!ConstructionCompanyPolicies.Valid(key,mode)||string.IsNullOrWhiteSpace(reason)||reason.Length>1000)return "InvalidPolicy";
  byte[]? expected=null;
  if(!string.IsNullOrEmpty(token)){try{expected=Convert.FromBase64String(token);}catch(FormatException){return "ConcurrencyConflict";}if(expected.Length!=16)return "ConcurrencyConflict";}
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return "CleanUnitOfWorkRequired";
  try
  {
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   var row=await db.Set<ConstructionCompanyPolicy>().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey==key,ct);
   if(row==null&&expected!=null||row!=null&&(expected==null||!row.ConcurrencyToken.SequenceEqual(expected))){db.ChangeTracker.Clear();return "ConcurrencyConflict";}
   var previous=row?.Mode;
   if(row==null){row=new(){CompanyID=c.CompanyId,PolicyKey=key};db.Add(row);}
   row.Mode=mode;row.Reason=reason.Trim();row.UpdatedBy=c.EmployeeId!.Value;row.UpdatedAt=DateTime.UtcNow;row.ConcurrencyToken=Guid.NewGuid().ToByteArray();
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,null,nameof(ConstructionCompanyPolicy),c.CompanyId,null,"Configure."+key,previous,JsonSerializer.Serialize(new{Mode=mode,Scope="Company",OperationalWorkflowActivated=false}),previous==null?ConstructionChangeKind.Created:ConstructionChangeKind.Updated,row.Reason,c.EmployeeId,nameof(ConstructionCompanyPolicyWorkspace),Guid.NewGuid())});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return null;
  }
  catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();return "ConcurrencyConflict";}
  catch(Exception ex)when(Conflict(ex)){db.ChangeTracker.Clear();return "ConcurrencyConflict";}
  catch{db.ChangeTracker.Clear();throw;}
 }
 private static bool Conflict(Exception ex){for(Exception? e=ex;e!=null;e=e.InnerException)if(e is Microsoft.Data.SqlClient.SqlException sql&&sql.Number is 1205 or 1222 or 2601 or 2627)return true;return false;}
}
