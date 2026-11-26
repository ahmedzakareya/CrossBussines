using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionBudgetExceptionResult(bool Ok, string Code, int? Id = null,
    string? Version = null, decimal? ProposedAmount = null, decimal? Overrun = null, int? CurrencyId = null);
public interface IConstructionBudgetExceptionService
{
    Task<CrossBuy.ViewModel.ConstructionBudgetExceptionPage?> ReadPageAsync(BusinessContext c, int projectId,
        int? orderId, CancellationToken ct = default);
    Task<ConstructionBudgetExceptionResult> PreviewAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, CancellationToken ct = default);
    Task<ConstructionBudgetExceptionResult> RequestCurrentAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, string expectedAdmissionVersion, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionBudgetExceptionResult> RequestAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionBudgetExceptionResult> ApproveAsync(BusinessContext c, int projectId, int exceptionId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionBudgetExceptionResult> RejectAsync(BusinessContext c, int projectId, int exceptionId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default);
    Task<ConstructionReservationResult> ConsumeAsync(BusinessContext c, int projectId, int exceptionId,
        Guid requestId, string reason, CancellationToken ct = default);
}

// Private workflow: no procurement endpoint activation, supplied money, implicit policy or financial writer.
public sealed class ConstructionBudgetExceptionService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit, IConstructionBudgetPolicyService policy) : IConstructionBudgetExceptionService
{
    private ConstructionCommitmentReservationService Kernel => new(db, access, audit, policy);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static bool Valid(Guid id, string reason) => id != Guid.Empty && !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 1000;
    private bool Clean => db.Database.CurrentTransaction == null && !db.ChangeTracker.HasChanges();
    private async Task<bool> Allowed(BusinessContext c, int project, CancellationToken ct) => !c.IsExternalPortal &&
        c.IsAuthenticated && c.EmployeeId is > 0 && c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId &&
        await access.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);
    private static bool ExceptionEligible(ConstructionAdmissionCandidate candidate) => candidate.Ready &&
        candidate.RequiresApproval && !candidate.Allowed && candidate.Policy?.Mode == ConstructionBudgetModes.ApprovalRequired &&
        candidate.Version?.Length == 64 && candidate.Overrun > 0;
    private static bool FitsStorage(decimal value) => value >= 0 && value < 100000000000000000000m && decimal.Round(value, 8, MidpointRounding.AwayFromZero) == value;
    private static ConstructionBudgetExceptionResult Receipt(ConstructionBudgetExceptionRequest row, string code) =>
        new(true, code, row.ID, row.AdmissionVersion, row.ProposedAmount, row.Overrun, row.CurrencyId);

    public async Task<CrossBuy.ViewModel.ConstructionBudgetExceptionPage?> ReadPageAsync(BusinessContext c, int projectId,
        int? orderId, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct) || !Clean) return null;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await Kernel.ProjectLock(c, projectId, ct)) return null;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.ID == projectId && x.CompanyID == c.CompanyId, ct);
        var orders = await db.PurchaseOrders.AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId &&
            (db.Set<ConstructionOrderAllocation>().Any(a => a.CompanyID == c.CompanyId && a.ProjectId == projectId && a.PurchaseOrderId == x.ID && a.IsSealed) ||
             db.Set<ConstructionBudgetExceptionRequest>().Any(r => r.CompanyID == c.CompanyId && r.ProjectId == projectId && r.PurchaseOrderId == x.ID)))
            .OrderByDescending(x => x.ID).Select(x => new CrossBuy.ViewModel.ConstructionExceptionOrder(x.ID, x.OrderNo)).ToListAsync(ct);
        if (orderId.HasValue && !orders.Any(x => x.Id == orderId)) return null;
        var allocation = orderId.HasValue ? await db.Set<ConstructionOrderAllocation>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.PurchaseOrderId == orderId && x.IsSealed)
            .OrderByDescending(x => x.RevisionNo).FirstOrDefaultAsync(ct) : null;
        ConstructionAdmissionCandidate? candidate = allocation == null ? null :
            await Kernel.PrepareLockedAsync(c, projectId, allocation.ID, allocation.SourceVersion, ct);
        ConstructionBudgetExceptionResult? preview = candidate == null ? null : !ExceptionEligible(candidate)
            ? new(false, candidate.Ready ? "ExceptionNotRequired" : candidate.Code)
            : new(true, "Ready", Version: candidate.Version, ProposedAmount: candidate.Lines!.Sum(x => x.NetDocumentAmount),
                Overrun: candidate.Overrun, CurrencyId: candidate.Baseline!.CurrencyId);
        if (preview?.Ok == true && (!FitsStorage(preview.ProposedAmount!.Value) || !FitsStorage(preview.Overrun!.Value))) preview = new(false, "AmountOverflow");
        var requests = await db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId &&
            x.ProjectId == projectId && (!orderId.HasValue || x.PurchaseOrderId == orderId)).OrderByDescending(x => x.ID).ToListAsync(ct);
        var decisions = await db.Set<ConstructionBudgetExceptionDecision>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToListAsync(ct);
        var consumed = await db.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).Select(x => x.ExceptionRequestId).ToListAsync(ct);
        var currencies = await db.Currencies.AsNoTracking().ToDictionaryAsync(x => x.ID, x => x.Code, ct);
        var history = requests.Select(r =>
        {
            var decision = decisions.SingleOrDefault(x => x.ExceptionRequestId == r.ID);
            var used = consumed.Contains(r.ID);
            var current = candidate != null && Matches(r, candidate);
            var independent = r.CreatedBy != c.EmployeeId;
            return new CrossBuy.ViewModel.ConstructionExceptionHistory(r.ID, r.PurchaseOrderId,
                orders.SingleOrDefault(x => x.Id == r.PurchaseOrderId)?.Number, r.ProposedAmount, r.Overrun,
                currencies.GetValueOrDefault(r.CurrencyId) ?? "", r.Reason, r.CreatedAt, r.CreatedBy,
                used ? "Consumed" : decision?.Outcome ?? (orderId.HasValue && !current ? "Stale" : "Pending"),
                r.AdmissionVersion, independent && decision == null && !used && current,
                independent && decision == null && !used, decision?.Reason);
        }).ToList();
        await tx.CommitAsync(ct);
        return new(projectId, project.Code, project.Name, project.NameEn, orderId, orders, preview,
            allocation?.ID, allocation?.SourceVersion, preview?.CurrencyId is int currency ? currencies.GetValueOrDefault(currency) : null, history);
    }
    private static bool Matches(ConstructionBudgetExceptionRequest request, ConstructionAdmissionCandidate candidate) =>
        ExceptionEligible(candidate) && request.AdmissionVersion == candidate.Version && request.SourceVersion == candidate.Proposal!.SourceVersion &&
        request.AllocationId == candidate.Proposal.ID && request.PurchaseOrderId == candidate.Proposal.PurchaseOrderId &&
        request.BudgetVersionId == candidate.Baseline!.ID && request.CurrencyId == candidate.Baseline.CurrencyId &&
        request.ProposedAmount == candidate.Lines!.Sum(x => x.NetDocumentAmount) && request.Overrun == candidate.Overrun;

    public async Task<ConstructionBudgetExceptionResult> PreviewAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (sourceVersion?.Length != 64) return new(false, "InvalidExceptionRequest");
        if (!Clean) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var candidate = await Kernel.PrepareLockedAsync(c, projectId, allocationId, sourceVersion, ct);
            if (!ExceptionEligible(candidate)) return new(false, candidate.Ready ? "ExceptionNotRequired" : candidate.Code);
            var amount = candidate.Lines!.Sum(x => x.NetDocumentAmount);
            if (!FitsStorage(amount) || !FitsStorage(candidate.Overrun)) return new(false, "AmountOverflow");
            await tx.CommitAsync(ct);
            return new(true, "Ready", Version: candidate.Version, ProposedAmount: amount,
                Overrun: candidate.Overrun, CurrencyId: candidate.Baseline!.CurrencyId);
        }
        catch (OverflowException) { return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { return new(false, "ConcurrencyConflict"); }
    }

    public Task<ConstructionBudgetExceptionResult> RequestCurrentAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, string expectedAdmissionVersion, Guid requestId, string reason, CancellationToken ct = default) =>
        RequestCoreAsync(c, projectId, allocationId, sourceVersion, expectedAdmissionVersion ?? "", requestId, reason, ct);

    public Task<ConstructionBudgetExceptionResult> RequestAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, Guid requestId, string reason, CancellationToken ct = default) =>
        RequestCoreAsync(c, projectId, allocationId, sourceVersion, null, requestId, reason, ct);

    private async Task<ConstructionBudgetExceptionResult> RequestCoreAsync(BusinessContext c, int projectId, int allocationId,
        string sourceVersion, string? expectedAdmissionVersion, Guid requestId, string reason, CancellationToken ct)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason) || sourceVersion?.Length != 64) return new(false, "InvalidExceptionRequest");
        if (expectedAdmissionVersion != null && (expectedAdmissionVersion.Length != 64 || !expectedAdmissionVersion.All(Uri.IsHexDigit)))
            return new(false, "InvalidExceptionRequest");
        if (!Clean) return new(false, "CleanUnitOfWorkRequired");
        // Keep legacy receipts replayable; browser receipts additionally bind the displayed financial witness.
        var hash = expectedAdmissionVersion == null
            ? Hash(new { projectId, allocationId, sourceVersion, Actor = c.EmployeeId, Reason = reason.Trim() })
            : Hash(new { projectId, allocationId, sourceVersion, expectedAdmissionVersion, Actor = c.EmployeeId, Reason = reason.Trim() });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var old = await db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (old != null)
            {
                if (old.ProjectId != projectId || old.RequestHash != hash) return new(false, "IdempotencyConflict");
                await tx.CommitAsync(ct); return Receipt(old, "AlreadyRequested"); // acknowledgement, never admission
            }
            var candidate = await Kernel.PrepareLockedAsync(c, projectId, allocationId, sourceVersion, ct);
            if (expectedAdmissionVersion != null && (!candidate.Ready || candidate.Version != expectedAdmissionVersion))
                return new(false, "ExceptionSourceChanged");
            if (!ExceptionEligible(candidate)) return new(false, candidate.Ready ? "ExceptionNotRequired" : candidate.Code);
            var amount = candidate.Lines!.Sum(x => x.NetDocumentAmount);
            if (!FitsStorage(amount) || !FitsStorage(candidate.Overrun)) return new(false, "AmountOverflow");
            var row = new ConstructionBudgetExceptionRequest { CompanyID = c.CompanyId, ProjectId = projectId,
                PurchaseOrderId = candidate.Proposal!.PurchaseOrderId, AllocationId = allocationId, BudgetVersionId = candidate.Baseline!.ID,
                CurrencyId = candidate.Baseline.CurrencyId, SourceVersion = sourceVersion, AdmissionVersion = candidate.Version!,
                ProposedAmount = amount, Overrun = candidate.Overrun, RequestId = requestId, RequestHash = hash,
                Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            Record(c, projectId, nameof(ConstructionBudgetExceptionRequest), row.ID, "RequestBudgetException", new { row.AllocationId, row.ProposedAmount, row.Overrun, row.CurrencyId, row.AdmissionVersion }, reason, requestId);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Receipt(row, "Requested");
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public Task<ConstructionBudgetExceptionResult> ApproveAsync(BusinessContext c, int projectId, int exceptionId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default) =>
        Decide(c, projectId, exceptionId, expectedVersion, requestId, reason, "Approved", ct);
    public Task<ConstructionBudgetExceptionResult> RejectAsync(BusinessContext c, int projectId, int exceptionId,
        string expectedVersion, Guid requestId, string reason, CancellationToken ct = default) =>
        Decide(c, projectId, exceptionId, expectedVersion, requestId, reason, "Rejected", ct);

    private async Task<ConstructionBudgetExceptionResult> Decide(BusinessContext c, int projectId, int exceptionId,
        string expectedVersion, Guid requestId, string reason, string outcome, CancellationToken ct)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason) || expectedVersion?.Length != 64) return new(false, "InvalidExceptionDecision");
        if (!Clean) return new(false, "CleanUnitOfWorkRequired");
        var hash = Hash(new { projectId, exceptionId, expectedVersion, outcome, Actor = c.EmployeeId, Reason = reason.Trim() });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
            var request = await db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == exceptionId && x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct);
            if (request == null) return new(false, "ExceptionNotFound");
            if (request.CreatedBy == c.EmployeeId) return new(false, "IndependentApproverRequired");
            if (request.AdmissionVersion != expectedVersion) return new(false, "ConcurrencyConflict");
            var replay = await db.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (replay != null)
            {
                if (replay.RequestHash != hash) return new(false, "IdempotencyConflict");
                await tx.CommitAsync(ct); return new(true, "AlreadyDecided", replay.ID);
            }
            if (await db.Set<ConstructionBudgetExceptionDecision>().AnyAsync(x => x.CompanyID == c.CompanyId && x.ExceptionRequestId == exceptionId, ct)) return new(false, "AlreadyDecided");
            if (outcome == "Approved")
            {
                var current = await Kernel.PrepareLockedAsync(c, projectId, request.AllocationId, request.SourceVersion, ct);
                if (!Matches(request, current)) return new(false, "ExceptionSourceChanged");
            }
            var row = new ConstructionBudgetExceptionDecision { CompanyID = c.CompanyId, ProjectId = projectId,
                ExceptionRequestId = exceptionId, Outcome = outcome, RequestId = requestId, RequestHash = hash,
                Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(row); await db.SaveChangesAsync(ct);
            Record(c, projectId, nameof(ConstructionBudgetExceptionDecision), row.ID, outcome + "BudgetException", new { exceptionId, request.AdmissionVersion }, reason, requestId);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, outcome, row.ID);
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<ConstructionReservationResult> ConsumeAsync(BusinessContext c, int projectId, int exceptionId,
        Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason)) return new(false, "InvalidReservationRequest");
        if (!Clean) return new(false, "CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await ConsumeCoreAsync(c, projectId, exceptionId, requestId, reason, ct);
            if (result.Ok) await tx.CommitAsync(ct);
            return result;
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    internal async Task<ConstructionReservationResult> ConsumeInTransactionAsync(BusinessContext c, int projectId, int exceptionId,
        Guid requestId, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ct)) return new(false, "AccessDenied");
        if (!Valid(requestId, reason)) return new(false, "InvalidReservationRequest");
        return await ConstructionAdmissionUnit.RunAsync(db, () => ConsumeCoreAsync(c, projectId, exceptionId, requestId, reason, ct), ct);
    }

    private async Task<ConstructionReservationResult> ConsumeCoreAsync(BusinessContext c, int projectId, int exceptionId,
        Guid requestId, string reason, CancellationToken ct = default)
    {
        var hash = Hash(new { projectId, exceptionId, Operation = "ConsumeBudgetException", Reason = reason.Trim() });

        if (!await Kernel.ProjectLock(c, projectId, ct)) return new(false, "ConcurrencyConflict");
        var replay = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
        if (replay != null)
        {
            if (replay.ProjectId != projectId || replay.RequestHash != hash) return new(false, "IdempotencyConflict");
            return new(true, "AlreadyReserved", replay.ID); // never renewed admission
        }
        var request = await db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == exceptionId && x.CompanyID == c.CompanyId && x.ProjectId == projectId, ct);
        if (request == null) return new(false, "ExceptionNotFound");
        if (await db.Set<ConstructionBudgetExceptionConsumption>().AnyAsync(x => x.CompanyID == c.CompanyId && x.ExceptionRequestId == exceptionId, ct)) return new(false, "ExceptionAlreadyConsumed");
        var decision = await db.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.ExceptionRequestId == exceptionId, ct);
        if (decision?.Outcome != "Approved" || decision.CreatedBy == request.CreatedBy) return new(false, "ApprovedExceptionRequired");
        var candidate = await Kernel.PrepareLockedAsync(c, projectId, request.AllocationId, request.SourceVersion, ct);
        if (!Matches(request, candidate)) return new(false, "ExceptionSourceChanged");
        var row = new ConstructionCommitmentReservation { CompanyID = c.CompanyId, ProjectId = projectId,
            PurchaseOrderId = request.PurchaseOrderId, AllocationId = request.AllocationId, BudgetVersionId = request.BudgetVersionId,
            CurrencyId = request.CurrencyId, NetDocumentAmount = request.ProposedAmount, RequestId = requestId, RequestHash = hash,
            SourceVersion = request.SourceVersion, PolicyMode = ConstructionBudgetModes.ApprovalRequired,
            DecisionCode = "ApprovedBudgetException", Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
        db.Add(row); await db.SaveChangesAsync(ct);
        db.Add(new ConstructionBudgetExceptionConsumption { CompanyID = c.CompanyId, ProjectId = projectId,
            ExceptionRequestId = exceptionId, ReservationId = row.ID, CreatedAt = DateTime.UtcNow });
        Record(c, projectId, nameof(ConstructionCommitmentReservation), row.ID, "ConsumeBudgetException", new { exceptionId, decision.ID, request.AdmissionVersion }, reason, requestId);
        await db.SaveChangesAsync(ct); return new(true, row.DecisionCode, row.ID);
    }

    private void Record(BusinessContext c, int project, string entity, int id, string action, object value, string reason, Guid request) =>
        audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, entity, id, null, action, null,
            JsonSerializer.Serialize(value), ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId, nameof(ConstructionBudgetExceptionService), request) });
    private static bool Conflict(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
            if (e is DbUpdateConcurrencyException || e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
