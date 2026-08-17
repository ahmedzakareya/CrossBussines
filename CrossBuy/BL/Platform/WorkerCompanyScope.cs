using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 0 (Slice-003) — the one place a background worker learns which companies to process.
    //
    // Four hosted services each carried `private const int CompanyId = 1`, so on a multi-company install every
    // scheduled job silently processed company 1 only: integrity reconciliation, CRM reminders, auto-task
    // generation and scheduled-task matching. This replaces that constant.
    //
    // IMPORTANT, and the reason this returns every row: the `Companies` entity has NO IsActive / IsDeleted /
    // Status column. The data model defines no notion of an inactive or deleted company, so "eligible" can only
    // honestly mean "exists". If such a state is ever added, this method is the single place that must change —
    // which is precisely why it exists rather than each worker running its own query.
    public interface IWorkerCompanyScope
    {
        Task<List<int>> EligibleCompanyIdsAsync(CancellationToken cancellationToken = default);
    }

    public class WorkerCompanyScope : IWorkerCompanyScope
    {
        private readonly CrossDbContext _db;
        public WorkerCompanyScope(CrossDbContext db) { _db = db; }

        public async Task<List<int>> EligibleCompanyIdsAsync(CancellationToken cancellationToken = default)
            => await _db.Companies.AsNoTracking()
                .Select(c => c.CompanyID)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync(cancellationToken);
    }

    // Stage 1 Batch B / B2 — a DI scope that is BOUND to one company before anything in it runs a query.
    //
    // WHY THIS EXISTS
    //
    // B2 installs company query filters that read ICompanyScopeHolder, and an UNRESOLVED scope reads nothing.
    // CompanyScopeMiddleware resolves the holder for HTTP requests, but a background worker has no request and no
    // middleware: its holder was left unresolved. So every worker that reads a pilot entity would have read
    // NOTHING, silently and on a schedule:
    //
    //   * CrmReminderHostedService reads Leads/Opportunities (via ICrmService) -> reminders stop firing;
    //   * IntegrityCheckHostedService reads JournalEntries -> the check reports zero problems because it examines
    //     zero rows. A reconciliation that passes by looking at nothing is worse than one that fails.
    //
    // WorkerCompanyRunner already gives each company its OWN scope — the missing half was telling that scope which
    // company it is. `ForWorker` does exactly that (BusinessContextFactory.Publish -> ICompanyScopeHolder.Set), and
    // it validates companyId > 0, so a worker cannot bind a scope to "no company".
    //
    // Use this instead of _scopes.CreateScope() in a per-company worker body. The name is the point: a reviewer
    // seeing a bare CreateScope() inside a per-company loop now has something to compare it against.
    public static class WorkerScope
    {
        public static Microsoft.Extensions.DependencyInjection.IServiceScope ForCompany(
            IServiceScopeFactory scopes, int companyId)
        {
            var scope = scopes.CreateScope();
            // Publishes company -> ICompanyScopeHolder, which is what the query filters read. Source = Worker, so
            // the context is NOT granted SystemContextPolicy's actions: a worker is still authorized like any
            // other caller.
            scope.ServiceProvider
                .GetRequiredService<IBusinessContextFactory>()
                .ForWorker(companyId);
            return scope;
        }
    }

    // Shared per-company driver. Every worker gets identical isolation semantics instead of four near-copies:
    //   * one company's failure is caught, logged with its id, and does NOT stop the remaining companies;
    //   * cancellation is honoured between companies;
    //   * an empty company list is logged as a warning rather than silently doing nothing;
    //   * there is NO fallback to company 1 — a worker with no companies processes nothing, loudly.
    public static class WorkerCompanyRunner
    {
        public static async Task<(int processed, int failed)> ForEachCompanyAsync(
            IWorkerCompanyScope scope,
            ILogger logger,
            string workerName,
            Func<int, Task> processCompany,
            CancellationToken cancellationToken)
        {
            var companies = await scope.EligibleCompanyIdsAsync(cancellationToken);
            if (companies.Count == 0)
            {
                logger.LogWarning("{Worker}: no companies found — nothing processed. (No fallback to company 1.)", workerName);
                return (0, 0);
            }

            int processed = 0, failed = 0;
            foreach (var companyId in companies)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    await processCompany(companyId);
                    processed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;   // shutdown, not a company failure
                }
                catch (Exception ex)
                {
                    failed++;
                    // Company id only — never the exception's data payload, which may carry business values.
                    logger.LogError(ex, "{Worker}: company {CompanyId} failed; continuing with the remaining companies", workerName, companyId);
                }
            }
            logger.LogInformation("{Worker}: {Processed} company/companies processed, {Failed} failed, {Total} eligible",
                workerName, processed, failed, companies.Count);
            return (processed, failed);
        }
    }
}
