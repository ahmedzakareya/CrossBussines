using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction
{
	// ==========================================================================================
	// Construction C1 — CR-02: THE SUBCONTRACTOR CERTIFICATION CAP.  Business decision D-07: HARD BLOCK.
	//
	// WHAT WAS WRONG:
	//   SubcontractBillingService computes period work as `cumulative − previouslyBilled` from a FREE-TYPED
	//   cumulative decimal. There are no certificate LINES at all, and no comparison to
	//   Subcontract.ContractValue or to any allocated scope. A subcontractor could therefore be certified
	//   without limit, and nothing in the system would notice.
	//
	// WHAT THIS DOES:
	//   * SubcontractScope carries the assigned quantity and rate — THE CAP.
	//   * Certification happens LINE BY LINE against a scope, and a line that would take the cumulative
	//     past the cap is REFUSED. Not warned. Refused, with the cap and the attempted figure named.
	//   * The cap moves only through RaiseCapAsync, which demands an APPROVED variation of the same
	//     project. Editing the assigned quantity once anything is certified is refused.
	//   * A second, independent VALUE cap applies against the subcontract's own value, because a scope
	//     set generously can still exceed what the subcontract is worth.
	//   * Correction is an adjustment LINE that names the line it adjusts. A certified line is never edited.
	//
	// This service posts NOTHING to the ledger. Accounting posting stays exactly where it is, in
	// PayableService via SubcontractBillingService — unchanged by this increment.
	// ==========================================================================================

	public sealed class CertifyLineInput
	{
		public int CompanyID { get; init; }
		public int SubcontractBillingId { get; init; }
		public int SubcontractScopeId { get; init; }

		/// Positive for work certified this period; negative ONLY as an adjustment, which must name
		/// AdjustsLineId.
		public decimal CurrentQuantity { get; init; }

		public int? ApprovedVariationOrderId { get; init; }
		public int? AdjustsLineId { get; init; }
		public int? ActorEmployeeId { get; init; }
		public string? Reason { get; init; }
		public byte[]? ExpectedScopeToken { get; init; }
	}

	public sealed class SubcontractCapLine
	{
		public int ScopeId { get; init; }
		public int? BoqItemId { get; init; }
		public string? ScopeCode { get; init; }
		public decimal AssignedQuantity { get; init; }
		public decimal ApprovedVariationQuantity { get; init; }
		public decimal CappedQuantity { get; init; }
		public decimal SubRate { get; init; }
		public decimal CappedValue { get; init; }
		public decimal CertifiedQuantity { get; init; }
		public decimal CertifiedValue { get; init; }
		public decimal RemainingQuantity => CappedQuantity - CertifiedQuantity;
	}

	public sealed class SubcontractCapView
	{
		public int SubcontractId { get; init; }
		public decimal? ContractValue { get; init; }
		public decimal TotalCappedValue { get; init; }
		public decimal TotalCertifiedValue { get; init; }
		public decimal RemainingValue => (ContractValue ?? TotalCappedValue) - TotalCertifiedValue;
		public List<SubcontractCapLine> Lines { get; init; } = new();
	}

	public interface ISubcontractScopeService
	{
		Task<(bool ok, string? error, int scopeId)> SaveScopeAsync(
			int companyId, int projectId, int subcontractId, int? boqItemId, string? scopeCode,
			string? description, string? unit, decimal assignedQuantity, decimal subRate,
			int? actorEmployeeId, string reason, int scopeId = 0, byte[]? expectedToken = null,
			CancellationToken ct = default);

		/// The ONLY way the cap moves. Demands an approved variation of the same project.
		Task<(bool ok, string? error)> RaiseCapAsync(
			int companyId, int scopeId, decimal additionalQuantity, int variationOrderId,
			int? actorEmployeeId, string reason, byte[]? expectedToken = null, CancellationToken ct = default);

		Task<(bool ok, string? error, int lineId)> CertifyLineAsync(
			CertifyLineInput input, CancellationToken ct = default);

		Task<SubcontractCapView> GetCapAsync(int companyId, int subcontractId, CancellationToken ct = default);

		/// The reconciliation guard: does the certificate HEADER agree with its lines?
		Task<(bool ok, string? error)> ValidateHeaderAgainstLinesAsync(
			int companyId, int subcontractBillingId, CancellationToken ct = default);
	}

	public sealed class SubcontractScopeService : ISubcontractScopeService
	{
		private const string Source = nameof(SubcontractScopeService);

		private readonly CrossDbContext _db;
		private readonly IConstructionAuditService _audit;

		public SubcontractScopeService(CrossDbContext db, IConstructionAuditService audit)
		{
			_db = db; _audit = audit;
		}

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		// Plain invariant formatting, no thousands separators: a refusal message must contain the
		// figures in a form a human — and a test — can match literally.
		private static string Q(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
		private static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

		public async Task<(bool ok, string? error, int scopeId)> SaveScopeAsync(
			int companyId, int projectId, int subcontractId, int? boqItemId, string? scopeCode,
			string? description, string? unit, decimal assignedQuantity, decimal subRate,
			int? actorEmployeeId, string reason, int scopeId = 0, byte[]? expectedToken = null,
			CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "Unresolved company", 0);
			if (string.IsNullOrWhiteSpace(reason)) return (false, "A reason for the change is required", 0);
			if (assignedQuantity <= 0) return (false, "The assigned quantity must be positive", 0);
			if (subRate < 0) return (false, "The rate cannot be negative", 0);

			var sub = await _db.Subcontracts.AsNoTracking()
				.FirstOrDefaultAsync(s => s.ID == subcontractId && s.CompanyID == companyId && s.ProjectId == projectId, ct);
			if (sub == null) return (false, "Subcontract not found", 0);

			if (boqItemId is > 0 &&
				!await _db.BoqItems.AnyAsync(b => b.ID == boqItemId && b.CompanyID == companyId && b.ProjectId == projectId, ct))
				return (false, "The BOQ line was not found in this project", 0);

			var correlation = Guid.NewGuid();
			var now = DateTime.UtcNow;
			var drafts = new List<ConstructionAuditDraft>();

			SubcontractScope scope;
			if (scopeId > 0)
			{
				scope = await _db.SubcontractScopes
					.FirstOrDefaultAsync(s => s.ID == scopeId && s.CompanyID == companyId, ct)
					?? throw new InvalidOperationException("scope not found");

				if (!ConstructionConcurrency.Matches(expectedToken, scope.ConcurrencyToken))
					return (false, ConstructionConcurrency.ConflictMessage, 0);

				// D-07: once anything is certified, the assigned quantity is frozen. Raising it is a
				// commercial act that requires an approved variation — not an edit of this field.
				if (scope.AssignedQuantity != assignedQuantity)
				{
					decimal certified = await CertifiedQuantityAsync(scope.ID, ct);
					if (certified != 0m)
						return (false,
							$"The assigned quantity cannot be changed after certification ({Q(certified)} certified) — an approved variation is required " +
							$"(assigned quantity is frozen once certified — raise the cap with an approved variation)", 0);

					drafts.Add(ConstructionAuditDraft.Numeric(companyId, projectId,
						ConstructionAuditEntityTypes.SubcontractScope, scope.ID, null, "AssignedQuantity",
						scope.AssignedQuantity, assignedQuantity, ConstructionChangeKind.Updated, reason,
						actorEmployeeId, Source, correlation));
				}

				if (scope.SubRate != subRate)
					drafts.Add(ConstructionAuditDraft.Numeric(companyId, projectId,
						ConstructionAuditEntityTypes.SubcontractScope, scope.ID, null, "SubRate",
						scope.SubRate, subRate, ConstructionChangeKind.Updated, reason,
						actorEmployeeId, Source, correlation));

				scope.UpdatedAt = now;
				scope.UpdatedBy = actorEmployeeId;
			}
			else
			{
				scope = new SubcontractScope
				{
					CompanyID = companyId, ProjectId = projectId, SubcontractId = subcontractId,
					Status = SubcontractScopeStatus.Active, CreatedAt = now, CreatedBy = actorEmployeeId
				};
				_db.SubcontractScopes.Add(scope);

				drafts.Add(ConstructionAuditDraft.Numeric(companyId, projectId,
					ConstructionAuditEntityTypes.SubcontractScope, 0, null, "AssignedQuantity",
					0m, assignedQuantity, ConstructionChangeKind.Created, reason,
					actorEmployeeId, Source, correlation));
			}

			scope.BoqItemId = boqItemId;
			scope.ScopeCode = string.IsNullOrWhiteSpace(scopeCode) ? null : scopeCode.Trim();
			scope.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
			scope.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
			scope.AssignedQuantity = assignedQuantity;
			scope.SubRate = subRate;
			scope.ConcurrencyToken = ConstructionConcurrency.NewToken();

			await _db.SaveChangesAsync(ct);

			// Audit rows for a NEW scope carry the id only after the insert assigned it.
			_audit.Record(drafts.Select(d => d.EntityId == 0
				? ConstructionAuditDraft.Numeric(d.CompanyID, d.ProjectId, d.EntityType, scope.ID, d.LineId,
					d.FieldName!, d.OldNumeric ?? 0m, d.NewNumeric ?? 0m, d.ChangeKind, d.Reason,
					d.ActorEmployeeId, d.SourceContext!, d.CorrelationId, d.RevisionId)
				: d));
			await _db.SaveChangesAsync(ct);

			return (true, null, scope.ID);
		}

		public async Task<(bool ok, string? error)> RaiseCapAsync(
			int companyId, int scopeId, decimal additionalQuantity, int variationOrderId,
			int? actorEmployeeId, string reason, byte[]? expectedToken = null, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(reason)) return (false, "A reason for raising the cap is required");
			if (additionalQuantity <= 0) return (false, "The additional quantity must be positive");

			var scope = await _db.SubcontractScopes
				.FirstOrDefaultAsync(s => s.ID == scopeId && s.CompanyID == companyId, ct);
			if (scope == null) return (false, "Scope not found");

			if (!ConstructionConcurrency.Matches(expectedToken, scope.ConcurrencyToken))
				return (false, ConstructionConcurrency.ConflictMessage);

			// D-07: the cap moves ONLY on an approved variation of this very project.
			var vo = await _db.VariationOrders.AsNoTracking()
				.FirstOrDefaultAsync(v => v.ID == variationOrderId && v.CompanyID == companyId
										  && v.ProjectId == scope.ProjectId, ct);
			if (vo == null)
				return (false, "Variation not found for this project");
			if (vo.Status != "Approved")
				return (false, $"The variation is not approved — status {vo.Status}");

			decimal capBefore = scope.CappedQuantity;
			scope.ApprovedVariationQuantity += additionalQuantity;
			scope.LastCapVariationOrderId = variationOrderId;
			scope.ConcurrencyToken = ConstructionConcurrency.NewToken();
			scope.UpdatedAt = DateTime.UtcNow;
			scope.UpdatedBy = actorEmployeeId;

			_audit.Record(new[]
			{
				ConstructionAuditDraft.Numeric(companyId, scope.ProjectId,
					ConstructionAuditEntityTypes.SubcontractScope, scope.ID, null, "CappedQuantity",
					capBefore, scope.CappedQuantity, ConstructionChangeKind.CapRaised,
					$"{reason} [VO#{vo.VoNo} id={vo.ID}]", actorEmployeeId, Source, Guid.NewGuid())
			});

			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<(bool ok, string? error, int lineId)> CertifyLineAsync(
			CertifyLineInput input, CancellationToken ct = default)
		{
			if (input.CompanyID <= 0) return (false, "Unresolved company", 0);
			if (string.IsNullOrWhiteSpace(input.Reason)) return (false, "A reason for the approval is required", 0);

			var scope = await _db.SubcontractScopes
				.FirstOrDefaultAsync(s => s.ID == input.SubcontractScopeId && s.CompanyID == input.CompanyID, ct);
			if (scope == null) return (false, "Scope not found", 0);
			if (scope.Status != SubcontractScopeStatus.Active) return (false, "The scope is not active", 0);

			if (!ConstructionConcurrency.Matches(input.ExpectedScopeToken, scope.ConcurrencyToken))
				return (false, ConstructionConcurrency.ConflictMessage, 0);

			var header = await _db.SubcontractBillings
				.FirstOrDefaultAsync(b => b.ID == input.SubcontractBillingId && b.CompanyID == input.CompanyID, ct);
			if (header == null) return (false, "Certificate not found", 0);
			if (header.SubcontractId != scope.SubcontractId)
				return (false, "The certificate belongs to a different subcontract", 0);

			// Previous certificates are immutable: lines are only ever added to a DRAFT certificate.
			if (!string.Equals(header.Status, "Draft", StringComparison.Ordinal))
				return (false, $"Lines may only be added while the certificate is Draft — it is {header.Status}", 0);

			// Quantity domain. A negative quantity is a CORRECTION and must name the line it corrects.
			if (input.CurrentQuantity == 0m)
				return (false, "The quantity is zero — a zero quantity certifies nothing", 0);
			if (input.CurrentQuantity < 0m && input.AdjustsLineId is not > 0)
				return (false, "A negative quantity is only allowed as an adjustment that references the line being adjusted " +
							   "(a negative quantity is only allowed as an adjustment naming the line it adjusts)", 0);

			if (input.AdjustsLineId is > 0 &&
				!await _db.SubcontractCertificateLines.AnyAsync(
					l => l.ID == input.AdjustsLineId && l.CompanyID == input.CompanyID
						 && l.SubcontractScopeId == scope.ID, ct))
				return (false, "The adjusted line was not found on this scope", 0);

			decimal previousQty = await CertifiedQuantityAsync(scope.ID, ct);
			decimal cumulativeQty = previousQty + input.CurrentQuantity;

			if (cumulativeQty < 0m)
				return (false, $"The adjustment exceeds what was certified ({Q(previousQty)}) — the result would be negative " +
							   $"(the adjustment would take cumulative certified below zero)", 0);

			// ---- THE HARD BLOCK (D-07): quantity cap -----------------------------------------------
			if (cumulativeQty > scope.CappedQuantity)
				return (false,
					$"Exceeds the approved scope: the cap is {Q(scope.CappedQuantity)} and {Q(cumulativeQty)} is requested — " +
					$"an approved variation is required to raise the cap " +
					$"(cap {Q(scope.CappedQuantity)}, attempted {Q(cumulativeQty)} — an approved variation is required)", 0);

			decimal rate = scope.SubRate;
			decimal currentValue = R(input.CurrentQuantity * rate);
			decimal previousValue = R(previousQty * rate);
			decimal cumulativeValue = R(cumulativeQty * rate);

			// ---- THE VALUE CAP — independent of the quantity cap -----------------------------------
			var sub = await _db.Subcontracts.AsNoTracking()
				.FirstOrDefaultAsync(s => s.ID == scope.SubcontractId && s.CompanyID == input.CompanyID, ct);
			if (sub?.ContractValue is > 0m)
			{
				decimal otherScopes = await CertifiedValueForSubcontractAsync(input.CompanyID, scope.SubcontractId, scope.ID, ct);
				decimal totalAfter = R(otherScopes + cumulativeValue);
				if (totalAfter > sub.ContractValue.Value)
					return (false,
						$"Exceeds the subcontract value: the value is {M(sub.ContractValue.Value)} and {M(totalAfter)} is requested — " +
						$"an approved variation is required " +
						$"(subcontract value {M(sub.ContractValue.Value)}, attempted {M(totalAfter)} — an approved variation is required)", 0);
			}

			// A quantity above the ORIGINAL assignment must name the variation that authorised it.
			if (cumulativeQty > scope.AssignedQuantity && input.ApprovedVariationOrderId is not > 0
				&& scope.LastCapVariationOrderId is not > 0)
				return (false, "The quantity exceeds the original assignment with no approved variation", 0);

			int? revisionId = null;
			if (scope.BoqItemId is > 0)
				revisionId = await _db.BoqLineStates.AsNoTracking()
					.Where(s => s.BoqItemId == scope.BoqItemId)
					.Select(s => s.CurrentRevisionId).FirstOrDefaultAsync(ct);

			var line = new SubcontractCertificateLine
			{
				CompanyID = input.CompanyID,
				SubcontractBillingId = header.ID,
				SubcontractId = scope.SubcontractId,
				SubcontractScopeId = scope.ID,
				BoqItemId = scope.BoqItemId,
				CommercialRevisionId = revisionId,
				ApprovedVariationOrderId = input.ApprovedVariationOrderId ?? scope.LastCapVariationOrderId,
				RateSnapshot = rate,
				PreviousQuantity = previousQty,
				PreviousValue = previousValue,
				CurrentQuantity = input.CurrentQuantity,
				CurrentValue = currentValue,
				CumulativeQuantity = cumulativeQty,
				CumulativeValue = cumulativeValue,
				AdjustsLineId = input.AdjustsLineId,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = input.ActorEmployeeId,
				ConcurrencyToken = ConstructionConcurrency.NewToken()
			};
			_db.SubcontractCertificateLines.Add(line);
			await _db.SaveChangesAsync(ct);

			_audit.Record(new[]
			{
				ConstructionAuditDraft.Numeric(input.CompanyID, scope.ProjectId,
					ConstructionAuditEntityTypes.SubcontractCertificate, header.ID, line.ID, "CertifiedQuantity",
					previousQty, cumulativeQty, ConstructionChangeKind.Created, input.Reason,
					input.ActorEmployeeId, Source, Guid.NewGuid(), revisionId)
			});
			await _db.SaveChangesAsync(ct);

			return (true, null, line.ID);
		}

		public async Task<SubcontractCapView> GetCapAsync(int companyId, int subcontractId, CancellationToken ct = default)
		{
			var sub = await _db.Subcontracts.AsNoTracking()
				.FirstOrDefaultAsync(s => s.ID == subcontractId && s.CompanyID == companyId, ct);

			var scopes = await _db.SubcontractScopes.AsNoTracking()
				.Where(s => s.CompanyID == companyId && s.SubcontractId == subcontractId)
				.OrderBy(s => s.ID).ToListAsync(ct);

			var certified = await _db.SubcontractCertificateLines.AsNoTracking()
				.Where(l => l.CompanyID == companyId && l.SubcontractId == subcontractId)
				.GroupBy(l => l.SubcontractScopeId)
				.Select(g => new { ScopeId = g.Key, Qty = g.Sum(x => x.CurrentQuantity), Val = g.Sum(x => x.CurrentValue) })
				.ToListAsync(ct);
			var byScope = certified.ToDictionary(x => x.ScopeId);

			var lines = scopes.Select(s => new SubcontractCapLine
			{
				ScopeId = s.ID,
				BoqItemId = s.BoqItemId,
				ScopeCode = s.ScopeCode,
				AssignedQuantity = s.AssignedQuantity,
				ApprovedVariationQuantity = s.ApprovedVariationQuantity,
				CappedQuantity = s.CappedQuantity,
				SubRate = s.SubRate,
				CappedValue = s.CappedValue,
				CertifiedQuantity = byScope.TryGetValue(s.ID, out var c) ? R(c.Qty) : 0m,
				CertifiedValue = byScope.TryGetValue(s.ID, out var v) ? R(v.Val) : 0m
			}).ToList();

			return new SubcontractCapView
			{
				SubcontractId = subcontractId,
				ContractValue = sub?.ContractValue,
				TotalCappedValue = R(lines.Sum(l => l.CappedValue)),
				TotalCertifiedValue = R(lines.Sum(l => l.CertifiedValue)),
				Lines = lines
			};
		}

		public async Task<(bool ok, string? error)> ValidateHeaderAgainstLinesAsync(
			int companyId, int subcontractBillingId, CancellationToken ct = default)
		{
			var header = await _db.SubcontractBillings.AsNoTracking()
				.FirstOrDefaultAsync(b => b.ID == subcontractBillingId && b.CompanyID == companyId, ct);
			if (header == null) return (false, "Certificate not found");

			decimal lineTotal = R(await _db.SubcontractCertificateLines.AsNoTracking()
				.Where(l => l.CompanyID == companyId && l.SubcontractBillingId == subcontractBillingId)
				.SumAsync(l => (decimal?)l.CurrentValue, ct) ?? 0m);

			if (R(header.GrossWork) != lineTotal)
				return (false,
					$"The certificate value does not match the sum of its lines: lines {M(lineTotal)}, header {M(header.GrossWork)} " +
					$"(lines {M(lineTotal)}, header {M(header.GrossWork)} — a header amount may not bypass the line controls)");

			return (true, null);
		}

		// ------------------------------------------------------------------------------------------
		/// Cumulative certified quantity on a scope = the SUM of its line movements (adjustments are
		/// negative movements). Derived, never stored, so a stored total cannot drift from its lines.
		private async Task<decimal> CertifiedQuantityAsync(int scopeId, CancellationToken ct) =>
			R(await _db.SubcontractCertificateLines.AsNoTracking()
				.Where(l => l.SubcontractScopeId == scopeId)
				.SumAsync(l => (decimal?)l.CurrentQuantity, ct) ?? 0m);

		private async Task<decimal> CertifiedValueForSubcontractAsync(
			int companyId, int subcontractId, int exceptScopeId, CancellationToken ct) =>
			R(await _db.SubcontractCertificateLines.AsNoTracking()
				.Where(l => l.CompanyID == companyId && l.SubcontractId == subcontractId && l.SubcontractScopeId != exceptScopeId)
				.SumAsync(l => (decimal?)l.CurrentValue, ct) ?? 0m);
	}
}
