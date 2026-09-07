using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction
{
	// ==========================================================================================
	// Construction C1 — line-level, append-only audit.
	//
	// Before C1 the construction entities carried CreatedBy/CreatedAt and, on postable documents,
	// PostedBy/PostedAt. That answers "who made this" and nothing else: a rate that changed yesterday
	// could not be shown as having changed, by whom, or why.
	//
	// Three properties this service has by construction, not by convention:
	//
	//   1. APPEND-ONLY. There is no update, delete, amend or purge method — asserted structurally by
	//      ConstructionC1ConcurrencyAndAuditTests.The_audit_service_exposes_no_way_to_change_or_delete_history,
	//      because an audit trail that can be edited is not evidence.
	//   2. WRITTEN IN THE CALLER'S TRANSACTION. RecordAsync only ADDS to the change tracker; the caller
	//      saves. An audit row that could be lost independently of the change it records would make the
	//      history look complete when it is not.
	//   3. A REASON IS MANDATORY for a commercial value. Enforced here (RequireReason) rather than left
	//      to each caller, so a new caller cannot forget it.
	// ==========================================================================================

	/// One field-level change, staged for the caller's transaction.
	public sealed class ConstructionAuditDraft
	{
		public int CompanyID { get; init; }
		public int? ProjectId { get; init; }
		public string EntityType { get; init; } = "";
		public int EntityId { get; init; }
		public int? LineId { get; init; }
		public string? FieldName { get; init; }
		public string? OldValue { get; init; }
		public string? NewValue { get; init; }
		public decimal? OldNumeric { get; init; }
		public decimal? NewNumeric { get; init; }
		public string ChangeKind { get; init; } = ConstructionChangeKind.Updated;
		public string? Reason { get; init; }
		public int? ActorEmployeeId { get; init; }
		public string? ActorUserId { get; init; }
		public string? SourceContext { get; init; }
		public Guid CorrelationId { get; init; }
		public int? RevisionId { get; init; }

		/// Convenience for a numeric field: sets text and numeric forms consistently.
		public static ConstructionAuditDraft Numeric(
			int companyId, int? projectId, string entityType, int entityId, int? lineId,
			string fieldName, decimal oldValue, decimal newValue, string changeKind, string? reason,
			int? actorEmployeeId, string sourceContext, Guid correlationId, int? revisionId = null) =>
			new()
			{
				CompanyID = companyId, ProjectId = projectId, EntityType = entityType, EntityId = entityId,
				LineId = lineId, FieldName = fieldName,
				OldValue = oldValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
				NewValue = newValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
				OldNumeric = oldValue, NewNumeric = newValue,
				ChangeKind = changeKind, Reason = reason, ActorEmployeeId = actorEmployeeId,
				SourceContext = sourceContext, CorrelationId = correlationId, RevisionId = revisionId
			};

		public static ConstructionAuditDraft Text(
			int companyId, int? projectId, string entityType, int entityId, int? lineId,
			string fieldName, string? oldValue, string? newValue, string changeKind, string? reason,
			int? actorEmployeeId, string sourceContext, Guid correlationId, int? revisionId = null) =>
			new()
			{
				CompanyID = companyId, ProjectId = projectId, EntityType = entityType, EntityId = entityId,
				LineId = lineId, FieldName = fieldName, OldValue = oldValue, NewValue = newValue,
				ChangeKind = changeKind, Reason = reason, ActorEmployeeId = actorEmployeeId,
				SourceContext = sourceContext, CorrelationId = correlationId, RevisionId = revisionId
			};
	}

	public interface IConstructionAuditService
	{
		/// Stages audit rows in the CALLER'S transaction. Returns how many rows were staged.
		/// Does NOT call SaveChanges — the caller's save is what makes the change and its history atomic.
		int Record(IEnumerable<ConstructionAuditDraft> drafts);

		Task<List<ConstructionAuditEntry>> ForEntityAsync(
			int companyId, string entityType, int entityId, CancellationToken ct = default);

		Task<List<ConstructionAuditEntry>> ForCompanyAsync(
			int companyId, string? entityType = null, CancellationToken ct = default);

		Task<List<ConstructionAuditEntry>> ForCorrelationAsync(
			int companyId, Guid correlationId, CancellationToken ct = default);
	}

	public sealed class ConstructionAuditService : IConstructionAuditService
	{
		// Fields whose change is a COMMERCIAL decision and therefore may never be recorded without a
		// stated reason. Compared case-insensitively so a caller's casing cannot bypass the rule.
		private static readonly HashSet<string> CommercialFields = new(StringComparer.OrdinalIgnoreCase)
		{
			"Quantity", "UnitPrice", "Rate", "SubRate", "RateSnapshot", "AssignedQuantity",
			"ApprovedVariationQuantity", "CappedQuantity", "ContractValue", "Amount", "Value",
			"RetentionPercent", "AdvancePercent", "TaxRate", "CertifiedQuantity", "CertifiedValue"
		};

		private readonly CrossDbContext _db;
		public ConstructionAuditService(CrossDbContext db) { _db = db; }

		public int Record(IEnumerable<ConstructionAuditDraft> drafts)
		{
			if (drafts == null) return 0;
			var now = DateTime.UtcNow;
			int count = 0;

			foreach (var d in drafts)
			{
				if (d.CompanyID <= 0)
					throw new InvalidOperationException(
						"Construction audit refused: CompanyID is unresolved. An unresolved company writes nothing.");
				if (string.IsNullOrWhiteSpace(d.EntityType))
					throw new InvalidOperationException("Construction audit refused: EntityType is required.");
				if (d.CorrelationId == Guid.Empty)
					throw new InvalidOperationException(
						"Construction audit refused: a CorrelationId is required, so one user action stays recognisable " +
						"as one action across the rows it produced.");
				if (d.FieldName != null && CommercialFields.Contains(d.FieldName) && string.IsNullOrWhiteSpace(d.Reason))
					throw new InvalidOperationException(
						$"Construction audit refused: '{d.FieldName}' is a commercial value and a change to it requires a reason.");

				_db.ConstructionAuditEntries.Add(new ConstructionAuditEntry
				{
					CompanyID = d.CompanyID,
					ProjectId = d.ProjectId,
					EntityType = d.EntityType,
					EntityId = d.EntityId,
					LineId = d.LineId,
					FieldName = d.FieldName,
					OldValue = d.OldValue,
					NewValue = d.NewValue,
					OldNumeric = d.OldNumeric,
					NewNumeric = d.NewNumeric,
					ChangeKind = ConstructionChangeKind.IsKnown(d.ChangeKind) ? d.ChangeKind : ConstructionChangeKind.Updated,
					Reason = string.IsNullOrWhiteSpace(d.Reason) ? null : d.Reason.Trim(),
					ActorEmployeeId = d.ActorEmployeeId,
					ActorUserId = d.ActorUserId,
					OccurredAt = now,
					SourceContext = d.SourceContext,
					CorrelationId = d.CorrelationId,
					RevisionId = d.RevisionId
				});
				count++;
			}
			return count;
		}

		public Task<List<ConstructionAuditEntry>> ForEntityAsync(
			int companyId, string entityType, int entityId, CancellationToken ct = default) =>
			_db.ConstructionAuditEntries.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.EntityType == entityType && a.EntityId == entityId)
				.OrderBy(a => a.ID)
				.ToListAsync(ct);

		public Task<List<ConstructionAuditEntry>> ForCompanyAsync(
			int companyId, string? entityType = null, CancellationToken ct = default) =>
			_db.ConstructionAuditEntries.AsNoTracking()
				.Where(a => a.CompanyID == companyId && (entityType == null || a.EntityType == entityType))
				.OrderBy(a => a.ID)
				.ToListAsync(ct);

		public Task<List<ConstructionAuditEntry>> ForCorrelationAsync(
			int companyId, Guid correlationId, CancellationToken ct = default) =>
			_db.ConstructionAuditEntries.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.CorrelationId == correlationId)
				.OrderBy(a => a.ID)
				.ToListAsync(ct);
	}

	// ==========================================================================================
	// The concurrency token. See ConstructionCommercial.cs for why it is application-rotated rather
	// than SQL Server `rowversion`.
	// ==========================================================================================
	public static class ConstructionConcurrency
	{
		/// A fresh token. Rotated on EVERY write, so a token can never be replayed.
		public static byte[] NewToken() => Guid.NewGuid().ToByteArray();

		/// The refusal message. One wording everywhere, because the caller-facing contract of a
		/// concurrency failure is "someone else changed this — reload and compare", never "save failed".
		public const string ConflictMessage =
			"This record was changed by another user — reload and compare before saving " +
			"(this record was changed by another user — reload and compare before saving)";

		public static bool Matches(byte[]? expected, byte[] actual)
		{
			if (expected == null) return true;             // caller did not present a token: no check requested
			if (expected.Length != actual.Length) return false;
			return expected.AsSpan().SequenceEqual(actual);
		}
	}
}
