using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — the persistence boundary. The future AI/RAG layer reads THIS and never a business
    // table, which is what makes the field list in AiProjectionBuilders an actual control rather than a
    // convention.
    public interface IAiProjectionStore
    {
        // Returns true when a row was written, false when an equivalent projection already existed.
        // Idempotent by (BusinessEventId, ProjectionType) — see UpsertAsync for why that pair.
        Task<bool> UpsertAsync(AiProjection projection, CancellationToken cancellationToken = default);

        Task<int> CountForCompanyAsync(int companyId, CancellationToken cancellationToken = default);
    }

    public sealed class AiProjectionStore : IAiProjectionStore
    {
        private readonly CrossDbContext _db;
        private readonly ILogger<AiProjectionStore> _log;

        public AiProjectionStore(CrossDbContext db, ILogger<AiProjectionStore> log) { _db = db; _log = log; }

        public async Task<bool> UpsertAsync(AiProjection projection, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(projection);

            // FAIL CLOSED on tenancy. This is the last line before persistence and it does not trust its
            // caller: a projection with no company is refused outright rather than written with a zero or
            // "corrected" to a default. There is no default company.
            if (projection.CompanyID <= 0)
                throw new BusinessContextUnresolvedException(
                    "An AI projection requires the company from its business event. There is no default company.");

            // The idempotency key is (BusinessEventId, ProjectionType), NOT the event alone: one event may
            // legitimately produce several SHAPES later, but never two rows of the same shape. A unique
            // index enforces the same pair in the database, so two workers racing produce one row even if
            // both pass this check — the in-memory test below is the fast path, not the guarantee.
            bool exists = await _db.Set<AiProjection>().AsNoTracking().AnyAsync(
                p => p.BusinessEventId == projection.BusinessEventId
                     && p.ProjectionType == projection.ProjectionType,
                cancellationToken);

            if (exists)
            {
                _log.LogDebug(
                    "AI projection for event {EventId} shape {Shape} already exists; retry did not duplicate it.",
                    projection.BusinessEventId, projection.ProjectionType);
                return false;
            }

            _db.Set<AiProjection>().Add(projection);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // The race the pre-check cannot close: two workers both saw "not exists". The unique index
                // is the authority, and losing this race is a SUCCESS — the projection exists. Detaching
                // keeps the context usable for the rest of the batch.
                _db.Entry(projection).State = EntityState.Detached;
                _log.LogInformation(
                    "AI projection for event {EventId} was written concurrently; treating as already projected.",
                    projection.BusinessEventId);
                return false;
            }
        }

        public Task<int> CountForCompanyAsync(int companyId, CancellationToken cancellationToken = default)
            => companyId <= 0
                ? Task.FromResult(0)
                : _db.Set<AiProjection>().AsNoTracking()
                     .CountAsync(p => p.CompanyID == companyId, cancellationToken);

        // SQL Server 2601/2627 are the unique-index and unique-constraint violations. Matched on the
        // provider's own numbers where available, with a message fallback so the SQLite-backed tests
        // (which the platform suite uses for real transactions) behave the same way.
        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            for (Exception? e = ex.InnerException; e != null; e = e.InnerException)
            {
                var number = e.GetType().GetProperty("Number")?.GetValue(e) as int?;
                if (number is 2601 or 2627) return true;

                var sqliteCode = e.GetType().GetProperty("SqliteErrorCode")?.GetValue(e) as int?;
                if (sqliteCode == 19) return true;   // SQLITE_CONSTRAINT

                if (e.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                    e.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
