using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionBudgetDraftLine(int WbsNodeId, int CostCodeId, decimal Amount,
    decimal? Quantity, decimal? Rate, bool IsContingency, string ChangeReason);
public sealed record ConstructionBudgetDocument(ConstructionBudgetVersion Header, List<ConstructionBudgetLine> Lines);

public interface IConstructionBudgetVersionService
{
    Task<ConstructionBudgetDocument?> GetAsync(BusinessContext context, int projectId, int versionId, CancellationToken ct = default);
    Task<StructureResult> CreateAsync(BusinessContext context, int projectId, string kind, int? previousVersionId,
        byte[]? previousVersion, string reason, CancellationToken ct = default);
    Task<StructureResult> SaveDraftAsync(BusinessContext context, int projectId, int versionId,
        byte[] expectedVersion, IReadOnlyList<ConstructionBudgetDraftLine> lines, string reason, CancellationToken ct = default);
    Task<StructureResult> SubmitAsync(BusinessContext context, int projectId, int versionId, byte[] expectedVersion,
        string reason, CancellationToken ct = default);
    Task<StructureResult> ApproveAsync(BusinessContext context, int projectId, int versionId, byte[] expectedVersion,
        string reason, CancellationToken ct = default);
}

// Versioned human estimates only. No caller-supplied approval flag, company, currency or approver.
// PO commitments, allocation reconciliation and policy enforcement at document approval are separate C2 work.
public sealed class ConstructionBudgetVersionService(CrossDbContext db, IProjectsAccessService access,
    IConstructionAuditService audit, ICurrencyRounding rounding) : IConstructionBudgetVersionService
{
    private const decimal Limit = 100000000000000000000m; // decimal(28,8) exclusive upper bound.
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 1000;
    private static bool Match(byte[]? submitted, byte[] current) => submitted is { Length: > 0 } && submitted.SequenceEqual(current);
    private static StructureResult Refuse(string code) => new(false, code);
    private async Task<bool> Allowed(BusinessContext c, int project, string action, CancellationToken ct)
        => c.IsAuthenticated && c.EmployeeId is > 0 && c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId
           && await access.CanAsync(c, action, new PermissionTarget { ProjectId = project }, ct)
           && await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);

    public async Task<ConstructionBudgetDocument?> GetAsync(BusinessContext c, int projectId, int versionId, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ProjectsActions.BudgetView, ct)) return null;
        var header = await db.Set<ConstructionBudgetVersion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ID == versionId && x.ProjectId == projectId && x.CompanyID == c.CompanyId, ct);
        if (header == null) return null;
        return new(header, await db.Set<ConstructionBudgetLine>().AsNoTracking()
            .Where(x => x.BudgetVersionId == versionId && x.ProjectId == projectId && x.CompanyID == c.CompanyId)
            .OrderBy(x => x.WbsNodeId).ThenBy(x => x.CostCodeId).ToListAsync(ct));
    }

    private void Rotate(object entity)
    {
        if (!db.Database.IsSqlServer()) db.Entry(entity).Property("RowVersion").CurrentValue = Guid.NewGuid().ToByteArray();
    }
    private void Record(BusinessContext c, int project, int id, string action, object? before, object after, string reason)
        => audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, project, nameof(ConstructionBudgetVersion), id,
            null, action, before == null ? null : JsonSerializer.Serialize(before), JsonSerializer.Serialize(after),
            before == null ? ConstructionChangeKind.Created : ConstructionChangeKind.Updated, reason, c.EmployeeId,
            nameof(ConstructionBudgetVersionService), Guid.NewGuid()) });

    private async Task<StructureResult> Execute(BusinessContext c, int project, string reason,
        Func<Task<StructureResult>> command, CancellationToken ct)
    {
        if (!await Allowed(c, project, ProjectsActions.BudgetManage, ct)) return Refuse("AccessDenied");
        if (!Text(reason)) return Refuse("ReasonRequired");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return Refuse("CleanUnitOfWorkRequired");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            // Baseline changes participate in the same project lock as admission and transfer
            // exposure reads. Acquire it before reading any policy or budget rows to preserve
            // one lock order; serializable row locks alone do not coordinate these workflows.
            if (!await ConstructionCommitmentReservationService.AcquireProjectLock(db, c, project, ct))
                return Refuse("ConcurrencyConflict");
            var policy = await db.Set<ConstructionBudgetPolicy>().AsNoTracking()
                .Where(x => x.CompanyID == c.CompanyId).Select(x => x.Mode).SingleOrDefaultAsync(ct);
            var result = ConstructionBudgetModes.IsKnown(policy) ? await command() : Refuse("PolicyNotConfigured");
            if (result.Ok) await tx.CommitAsync(ct);
            else { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); }
            return result;
        }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Refuse("ConcurrencyConflict"); }
        catch (Exception ex) when (SqlRefusal(ex) != null)
        { db.ChangeTracker.Clear(); return Refuse(SqlRefusal(ex)!); }
    }
    private static string? SqlRefusal(Exception exception)
    {
        for (Exception? ex = exception; ex != null; ex = ex.InnerException)
            if (ex is Microsoft.Data.SqlClient.SqlException sql) return sql.Number switch
            {
                1205 or 1222 or 2601 or 2627 => "ConcurrencyConflict",
                547 => "InvalidReference", 51201 => "ImmutableBudget", 51202 => "InvalidBudgetTransition", _ => null
            };
        return null;
    }
    private Task<ConstructionBudgetVersion?> Header(BusinessContext c, int project, int id, CancellationToken ct)
        => db.Set<ConstructionBudgetVersion>().SingleOrDefaultAsync(x => x.ID == id && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);

    internal async Task<string?> ValidateStoredLines(BusinessContext c, ConstructionBudgetVersion header, CancellationToken ct)
    {
        var lines = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == header.ProjectId && x.BudgetVersionId == header.ID).ToListAsync(ct);
        if (lines.Count == 0) return "BudgetHasNoLines";
        var nodeIds = lines.Select(x => x.WbsNodeId).ToHashSet(); var codeIds = lines.Select(x => x.CostCodeId).ToHashSet();
        var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == header.ProjectId && nodeIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
        var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && codeIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
        if (!await db.Currencies.AsNoTracking().AnyAsync(x => x.ID == header.CurrencyId && x.DecimalPlaces <= 8, ct)) return "InvalidCurrencyPrecision";
        decimal total = 0;
        foreach (var line in lines)
        {
            if (!nodes.TryGetValue(line.WbsNodeId, out var node) || !node.OwnsBudget || node.Status is "Archived" or "Completed"
                || !codes.TryGetValue(line.CostCodeId, out var code) || !code.IsActive || !code.AllowDirectPosting || code.Category != line.CostCategory)
                return "InvalidBudgetDimension";
            if (line.Amount < 0 || await rounding.RoundAsync(c.CompanyId, line.Amount, header.CurrencyId) != line.Amount) return "InvalidCurrencyPrecision";
            total += line.Amount; if (total >= Limit) return "BudgetAmountTooLarge";
        }
        return null;
    }

    public Task<StructureResult> CreateAsync(BusinessContext c, int projectId, string kind, int? previousVersionId,
        byte[]? previousVersion, string reason, CancellationToken ct = default) => Execute(c, projectId, reason, async () =>
    {
        if (kind is not ("Original" or "Revised" or "Scenario")) return Refuse("InvalidBudgetKind");
        var currency = await db.Companies.AsNoTracking().Where(x => x.CompanyID == c.CompanyId)
            .Select(x => x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
        if (currency is not > 0 || !await db.Currencies.AsNoTracking().AnyAsync(x => x.ID == currency && x.DecimalPlaces <= 8, ct))
            return Refuse("CompanyCurrencyNotConfigured");
        ConstructionBudgetVersion? previous = null;
        if (previousVersionId.HasValue)
        {
            previous = await Header(c, projectId, previousVersionId.Value, ct);
            if (previous == null || previous.Status is not ("Approved" or "Superseded")) return Refuse("InvalidPreviousBudget");
            if (!Match(previousVersion, previous.RowVersion)) return Refuse("ConcurrencyConflict");
            if (previous.CurrencyId != currency) return Refuse("BudgetCurrencyChanged");
        }
        if (kind == "Revised" && previous == null || kind == "Original" && previous != null) return Refuse("InvalidPreviousBudget");
        var numbers = db.Set<ConstructionBudgetVersion>().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId);
        if (kind == "Original" && await numbers.AnyAsync(x => x.Kind == "Original", ct)) return Refuse("OriginalBudgetAlreadyExists");
        var next = (await numbers.Select(x => (int?)x.VersionNo).MaxAsync(ct) ?? 0) + 1;
        var header = new ConstructionBudgetVersion { CompanyID = c.CompanyId, ProjectId = projectId, VersionNo = next,
            PreviousVersionId = previous?.ID, CurrencyId = currency.Value, Kind = kind, Reason = reason.Trim(),
            CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
        db.Add(header); await db.SaveChangesAsync(ct);
        if (previous != null)
        {
            var oldLines = await db.Set<ConstructionBudgetLine>().AsNoTracking()
                .Where(x => x.BudgetVersionId == previous.ID && x.ProjectId == projectId && x.CompanyID == c.CompanyId).ToListAsync(ct);
            db.AddRange(oldLines.Select(x => new ConstructionBudgetLine { CompanyID = c.CompanyId, ProjectId = projectId,
                BudgetVersionId = header.ID, WbsNodeId = x.WbsNodeId, CostCodeId = x.CostCodeId, CostCategory = x.CostCategory,
                Amount = x.Amount, PreviousAmount = x.Amount, Quantity = x.Quantity, Rate = x.Rate, IsContingency = x.IsContingency,
                ChangeReason = reason.Trim() }));
        }
        Record(c, projectId, header.ID, "CreateVersion", null, new { header.VersionNo, header.Kind, header.PreviousVersionId, header.CurrencyId }, reason);
        await db.SaveChangesAsync(ct);
        if (db.Database.IsSqlServer()) await db.Entry(header).ReloadAsync(ct);
        return new(true, "Saved", header.ID, header.RowVersion.ToArray());
    }, ct);

    public Task<StructureResult> SaveDraftAsync(BusinessContext c, int projectId, int versionId, byte[] expectedVersion,
        IReadOnlyList<ConstructionBudgetDraftLine> inputs, string reason, CancellationToken ct = default) => Execute(c, projectId, reason, async () =>
    {
        var header = await Header(c, projectId, versionId, ct);
        if (header == null) return Refuse("BudgetNotFound");
        if (!Match(expectedVersion, header.RowVersion)) return Refuse("ConcurrencyConflict");
        if (header.Status != "Draft") return Refuse("ImmutableBudget");
        if (!await db.Currencies.AsNoTracking().AnyAsync(x => x.ID == header.CurrencyId && x.DecimalPlaces <= 8, ct))
            return Refuse("InvalidCurrencyPrecision");
        if (inputs == null || inputs.Count > 10000 || inputs.Any(x => x == null) || inputs.Select(x => (x.WbsNodeId, x.CostCodeId)).Distinct().Count() != inputs.Count)
            return Refuse("InvalidBudgetLines");
        var before = await db.Set<ConstructionBudgetLine>().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.BudgetVersionId == versionId).ToListAsync(ct);
        var beforeAudit = before.Select(x => new { x.WbsNodeId, x.CostCodeId, x.Amount }).ToArray();
        var historicalAmounts = header.PreviousVersionId.HasValue
            ? await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.BudgetVersionId == header.PreviousVersionId)
                .ToDictionaryAsync(x => new { x.WbsNodeId, x.CostCodeId }, x => x.Amount, ct)
            : null;
        var nodeIds = inputs.Select(x => x.WbsNodeId).ToHashSet(); var codeIds = inputs.Select(x => x.CostCodeId).ToHashSet();
        var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && nodeIds.Contains(x.ID))
            .ToDictionaryAsync(x => x.ID, ct);
        var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && codeIds.Contains(x.ID))
            .ToDictionaryAsync(x => x.ID, ct);
        var planned = new List<(ConstructionBudgetDraftLine Input, ConstructionCostCode Code)>();
        decimal total = 0;
        foreach (var input in inputs)
        {
            if (!nodes.TryGetValue(input.WbsNodeId, out var node) || !node.OwnsBudget || node.Status is "Archived" or "Completed"
                || !codes.TryGetValue(input.CostCodeId, out var code) || !code.IsActive || !code.AllowDirectPosting) return Refuse("InvalidBudgetDimension");
            if (!Text(input.ChangeReason) || input.Amount < 0 || input.Amount >= Limit || input.Quantity is < 0 || input.Rate is < 0
                || input.Quantity >= Limit || input.Rate >= Limit || input.Quantity.HasValue != input.Rate.HasValue
                || input.IsContingency != (code.Category == "Contingency")) return Refuse("InvalidBudgetLines");
            if (await rounding.RoundAsync(c.CompanyId, input.Amount, header.CurrencyId) != input.Amount) return Refuse("InvalidCurrencyPrecision");
            // Optional quantities/rates are not monetary balances; refuse storage truncation.
            if (input.Quantity.HasValue && (decimal.Round(input.Quantity.Value, 8) != input.Quantity.Value || decimal.Round(input.Rate!.Value, 8) != input.Rate.Value))
                return Refuse("InvalidBudgetLines");
            total += input.Amount; if (total >= Limit) return Refuse("BudgetAmountTooLarge");
            if (input.Quantity.HasValue)
            {
                decimal product;
                try { product = checked(input.Quantity.Value * input.Rate!.Value); }
                catch (OverflowException) { return Refuse("BudgetAmountTooLarge"); }
                if (product >= Limit || await rounding.RoundAsync(c.CompanyId, product, header.CurrencyId) != input.Amount) return Refuse("BudgetAmountMismatch");
            }
            planned.Add((input, code));
        }
        // Claim the displayed header version before any line trigger rotates it. The later line/audit
        // save remains in this same transaction, so a failure also rolls back this claim.
        header.Reason = reason.Trim(); Rotate(header);
        db.Entry(header).Property(x => x.Reason).IsModified = true;
        await db.SaveChangesAsync(ct);
        // Draft lines alone may be removed. Approved/submitted line sets never reach this branch.
        var wanted = planned.Select(x => (x.Input.WbsNodeId, x.Input.CostCodeId)).ToHashSet();
        db.RemoveRange(before.Where(x => !wanted.Contains((x.WbsNodeId, x.CostCodeId))));
        foreach (var (input, code) in planned)
        {
            var line = before.SingleOrDefault(x => x.WbsNodeId == input.WbsNodeId && x.CostCodeId == input.CostCodeId);
            if (line == null) { line = new() { CompanyID = c.CompanyId, ProjectId = projectId, BudgetVersionId = versionId,
                WbsNodeId = input.WbsNodeId, CostCodeId = input.CostCodeId, CostCategory = code.Category,
                PreviousAmount = historicalAmounts != null && historicalAmounts.TryGetValue(new { input.WbsNodeId, input.CostCodeId }, out var oldAmount) ? oldAmount : null }; db.Add(line); }
            line.Amount = input.Amount; line.Quantity = input.Quantity; line.Rate = input.Rate;
            line.IsContingency = input.IsContingency; line.ChangeReason = input.ChangeReason.Trim(); Rotate(line);
        }
        Record(c, projectId, versionId, "BudgetLines", beforeAudit, inputs, reason);
        await db.SaveChangesAsync(ct);
        if (db.Database.IsSqlServer()) await db.Entry(header).ReloadAsync(ct);
        return new(true, "Saved", versionId, header.RowVersion.ToArray());
    }, ct);

    public Task<StructureResult> SubmitAsync(BusinessContext c, int projectId, int versionId, byte[] expectedVersion, string reason, CancellationToken ct = default)
        => Execute(c, projectId, reason, async () =>
    {
        var header = await Header(c, projectId, versionId, ct);
        if (header == null) return Refuse("BudgetNotFound");
        if (!Match(expectedVersion, header.RowVersion)) return Refuse("ConcurrencyConflict");
        if (header.Status != "Draft") return Refuse("InvalidBudgetTransition");
        var invalid = await ValidateStoredLines(c, header, ct); if (invalid != null) return Refuse(invalid);
        header.Status = "Submitted"; header.SubmittedBy = c.EmployeeId; header.SubmittedAt = DateTime.UtcNow; header.Reason = reason.Trim(); Rotate(header);
        Record(c, projectId, versionId, "SubmitBudget", "Draft", "Submitted", reason);
        await db.SaveChangesAsync(ct); return new(true, "Saved", versionId, header.RowVersion.ToArray());
    }, ct);

    private async Task<bool> RedistributesAllocations(BusinessContext c,
        ConstructionBudgetVersion proposed, int baselineId, CancellationToken ct)
    {
        var rows = await db.Set<ConstructionBudgetLine>().AsNoTracking()
            .Where(x => x.CompanyID == c.CompanyId && x.ProjectId == proposed.ProjectId
                && (x.BudgetVersionId == baselineId || x.BudgetVersionId == proposed.ID))
            .ToListAsync(ct);
        // Include contingency classification: moving an amount into/out of a reserve
        // must not disappear simply because its WBS and cost code remain the same.
        var old = rows.Where(x => x.BudgetVersionId == baselineId)
            .ToDictionary(x => (x.WbsNodeId, x.CostCodeId, x.IsContingency), x => x.Amount);
        var next = rows.Where(x => x.BudgetVersionId == proposed.ID)
            .ToDictionary(x => (x.WbsNodeId, x.CostCodeId, x.IsContingency), x => x.Amount);
        bool decrease = false, increase = false;
        foreach (var key in old.Keys.Union(next.Keys))
        {
            var from = old.GetValueOrDefault(key);
            var to = next.GetValueOrDefault(key);
            decrease |= to < from;
            increase |= to > from;
        }
        return decrease && increase;
    }

    public Task<StructureResult> ApproveAsync(BusinessContext c, int projectId, int versionId, byte[] expectedVersion, string reason, CancellationToken ct = default)
        => Execute(c, projectId, reason, async () =>
    {
        var header = await Header(c, projectId, versionId, ct);
        if (header == null) return Refuse("BudgetNotFound");
        if (!Match(expectedVersion, header.RowVersion)) return Refuse("ConcurrencyConflict");
        if (header.Status != "Submitted") return Refuse("InvalidBudgetTransition");
        if (header.CreatedBy == c.EmployeeId || header.SubmittedBy == c.EmployeeId) return Refuse("SeparateApproverRequired");
        var invalid = await ValidateStoredLines(c, header, ct); if (invalid != null) return Refuse(invalid);
        if (header.Kind != "Scenario")
        {
            var previous = await db.Set<ConstructionBudgetVersion>().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId && x.IsBaseline, ct);
            if (previous != null)
            {
                if (header.PreviousVersionId != previous.ID || previous.CurrencyId != header.CurrencyId) return Refuse("BaselineChanged");
                // A revised estimate must not act as a transfer command. Check the full
                // scoped allocation vector under the already-held serializable project lock,
                // before changing either baseline or audit. A net increase/decrease does not
                // cancel a simultaneous movement from one allocation into another.
                if (await RedistributesAllocations(c, header, previous.ID, ct))
                    return Refuse("BudgetTransferWorkflowRequired");
                previous.IsBaseline = false; previous.Status = "Superseded"; Rotate(previous);
                Record(c, projectId, previous.ID, "SupersedeBudget", "Approved", new { Status = "Superseded", ReplacementId = header.ID }, reason);
                // Release the unique filtered baseline slot before acquiring it, inside this transaction.
                await db.SaveChangesAsync(ct);
            }
            header.IsBaseline = true;
        }
        header.Status = "Approved"; header.ApprovedBy = c.EmployeeId; header.ApprovedAt = DateTime.UtcNow; header.Reason = reason.Trim(); Rotate(header);
        Record(c, projectId, versionId, "ApproveBudget", "Submitted", new { Status = header.Status, header.IsBaseline }, reason);
        await db.SaveChangesAsync(ct); return new(true, "Saved", versionId, header.RowVersion.ToArray());
    }, ct);
}
