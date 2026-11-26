using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionBudgetDecision(string Code, bool Allowed, bool Warning, bool RequiresApproval);

public interface IConstructionBudgetPolicyService
{
    Task<(bool ok, string? error)> ConfigureAsync(BusinessContext context, string mode, string reason,
        byte[]? expectedToken = null, CancellationToken ct = default);
    Task<ConstructionBudgetDecision> EvaluateAsync(BusinessContext context, int projectId,
        decimal approvedBudget, decimal consumedAndCommitted, decimal proposedAmount, CancellationToken ct = default);
}

/// <summary>Policy selection only. C2 computes budget/commitment totals from persisted facts before calling this;
/// this service does not approve a document, post money, or accept a caller-supplied company.</summary>
public sealed class ConstructionBudgetPolicyService : IConstructionBudgetPolicyService
{
    private readonly CrossDbContext _db;
    private readonly IProjectsAccessService _access;
    private readonly IConstructionAuditService _audit;

    public ConstructionBudgetPolicyService(CrossDbContext db, IProjectsAccessService access, IConstructionAuditService audit)
    { _db = db; _access = access; _audit = audit; }

    public async Task<(bool ok, string? error)> ConfigureAsync(BusinessContext context, string mode, string reason,
        byte[]? expectedToken = null, CancellationToken ct = default)
    {
        if (context.CompanyId <= 0 || _db.CompanyScope.FilterCompanyId != context.CompanyId || !context.IsAuthenticated ||
            !await _access.CanAsync(context, ProjectsActions.BudgetManage, cancellationToken: ct))
            return (false, "Access denied");
        if (!ConstructionBudgetModes.IsKnown(mode)) return (false, "An explicit budget policy is required");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return (false, "A policy change reason is required (maximum 1000 characters)");
        var policy = await _db.Set<ConstructionBudgetPolicy>().SingleOrDefaultAsync(x => x.CompanyID == context.CompanyId, ct);
        string? previous = policy?.Mode;
        if (policy != null && (expectedToken == null || !ConstructionConcurrency.Matches(expectedToken, policy.ConcurrencyToken)))
            return (false, ConstructionConcurrency.ConflictMessage);
        if (policy == null)
        {
            policy = new ConstructionBudgetPolicy { CompanyID = context.CompanyId };
            _db.Add(policy);
        }
        policy.Mode = mode; policy.Reason = reason.Trim(); policy.UpdatedAt = DateTime.UtcNow;
        policy.UpdatedBy = context.EmployeeId; policy.ConcurrencyToken = ConstructionConcurrency.NewToken();
        _audit.Record(new[] { ConstructionAuditDraft.Text(context.CompanyId, null,
            nameof(ConstructionBudgetPolicy), context.CompanyId, null, "Mode", previous, mode,
            ConstructionChangeKind.Updated, reason, context.EmployeeId, nameof(ConstructionBudgetPolicyService), Guid.NewGuid()) });
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return (false, ConstructionConcurrency.ConflictMessage); }
        return (true, null);
    }

    public async Task<ConstructionBudgetDecision> EvaluateAsync(BusinessContext context, int projectId,
        decimal approvedBudget, decimal consumedAndCommitted, decimal proposedAmount, CancellationToken ct = default)
    {
        if (context.CompanyId <= 0 || _db.CompanyScope.FilterCompanyId != context.CompanyId || !context.IsAuthenticated ||
            !await _access.CanAsync(context, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = projectId }, ct))
            return new("AccessDenied", false, false, false);
        if (!await _db.Projects.AsNoTracking().AnyAsync(x => x.ID == projectId && x.CompanyID == context.CompanyId, ct))
            return new("ProjectNotFound", false, false, false);
        if (approvedBudget < 0 || consumedAndCommitted < 0 || proposedAmount < 0)
            return new("InvalidAmounts", false, false, false);
        var mode = await _db.Set<ConstructionBudgetPolicy>().AsNoTracking()
            .Where(x => x.CompanyID == context.CompanyId).Select(x => x.Mode).SingleOrDefaultAsync(ct);
        if (!ConstructionBudgetModes.IsKnown(mode)) return new("PolicyNotConfigured", false, false, false);
        // Subtraction avoids overflow when caller totals are individually valid decimal values.
        var remaining = approvedBudget - consumedAndCommitted;
        if (proposedAmount <= remaining) return new("WithinBudget", true, false, false);
        return mode switch
        {
            ConstructionBudgetModes.Warning => new("BudgetWarning", true, true, false),
            ConstructionBudgetModes.ApprovalRequired => new("BudgetApprovalRequired", false, false, true),
            _ => new("BudgetHardBlock", false, false, false)
        };
    }
}
