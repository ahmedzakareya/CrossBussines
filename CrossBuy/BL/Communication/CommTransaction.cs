using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §6) — the transaction scope every write path uses.
    //
    // THE PROBLEM IT SOLVES
    //
    // One comment operation writes to as many as six tables: CommThreads (counters), CommComments,
    // CommMentions, CommMentionRecipients, CommNotifications + CommNotificationDeliveries, and
    // CommAuditEntries. Those rows must share one fate. A comment with no audit row, or mentions with no
    // comment, is worse than a failed post — it is a lie in an append-only log.
    //
    // ENROL, NEVER OWN — when someone else already has a transaction.
    //
    // A caller may already be in one: a business service posting a document and adding a system comment in the
    // same operation. Opening a nested transaction there would either throw or silently commit part of their
    // work. So this type ENROLS (does nothing, commits nothing) when a transaction is already ambient, and the
    // outer owner decides the outcome. The same enrol-never-own rule ADR-001 states for the kernel's
    // RecordAsync, generalised to a scope object because this platform — unlike the kernel — is sometimes the
    // outermost caller and must then own one.
    //
    // Usage:
    //     await using var tx = await CommTransaction.BeginAsync(_db, ct);
    //     ... writes ...
    //     await tx.CommitAsync(ct);          // no-op when enrolled in someone else's transaction
    //
    // Not disposing, or leaving without CommitAsync, rolls back — which is the correct behaviour for an
    // exception path and the reason CommitAsync is explicit rather than implicit on dispose.
    // =============================================================================================
    public sealed class CommTransaction : IAsyncDisposable
    {
        private readonly CrossDbContext _db;
        private readonly IDbContextTransaction? _owned;
        private bool _committed;

        private CommTransaction(CrossDbContext db, IDbContextTransaction? owned)
        { _db = db; _owned = owned; }

        // True when this scope opened the transaction and is therefore responsible for it.
        public bool IsOwner => _owned != null;

        public static async Task<CommTransaction> BeginAsync(CrossDbContext db, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(db);

            if (db.Database.CurrentTransaction != null)
                return new CommTransaction(db, owned: null);   // enrol

            // A provider without real transactions (the EF InMemory provider) would make every rollback test
            // pass while proving nothing. Rather than pretend, this returns a non-owning scope and the SaveChanges
            // below still runs — and CommunicationTestHost uses SQLite precisely so the tests get real
            // transactions, for the same reason PlatformTestHost gives.
            try
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                return new CommTransaction(db, transaction);
            }
            catch (InvalidOperationException)
            {
                return new CommTransaction(db, owned: null);
            }
        }

        // Saves the change tracker, then commits if this scope owns the transaction. Saving here rather than in
        // each service is what guarantees the business row and its audit row go in one SaveChanges — the
        // invariant CommAuditWriter's contract depends on.
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _db.SaveChangesAsync(cancellationToken);
            if (_owned != null)
            {
                await _owned.CommitAsync(cancellationToken);
            }
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_owned == null) return;

            // An uncommitted owned transaction is an abandoned one: roll it back explicitly rather than relying
            // on disposal semantics, so the intent is readable in the code and in a debugger.
            if (!_committed)
            {
                try { await _owned.RollbackAsync(); }
                catch (Exception) { /* the connection is already gone; disposal below is all that is left */ }
            }
            await _owned.DisposeAsync();
        }
    }
}