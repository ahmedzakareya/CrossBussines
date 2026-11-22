using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // ICommPushTokenStore — the half of gap G3 that had a name and no type.
    //
    // CPS-001 §9 records G3 as "ICommPushTokenStore has no implementation". Until now the interface
    // did not exist either: it was named in two comments (CommNotificationDispatcher.cs:131 and
    // CommNotificationContracts.cs:263) and nowhere else. deploy/sql/comm_push_tokens.sql is the
    // schema half, authored and NOT APPLIED; this file is the store half.
    //
    // WHAT THIS STILL DOES NOT CLOSE, so an added file is not read as a closed gap. The slice's own
    // footer names three things after it, in this order: the store, a registration endpoint, and one
    // adapter. This is the first. There is deliberately NO registration endpoint here — an endpoint is
    // the thing that accepts a credential off the network, and storing device credentials that no
    // adapter can yet use would add attack surface for no delivery. The order in the slice exists for
    // the opposite reason: the adapter cannot be tested without somewhere to read tokens from.
    //
    // THE PUSH CHANNEL IS UNAFFECTED BY THIS FILE. UnwiredCommNotificationChannel.Push() still answers
    // Skip, because no adapter is registered. A push row parks as Skipped rather than queueing as
    // Failed, which is what makes an unwired channel harmless; registering a store does not change it.
    // =============================================================================================
    public interface ICommPushTokenStore
    {
        // True when this deployment actually has somewhere to keep a token. Callers ask BEFORE
        // offering device registration, so nobody is shown an enrol button that will refuse.
        bool IsProvisioned { get; }

        // Every LIVE token for one recipient in one company. The send path's read.
        //
        // Returns the CONTRACT, never the row: an adapter receives the frozen shape and never sees
        // RevokedAt, RevokedReason or who created the registration.
        Task<IReadOnlyList<CommPushToken>> ForRecipientAsync(
            int companyId, int employeeId, CancellationToken cancellationToken = default);

        // Idempotent by contract. Re-registering a token this company already holds LIVE refreshes
        // LastSeenAt and returns it rather than inserting a duplicate — a mobile client re-enrols on
        // every cold start, and each of those must not become a row.
        Task<CommPushToken?> RegisterAsync(
            int companyId, int employeeId, string platform, string token, int? registeredBy,
            CancellationToken cancellationToken = default);

        // SOFT retire. Returns false when no live row held that token, so a double revoke is visible
        // to the caller as "nothing to do" rather than as a success that did nothing.
        Task<bool> RevokeAsync(
            int companyId, string token, string? reason, int? revokedBy,
            CancellationToken cancellationToken = default);
    }

    // ---------------------------------------------------------------------------------------------
    // THE DEFAULT, and the one registered wherever CommPushTokens is absent.
    //
    // Reports rather than throws, identically on every member, so a caller that checked IsProvisioned
    // never reaches these and a caller that did not gets an empty answer instead of a SQL-208 from the
    // driver. Same shape as UnprovisionedCommTaskLinkStore, for the same reason (HM-D44).
    // ---------------------------------------------------------------------------------------------
    public sealed class UnprovisionedCommPushTokenStore : ICommPushTokenStore
    {
        public bool IsProvisioned => false;

        public Task<IReadOnlyList<CommPushToken>> ForRecipientAsync(
            int companyId, int employeeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommPushToken>>(Array.Empty<CommPushToken>());

        public Task<CommPushToken?> RegisterAsync(
            int companyId, int employeeId, string platform, string token, int? registeredBy,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CommPushToken?>(null);

        public Task<bool> RevokeAsync(
            int companyId, string token, string? reason, int? revokedBy,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    // ---------------------------------------------------------------------------------------------
    // THE PERSISTENT ONE. Complete, and registered only where the database actually has the table —
    // the same rule task links follow, for the same reason.
    // ---------------------------------------------------------------------------------------------
    public sealed class EfCommPushTokenStore : ICommPushTokenStore
    {
        private readonly CrossDbContext _db;

        public EfCommPushTokenStore(CrossDbContext db) => _db = db;

        public bool IsProvisioned => true;

        private DbSet<CommPushTokenRecord> Rows => _db.Set<CommPushTokenRecord>();

        // The contract's shape, built in one place so no caller assembles it differently.
        private static CommPushToken Project(CommPushTokenRecord row) => new()
        {
            EmployeeId = row.EmployeeId,
            Platform = row.Platform,
            Token = row.Token,
            RegisteredAt = row.RegisteredAt,
            LastSeenAt = row.LastSeenAt,
        };

        public async Task<IReadOnlyList<CommPushToken>> ForRecipientAsync(
            int companyId, int employeeId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || employeeId <= 0) return Array.Empty<CommPushToken>();

            var rows = await Rows.AsNoTracking()
                .Where(x => x.CompanyID == companyId && x.EmployeeId == employeeId && x.RevokedAt == null)
                .OrderByDescending(x => x.LastSeenAt)
                .ToListAsync(cancellationToken);

            return rows.Select(Project).ToList();
        }

        public async Task<CommPushToken?> RegisterAsync(
            int companyId, int employeeId, string platform, string token, int? registeredBy,
            CancellationToken cancellationToken = default)
        {
            // REFUSED, NOT NORMALISED. A platform this product has no transport for is a caller
            // mistake, and coercing it to a default would deliver somebody's notification nowhere
            // while reporting success. Case is the one thing that IS normalised — see Canonical.
            if (companyId <= 0 || employeeId <= 0) return null;
            if (string.IsNullOrWhiteSpace(token)) return null;

            var canonicalPlatform = CommPushPlatform.Canonical(platform);
            if (canonicalPlatform == null) return null;

            token = token.Trim();
            var now = DateTime.UtcNow;

            // READ BEFORE WRITE, matching UX_CommPushTokens_Live. The index is what actually refuses a
            // duplicate under a race; this read is what makes the common case cheap rather than an
            // exception. Note it is keyed on (company, token) and NOT on the employee: a device handed
            // to a different person re-registers the same token, and the row must move to its new
            // owner rather than silently keep notifying the previous one.
            var existing = await Rows
                .Where(x => x.CompanyID == companyId && x.Token == token && x.RevokedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null)
            {
                existing.EmployeeId = employeeId;
                existing.Platform = canonicalPlatform;
                existing.LastSeenAt = now;
                existing.UpdatedBy = registeredBy;
                existing.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
                return Project(existing);
            }

            var row = new CommPushTokenRecord
            {
                CompanyID = companyId,
                EmployeeId = employeeId,
                Platform = canonicalPlatform,
                Token = token,
                RegisteredAt = now,
                LastSeenAt = now,
                CreatedBy = registeredBy,
                CreatedAt = now,
            };

            Rows.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            return Project(row);
        }

        public async Task<bool> RevokeAsync(
            int companyId, string token, string? reason, int? revokedBy,
            CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || string.IsNullOrWhiteSpace(token)) return false;

            token = token.Trim();

            var row = await Rows
                .Where(x => x.CompanyID == companyId && x.Token == token && x.RevokedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null) return false;

            var now = DateTime.UtcNow;
            row.RevokedAt = now;
            row.RevokedReason = Trim(reason, 200);
            row.UpdatedBy = revokedBy;
            row.UpdatedAt = now;

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        private static string? Trim(string? value, int max) =>
            string.IsNullOrWhiteSpace(value) ? null
                : value.Length <= max ? value : value.Substring(0, max);
    }
}
