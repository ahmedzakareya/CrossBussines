using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionReservationResult(bool Ok, string Code, int? Id = null, bool Warning = false, bool RequiresApproval = false)
{
    // A replay can acknowledge history after cancellation or source evolution; it cannot renew admission.
    public bool GrantsCurrentAdmission => Ok && (Code is "WithinBudget" or "BudgetWarning" or "ApprovedBudgetException");
}
// Complete financial evidence is separate from permission to spend. Approval-required proposals
// retain an exact witness but are never admitted by this read result.
internal sealed record ConstructionAdmissionCandidate(bool Ready, string Code, bool Allowed = false,
    bool Warning = false, bool RequiresApproval = false, ConstructionOrderAllocation? Proposal = null,
    ConstructionBudgetVersion? Baseline = null, ConstructionBudgetPolicy? Policy = null,
    IReadOnlyList<ConstructionOrderAllocationLine>? Lines = null, string? Version = null, decimal Overrun = 0);
public interface IConstructionCommitmentReservationService
{
    Task<ConstructionReservationResult> ReserveAsync(BusinessContext context, int projectId, int allocationId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionReservationResult> ReleaseCancelledAsync(BusinessContext context, int projectId, int reservationId,
        Guid requestId, string reason, CancellationToken ct = default);
}

// Private, fail-closed admission kernel. Existing procurement endpoints do not yet call this service.
// No supplied money totals, FX, approval flag, GL/stock write or implicit company policy.
public sealed class ConstructionCommitmentReservationService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit, IConstructionBudgetPolicyService policyService) : IConstructionCommitmentReservationService
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static bool Valid(Guid request, string reason) => request != Guid.Empty && !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 1000;
    private async Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) => !c.IsExternalPortal && c.IsAuthenticated && c.EmployeeId is > 0 &&
        c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    internal Task<bool> ProjectLock(BusinessContext c, int project, CancellationToken ct) => AcquireProjectLock(db, c, project, ct);

    internal static Task<bool> AcquireProjectLock(CrossDbContext db, BusinessContext c, int project, CancellationToken ct)
        => AcquireProjectScopeLock(db, c.CompanyId, project, ct);

    /// A test-only seam that forces the outcome of the project lock. Never set outside tests.
    /// PUBLIC only because the test assembly is separate and this repository declares no
    /// InternalsVisibleTo - the same reason InventoryApprovalService.BeforeDecisionSaveHookForTests
    /// is public. It is never assigned by any production path.
    /// WHY IT EXISTS: a losing racer only fails in reality once sys.sp_getapplock exhausts its
    /// 10-second budget, which needs real contention - the defect this reproduces appeared exactly
    /// once in a 52-minute run. Forcing the outcome makes that path deterministic without changing
    /// how the lock itself behaves; when it is null the real sp_getapplock call is unchanged.
    /// ASYNCLOCAL, NOT A PLAIN STATIC, and that difference is the point. The house precedent
    /// (BeforeDecisionSaveHookForTests) is an INSTANCE member and therefore cannot leak between
    /// callers; this seam has to be static because AcquireProjectScopeLock is static, so a plain
    /// static field would be process-wide mutable state that one test could impose on every other
    /// request and on the 30-plus suites running in other xUnit collections in parallel. AsyncLocal
    /// confines the override to the async flow that set it, which restores the isolation the
    /// instance precedent gets for free.
    private static readonly AsyncLocal<Func<int, int, bool>?> LockOutcomeOverride = new();
    public static Func<int, int, bool>? LockOutcomeForTests
    {
        get => LockOutcomeOverride.Value;
        set => LockOutcomeOverride.Value = value;
    }

    // Mutual exclusion only, never authorization or a spending grant. Company guard remains mandatory.
    internal static async Task<bool> AcquireProjectScopeLock(CrossDbContext db, int company, int project, CancellationToken ct)
    {
        if (company <= 0 || project <= 0 || db.CompanyScope.FilterCompanyId != company ||
            db.CompanyScope.AllowsCrossCompany || db.Database.CurrentTransaction == null) return false;
        if (!db.Database.IsSqlServer()) return true; // Native concurrency is proved separately; SQLite is serial unit evidence only.
        // Deliberately AFTER the provider check: the seam can only ever affect a real SQL Server
        // acquisition, so no in-memory suite can be perturbed by it even if one leaked a value.
        if (LockOutcomeForTests is { } gate && !gate(company, project)) return false;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@scope,@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=10000; SELECT @r;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@scope";
        parameter.Value = FormattableString.Invariant($"R7:Commitments:{company}:{project}"); command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) >= 0;
    }

    private async Task<bool> CancellationClean(BusinessContext c, int order, CancellationToken ct) =>
        !await db.PurchaseOrderLines.AsNoTracking().AnyAsync(x => x.PurchaseOrderId == order && x.ReceivedQty != 0, ct) &&
        !await db.GoodsReceipts.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order, ct) &&
        !await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.PurchaseOrderId == order, ct);

    private Task<(string? Error, List<ConstructionOrderAllocationLine> Lines)> Current(BusinessContext c, int project,
        ConstructionOrderAllocation header, CancellationToken ct) =>
        new ConstructionOrderAllocationService(db, access, audit).ValidateCurrentAsync(c, project, header, ct);

    // Shared by reservation and the forthcoming independent approval workflow. The caller owns
    // the serializable transaction and project lock; there is no public caller-supplied total.
    internal async Task<ConstructionAdmissionCandidate> PrepareLockedAsync(BusinessContext c, int projectId,
        int allocationId, string expectedVersion, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("An admission transaction is required");
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        var proposal = await db.Set<ConstructionOrderAllocation>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == allocationId && x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct);
        if (proposal == null) return new(false, "AllocationNotFound");
        if (proposal.SourceVersion != expectedVersion) return new(false, "ConcurrencyConflict");
        if (await db.Set<ConstructionCommitmentReservation>().AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == proposal.PurchaseOrderId, ct)) return new(false, "SourceAlreadyProcessed");
        var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId, ct);
        if (policy == null || !ConstructionBudgetModes.IsKnown(policy.Mode)) return new(false, "PolicyNotConfigured");
        var baseline = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.IsBaseline && x.Status == "Approved", ct);
        if (baseline == null || baseline.ID != proposal.BudgetVersionId) return new(false, "ApprovedBaselineRequired");
        var checkedProposal = await Current(c, projectId, proposal, ct);
        if (checkedProposal.Error != null) return new(false, checkedProposal.Error);
        var exposure = await new ConstructionBudgetExposureService(db, access, audit).ReadLocked(c, projectId, baseline, proposal.PurchaseOrderId, ct);
        if (!exposure.Ok) return new(false, exposure.Code);
        var consumed = exposure.Rows!.ToDictionary(x => (Node: x.WbsNodeId, Code: x.CostCodeId), x => x.Actual + x.Commitment);
        var budgets = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.BudgetVersionId == baseline.ID).ToListAsync(ct);
        if (consumed.Keys.Any(key => !budgets.Any(x => x.WbsNodeId == key.Node && x.CostCodeId == key.Code))) return new(false, "BudgetDimensionCoverageRequired");
        var warning = false; var requiresApproval = false; decimal overrun = 0;
        foreach (var proposed in checkedProposal.Lines.GroupBy(x => (x.WbsNodeId, x.CostCodeId)))
        {
            var budget = budgets.SingleOrDefault(x => x.WbsNodeId == proposed.Key.WbsNodeId && x.CostCodeId == proposed.Key.CostCodeId);
            if (budget == null || budget.Amount < 0) return new(false, "BudgetDimensionCoverageRequired");
            var amount = proposed.Sum(x => x.NetDocumentAmount);
            var used = consumed.GetValueOrDefault(proposed.Key);
            var decision = await policyService.EvaluateAsync(c, projectId, budget.Amount, used, amount, ct);
            // An approval request cannot conceal an invalid dimension encountered later in the proposal.
            if (!decision.Allowed && !decision.RequiresApproval) return new(false, decision.Code);
            warning |= decision.Warning; requiresApproval |= decision.RequiresApproval;
            overrun += Math.Max(0, amount - (budget.Amount - used));
        }
        var version = Hash(new { Company = c.CompanyId, Project = projectId, Proposal = proposal,
            Lines = checkedProposal.Lines.OrderBy(x => x.ID), Exposure = exposure.Version,
            Policy = new { policy.Mode, policy.ConcurrencyToken }, Baseline = new { baseline.ID, baseline.RowVersion } });
        return new(true, requiresApproval ? "BudgetApprovalRequired" : warning ? "BudgetWarning" : "WithinBudget",
            !requiresApproval, warning, requiresApproval, proposal, baseline, policy, checkedProposal.Lines.AsReadOnly(), version, overrun);
    }

    public async Task<ConstructionReservationResult> ReserveAsync(BusinessContext c, int projectId, int allocationId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason) || expectedVersion?.Length != 64) return new(false, "InvalidReservationRequest");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await ReserveCoreAsync(c, projectId, allocationId, expectedVersion, requestId, reason, ct);
            if (result.Ok) await tx.CommitAsync(ct);
            return result;
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    internal async Task<ConstructionReservationResult> ReserveInTransactionAsync(BusinessContext c, int projectId, int allocationId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason) || expectedVersion?.Length != 64) return new(false, "InvalidReservationRequest");
        return await ConstructionAdmissionUnit.RunAsync(db, () => ReserveCoreAsync(c, projectId, allocationId, expectedVersion, requestId, reason, ct), ct);
    }

    private async Task<ConstructionReservationResult> ReserveCoreAsync(BusinessContext c, int projectId, int allocationId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default)

    {
        var requestHash = Hash(new { projectId, allocationId, expectedVersion, Reason = reason.Trim() });

        if (!await ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
        var existing = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
        if (existing != null)
        {
            if (existing.ProjectId != projectId || existing.RequestHash != requestHash) return new(false, "IdempotencyConflict");

            // Historical acknowledgement only, never a renewed permission to post or reopen the source.
            return new(true, "AlreadyReserved", existing.ID, existing.DecisionCode == "BudgetWarning");
        }
        var candidate = await PrepareLockedAsync(c, projectId, allocationId, expectedVersion, ct);
        if (!candidate.Ready || !candidate.Allowed) return new(false, candidate.Code, RequiresApproval: candidate.RequiresApproval);
        var proposal = candidate.Proposal!; var baseline = candidate.Baseline!; var policy = candidate.Policy!;
        var warning = candidate.Warning;
        var row = new ConstructionCommitmentReservation { CompanyID = c.CompanyId, ProjectId = projectId, PurchaseOrderId = proposal.PurchaseOrderId,
            AllocationId = proposal.ID, BudgetVersionId = baseline.ID, CurrencyId = baseline.CurrencyId,
            NetDocumentAmount = candidate.Lines!.Sum(x => x.NetDocumentAmount), RequestId = requestId, RequestHash = requestHash,
            SourceVersion = expectedVersion, PolicyMode = policy.Mode, DecisionCode = warning ? "BudgetWarning" : "WithinBudget",
            Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
        db.Add(row); await db.SaveChangesAsync(ct);
        Record(c, projectId, row.ID, "ReserveCommitment", new { row.AllocationId, row.NetDocumentAmount, row.CurrencyId, row.PolicyMode, row.DecisionCode }, reason, requestId);
        await db.SaveChangesAsync(ct);
        return new(true, row.DecisionCode, row.ID, warning);
    }

    public async Task<ConstructionReservationResult> ReleaseCancelledAsync(BusinessContext c, int projectId, int reservationId,
        Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason)) return new(false, "InvalidReleaseRequest");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        var hash = Hash(new { projectId, reservationId, Reason = reason.Trim() });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var old = await db.Set<ConstructionCommitmentRelease>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (old != null)
            {
                if (old.ProjectId != projectId || old.RequestHash != hash) return new(false, "IdempotencyConflict");
                await tx.CommitAsync(ct); return new(true, "AlreadyReleased", old.ID);
            }
            var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == reservationId && x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct);
            if (reservation == null) return new(false, "ReservationNotFound");
            if (await db.Set<ConstructionCommitmentRelease>().AnyAsync(x => x.ReservationId == reservationId && x.CompanyID == c.CompanyId, ct)) return new(false, "SourceAlreadyReleased");
            if (!await db.PurchaseOrders.AsNoTracking().AnyAsync(x => x.ID == reservation.PurchaseOrderId && x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.Status == "Cancelled", ct)) return new(false, "CancellationRequired");
            if (!await CancellationClean(c, reservation.PurchaseOrderId, ct)) return new(false, "CancellationCoverageRequired");
            var release = new ConstructionCommitmentRelease { CompanyID = c.CompanyId, ProjectId = projectId, ReservationId = reservationId,
                RequestId = requestId, RequestHash = hash, Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(release); await db.SaveChangesAsync(ct);
            Record(c, projectId, reservationId, "ReleaseCancelledCommitment", new { reservationId, releaseId = release.ID }, reason, requestId);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, "Released", release.ID);
        }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private void Record(BusinessContext c, int project, int id, string action, object value, string reason, Guid request) =>
        audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionCommitmentReservation), id,
            null, action, null, JsonSerializer.Serialize(value), ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId,
            nameof(ConstructionCommitmentReservationService), request) });
    private static bool Conflict(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
            if (e is DbUpdateConcurrencyException || e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
