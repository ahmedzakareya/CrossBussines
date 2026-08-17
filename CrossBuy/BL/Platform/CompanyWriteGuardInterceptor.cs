using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch B / B4 — write-side company enforcement for the pilot entities.
    //
    // WHY A READ FILTER IS NOT ENOUGH
    //
    // B2's filters are a READ control. They make another company's rows invisible; they do not stop a write. The
    // gap is demonstrable and is asserted as a test
    // (Stage1QueryFilterTests.A_filter_does_not_block_a_write_because_that_is_B4s_job): a company-1 scope can
    // `Add` a row carrying `CompanyID = 2` and SaveChanges will persist it. Nothing in EF objects, because the
    // filter never runs on the write path.
    //
    // Two concrete attacks that a read filter alone leaves open:
    //   1. INSERT INTO another company. A posted `companyId` reaching a `new SalesInvoice { CompanyID = model.CompanyId }`
    //      writes into a company the caller cannot even read — the AccountingApiController shape (Hotfix A.1).
    //   2. UPDATE/DELETE another company's row by id. The filter hides the row from a query, but attaching a stub
    //      (`db.Attach(new Customer { ID = knownId }); entry.State = Modified;`) never queries anything.
    //
    // WHERE THIS SITS
    //
    // A SaveChanges interceptor, registered once on the DbContext, rather than a check in each service. Per-service
    // checks are what the ~1,100 hand-written `.Where(CompanyID == …)` predicates already are: correct wherever
    // someone remembered. This cannot be forgotten by a new service, and it covers the parallel team's writers too.
    //
    // WHAT IT DOES NOT DO
    //   * It does not touch non-pilot entities. Filtering and guarding are the same 12-entity pilot; a guard on an
    //     unclassified entity would be the "apply blindly" the brief forbids.
    //   * It does not authorize. WHO may write is still the module access services' decision; this only enforces
    //     WHICH COMPANY a write may land in.
    //   * It does not filter reads (B2) and it does not touch raw SQL (B5 — a raw INSERT bypasses EF entirely, and
    //     that is stated rather than implied).
    public sealed class CompanyWriteGuardInterceptor : SaveChangesInterceptor
    {
        private readonly ILogger<CompanyWriteGuardInterceptor> _log;

        public CompanyWriteGuardInterceptor(ILogger<CompanyWriteGuardInterceptor> log) { _log = log; }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Guard(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Guard(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void Guard(DbContext? context)
        {
            if (context is not CrossBuy.Models.Context.CrossDbContext db) return;

            var scope = db.CompanyScope;

            // A cross-company WRITE right. PublicCompanyRead and PlatformMonitoring are read-only kinds, so they
            // deliberately do NOT satisfy this — which is B4 closing the gap ADR-023 recorded as open
            // ("IsReadOnly() is advisory today; nothing enforces it"). PlatformDispatch does: the notification
            // projection legitimately writes a Notification for a recipient in the EVENT's company, and the
            // dispatcher's scope has no company of its own to compare against.
            var bypass = scope.ActiveBypass;
            bool mayWriteAnyCompany = bypass != null
                                   && bypass.Kind.AllowsCrossCompany()
                                   && !bypass.Kind.IsReadOnly();

            if (bypass != null && bypass.Kind.IsReadOnly() && HasPilotWrite(db))
                throw new CompanyWriteDeniedException(
                    $"A {bypass.Kind} bypass is read-only, but this save writes a company-scoped entity. " +
                    "Reading another company's data and writing it are different rights.");

            if (mayWriteAnyCompany) return;

            foreach (var entry in db.ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

                var entityName = entry.Metadata.ClrType.Name;
                if (!CompanyQueryFilters.IsPilotEntity(entityName)) continue;

                var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "CompanyID");
                if (property == null) continue;   // a pilot entity always has one; defensive, not expected

                switch (entry.State)
                {
                    case EntityState.Added:
                        GuardInsert(entry, property, scope, entityName);
                        break;

                    // For an UPDATE or a DELETE the ORIGINAL value is what matters: the row's real owner, not
                    // whatever the caller has just assigned to the property. Checking the current value would let a
                    // caller "become" the owner by assigning its own company id before saving.
                    case EntityState.Modified:
                        GuardExisting(ToCompanyId(property.OriginalValue), scope, entityName, "modify");
                        // ...and it must not be RE-POINTED at another company either.
                        if (property.IsModified)
                            GuardExisting(ToCompanyId(property.CurrentValue), scope, entityName, "move to another company");
                        break;

                    case EntityState.Deleted:
                        GuardExisting(ToCompanyId(property.OriginalValue), scope, entityName, "delete");
                        break;
                }
            }
        }

        private void GuardInsert(
            EntityEntry entry, PropertyEntry property, ICompanyScopeHolder scope, string entityName)
        {
            var companyId = ToCompanyId(property.CurrentValue);

            // Notification.CompanyID is nullable, and a null on insert is exactly the unattributed row
            // NotificationCompanyPolicy refuses to guess about. Stamp it rather than create the legacy case the
            // B3 backlog exists to remove.
            if (companyId is null or 0)
            {
                if (scope.CompanyId is not > 0)
                    throw new CompanyWriteDeniedException(
                        $"A new {entityName} carries no company, and this scope has no company to give it. " +
                        "There is no default company — resolve a BusinessContext first.");

                // Deliberately a stamp-and-WARN rather than a silent fix or a hard refusal:
                //   * silent would change data with no trace;
                //   * refusing would turn a pre-existing omission into a runtime failure on a path this batch has
                //     not enumerated, and the row would otherwise be written with CompanyID = 0 — an orphan that
                //     no company can read and no filter can place.
                // The warning names the entity so the omission gets fixed at its source.
                property.CurrentValue = scope.CompanyId.Value;
                _log.LogWarning(
                    "A new {Entity} was saved without a company; it has been stamped with the scope's company " +
                    "{Company}. The writing code should set CompanyID explicitly.",
                    entityName, scope.CompanyId.Value);
                return;
            }

            if (scope.CompanyId is not > 0)
                throw new CompanyWriteDeniedException(
                    $"This scope has no resolved company, so it may not insert a {entityName} into company " +
                    $"{companyId}. A write must name a company the scope actually operates as.");

            if (companyId != scope.CompanyId)
                throw new CompanyWriteDeniedException(
                    $"This scope operates as company {scope.CompanyId} but the new {entityName} names company " +
                    $"{companyId}. A company id from a request, a view model or a route may not redirect a write.");
        }

        private static void GuardExisting(int? rowCompanyId, ICompanyScopeHolder scope, string entityName, string verb)
        {
            if (scope.CompanyId is not > 0)
                throw new CompanyWriteDeniedException(
                    $"This scope has no resolved company, so it may not {verb} a {entityName}.");

            // A null owner (only possible for Notification) is not "mine by default" — same rule as the read side.
            if (rowCompanyId != scope.CompanyId)
                throw new CompanyWriteDeniedException(
                    $"This scope operates as company {scope.CompanyId} and may not {verb} a {entityName} " +
                    $"belonging to company {rowCompanyId?.ToString() ?? "(none)"}.");
        }

        private static bool HasPilotWrite(CrossBuy.Models.Context.CrossDbContext db)
            => db.ChangeTracker.Entries().Any(e =>
                   e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && CompanyQueryFilters.IsPilotEntity(e.Metadata.ClrType.Name));

        private static int? ToCompanyId(object? value) => value switch
        {
            int i => i,
            null => null,
            _ => Convert.ToInt32(value),
        };
    }

    // Raised when a write would land in a company the scope does not operate as. Deliberately NOT a silent skip:
    // dropping the row would leave the caller believing it saved, which is how data goes missing quietly.
    public sealed class CompanyWriteDeniedException : Exception
    {
        public CompanyWriteDeniedException(string message) : base(message) { }
    }
}
