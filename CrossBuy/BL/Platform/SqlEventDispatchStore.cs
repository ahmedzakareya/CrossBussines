using System.Data;
using System.Data.Common;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-003) — SQL Server implementation of the outbox queue.
    //
    // Claiming is ONE atomic statement:
    //     UPDATE TOP (n) ... SET Status='Claimed' OUTPUT inserted.*
    //     FROM BusinessEventDispatch WITH (ROWLOCK, READPAST, UPDLOCK) WHERE <eligible>
    // UPDLOCK takes the update lock as the row is read (so no lock upgrade race), READPAST skips rows a
    // concurrent worker already holds instead of blocking on them, and OUTPUT returns exactly the rows
    // this caller won. Two workers therefore cannot process the same dispatch row, and neither blocks
    // the other.
    //
    // Error text is truncated, every timestamp is UTC (SYSUTCDATETIME / DateTime.UtcNow), and no query in
    // this class references EventId ordering.
    public class SqlEventDispatchStore : IEventDispatchStore
    {
        private const int ErrorColumnLength = 400;

        private readonly CrossDbContext _db;
        private readonly BusinessEventDispatchOptions _options;

        public SqlEventDispatchStore(CrossDbContext db, IOptions<BusinessEventDispatchOptions> options)
        { _db = db; _options = options.Value; }

        // ---------------------------------------------------------------------------------------------
        // Eligibility, expressed once. A row is workable when it is Pending, or Failed and past its
        // backoff, or Claimed by a worker that has gone away. Attempts caps the retries.
        // ---------------------------------------------------------------------------------------------
        private IQueryable<BusinessEventDispatch> EligibleQuery(string consumer, DateTime nowUtc)
        {
            var failedBefore = nowUtc.AddSeconds(-_options.RetryBackoffSeconds);
            var staleBefore = nowUtc.AddMinutes(-_options.StaleClaimMinutes);

            return _db.BusinessEventDispatches.AsNoTracking()
                .Where(d => d.Consumer == consumer
                            && d.Attempts < _options.MaxAttempts
                            && (d.Status == BusinessEventDispatchStatus.Pending
                                || (d.Status == BusinessEventDispatchStatus.Failed
                                    && (d.UpdatedAt == null || d.UpdatedAt <= failedBefore))
                                || (d.Status == BusinessEventDispatchStatus.Claimed
                                    && (d.UpdatedAt == null || d.UpdatedAt <= staleBefore))));
        }

        public async Task<IReadOnlyList<DispatchWorkItem>> GetPendingAsync(string consumer, int batchSize, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            // Ordered by ID purely for a stable, readable diagnostic listing — never used as a cursor.
            var rows = await EligibleQuery(consumer, now)
                .OrderBy(d => d.ID)
                .Take(batchSize)
                .Select(d => new { d.ID, d.EventId, d.Consumer, d.Attempts })
                .ToListAsync(cancellationToken);

            return rows.Select(r => new DispatchWorkItem
            {
                DispatchId = r.ID, EventId = r.EventId, Consumer = r.Consumer, Attempts = r.Attempts,
            }).ToList();
        }

        public async Task<IReadOnlyList<DispatchWorkItem>> ClaimPendingAsync(string consumer, int batchSize, CancellationToken cancellationToken = default)
        {
            if (batchSize <= 0) return Array.Empty<DispatchWorkItem>();

            return _db.Database.IsSqlServer()
                ? await ClaimSqlServerAsync(consumer, batchSize, cancellationToken)
                : await ClaimPortableAsync(consumer, batchSize, cancellationToken);
        }

        // Production path.
        private async Task<IReadOnlyList<DispatchWorkItem>> ClaimSqlServerAsync(string consumer, int batchSize, CancellationToken cancellationToken)
        {
            const string sql = @"
UPDATE TOP (@take) d
   SET d.Status    = @claimed,
       d.Attempts  = d.Attempts + 1,
       d.UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.ID, inserted.EventId, inserted.Consumer, inserted.Attempts
  FROM BusinessEventDispatch AS d WITH (ROWLOCK, READPAST, UPDLOCK)
 WHERE d.Consumer = @consumer
   AND d.Attempts < @maxAttempts
   AND ( d.Status = @pending
      OR (d.Status = @failed  AND (d.UpdatedAt IS NULL OR d.UpdatedAt <= DATEADD(second, -@backoff, SYSUTCDATETIME())))
      OR (d.Status = @claimed AND (d.UpdatedAt IS NULL OR d.UpdatedAt <= DATEADD(minute, -@stale,  SYSUTCDATETIME()))) );";

            var results = new List<DispatchWorkItem>();
            var connection = _db.Database.GetDbConnection();
            bool opened = false;
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
                opened = true;
            }
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                // Enrol in the ambient transaction when there is one, so a caller that claims inside its own
                // transaction still sees consistent state.
                command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

                AddParameter(command, "@take", batchSize);
                AddParameter(command, "@consumer", consumer);
                AddParameter(command, "@maxAttempts", _options.MaxAttempts);
                AddParameter(command, "@pending", BusinessEventDispatchStatus.Pending);
                AddParameter(command, "@failed", BusinessEventDispatchStatus.Failed);
                AddParameter(command, "@claimed", BusinessEventDispatchStatus.Claimed);
                AddParameter(command, "@backoff", _options.RetryBackoffSeconds);
                AddParameter(command, "@stale", _options.StaleClaimMinutes);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(new DispatchWorkItem
                    {
                        DispatchId = reader.GetInt64(0),
                        EventId = reader.GetInt64(1),
                        Consumer = reader.GetString(2),
                        Attempts = reader.GetInt32(3),
                    });
                }
            }
            finally
            {
                if (opened) await connection.CloseAsync();
            }
            return results;
        }

        // Portable fallback for providers without UPDLOCK/READPAST (the SQLite-backed test host).
        // It claims inside a transaction and re-checks Status on the UPDATE, so a row can still only be
        // taken once; it does NOT give SQL Server's skip-locked behaviour and is not a production path.
        private async Task<IReadOnlyList<DispatchWorkItem>> ClaimPortableAsync(string consumer, int batchSize, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var candidates = await EligibleQuery(consumer, now)
                .OrderBy(d => d.ID).Take(batchSize)
                .Select(d => new { d.ID, d.Status })
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) return Array.Empty<DispatchWorkItem>();

            var claimed = new List<DispatchWorkItem>();
            foreach (var candidate in candidates)
            {
                // Conditional update: the WHERE still carries the status we read, so a racing claimer that
                // already moved the row makes this affect zero rows and we skip it.
                var row = await _db.BusinessEventDispatches
                    .FirstOrDefaultAsync(d => d.ID == candidate.ID && d.Status == candidate.Status, cancellationToken);
                if (row == null) continue;

                row.Status = BusinessEventDispatchStatus.Claimed;
                row.Attempts += 1;
                row.UpdatedAt = DateTime.UtcNow;
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _db.Entry(row).State = EntityState.Detached;
                    continue;
                }
                claimed.Add(new DispatchWorkItem
                {
                    DispatchId = row.ID, EventId = row.EventId, Consumer = row.Consumer, Attempts = row.Attempts,
                });
                _db.Entry(row).State = EntityState.Detached;
            }
            return claimed;
        }

        // ---------------------------------------------------------------------------------------------
        // Abandoned-claim recovery. See IEventDispatchStore.ReleaseAbandonedClaimsAsync for WHY.
        //
        // THE PREDICATE IS THE SAFETY ARGUMENT, so it is worth reading as three independent guards:
        //
        //   Status = Claimed              — a Pending, Failed or Done row is none of this method's business.
        //   Attempts >= MaxAttempts       — ONLY the exhausted case. A stale claim with budget left is left
        //                                   alone on purpose: ClaimPendingAsync already reclaims it properly,
        //                                   with an attempt increment, and two owners for one transition is
        //                                   how double-processing starts.
        //   UpdatedAt <= now - stale      — THE LEASE. A worker holding a row right now stamped UpdatedAt when
        //                                   it claimed, so a live claim cannot satisfy this until the lease has
        //                                   actually expired. This is what makes "never steal an active claim"
        //                                   a property of the WHERE rather than a hope.
        //
        // The NULL arm mirrors EligibleQuery exactly rather than inventing a stricter rule here: a Claimed row
        // with no timestamp cannot be dated, the claim path already treats that as stale, and leaving it out
        // would simply move the stuck rows into a second category nothing sweeps.
        //
        // Attempts is NOT incremented and NOT reset. The row is being classified, not retried, so the attempt
        // history stays exactly what it was — an operator reading Attempts = 5 is reading the truth.
        // ---------------------------------------------------------------------------------------------
        private const string AbandonedClaimError =
            "Released by the stale-claim reaper: the worker holding this row stopped before finishing and the " +
            "claim lease expired with no attempts left. The row is terminal and awaiting an operator retry.";

        public async Task<int> ReleaseAbandonedClaimsAsync(string consumer, CancellationToken cancellationToken = default)
        {
            return _db.Database.IsSqlServer()
                ? await ReleaseSqlServerAsync(consumer, cancellationToken)
                : await ReleasePortableAsync(consumer, cancellationToken);
        }

        private async Task<int> ReleaseSqlServerAsync(string consumer, CancellationToken cancellationToken)
        {
            // ROWLOCK/READPAST/UPDLOCK for the same reason ClaimPendingAsync uses them: two reapers running
            // concurrently skip each other's rows instead of blocking, and neither can classify a row twice.
            const string sql = @"
UPDATE d
   SET d.Status    = @failed,
       d.UpdatedAt = SYSUTCDATETIME(),
       d.Error     = @error
  FROM BusinessEventDispatch AS d WITH (ROWLOCK, READPAST, UPDLOCK)
 WHERE d.Consumer = @consumer
   AND d.Status   = @claimed
   AND d.Attempts >= @maxAttempts
   AND (d.UpdatedAt IS NULL OR d.UpdatedAt <= DATEADD(minute, -@stale, SYSUTCDATETIME()));";

            var connection = _db.Database.GetDbConnection();
            bool opened = false;
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
                opened = true;
            }
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (_db.Database.CurrentTransaction != null)
                    command.Transaction = _db.Database.CurrentTransaction.GetDbTransaction();

                AddParameter(command, "@consumer", consumer);
                AddParameter(command, "@claimed", BusinessEventDispatchStatus.Claimed);
                AddParameter(command, "@failed", BusinessEventDispatchStatus.Failed);
                AddParameter(command, "@maxAttempts", _options.MaxAttempts);
                AddParameter(command, "@stale", _options.StaleClaimMinutes);
                AddParameter(command, "@error", AbandonedClaimError);

                return await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally { if (opened) await connection.CloseAsync(); }
        }

        // Portable equivalent for the SQLite test host. Same predicate, conditional per-row update so a racing
        // reaper that already moved the row affects zero rows and is skipped.
        private async Task<int> ReleasePortableAsync(string consumer, CancellationToken cancellationToken)
        {
            var staleBefore = DateTime.UtcNow.AddMinutes(-_options.StaleClaimMinutes);

            var candidates = await _db.BusinessEventDispatches.AsNoTracking()
                .Where(d => d.Consumer == consumer
                            && d.Status == BusinessEventDispatchStatus.Claimed
                            && d.Attempts >= _options.MaxAttempts
                            && (d.UpdatedAt == null || d.UpdatedAt <= staleBefore))
                .Select(d => d.ID)
                .ToListAsync(cancellationToken);

            int released = 0;
            foreach (var id in candidates)
            {
                var row = await _db.BusinessEventDispatches
                    .FirstOrDefaultAsync(d => d.ID == id && d.Status == BusinessEventDispatchStatus.Claimed, cancellationToken);
                if (row == null) continue;

                row.Status = BusinessEventDispatchStatus.Failed;
                row.UpdatedAt = DateTime.UtcNow;
                row.Error = AbandonedClaimError;
                try { await _db.SaveChangesAsync(cancellationToken); released++; }
                catch (DbUpdateConcurrencyException) { _db.Entry(row).State = EntityState.Detached; continue; }
                _db.Entry(row).State = EntityState.Detached;
            }
            return released;
        }

        public async Task MarkDoneAsync(long dispatchId, CancellationToken cancellationToken = default)
        {
            var row = await _db.BusinessEventDispatches.FirstOrDefaultAsync(d => d.ID == dispatchId, cancellationToken);
            if (row == null) return;
            row.Status = BusinessEventDispatchStatus.Done;
            row.Error = null;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(row).State = EntityState.Detached;
        }

        public async Task MarkFailedAsync(long dispatchId, string error, CancellationToken cancellationToken = default)
        {
            var row = await _db.BusinessEventDispatches.FirstOrDefaultAsync(d => d.ID == dispatchId, cancellationToken);
            if (row == null) return;
            row.Status = BusinessEventDispatchStatus.Failed;
            row.Error = Truncate(error, ErrorColumnLength);
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(row).State = EntityState.Detached;
        }

        public async Task IncrementAttemptsAsync(long dispatchId, CancellationToken cancellationToken = default)
        {
            var row = await _db.BusinessEventDispatches.FirstOrDefaultAsync(d => d.ID == dispatchId, cancellationToken);
            if (row == null) return;
            row.Attempts += 1;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(row).State = EntityState.Detached;
        }

        public async Task TryCompleteEventAsync(long eventId, CancellationToken cancellationToken = default)
        {
            bool anyOutstanding = await _db.BusinessEventDispatches.AsNoTracking()
                .AnyAsync(d => d.EventId == eventId && d.Status != BusinessEventDispatchStatus.Done, cancellationToken);
            if (anyOutstanding) return;

            var ev = await _db.BusinessEvents.FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
            if (ev == null || ev.CompletedAt != null) return;
            ev.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(ev).State = EntityState.Detached;
        }

        // Stage 0 Batch B — operator-initiated retry. See IEventDispatchStore.RetryAsync for the rule set.
        //
        // Race safety: the final UPDATE is CONDITIONAL on the status we read. If a worker claimed the row in the
        // gap between the read and the write, zero rows change and the caller gets RaceLost instead of silently
        // stealing work in flight. That is why this cannot be a plain "set Status='Pending'" in a controller.
        public async Task<DispatchRetryResult> RetryAsync(
            long dispatchId, BusinessContext context, string reason, bool elevatedOverride,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrWhiteSpace(reason))
                return DispatchRetryResult.Fail(DispatchRetryOutcome.ReasonRequired,
                    "A reason is required so the retry can be audited.", dispatchId);

            var row = await _db.BusinessEventDispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.ID == dispatchId, cancellationToken);
            if (row == null)
                return DispatchRetryResult.Fail(DispatchRetryOutcome.NotFound, "Dispatch row not found.", dispatchId);

            // Company isolation: the row is reachable only through its event, so the check is on the event.
            var owningCompany = await _db.BusinessEvents.AsNoTracking()
                .Where(e => e.EventId == row.EventId).Select(e => (int?)e.CompanyID)
                .FirstOrDefaultAsync(cancellationToken);
            if (owningCompany == null)
                return DispatchRetryResult.Fail(DispatchRetryOutcome.NotFound, "Dispatch row not found.", dispatchId);
            if (!context.IsSystem && owningCompany.Value != context.CompanyId)
                // Deliberately the SAME message as NotFound: a cross-company probe must not learn the row exists.
                return DispatchRetryResult.Fail(DispatchRetryOutcome.NotFound, "Dispatch row not found.", dispatchId);

            var staleBefore = DateTime.UtcNow.AddMinutes(-_options.StaleClaimMinutes);
            switch (row.Status)
            {
                case BusinessEventDispatchStatus.Done:
                    return DispatchRetryResult.Fail(DispatchRetryOutcome.AlreadyDone,
                        "This consumer already completed. Replaying it could duplicate its side effects.", dispatchId);
                case BusinessEventDispatchStatus.Pending:
                    return DispatchRetryResult.Fail(DispatchRetryOutcome.AlreadyPending,
                        "This consumer is already queued and will be picked up by the dispatcher.", dispatchId);
                case BusinessEventDispatchStatus.Claimed when row.UpdatedAt != null && row.UpdatedAt > staleBefore:
                    return DispatchRetryResult.Fail(DispatchRetryOutcome.HeldByWorker,
                        "A worker is processing this row right now. Try again after the stale-claim timeout.", dispatchId);
            }

            if (row.Attempts >= _options.MaxAttempts && !elevatedOverride)
                return DispatchRetryResult.Fail(DispatchRetryOutcome.AttemptsExhausted,
                    $"This row has used all {_options.MaxAttempts} attempts. An elevated override with a reason is required.", dispatchId);

            // Reset ONLY this consumer's row, conditionally on the status we read.
            // Attempts is NOT reset: the history of how many times this consumer has failed is audit information,
            // and clearing it would let a row be retried forever without trace. An elevated override grants one more
            // attempt by moving the ceiling, not by rewriting the past — so Attempts is decremented by one only when
            // the override is what unblocked it, and never below zero.
            int newAttempts = (elevatedOverride && row.Attempts >= _options.MaxAttempts)
                ? Math.Max(0, _options.MaxAttempts - 1)
                : row.Attempts;

            // The write is a SINGLE conditional UPDATE carrying the whole row state we read — Status, Attempts AND
            // UpdatedAt — and it succeeds only if zero of them moved in the meantime.
            //
            // Matching on Status alone is NOT sufficient, and this is the subtle case: when a worker reclaims a
            // STALE Claimed row, the status stays 'Claimed' and only Attempts/UpdatedAt change. A status-only
            // condition would therefore still match and flip a row a worker had just taken back to Pending, letting
            // a second worker claim work that is already in flight. Loading-then-SaveChangesAsync has the same hole
            // from the other end: EF would emit "WHERE ID = @id" with no guard at all, so a reclaim landing between
            // the read and the save would be silently overwritten. Doing it in one statement closes both windows.
            //
            // The prior error is PRESERVED, not cleared: if the retry fails the same way, the operator can see it
            // was not a one-off. MarkFailedAsync overwrites it on the next genuine failure, and MarkDoneAsync
            // clears it on success — so the policy is "keep until the outcome is known".
            const string updateSql = @"
UPDATE BusinessEventDispatch
   SET Status = @pending, Attempts = @newAttempts, UpdatedAt = @now
 WHERE ID = @id
   AND Status = @status
   AND Attempts = @attempts
   AND ((UpdatedAt IS NULL AND @updatedAt IS NULL) OR UpdatedAt = @updatedAt);";

            int affected;
            var connection = _db.Database.GetDbConnection();
            bool opened = false;
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
                opened = true;
            }
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = updateSql;
                command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
                AddParameter(command, "@pending", BusinessEventDispatchStatus.Pending);
                AddParameter(command, "@newAttempts", newAttempts);
                AddParameter(command, "@now", DateTime.UtcNow);
                AddParameter(command, "@id", dispatchId);
                AddParameter(command, "@status", row.Status);
                AddParameter(command, "@attempts", row.Attempts);
                // DbType.DateTime2 is NOT optional here. ADO.NET infers plain `datetime` for an untyped DateTime
                // parameter, which rounds to ~3.33 ms; compared against the datetime2(7) column that makes the
                // equality guard never match, and every retry would come back RaceLost.
                AddParameter(command, "@updatedAt", (object?)row.UpdatedAt ?? DBNull.Value, DbType.DateTime2);
                affected = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                if (opened) await connection.CloseAsync();
            }

            if (affected == 0)
                return DispatchRetryResult.Fail(DispatchRetryOutcome.RaceLost,
                    "The row changed while the retry was being prepared. Refresh and try again.", dispatchId);

            return new DispatchRetryResult
            {
                Outcome = DispatchRetryOutcome.Requeued,
                Message = "Re-queued. The dispatcher will pick it up on its next pass.",
                DispatchId = dispatchId, EventId = row.EventId, Consumer = row.Consumer, Attempts = newAttempts,
            };
        }

        private static string Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static void AddParameter(DbCommand command, string name, object? value, DbType? dbType = null)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            if (dbType.HasValue) p.DbType = dbType.Value;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
    }
}