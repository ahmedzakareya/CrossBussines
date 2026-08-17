using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 2. The revocation boundary.
    //
    // Increment 1 stated plainly that immutable projection copies do NOT solve revocation. This is the
    // smallest architecture that actually does: a tenant-scoped operation that destroys the PAYLOAD,
    // ends retrievability, and leaves an audit row saying the data existed and was revoked.
    //
    // HARD DELETE vs TOMBSTONE — the decision, and why.
    //
    //   Three requirements had to hold together: revoked content must no longer be retrievable; audit
    //   must still be able to say it once existed and was removed; and cross-company deletion must be
    //   impossible. A hard DELETE satisfies the first and third and destroys the second — after it,
    //   nobody can answer "did this company's data ever reach the AI subsystem, and when was it
    //   removed?", which is exactly what a privacy or audit review asks.
    //
    //   So: the ROW survives as evidence, the PAYLOAD is destroyed. This also matches conventions the
    //   platform already has — "reverse, never delete" for financial history, DeletedAt soft-delete
    //   across the feature modules — instead of inventing a fourth.
    //
    // TENANT SAFETY is a property of every query here: each one filters on companyId FIRST, and the
    // company is a required argument that is validated, never inferred. A revocation that could reach
    // another tenant's rows would be a worse defect than the exposure it was meant to fix.
    public interface IAiProjectionRevocationService
    {
        // Everything the AI subsystem holds about ONE record in ONE company.
        Task<int> RevokeEntityAsync(int companyId, string entityType, int entityId, string reason,
            CancellationToken cancellationToken = default);

        // Everything for one company — the "company access revoked" case.
        Task<int> RevokeCompanyAsync(int companyId, string reason, CancellationToken cancellationToken = default);

        // One retired shape (optionally one version) within one company — the "projection schema
        // retired / rebuild" case.
        Task<int> RevokeProjectionTypeAsync(int companyId, string projectionType, int? projectionVersion,
            string reason, CancellationToken cancellationToken = default);

        Task<int> CountRevokedAsync(int companyId, CancellationToken cancellationToken = default);
    }

    public sealed class AiProjectionRevocationService : IAiProjectionRevocationService
    {
        // The payload is replaced with this rather than NULL: the column is NOT NULL, and an empty JSON
        // object is unambiguously "there is nothing here" to any reader, whereas an empty string would
        // be indistinguishable from corruption.
        private const string EmptyPayload = "{}";

        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _context;
        private readonly ILogger<AiProjectionRevocationService> _log;

        public AiProjectionRevocationService(
            CrossDbContext db, IBusinessContextAccessor context, ILogger<AiProjectionRevocationService> log)
        { _db = db; _context = context; _log = log; }

        public Task<int> RevokeEntityAsync(int companyId, string entityType, int entityId, string reason,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(companyId);
            if (string.IsNullOrWhiteSpace(entityType))
                throw new ArgumentException("An entity type is required to revoke by entity.", nameof(entityType));
            if (entityId <= 0)
                throw new ArgumentOutOfRangeException(nameof(entityId), "A real entity id is required.");

            // companyId FIRST in the predicate, and entityId never alone: the same EntityId exists in
            // every company, so revoking by id without the company would delete other tenants' data.
            return RevokeWhereAsync(
                p => p.CompanyID == companyId && p.EntityType == entityType && p.EntityId == entityId,
                companyId, reason, $"entity {entityType}/{entityId}", cancellationToken);
        }

        public Task<int> RevokeCompanyAsync(int companyId, string reason, CancellationToken cancellationToken = default)
        {
            RequireCompany(companyId);
            return RevokeWhereAsync(p => p.CompanyID == companyId, companyId, reason, "whole company", cancellationToken);
        }

        public Task<int> RevokeProjectionTypeAsync(int companyId, string projectionType, int? projectionVersion,
            string reason, CancellationToken cancellationToken = default)
        {
            RequireCompany(companyId);
            if (string.IsNullOrWhiteSpace(projectionType))
                throw new ArgumentException("A projection type is required.", nameof(projectionType));

            return RevokeWhereAsync(
                p => p.CompanyID == companyId && p.ProjectionType == projectionType
                     && (projectionVersion == null || p.ProjectionVersion == projectionVersion),
                companyId, reason,
                $"shape {projectionType}{(projectionVersion.HasValue ? " v" + projectionVersion : " (all versions)")}",
                cancellationToken);
        }

        public Task<int> CountRevokedAsync(int companyId, CancellationToken cancellationToken = default)
            => companyId <= 0
                ? Task.FromResult(0)
                : _db.Set<AiProjection>().AsNoTracking()
                     .CountAsync(p => p.CompanyID == companyId && p.RevokedAt != null, cancellationToken);

        // ------------------------------------------------------------------------------------------
        private async Task<int> RevokeWhereAsync(
            System.Linq.Expressions.Expression<Func<AiProjection, bool>> predicate,
            int companyId, string reason, string what, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException(
                    "A revocation reason is required — it is the audit evidence this operation exists to leave.",
                    nameof(reason));

            // IDEMPOTENT: already-revoked rows are excluded, so a second run affects nothing, does not
            // overwrite the original RevokedAt/RevokedBy, and returns 0. Re-stamping would destroy the
            // record of WHEN the data actually stopped being retrievable.
            var rows = await _db.Set<AiProjection>()
                .Where(predicate)
                .Where(p => p.RevokedAt == null)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0) return 0;

            // The actor, best-effort: revocation may run from a background sweep with no user. An
            // unattributed revocation is still recorded — "system" is an honest answer, and refusing to
            // revoke because nobody is signed in would be the wrong failure mode for a cleanup path.
            var actor = await _context.TryGetCurrentAsync(cancellationToken);
            string by = actor?.EmployeeId is > 0
                ? $"employee:{actor.EmployeeId}"
                : (!string.IsNullOrEmpty(actor?.UserId) ? $"user:{actor.UserId}" : "system");

            var now = DateTime.UtcNow;
            foreach (var row in rows)
            {
                row.RevokedAt = now;
                row.RevokedBy = by;
                row.RevocationReason = Truncate(reason, 200);
                row.PayloadJson = EmptyPayload;      // the content is destroyed; the evidence is not
            }

            await _db.SaveChangesAsync(cancellationToken);

            _log.LogWarning(
                "AI revocation: company={Company} scope={What} rows={Rows} by={By} reason={Reason}",
                companyId, what, rows.Count, by, row0Reason(reason));

            return rows.Count;
        }

        private static void RequireCompany(int companyId)
        {
            // Fail closed, loudly. A revocation with no company is either a bug or an attempt to reach
            // every tenant at once; neither may be allowed to proceed by defaulting.
            if (companyId <= 0)
                throw new BusinessContextUnresolvedException(
                    "Revocation requires an explicit company. There is no default company, and a company-less " +
                    "revocation would cross tenants.");
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        // Reasons are operator-supplied text; keep the log line bounded.
        private static string row0Reason(string reason) => Truncate(reason, 120);
    }
}
