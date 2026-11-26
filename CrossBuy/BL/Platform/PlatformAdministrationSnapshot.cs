using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform;

public sealed class PlatformAdministrationSnapshot
{
    public required int CompanyId { get; init; }
    public IReadOnlyList<PlatformGrantView> Grants { get; init; } = Array.Empty<PlatformGrantView>();
    public IReadOnlyList<BootstrapAccessPolicy> Policies { get; init; } = Array.Empty<BootstrapAccessPolicy>();
    public IReadOnlyDictionary<string,string[]> Roles { get; init; } = new Dictionary<string,string[]>();
    public IReadOnlyDictionary<int,string> Employees { get; init; } = new Dictionary<int,string>();
    public IReadOnlyDictionary<int,AuthorizationDecision> PolicyDecisions { get; init; } = new Dictionary<int,AuthorizationDecision>();
    public int? AuthorizationDebt { get; init; }
    public bool DebtReconciled { get; init; }
}

// Read projection only. Grant mutations continue through IPlatformGrantWriter.
public static class PlatformAdministrationReader
{
    public static async Task<bool> CanReadAsync(BusinessContext? context, IPlatformAdminIdentity identity, CancellationToken ct = default)
    {
        if(context is not {CompanyId: > 0, EmployeeId: > 0} || context.IsExternalPortal || string.IsNullOrWhiteSpace(context.UserId)) return false;
        var held=await identity.RolesAsync(context.UserId,ct);
        return held.Any(r=>GrantAdminIdentityRoles.PlatformSecurity.Contains(r,StringComparer.Ordinal));
    }
    public static async Task<PlatformAdministrationSnapshot?> ReadAsync(BusinessContext? context, CrossDbContext db,
        IPlatformAdminIdentity identity,IPlatformGrantWriter writer,IBootstrapAccessPolicyReader policies,
        IPlatformPermissionVocabularyRegistry vocabulary,string contentRoot,CancellationToken ct=default)
    {
        if(!await CanReadAsync(context,identity,ct)) return null;
        var grants=await writer.ListDirectGrantsAsync(context!,new DirectGrantQuery {CompanyId=context!.CompanyId,IncludeInactive=true},ct);
        var policyRows=await policies.ListPoliciesAsync(context,includeInactive:true,cancellationToken:ct);
        var decisions=new Dictionary<int,AuthorizationDecision>();
        foreach(var row in policyRows) decisions[row.ID]=await policies.ResolveDecisionAsync(context,row.Scope,row.ActionCode,ct);
        var employees=await db.Employee.AsNoTracking().Where(e=>e.EmpCompanyID==context.CompanyId && e.IsActive)
            .OrderBy(e=>e.ID).Take(200).Select(e=>new {e.ID,e.FullName,e.FullNameEn}).ToListAsync(ct);
        int? debt=null;bool reconciled=false;
        var baseline=Path.Combine(Path.GetFullPath(Path.Combine(contentRoot,"..")),"engineering","authorization-baseline.json");
        if(File.Exists(baseline))
        {
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(baseline,ct));
            debt=document.RootElement.GetProperty("frozenBaseline").GetProperty("gaps").GetInt32();
            reconciled=debt==document.RootElement.GetProperty("entries").GetArrayLength();
        }
        return new PlatformAdministrationSnapshot {CompanyId=context.CompanyId,Grants=grants,Policies=policyRows,
            PolicyDecisions=decisions,Roles=vocabulary.AdministrableScopes.Where(s=>s!=EntityRegistry.ScopePos).ToDictionary(s=>s,s=>vocabulary.KnownRoles(s).Split(", ",StringSplitOptions.RemoveEmptyEntries)),
            Employees=employees.ToDictionary(e=>e.ID,e=>DisplayName.Of(e.FullName,e.FullNameEn)),AuthorizationDebt=debt,DebtReconciled=reconciled};
    }
}
