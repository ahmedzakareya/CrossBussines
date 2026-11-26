using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionActualSplit(int WbsNodeId, int CostCodeId, decimal NetBaseAmount);
public sealed record ConstructionActualPreview(bool Ok, string Code, string? Version = null, int? CurrencyId = null, decimal? NetBaseAmount = null);
public sealed record ConstructionActualWrite(bool Ok, string Code, int? Id = null);
public sealed record ConstructionActualCoverage(bool Ok, string Code, IReadOnlyList<ConstructionSourceIssue> Issues,
    IReadOnlyDictionary<int, decimal>? NetActualsByCurrency);
internal sealed record ConstructionActualDimension(int EntryId, int JournalLineId, int WbsNodeId, int CostCodeId,
    string CostCategory, int CurrencyId, decimal Amount);
public interface IConstructionActualCostService
{
    Task<ConstructionActualPreview> PreviewAsync(BusinessContext context, int projectId, int journalLineId, CancellationToken ct = default);
    Task<ConstructionActualWrite> AllocateAsync(BusinessContext context, int projectId, int journalLineId, string expectedVersion,
        Guid requestId, IReadOnlyList<ConstructionActualSplit> splits, string reason, CancellationToken ct = default);
    Task<ConstructionActualCoverage> CoverageAsync(BusinessContext context, int projectId, DateTime? from = null, DateTime? through = null, CancellationToken ct = default);
}

// Analytical attribution only. JournalEntryService remains the sole GL writer. No defaults, FX inference,
// invoice gross-as-expense, matching by note text, historical backfill or implicit zero for incomplete coverage.
public sealed class ConstructionActualCostService(CrossDbContext db, IProjectsAccessService access, IConstructionAuditService audit) : IConstructionActualCostService
{
    private sealed record Source(int EntryId, int LineId, int AccountId, DateTime Date, int PeriodId, int? Currency,
        int? Decimals, decimal Debit, decimal Credit, string Classification, string Version);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private async Task<bool> Allowed(BusinessContext c, int project, string action, CancellationToken ct) => c.IsAuthenticated && c.EmployeeId is > 0 &&
        c.CompanyId > 0 && db.CompanyScope.FilterCompanyId == c.CompanyId && await access.CanAsync(c, action, new PermissionTarget { ProjectId = project }, ct) &&
        await db.Projects.AsNoTracking().AnyAsync(x => x.ID == project && x.CompanyID == c.CompanyId, ct);
    private async Task<List<Source>> Sources(BusinessContext c, int project, int? lineId, DateTime? from, DateTime? through, CancellationToken ct)
    {
        var query = from entry in db.JournalEntries.AsNoTracking()
                    join line in db.JournalEntryLines.AsNoTracking() on entry.ID equals line.JournalEntryId
                    where entry.CompanyID == c.CompanyId && line.ProjectId == project && (entry.Status == "Posted" || entry.Status == "Reversed")
                    join account in db.Accounts.AsNoTracking().Where(x => x.CompanyID == c.CompanyId) on line.AccountId equals account.ID into accounts
                    from account in accounts.DefaultIfEmpty()
                    join type in db.AccountTypes.AsNoTracking() on account.AccountTypeId equals type.ID into types
                    from type in types.DefaultIfEmpty()
                    select new { Entry = entry, Line = line, Type = type == null ? null : type.Code, Statement = type == null ? null : type.StatementType };
        if (lineId.HasValue) query = query.Where(x => x.Line.ID == lineId);
        if (from.HasValue) query = query.Where(x => x.Entry.EntryDate >= from.Value.Date);
        if (through.HasValue) query = query.Where(x => x.Entry.EntryDate.Date <= through.Value.Date);
        var raw = await query.OrderBy(x => x.Line.ID).ToListAsync(ct);
        var currencies = await db.Currencies.AsNoTracking().ToDictionaryAsync(x => x.ID, x => x.Code, ct);
        return raw.Select(x =>
        {
            var classification = x.Type == "EXP" && x.Statement == "IncomeStatement" ? "ExpensePosting"
                : (x.Type is "ASSET" or "LIAB" or "EQUITY") && x.Statement == "BalanceSheet" || x.Type == "REV" && x.Statement == "IncomeStatement" ? "NotExpense" : "AccountClassificationNotProven";
            var currency = x.Entry.FunctionalCurrencyId is > 0 && currencies.TryGetValue(x.Entry.FunctionalCurrencyId.Value, out var code) && !string.IsNullOrWhiteSpace(code) &&
                x.Entry.FunctionalCurrencyDecimals is >= 0 and <= 8 ? x.Entry.FunctionalCurrencyId : null;
            var version = Hash(new { Company = c.CompanyId, Project = project, Entry = x.Entry.ID, x.Entry.EntryDate, x.Entry.FiscalPeriodId,
                x.Entry.SourceType, x.Entry.SourceId, x.Entry.FunctionalCurrencyId, x.Entry.FunctionalCurrencyDecimals,
                Line = x.Line.ID, x.Line.LineNo, x.Line.AccountId, x.Line.Debit, x.Line.Credit, x.Line.ProjectId, x.Type, x.Statement });
            // Posted -> Reversed changes lifecycle, not the original line's financial amount or period.
            return new Source(x.Entry.ID, x.Line.ID, x.Line.AccountId, x.Entry.EntryDate, x.Entry.FiscalPeriodId,
                currency, currency.HasValue ? x.Entry.FunctionalCurrencyDecimals : null, x.Line.Debit, x.Line.Credit, classification, version);
        }).ToList();
    }
    private static string? Invalid(Source? source)
    {
        if (source == null) return "JournalSourceNotFound";
        if (source.Classification != "ExpensePosting") return source.Classification == "NotExpense" ? "SourceNotExpense" : source.Classification;
        if (source.Currency == null) return "HistoricalBaseCurrencyIdentityNotProven";
        if (source.Debit < 0 || source.Credit < 0 || (source.Debit > 0) == (source.Credit > 0)) return "InvalidJournalAmounts";
        return null;
    }
    public async Task<ConstructionActualPreview> PreviewAsync(BusinessContext c, int projectId, int journalLineId, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ProjectsActions.BudgetManage, ct)) return new(false, "AccessDenied");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var source = (await Sources(c, projectId, journalLineId, null, null, ct)).SingleOrDefault(); var error = Invalid(source);
        await tx.CommitAsync(ct);
        return error == null ? new(true, "Ready", source!.Version, source.Currency, source.Debit - source.Credit) : new(false, error);
    }

    public async Task<ConstructionActualWrite> AllocateAsync(BusinessContext c, int projectId, int journalLineId, string expectedVersion,
        Guid requestId, IReadOnlyList<ConstructionActualSplit> splits, string reason, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ProjectsActions.BudgetManage, ct)) return new(false, "AccessDenied");
        if (requestId == Guid.Empty || expectedVersion?.Length != 64 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000 || splits.Count == 0 || splits.Count > 10000)
            return new(false, "InvalidAllocationRequest");
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired");
        var input = splits.OrderBy(x => x.WbsNodeId).ThenBy(x => x.CostCodeId).ToArray();
        var requestHash = Hash(new { projectId, journalLineId, expectedVersion, Splits = input, Reason = reason.Trim() });
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var previous = await db.Set<ConstructionActualCostAllocation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyID == c.CompanyId && x.RequestId == requestId, ct);
            if (previous != null)
            {
                if (previous.ProjectId != projectId || previous.RequestHash != requestHash || !previous.IsSealed) return new(false, "IdempotencyConflict");
                await tx.CommitAsync(ct); return new(true, "AlreadyAllocated", previous.ID);
            }
            var source = (await Sources(c, projectId, journalLineId, null, null, ct)).SingleOrDefault(); var error = Invalid(source);
            if (error != null) return new(false, error);
            if (source!.Version != expectedVersion) return new(false, "ConcurrencyConflict");
            if (await db.Set<ConstructionActualCostAllocation>().AnyAsync(x => x.CompanyID == c.CompanyId && x.JournalEntryLineId == journalLineId, ct)) return new(false, "SourceAlreadyAllocated");
            var net = source.Debit - source.Credit;
            if (input.GroupBy(x => (x.WbsNodeId, x.CostCodeId)).Any(x => x.Count() != 1) || input.Any(x =>
                Math.Sign(x.NetBaseAmount) != Math.Sign(net) || Math.Abs(x.NetBaseAmount) >= 100000000000000000000m ||
                decimal.Round(x.NetBaseAmount, 8, MidpointRounding.AwayFromZero) != x.NetBaseAmount) || input.Sum(x => x.NetBaseAmount) != net)
                return new(false, "ActualAllocationDoesNotReconcile");
            var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToDictionaryAsync(x => x.ID, ct);
            var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId).ToDictionaryAsync(x => x.ID, ct);
            if (input.Any(x => !nodes.TryGetValue(x.WbsNodeId, out var node) || !node.OwnsCost || node.Status == "Archived" ||
                !codes.TryGetValue(x.CostCodeId, out var code) || !code.IsActive || !code.AllowDirectPosting ||
                code.DefaultAccountId.HasValue && code.DefaultAccountId != source.AccountId)) return new(false, "InvalidActualCostDimension");
            var header = new ConstructionActualCostAllocation { CompanyID = c.CompanyId, ProjectId = projectId, JournalEntryId = source.EntryId,
                JournalEntryLineId = source.LineId, FunctionalCurrencyId = source.Currency!.Value, FunctionalCurrencyDecimals = source.Decimals!.Value,
                NetBaseAmount = net, RequestId = requestId, RequestHash = requestHash, SourceVersion = expectedVersion,
                Reason = reason.Trim(), CreatedBy = c.EmployeeId!.Value, CreatedAt = DateTime.UtcNow };
            db.Add(header); await db.SaveChangesAsync(ct);
            db.AddRange(input.Select(x => new ConstructionActualCostAllocationLine { CompanyID = c.CompanyId, ProjectId = projectId,
                AllocationId = header.ID, WbsNodeId = x.WbsNodeId, CostCodeId = x.CostCodeId, CostCategory = codes[x.CostCodeId].Category, NetBaseAmount = x.NetBaseAmount }));
            await db.SaveChangesAsync(ct); header.IsSealed = true;
            audit.Record(new[] { ConstructionAuditDraft.Text(c.CompanyId, projectId, nameof(ConstructionActualCostAllocation), header.ID,
                null, "AllocateActualCost", null, JsonSerializer.Serialize(new { source.EntryId, source.LineId, header.FunctionalCurrencyId, header.NetBaseAmount, Splits = input }),
                ConstructionChangeKind.Created, reason.Trim(), c.EmployeeId, nameof(ConstructionActualCostService), requestId) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, "Allocated", header.ID);
        }
        catch (OverflowException) { db.ChangeTracker.Clear(); return new(false, "AmountOverflow"); }
        catch (Exception ex) when (Conflict(ex)) { db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict"); }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<ConstructionActualCoverage> CoverageAsync(BusinessContext c, int projectId, DateTime? from = null, DateTime? through = null, CancellationToken ct = default)
    {
        if (!await Allowed(c, projectId, ProjectsActions.BudgetView, ct)) return new(false, "AccessDenied", Array.Empty<ConstructionSourceIssue>(), null);
        if (from.HasValue && through.HasValue && from.Value.Date > through.Value.Date) return new(false, "InvalidPeriod", Array.Empty<ConstructionSourceIssue>(), null);
        if (db.Database.CurrentTransaction != null || db.ChangeTracker.HasChanges()) return new(false, "CleanUnitOfWorkRequired", Array.Empty<ConstructionSourceIssue>(), null);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await ReadCoverageLocked(c, projectId, from, through, ct);
        await tx.CommitAsync(ct);
        return result.Coverage;
    }

    // Used by budget admission inside its own serializable transaction. No nested transaction,
    // second authorization gateway or caller-supplied totals. The public reader keeps its existing guard.
    internal async Task<(ConstructionActualCoverage Coverage, IReadOnlyList<ConstructionActualDimension> Dimensions, string EvidenceVersion)> ReadCoverageLocked(
        BusinessContext c, int projectId, DateTime? from, DateTime? through, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("A budget coverage transaction is required");
        var sources = await Sources(c, projectId, null, from, through, ct);
        var headers = await db.Set<ConstructionActualCostAllocation>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToListAsync(ct);
        var splits = await db.Set<ConstructionActualCostAllocationLine>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == projectId).ToListAsync(ct);
        var issues = new List<ConstructionSourceIssue>(); var totals = new Dictionary<int, decimal>();
        var dimensions = new List<ConstructionActualDimension>();
        var allSources = from.HasValue || through.HasValue ? await Sources(c, projectId, null, null, null, ct) : sources;
        foreach (var header in headers)
        {
            var current = allSources.SingleOrDefault(x => x.LineId == header.JournalEntryLineId);
            if (current == null) issues.Add(new("ActualAllocationSourceMissing", "JournalEntryLine", header.JournalEntryLineId));
            else if (current.Classification == "NotExpense") issues.Add(new("ActualAllocationSourceChanged", "JournalEntryLine", header.JournalEntryLineId));
        }
        foreach (var source in sources)
        {
            if (source.Classification == "NotExpense") continue;
            var error = Invalid(source);
            var header = headers.SingleOrDefault(x => x.JournalEntryLineId == source.LineId);
            if (error == null && (header == null || !header.IsSealed)) error = "ActualAllocationMissing";
            if (error == null && (header!.SourceVersion != source.Version || header.NetBaseAmount != source.Debit - source.Credit ||
                header.FunctionalCurrencyId != source.Currency || header.FunctionalCurrencyDecimals != source.Decimals)) error = "ActualAllocationSourceChanged";
            var own = header == null ? new List<ConstructionActualCostAllocationLine>() : splits.Where(x => x.AllocationId == header.ID).ToList();
            if (error == null && (own.Count == 0 || own.Sum(x => x.NetBaseAmount) != source.Debit - source.Credit)) error = "ActualAllocationDoesNotReconcile";
            if (error == null && (own.Select(x => (x.WbsNodeId, x.CostCodeId)).Distinct().Count() != own.Count ||
                own.Any(x => Math.Sign(x.NetBaseAmount) != Math.Sign(source.Debit - source.Credit)))) error = "ActualAllocationDoesNotReconcile";
            if (error != null) { issues.Add(new(error, "JournalEntryLine", source.LineId)); continue; }
            totals[source.Currency!.Value] = totals.GetValueOrDefault(source.Currency.Value) + source.Debit - source.Credit;
            dimensions.AddRange(own.Select(x => new ConstructionActualDimension(source.EntryId, source.LineId, x.WbsNodeId,
                x.CostCodeId, x.CostCategory, source.Currency.Value, x.NetBaseAmount)));
        }
        return (new(true, issues.Count == 0 ? "CompleteExpenseLedgerCoverage" : "IncompleteExpenseLedgerCoverage", issues.AsReadOnly(),
            issues.Count == 0 ? new System.Collections.ObjectModel.ReadOnlyDictionary<int, decimal>(totals) : null),
            issues.Count == 0 ? dimensions.AsReadOnly() : Array.Empty<ConstructionActualDimension>(),
            Hash(new { Sources = allSources.OrderBy(x => x.LineId).Select(x => new { x.EntryId, x.LineId, x.Version }),
                Allocations = headers.OrderBy(x => x.ID), Splits = splits.OrderBy(x => x.ID) }));
    }
    private static bool Conflict(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
            if (e is DbUpdateConcurrencyException || e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 1205 or 1222 or 2601 or 2627) return true;
        return false;
    }
}
