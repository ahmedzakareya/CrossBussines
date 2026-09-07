using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction
{
	// ==========================================================================================
	// Construction C1 — CR-03: IMMUTABLE COMMERCIAL REVISIONS.
	//
	// WHAT WAS WRONG:
	//   VariationOrderService.ApproveAsync assigned the new quantity and rate STRAIGHT ONTO the live
	//   BoqItem. The previous values survived only as OldQuantity/OldUnitPrice on the variation line,
	//   and a certificate line carries neither a revision nor a rate — so a POSTED certificate's
	//   commercial basis changed underneath it and could no longer be reproduced from its own data.
	//
	// WHAT THIS DOES INSTEAD:
	//   A commercial change is staged on a DRAFT revision, and approving it does three things in ONE
	//   transaction: records previous-and-new per line, applies the new values, and supersedes the
	//   revision that was in force. An approved revision is then immutable — there is no code path that
	//   writes to one.
	//
	//   Certificates capture a SNAPSHOT (revision id + contracted quantity + rate) at certification, so
	//   reproduction never re-reads today's BOQ.
	//
	// This service writes NO general ledger and NO stock. It changes operational/commercial data only.
	// ==========================================================================================

	/// What a certificate line was certified against — reproduced from its own snapshot, never from
	/// the current BOQ.
	public sealed class ReproducedCommercialValue
	{
		public int ProgressBillingLineId { get; init; }
		public int? CommercialRevisionId { get; init; }
		public int? BoqItemId { get; init; }
		public decimal ContractedQuantity { get; init; }
		public decimal ContractedRate { get; init; }
		public decimal CertifiedQuantity { get; init; }
		public decimal CertifiedValue { get; init; }
		public DateTime CapturedAt { get; init; }
	}

	public interface ICommercialRevisionService
	{
		Task<(bool ok, string? error, int revisionId)> OpenAsync(
			int companyId, int projectId, int? clientContractId, string source, int? sourceVariationOrderId,
			DateTime effectiveDate, int? actorEmployeeId, CancellationToken ct = default);

		/// The as-awarded baseline: an Original revision capturing the CURRENT values of every active
		/// line, approved immediately. It exists so a certificate raised on day one has a revision to
		/// point at — without it, "the revision in force" would be null exactly when it matters most.
		Task<(bool ok, string? error, int revisionId)> OpenBaselineAsync(
			int companyId, int projectId, int? clientContractId, DateTime effectiveDate,
			int? actorEmployeeId, string reason, CancellationToken ct = default);

		Task<(bool ok, string? error)> StageLineChangeAsync(
			int companyId, int revisionId, int boqItemId, decimal newQuantity, decimal newRate,
			string? note, CancellationToken ct = default);

		Task<(bool ok, string? error)> ApproveAsync(
			int companyId, int revisionId, int? actorEmployeeId, string reason,
			byte[]? expectedToken = null, CancellationToken ct = default);

		Task<(bool ok, string? error, int snapshotId)> CaptureCertificateLineSnapshotAsync(
			int companyId, int progressBillingLineId, decimal certifiedQuantity,
			int? actorEmployeeId, CancellationToken ct = default);

		Task<ReproducedCommercialValue?> ReproduceCertificateLineAsync(
			int companyId, int progressBillingLineId, CancellationToken ct = default);

		Task<CommercialRevision?> InForceAsync(int companyId, int projectId, CancellationToken ct = default);
	}

	public sealed class CommercialRevisionService : ICommercialRevisionService
	{
		private const string Source = nameof(CommercialRevisionService);

		private readonly CrossDbContext _db;
		private readonly IConstructionAuditService _audit;

		public CommercialRevisionService(CrossDbContext db, IConstructionAuditService audit)
		{
			_db = db; _audit = audit;
		}

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public async Task<(bool ok, string? error, int revisionId)> OpenAsync(
			int companyId, int projectId, int? clientContractId, string source, int? sourceVariationOrderId,
			DateTime effectiveDate, int? actorEmployeeId, CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "Unresolved company", 0);
			if (!CommercialRevisionSource.IsKnown(source)) return (false, "Unknown revision source", 0);

			if (!await _db.Projects.AnyAsync(p => p.ID == projectId && p.CompanyID == companyId, ct))
				return (false, "Project not found", 0);

			if (clientContractId is > 0 &&
				!await _db.ClientContracts.AnyAsync(c => c.ID == clientContractId && c.CompanyID == companyId && c.ProjectId == projectId, ct))
				return (false, "Contract not found for this project", 0);

			// A variation-sourced revision must name an APPROVED variation of this project.
			if (string.Equals(source, CommercialRevisionSource.Variation, StringComparison.Ordinal))
			{
				if (sourceVariationOrderId is not > 0)
					return (false, "A variation id is required for a Variation revision", 0);
				bool approved = await _db.VariationOrders.AnyAsync(
					v => v.ID == sourceVariationOrderId && v.CompanyID == companyId && v.ProjectId == projectId
						 && v.Status == "Approved", ct);
				if (!approved) return (false, "The variation is not approved", 0);
			}

			if (await _db.CommercialRevisions.AnyAsync(
					r => r.CompanyID == companyId && r.ProjectId == projectId && r.Status == CommercialRevisionStatus.Draft, ct))
				return (false, "A draft revision is already open for this project", 0);

			var rev = new CommercialRevision
			{
				CompanyID = companyId,
				ProjectId = projectId,
				ClientContractId = clientContractId,
				RevisionNo = await NextRevisionNoAsync(companyId, projectId, clientContractId, ct),
				Kind = CommercialRevisionKind.Revised,
				Source = source,
				SourceVariationOrderId = sourceVariationOrderId,
				EffectiveDate = effectiveDate,
				Status = CommercialRevisionStatus.Draft,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = actorEmployeeId,
				ConcurrencyToken = ConstructionConcurrency.NewToken()
			};
			_db.CommercialRevisions.Add(rev);
			await _db.SaveChangesAsync(ct);
			return (true, null, rev.ID);
		}

		public async Task<(bool ok, string? error, int revisionId)> OpenBaselineAsync(
			int companyId, int projectId, int? clientContractId, DateTime effectiveDate,
			int? actorEmployeeId, string reason, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(reason))
				return (false, "A reason for the revision is required", 0);
			if (!await _db.Projects.AnyAsync(p => p.ID == projectId && p.CompanyID == companyId, ct))
				return (false, "Project not found", 0);

			var lines = await ActiveLinesAsync(companyId, projectId, ct);
			if (lines.Count == 0) return (false, "There are no active BOQ lines to baseline", 0);

			var correlation = Guid.NewGuid();
			var now = DateTime.UtcNow;

			var rev = new CommercialRevision
			{
				CompanyID = companyId,
				ProjectId = projectId,
				ClientContractId = clientContractId,
				RevisionNo = await NextRevisionNoAsync(companyId, projectId, clientContractId, ct),
				Kind = CommercialRevisionKind.Original,
				Source = CommercialRevisionSource.Contract,
				EffectiveDate = effectiveDate,
				Status = CommercialRevisionStatus.Approved,
				Reason = reason.Trim(),
				ApprovedBy = actorEmployeeId,
				ApprovedAt = now,
				CreatedAt = now,
				CreatedBy = actorEmployeeId,
				ConcurrencyToken = ConstructionConcurrency.NewToken()
			};

			decimal total = 0m;
			foreach (var l in lines)
			{
				decimal value = R(l.Quantity * l.UnitPrice);
				total += value;
				rev.Lines.Add(new CommercialRevisionLine
				{
					CompanyID = companyId, BoqItemId = l.ID, LineCode = l.Code, Description = l.Description,
					Unit = l.Unit,
					PreviousQuantity = l.Quantity, NewQuantity = l.Quantity,
					PreviousRate = l.UnitPrice, NewRate = l.UnitPrice,
					QuantityImpact = 0m, ValueImpact = 0m,
					ChangeKind = CommercialLineChangeKind.Unchanged,
					Note = "as-awarded baseline"
				});
			}
			rev.TotalValueBefore = R(total);
			rev.TotalValueAfter = R(total);
			rev.ValueImpact = 0m;

			_db.CommercialRevisions.Add(rev);
			await SupersedePreviousAsync(companyId, projectId, clientContractId, rev, ct);
			await _db.SaveChangesAsync(ct);

			// Point every active line at the baseline.
			var drafts = new List<ConstructionAuditDraft>();
			foreach (var l in lines)
			{
				var state = await EnsureStateAsync(companyId, projectId, l.ID, actorEmployeeId, now, ct);
				state.CurrentRevisionId = rev.ID;
				state.ConcurrencyToken = ConstructionConcurrency.NewToken();
				state.UpdatedAt = now;
				state.UpdatedBy = actorEmployeeId;

				drafts.Add(ConstructionAuditDraft.Text(companyId, projectId,
					ConstructionAuditEntityTypes.CommercialRevision, rev.ID, l.ID, "Baseline",
					null, $"rev {rev.RevisionNo}", ConstructionChangeKind.Created, reason,
					actorEmployeeId, Source, correlation, rev.ID));
			}
			_audit.Record(drafts);
			await _db.SaveChangesAsync(ct);

			return (true, null, rev.ID);
		}

		public async Task<(bool ok, string? error)> StageLineChangeAsync(
			int companyId, int revisionId, int boqItemId, decimal newQuantity, decimal newRate,
			string? note, CancellationToken ct = default)
		{
			if (newQuantity < 0 || newRate < 0) return (false, "Quantity and rate cannot be negative");

			var rev = await _db.CommercialRevisions.Include(r => r.Lines)
				.FirstOrDefaultAsync(r => r.ID == revisionId && r.CompanyID == companyId, ct);
			if (rev == null) return (false, "Revision not found");

			// An APPROVED revision is immutable. There is no path that writes to one.
			if (rev.Status != CommercialRevisionStatus.Draft)
				return (false, $"An approved revision is immutable — status {rev.Status}");

			// The line must belong to the revision's own company AND project.
			var line = await _db.BoqItems.AsNoTracking()
				.FirstOrDefaultAsync(b => b.ID == boqItemId && b.CompanyID == companyId && b.ProjectId == rev.ProjectId, ct);
			if (line == null) return (false, "The line was not found in this revision's project");

			var existing = rev.Lines.FirstOrDefault(l => l.BoqItemId == boqItemId);
			if (existing == null)
			{
				existing = new CommercialRevisionLine
				{
					CompanyID = companyId, CommercialRevisionId = rev.ID, BoqItemId = boqItemId
				};
				rev.Lines.Add(existing);
			}

			existing.LineCode = line.Code;
			existing.Description = line.Description;
			existing.Unit = line.Unit;
			existing.PreviousQuantity = line.Quantity;
			existing.PreviousRate = line.UnitPrice;
			existing.NewQuantity = newQuantity;
			existing.NewRate = newRate;
			existing.QuantityImpact = newQuantity - line.Quantity;
			existing.ValueImpact = R(newQuantity * newRate) - R(line.Quantity * line.UnitPrice);
			existing.ChangeKind = existing.QuantityImpact == 0 && existing.NewRate == existing.PreviousRate
				? CommercialLineChangeKind.Unchanged
				: CommercialLineChangeKind.Adjusted;
			existing.Note = note;

			rev.ConcurrencyToken = ConstructionConcurrency.NewToken();
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> ApproveAsync(
			int companyId, int revisionId, int? actorEmployeeId, string reason,
			byte[]? expectedToken = null, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(reason))
				return (false, "A reason is required to approve a commercial revision");

			var rev = await _db.CommercialRevisions.Include(r => r.Lines)
				.FirstOrDefaultAsync(r => r.ID == revisionId && r.CompanyID == companyId, ct);
			if (rev == null) return (false, "Revision not found");
			if (rev.Status != CommercialRevisionStatus.Draft)
				return (false, $"The revision is {rev.Status} — only a draft can be approved");
			if (!ConstructionConcurrency.Matches(expectedToken, rev.ConcurrencyToken))
				return (false, ConstructionConcurrency.ConflictMessage);
			if (rev.Lines.Count == 0)
				return (false, "The revision has no lines — a revision with no lines changes nothing");

			var correlation = Guid.NewGuid();
			var now = DateTime.UtcNow;
			var drafts = new List<ConstructionAuditDraft>();
			decimal before = 0m, after = 0m;

			foreach (var rl in rev.Lines)
			{
				if (rl.BoqItemId is not > 0) continue;

				var item = await _db.BoqItems
					.FirstOrDefaultAsync(b => b.ID == rl.BoqItemId && b.CompanyID == companyId && b.ProjectId == rev.ProjectId, ct);
				if (item == null)
					return (false, $"Line #{rl.BoqItemId} no longer exists — the revision cannot be applied");

				// Re-snapshot the ACTUAL current values at approval time. Staging may be hours old, and the
				// truth of "previous" is what the line holds now, not what it held when the line was staged.
				decimal prevQty = item.Quantity, prevRate = item.UnitPrice;
				rl.PreviousQuantity = prevQty;
				rl.PreviousRate = prevRate;
				rl.QuantityImpact = rl.NewQuantity - prevQty;
				rl.ValueImpact = R(rl.NewQuantity * rl.NewRate) - R(prevQty * prevRate);
				rl.ChangeKind = rl.QuantityImpact == 0 && rl.NewRate == prevRate
					? CommercialLineChangeKind.Unchanged
					: CommercialLineChangeKind.Adjusted;

				before += R(prevQty * prevRate);
				after += R(rl.NewQuantity * rl.NewRate);

				// Apply.
				item.Quantity = rl.NewQuantity;
				item.UnitPrice = rl.NewRate;

				var state = await EnsureStateAsync(companyId, rev.ProjectId, item.ID, actorEmployeeId, now, ct);
				state.CurrentRevisionId = rev.ID;
				state.ConcurrencyToken = ConstructionConcurrency.NewToken();
				state.UpdatedAt = now;
				state.UpdatedBy = actorEmployeeId;

				// Both commercial fields are audited every time — including when a value did not move —
				// so the record shows what was CONSIDERED, not only what changed.
				drafts.Add(ConstructionAuditDraft.Numeric(companyId, rev.ProjectId,
					ConstructionAuditEntityTypes.BoqItem, item.ID, item.ID, "Quantity",
					prevQty, rl.NewQuantity, ConstructionChangeKind.Approved, reason,
					actorEmployeeId, Source, correlation, rev.ID));
				drafts.Add(ConstructionAuditDraft.Numeric(companyId, rev.ProjectId,
					ConstructionAuditEntityTypes.BoqItem, item.ID, item.ID, "UnitPrice",
					prevRate, rl.NewRate, ConstructionChangeKind.Approved, reason,
					actorEmployeeId, Source, correlation, rev.ID));
			}

			rev.TotalValueBefore = R(before);
			rev.TotalValueAfter = R(after);
			rev.ValueImpact = R(after - before);
			rev.Status = CommercialRevisionStatus.Approved;
			rev.Reason = reason.Trim();
			rev.ApprovedBy = actorEmployeeId;
			rev.ApprovedAt = now;
			rev.ConcurrencyToken = ConstructionConcurrency.NewToken();

			await SupersedePreviousAsync(companyId, rev.ProjectId, rev.ClientContractId, rev, ct);

			drafts.Add(ConstructionAuditDraft.Text(companyId, rev.ProjectId,
				ConstructionAuditEntityTypes.CommercialRevision, rev.ID, null, "Status",
				CommercialRevisionStatus.Draft, CommercialRevisionStatus.Approved,
				ConstructionChangeKind.Approved, reason, actorEmployeeId, Source, correlation, rev.ID));

			_audit.Record(drafts);
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		public async Task<(bool ok, string? error, int snapshotId)> CaptureCertificateLineSnapshotAsync(
			int companyId, int progressBillingLineId, decimal certifiedQuantity,
			int? actorEmployeeId, CancellationToken ct = default)
		{
			var line = await (from l in _db.ProgressBillingLines.AsNoTracking()
							  join h in _db.ProgressBillings.AsNoTracking() on l.BillingId equals h.ID
							  where l.ID == progressBillingLineId && h.CompanyID == companyId
							  select new { l.ID, l.BillingId, l.BoqItemId, l.PeriodValue, h.ProjectId })
							 .FirstOrDefaultAsync(ct);
			if (line == null) return (false, "Certificate line not found", 0);

			if (await _db.CertificateLineSnapshots.AnyAsync(s => s.ProgressBillingLineId == progressBillingLineId, ct))
				return (false, "A snapshot already exists for this line — a snapshot is never overwritten", 0);

			decimal contractedQty = 0m, contractedRate = 0m;
			int? revisionId = null;
			if (line.BoqItemId is > 0)
			{
				var item = await _db.BoqItems.AsNoTracking()
					.FirstOrDefaultAsync(b => b.ID == line.BoqItemId && b.CompanyID == companyId, ct);
				if (item != null) { contractedQty = item.Quantity; contractedRate = item.UnitPrice; }

				revisionId = await _db.BoqLineStates.AsNoTracking()
					.Where(s => s.BoqItemId == line.BoqItemId).Select(s => s.CurrentRevisionId).FirstOrDefaultAsync(ct);
			}

			var contractId = await _db.BoqLineStates.AsNoTracking()
				.Where(s => s.BoqItemId == line.BoqItemId).Select(s => s.ClientContractId).FirstOrDefaultAsync(ct);

			var snap = new CertificateLineSnapshot
			{
				CompanyID = companyId,
				ProjectId = line.ProjectId,
				ProgressBillingLineId = line.ID,
				ProgressBillingId = line.BillingId,
				BoqItemId = line.BoqItemId,
				ClientContractId = contractId,
				CommercialRevisionId = revisionId,
				ContractedQuantity = contractedQty,
				ContractedRate = contractedRate,
				CertifiedQuantity = certifiedQuantity,
				CertifiedValue = R(line.PeriodValue),
				CapturedAt = DateTime.UtcNow,
				CapturedBy = actorEmployeeId
			};
			_db.CertificateLineSnapshots.Add(snap);
			await _db.SaveChangesAsync(ct);
			return (true, null, snap.ID);
		}

		public async Task<ReproducedCommercialValue?> ReproduceCertificateLineAsync(
			int companyId, int progressBillingLineId, CancellationToken ct = default)
		{
			var s = await _db.CertificateLineSnapshots.AsNoTracking()
				.FirstOrDefaultAsync(x => x.ProgressBillingLineId == progressBillingLineId && x.CompanyID == companyId, ct);
			if (s == null) return null;

			return new ReproducedCommercialValue
			{
				ProgressBillingLineId = s.ProgressBillingLineId,
				CommercialRevisionId = s.CommercialRevisionId,
				BoqItemId = s.BoqItemId,
				ContractedQuantity = s.ContractedQuantity,
				ContractedRate = s.ContractedRate,
				CertifiedQuantity = s.CertifiedQuantity,
				CertifiedValue = s.CertifiedValue,
				CapturedAt = s.CapturedAt
			};
		}

		public Task<CommercialRevision?> InForceAsync(int companyId, int projectId, CancellationToken ct = default) =>
			_db.CommercialRevisions.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.ProjectId == projectId && r.Status == CommercialRevisionStatus.Approved)
				.OrderByDescending(r => r.RevisionNo)
				.FirstOrDefaultAsync(ct);

		// ------------------------------------------------------------------------------------------
		private async Task<int> NextRevisionNoAsync(int companyId, int projectId, int? contractId, CancellationToken ct) =>
			(await _db.CommercialRevisions
				.Where(r => r.CompanyID == companyId && r.ProjectId == projectId && r.ClientContractId == contractId)
				.Select(r => (int?)r.RevisionNo).MaxAsync(ct) ?? 0) + 1;

		private async Task SupersedePreviousAsync(
			int companyId, int projectId, int? contractId, CommercialRevision current, CancellationToken ct)
		{
			var previous = await _db.CommercialRevisions
				.Where(r => r.CompanyID == companyId && r.ProjectId == projectId && r.ClientContractId == contractId
							&& r.Status == CommercialRevisionStatus.Approved && r.ID != current.ID)
				.ToListAsync(ct);

			foreach (var p in previous)
			{
				p.Status = CommercialRevisionStatus.Superseded;
				p.SupersededByRevisionId = current.ID;
				p.ConcurrencyToken = ConstructionConcurrency.NewToken();
			}
		}

		private async Task<List<Models.Context.Accounting.BoqItem>> ActiveLinesAsync(
			int companyId, int projectId, CancellationToken ct)
		{
			var retired = await _db.BoqLineStates.AsNoTracking()
				.Where(s => s.CompanyID == companyId && s.ProjectId == projectId && s.Status == BoqLineStatus.Retired)
				.Select(s => s.BoqItemId).ToListAsync(ct);

			return await _db.BoqItems.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId && !retired.Contains(b.ID))
				.OrderBy(b => b.SortOrder).ThenBy(b => b.ID).ToListAsync(ct);
		}

		private async Task<BoqLineState> EnsureStateAsync(
			int companyId, int projectId, int boqItemId, int? actor, DateTime now, CancellationToken ct)
		{
			var state = await _db.BoqLineStates.FirstOrDefaultAsync(s => s.BoqItemId == boqItemId, ct);
			if (state != null) return state;

			state = new BoqLineState
			{
				CompanyID = companyId, ProjectId = projectId, BoqItemId = boqItemId,
				Status = BoqLineStatus.Active, CreatedAt = now, CreatedBy = actor,
				ConcurrencyToken = ConstructionConcurrency.NewToken()
			};
			_db.BoqLineStates.Add(state);
			return state;
		}
	}
}
