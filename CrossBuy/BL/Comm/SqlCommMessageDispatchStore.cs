using System.Data;
using System.Data.Common;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Comm
{
    // Stage 0 (Slice-003) — SQL Server implementation of the email outbox queue.
    //
    // Claiming is ONE atomic statement:
    //     UPDATE TOP (n) ... SET Status='Claimed' OUTPUT inserted.*
    //     FROM CommMessages WITH (ROWLOCK, READPAST, UPDLOCK) WHERE <eligible>
    // UPDLOCK takes the update lock as the row is read, READPAST skips rows a concurrent worker already holds
    // instead of blocking, and OUTPUT returns exactly the rows this caller won. Two workers therefore cannot send
    // the same email — which is the property that matters most here, because a duplicate send is visible to a
    // customer and cannot be undone.
    //
    // No cursor of any kind: eligibility is by Status. There is no "last sent id" to advance past.
    public class SqlCommMessageDispatchStore : ICommMessageDispatchStore
    {
        // The column is nvarchar(1000) in the schema; CommService already truncates at 900. Keep the store's own
        // cap below the column so a long SMTP exception can never break the write.
        private const int ErrorColumnLength = 900;

        private readonly CrossDbContext _db;
        private readonly CommMessageDispatchOptions _options;

        public SqlCommMessageDispatchStore(CrossDbContext db, IOptions<CommMessageDispatchOptions> options)
        { _db = db; _options = options.Value; }

        // Eligibility, expressed once. A message is workable when it is Queued, or Failed and past its backoff,
        // or Claimed by a worker that has gone away. Attempts caps the retries, and Sent is absent by
        // construction — it is terminal.
        private IQueryable<Models.Context.Comm.CommMessage> EligibleQuery(DateTime nowUtc)
        {
            var failedBefore = nowUtc.AddSeconds(-_options.RetryBackoffSeconds);
            var staleBefore = nowUtc.AddMinutes(-_options.StaleClaimMinutes);

            return _db.CommMessages.AsNoTracking()
                .Where(m => m.DeletedAt == null
                            && m.Attempts < _options.MaxAttempts
                            && (m.Status == CommMessageStatus.Queued
                                || (m.Status == CommMessageStatus.Failed
                                    && (m.UpdatedAt == null || m.UpdatedAt <= failedBefore))
                                || (m.Status == CommMessageStatus.Claimed
                                    && (m.ClaimedAt == null || m.ClaimedAt <= staleBefore))));
        }

        public async Task<IReadOnlyList<CommMessageWorkItem>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            var rows = await EligibleQuery(DateTime.UtcNow)
                .OrderBy(m => m.Id)   // stable diagnostic ordering only — never used as a cursor
                .Take(batchSize)
                .Select(m => new { m.Id, m.CompanyID, m.Attempts })
                .ToListAsync(cancellationToken);
            return rows.Select(r => new CommMessageWorkItem
            { MessageId = r.Id, CompanyId = r.CompanyID, Attempts = r.Attempts }).ToList();
        }

        public async Task<IReadOnlyList<CommMessageWorkItem>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            if (batchSize <= 0) return Array.Empty<CommMessageWorkItem>();
            return _db.Database.IsSqlServer()
                ? await ClaimSqlServerAsync(batchSize, cancellationToken)
                : await ClaimPortableAsync(batchSize, cancellationToken);
        }

        // Production path.
        private async Task<IReadOnlyList<CommMessageWorkItem>> ClaimSqlServerAsync(int batchSize, CancellationToken cancellationToken)
        {
            const string sql = @"
UPDATE TOP (@take) m
   SET m.Status    = @claimed,
       m.Attempts  = m.Attempts + 1,
       m.ClaimedAt = SYSUTCDATETIME(),
       m.UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.Id, inserted.CompanyID, inserted.Attempts
  FROM CommMessages AS m WITH (ROWLOCK, READPAST, UPDLOCK)
 WHERE m.DeletedAt IS NULL
   AND m.Attempts < @maxAttempts
   AND ( m.Status = @queued
      OR (m.Status = @failed  AND (m.UpdatedAt IS NULL OR m.UpdatedAt <= DATEADD(second, -@backoff, SYSUTCDATETIME())))
      OR (m.Status = @claimed AND (m.ClaimedAt IS NULL OR m.ClaimedAt <= DATEADD(minute, -@stale,  SYSUTCDATETIME()))) );";

            var results = new List<CommMessageWorkItem>();
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
                command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
                AddParameter(command, "@take", batchSize);
                AddParameter(command, "@maxAttempts", _options.MaxAttempts);
                AddParameter(command, "@queued", CommMessageStatus.Queued);
                AddParameter(command, "@claimed", CommMessageStatus.Claimed);
                AddParameter(command, "@failed", CommMessageStatus.Failed);
                AddParameter(command, "@backoff", _options.RetryBackoffSeconds);
                AddParameter(command, "@stale", _options.StaleClaimMinutes);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(new CommMessageWorkItem
                    {
                        MessageId = reader.GetInt32(0),
                        CompanyId = reader.GetInt32(1),
                        Attempts = reader.GetInt32(2),
                    });
                }
            }
            finally
            {
                if (opened) await connection.CloseAsync();
            }
            return results;
        }

        // Portable fallback for providers without UPDLOCK/READPAST (the SQLite-backed test host). It claims with a
        // conditional update so a row can still only be taken once; it does NOT give SQL Server's skip-locked
        // behaviour and is not a production path.
        private async Task<IReadOnlyList<CommMessageWorkItem>> ClaimPortableAsync(int batchSize, CancellationToken cancellationToken)
        {
            var candidates = await EligibleQuery(DateTime.UtcNow)
                .OrderBy(m => m.Id).Take(batchSize)
                .Select(m => new { m.Id, m.Status })
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) return Array.Empty<CommMessageWorkItem>();

            var claimed = new List<CommMessageWorkItem>();
            foreach (var candidate in candidates)
            {
                // The WHERE still carries the status we read, so a racing claimer that already moved the row makes
                // this affect zero rows and we skip it.
                var row = await _db.CommMessages
                    .FirstOrDefaultAsync(m => m.Id == candidate.Id && m.Status == candidate.Status, cancellationToken);
                if (row == null) continue;

                row.Status = CommMessageStatus.Claimed;
                row.Attempts += 1;
                row.ClaimedAt = DateTime.UtcNow;
                row.UpdatedAt = DateTime.UtcNow;
                try { await _db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateConcurrencyException) { _db.Entry(row).State = EntityState.Detached; continue; }

                claimed.Add(new CommMessageWorkItem { MessageId = row.Id, CompanyId = row.CompanyID, Attempts = row.Attempts });
                _db.Entry(row).State = EntityState.Detached;
            }
            return claimed;
        }

        public async Task MarkSentAsync(int messageId, CancellationToken cancellationToken = default)
        {
            var row = await _db.CommMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
            if (row == null) return;
            row.Status = CommMessageStatus.Sent;
            row.SentAt = DateTime.UtcNow;
            row.Error = null;
            row.ClaimedAt = null;          // terminal: release the claim clock
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(row).State = EntityState.Detached;
        }

        public async Task MarkFailedAsync(int messageId, string error, CancellationToken cancellationToken = default)
        {
            var row = await _db.CommMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
            if (row == null) return;
            row.Status = CommMessageStatus.Failed;
            row.Error = Truncate(error, ErrorColumnLength);
            row.ClaimedAt = null;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(row).State = EntityState.Detached;
        }

        private static string Truncate(string? value, int max)
            => string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value.Substring(0, max));

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
    }

    // The frozen status vocabulary, mirrored by CK_CommMessages_Status in
    // deploy/sql/comm_outbox_slice_003.sql. "Claimed" is new in Stage 0.
    public static class CommMessageStatus
    {
        public const string Queued = "Queued";
        public const string Claimed = "Claimed";
        public const string Sent = "Sent";
        public const string Failed = "Failed";
    }
}
