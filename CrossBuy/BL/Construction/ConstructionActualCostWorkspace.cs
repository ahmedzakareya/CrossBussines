using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public interface IConstructionActualCostWorkspace
{
    Task<ConstructionActualCostPage?> ReadAsync(int project, int? line, CancellationToken ct = default);
    Task<ConstructionActualPreview> PreviewAsync(int project, int line, ConstructionActualInput input, CancellationToken ct = default);
    Task<ConstructionActualWrite> AllocateAsync(int project, int line, ConstructionActualInput input, CancellationToken ct = default);
}
// Browser boundary only. Attribution delegates to the qualified analytical writer; no GL or stock posting.
public sealed class ConstructionActualCostWorkspace(CrossDbContext db, IBusinessContextAccessor contexts,
    IProjectsAccessService projects, IConstructionActualCostService actuals) : IConstructionActualCostWorkspace
{
    private async Task<BusinessContext?> Actor(int project, CancellationToken ct)
    {
        var c = await contexts.TryGetCurrentAsync(ct);
        return c is { IsAuthenticated: true, IsExternalPortal: false, EmployeeId: > 0, CompanyId: > 0 } &&
            db.CompanyScope.FilterCompanyId == c.CompanyId && !db.CompanyScope.AllowsCrossCompany &&
            await projects.CanAsync(c, ProjectsActions.BudgetManage, new PermissionTarget { ProjectId = project }, ct) &&
            await db.Projects.AsNoTracking().AnyAsync(x => x.CompanyID == c.CompanyId && x.ID == project, ct) ? c : null;
    }
    public async Task<ConstructionActualCostPage?> ReadAsync(int project, int? line, CancellationToken ct = default)
    {
        var c = await Actor(project, ct); if (c == null) return null;
        var p = await db.Projects.AsNoTracking().SingleAsync(x => x.CompanyID == c.CompanyId && x.ID == project, ct);
        var query = from entry in db.JournalEntries.AsNoTracking()
            join row in db.JournalEntryLines.AsNoTracking() on entry.ID equals row.JournalEntryId
            join account in db.Accounts.AsNoTracking() on row.AccountId equals account.ID
            join type in db.AccountTypes.AsNoTracking() on account.AccountTypeId equals type.ID
            join currency in db.Currencies.AsNoTracking() on entry.FunctionalCurrencyId equals (int?)currency.ID into currencies
            from currency in currencies.DefaultIfEmpty()
            where entry.CompanyID == c.CompanyId && account.CompanyID == c.CompanyId && row.ProjectId == project &&
                (entry.Status == "Posted" || entry.Status == "Reversed") && type.Code == "EXP" && type.StatementType == "IncomeStatement"
            orderby row.ID descending
            select new ConstructionActualSourceRow(row.ID, entry.ID, entry.EntryDate, row.Debit - row.Credit,
                currency == null ? null : currency.Code, db.Set<ConstructionActualCostAllocation>().Any(x =>
                    x.CompanyID == c.CompanyId && x.ProjectId == project && x.JournalEntryLineId == row.ID));
        var rows = await query.Take(101).ToListAsync(ct);
        if (line.HasValue && !await (from row in db.JournalEntryLines.AsNoTracking()
            join entry in db.JournalEntries.AsNoTracking() on row.JournalEntryId equals entry.ID
            join account in db.Accounts.AsNoTracking() on row.AccountId equals account.ID
            join type in db.AccountTypes.AsNoTracking() on account.AccountTypeId equals type.ID
            where row.ID == line && row.ProjectId == project && entry.CompanyID == c.CompanyId && account.CompanyID == c.CompanyId &&
                (entry.Status == "Posted" || entry.Status == "Reversed") && type.Code == "EXP" && type.StatementType == "IncomeStatement"
            select row.ID).AnyAsync(ct)) return null;
        var allocated = line.HasValue && await db.Set<ConstructionActualCostAllocation>().AsNoTracking().AnyAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.JournalEntryLineId == line, ct);
        var source = line.HasValue ? await actuals.PreviewAsync(c, project, line.Value, ct) : null;
        var nodes = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.OwnsCost && x.Status != "Archived").OrderBy(x => x.Code).Select(x => new ConstructionReferenceOption(x.ID, x.Code, x.Name, x.NameEn)).ToListAsync(ct);
        var codes = await db.Set<ConstructionCostCode>().AsNoTracking().Where(x => x.CompanyID == c.CompanyId && x.IsActive && x.AllowDirectPosting)
            .OrderBy(x => x.Code).Select(x => new ConstructionReferenceOption(x.ID, x.Code, x.Name, x.NameEn)).ToListAsync(ct);
        var sourceCurrency = source?.CurrencyId is int currencyId ? await db.Currencies.AsNoTracking().Where(x => x.ID == currencyId).Select(x => x.Code).SingleOrDefaultAsync(ct) : null;
        return new(project, p.Code, p.Name, p.NameEn, rows.Take(100).ToArray(), rows.Count > 100, line, allocated, source, nodes, codes,
            new() { RequestId = Guid.NewGuid(), Rows = Enumerable.Range(0, 8).Select(_ => new ConstructionActualInputRow()).ToList() }, SourceCurrency: sourceCurrency);
    }
    private static ConstructionActualSplit[]? Splits(ConstructionActualInput input)
    {
        if (input.Rows is not { Count: > 0 and <= 500 } || input.Rows.Any(x => x == null)) return null;
        var rows = input.Rows.Where(x => x.WbsNodeId != 0 || x.CostCodeId != 0 || x.Amount.GetValueOrDefault() != 0).ToArray();
        if (rows.Length == 0 || rows.Any(x => x.WbsNodeId <= 0 || x.CostCodeId <= 0 || x.Amount.GetValueOrDefault() == 0) ||
            rows.Select(x => (x.WbsNodeId, x.CostCodeId)).Distinct().Count() != rows.Length) return null;
        return rows.Select(x => new ConstructionActualSplit(x.WbsNodeId, x.CostCodeId, x.Amount.GetValueOrDefault())).ToArray();
    }
    public async Task<ConstructionActualPreview> PreviewAsync(int project, int line, ConstructionActualInput input, CancellationToken ct = default)
    {
        var c = await Actor(project, ct); if (c == null) return new(false, "AccessDenied");
        var splits = Splits(input); if (splits == null || input.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 1000)
            return new(false, "InvalidAllocationRequest");
        var preview = await actuals.PreviewAsync(c, project, line, ct); if (!preview.Ok) return preview;
        try
        {
            if (splits.Sum(x => x.NetBaseAmount) != preview.NetBaseAmount || splits.Any(x => Math.Sign(x.NetBaseAmount) != Math.Sign(preview.NetBaseAmount!.Value)))
                return new(false, "ActualAllocationDoesNotReconcile");
        }
        catch (OverflowException) { return new(false, "AmountOverflow"); }
        return preview;
    }
    public async Task<ConstructionActualWrite> AllocateAsync(int project, int line, ConstructionActualInput input, CancellationToken ct = default)
    {
        var c = await Actor(project, ct); if (c == null) return new(false, "AccessDenied");
        var splits = Splits(input); if (splits == null || input.Version is not { Length: 64 } || input.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 1000) return new(false, "InvalidAllocationRequest");
        return await actuals.AllocateAsync(c, project, line, input.Version, input.RequestId, splits, input.Reason, ct);
    }
}
