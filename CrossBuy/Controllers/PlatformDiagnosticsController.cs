using System.Data.Common;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.Controllers;

/// <summary>
/// Read-only diagnostics for platform operators. This deliberately reports only connection identity
/// and schema readiness; it never returns a connection string or reads business rows.
/// </summary>
[SessionValidation]
[Route("Platform/Diagnostics")]
public sealed class PlatformDiagnosticsController : Controller
{
    private readonly IBusinessContextAccessor _contexts;
    private readonly IPlatformAdminIdentity _platformIdentity;
    private readonly CrossDbContext _db;
    private readonly ICommTaskLinkSchemaProbe _taskLinkSchema;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlatformDiagnosticsController> _logger;

    public PlatformDiagnosticsController(
        IBusinessContextAccessor contexts,
        IPlatformAdminIdentity platformIdentity,
        CrossDbContext db,
        ICommTaskLinkSchemaProbe taskLinkSchema,
        IConfiguration configuration,
        ILogger<PlatformDiagnosticsController> logger)
    {
        _contexts = contexts;
        _platformIdentity = platformIdentity;
        _db = db;
        _taskLinkSchema = taskLinkSchema;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("CommunicationTaskLinks")]
    public async Task<IActionResult> CommunicationTaskLinks(CancellationToken cancellationToken)
    {
        var context = await _contexts.TryGetCurrentAsync(cancellationToken);
        if (!await PlatformAdministrationReader.CanReadAsync(context, _platformIdentity, cancellationToken))
            return Forbid();

        var featureEnabled = _configuration.GetValue<bool>("CommunicationPlatform:TaskLinksProvisioned");
        string? serverName = null;
        string? databaseName = null;
        string? connectionError = null;

        try
        {
            var connection = _db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await _db.Database.OpenConnectionAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), DB_NAME()";
            command.CommandTimeout = 5;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                serverName = reader.IsDBNull(0) ? null : reader.GetString(0);
                databaseName = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (Exception ex)
        {
            // Keep secrets and provider connection details out of the response. The full exception is
            // available only in the server log for an authorized operator.
            _logger.LogError(ex, "Platform database connectivity diagnostic failed.");
            connectionError = ex.GetType().Name;
        }

        CommTaskLinkSchemaStatus? schema = null;
        if (databaseName != null)
            schema = await _taskLinkSchema.CheckAsync(cancellationToken);

        return Json(new
        {
            databaseConnected = databaseName != null,
            serverName,
            databaseName,
            expectedDatabase = "CrossBuyDB2",
            expectedDatabaseConnected = string.Equals(databaseName, "CrossBuyDB2", StringComparison.OrdinalIgnoreCase),
            taskLinksProvisioned = featureEnabled,
            taskLinkSchemaVerified = schema?.Provisioned,
            missingSchemaObjects = schema?.Missing ?? Array.Empty<string>(),
            schemaCheckInconclusive = schema?.Inconclusive,
            connectionError
        });
    }
}
