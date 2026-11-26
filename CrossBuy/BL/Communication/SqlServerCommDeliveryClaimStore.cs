using System.Data;
using System.Data.Common;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication;

/// <summary>
/// Atomically reserves a bounded notification batch on SQL Server. UPDATE ... OUTPUT means two
/// workers cannot both observe and claim the same Pending row; READPAST lets a competing worker
/// continue to the next unlocked row. SQLite and other providers continue to use the EF store.
/// </summary>
public sealed class SqlServerCommDeliveryClaimStore : ICommDeliveryClaimStore
{
    private const string ClaimSql = @"
;WITH [NextBatch] AS
(
    SELECT TOP (@BatchSize) *
    FROM [dbo].[CommNotificationDeliveries] WITH (ROWLOCK, READPAST, UPDLOCK)
    WHERE [CompanyID] = @CompanyId
      AND [Attempts] < @MaxAttempts
      AND
      (
          [Status] = @PendingStatus
          OR [Status] = @FailedStatus
          OR ([Status] = @ClaimedStatus AND [ClaimedAt] IS NOT NULL AND [ClaimedAt] < @StaleBefore)
      )
    ORDER BY [Id] ASC
)
UPDATE [NextBatch]
SET [Status] = @ClaimedStatus,
    [ClaimedAt] = @Now,
    [UpdatedAt] = @Now
OUTPUT INSERTED.[Id], INSERTED.[CompanyID], INSERTED.[NotificationId], INSERTED.[Channel],
       INSERTED.[Status], INSERTED.[Attempts], INSERTED.[ClaimedAt], INSERTED.[SentAt],
       INSERTED.[Error], INSERTED.[ExternalReference], INSERTED.[DedupKey],
       INSERTED.[CreatedBy], INSERTED.[CreatedAt], INSERTED.[updatedBy], INSERTED.[UpdatedAt];";

    private readonly CrossDbContext _db;
    private readonly CommunicationPlatformOptions _options;

    public SqlServerCommDeliveryClaimStore(CrossDbContext db, IOptions<CommunicationPlatformOptions> options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<CommNotificationDelivery>> ClaimAsync(
        int? companyId, int batchSize, CancellationToken cancellationToken = default)
    {
        if (companyId is null or <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId), "SQL notification claims require an explicit positive company scope.");

        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            command.CommandTimeout = 30;
            if (_db.Database.CurrentTransaction != null)
                command.Transaction = _db.Database.CurrentTransaction.GetDbTransaction();

            Add(command, "@BatchSize", DbType.Int32, Math.Max(1, batchSize));
            Add(command, "@CompanyId", DbType.Int32, companyId.Value);
            Add(command, "@MaxAttempts", DbType.Int32, Math.Max(1, _options.MaxDeliveryAttempts));
            Add(command, "@PendingStatus", DbType.String, CommDeliveryStatus.Pending, 40);
            Add(command, "@FailedStatus", DbType.String, CommDeliveryStatus.Failed, 40);
            Add(command, "@ClaimedStatus", DbType.String, CommDeliveryStatus.Claimed, 40);

            var now = DateTime.UtcNow;
            Add(command, "@Now", DbType.DateTime2, now);
            Add(command, "@StaleBefore", DbType.DateTime2,
                now.AddMinutes(-Math.Max(1, _options.StaleClaimMinutes)));

            var rows = new List<CommNotificationDelivery>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var ord = Enumerable.Range(0, reader.FieldCount)
                .ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new CommNotificationDelivery
                {
                    Id = reader.GetInt64(ord["Id"]),
                    CompanyID = reader.GetInt32(ord["CompanyID"]),
                    NotificationId = reader.GetInt64(ord["NotificationId"]),
                    Channel = reader.GetString(ord["Channel"]),
                    Status = reader.GetString(ord["Status"]),
                    Attempts = reader.GetInt32(ord["Attempts"]),
                    ClaimedAt = NullableDateTime(reader, ord["ClaimedAt"]),
                    SentAt = NullableDateTime(reader, ord["SentAt"]),
                    Error = NullableString(reader, ord["Error"]),
                    ExternalReference = NullableString(reader, ord["ExternalReference"]),
                    DedupKey = reader.GetString(ord["DedupKey"]),
                    CreatedBy = NullableInt32(reader, ord["CreatedBy"]),
                    CreatedAt = NullableDateTime(reader, ord["CreatedAt"]),
                    updatedBy = NullableInt32(reader, ord["updatedBy"]),
                    UpdatedAt = NullableDateTime(reader, ord["UpdatedAt"]),
                });
            }

            return rows;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static void Add(DbCommand command, string name, DbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }

    private static DateTime? NullableDateTime(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static int? NullableInt32(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? NullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
