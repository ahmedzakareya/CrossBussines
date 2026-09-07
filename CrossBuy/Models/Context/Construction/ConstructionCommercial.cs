using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction
{
	// ==========================================================================================
	// CrossBusiness Construction & Contracting — C1 COMMERCIAL FOUNDATION
	//
	// The entities that close CR-01 (BOQ stable identity), CR-02 (subcontract certification cap) and
	// CR-03 (immutable commercial revisions), plus the concurrency token and line-level audit those
	// three depend on.
	//
	// ------------------------------------------------------------------------------------------
	// WHY EVERY ONE OF THESE IS A NEW TABLE AND NOTHING EXISTING IS ALTERED
	//
	// The obvious design would add columns to BoqItems (Status, ClientContractId, RowVersion) and to
	// ProgressBillingLines (BoqRevisionId, rate snapshot). It is rejected DELIBERATELY:
	//
	//   * This increment may not execute SQL against any database. The schema script ships in
	//     deploy/sql and the OWNER applies it ("SQL before code", the repository's standing rule).
	//   * Between shipping and applying, an EF mapping that referenced a not-yet-created COLUMN on
	//     BoqItems would break EVERY read of the existing Projects screens — for three other tabs
	//     working in this same tree.
	//   * A missing new TABLE, by contrast, only fails the NEW construction code paths. Every
	//     existing screen keeps working untouched.
	//
	// So: BoqLineState is a satellite of BoqItems keyed 1:1 on BoqItemId, and CertificateLineSnapshot
	// is a satellite of ProgressBillingLines. No existing entity class in this repository is modified.
	// Folding the satellites into their base tables is a later, owner-scheduled consolidation and is
	// recorded as such in Stage-Construction-Concurrency-and-Audit-Design.md §6.
	//
	// ------------------------------------------------------------------------------------------
	// CONCURRENCY: an application-rotated token, not SQL Server `rowversion`.
	//
	// `ConcurrencyToken` is a varbinary(16) configured with IsConcurrencyToken() and rotated by the
	// owning service on every write. A native `rowversion` would work only on SQL Server, and the
	// test suite runs on SQLite (the only in-process provider with real transactions) — so a native
	// token would leave every concurrency test unexercised on the platform the suite actually uses.
	// A rotated token is provider-independent, so the stale-token tests are REAL tests.
	//
	// ------------------------------------------------------------------------------------------
	// Every table carries CompanyID. No table carries a company default: an unresolved company reads
	// nothing and writes nothing.
	// ==========================================================================================

	/// عقد العميل — a client contract. D-01: a project MAY hold several, with explicitly separate scope.
	public class ClientContract
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }
		public string ContractNo { get; set; } = "";      // unique per company
		public int? CustomerId { get; set; }               // an existing Customer — never a duplicated party master
		public string? Title { get; set; }
		public string? TitleEn { get; set; }
		public string? ScopeDescription { get; set; }
		public int? CurrencyId { get; set; }
		public decimal? ContractValue { get; set; }        // original; revised value = original + approved variations
		public DateTime? SignedDate { get; set; }
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }

		/// D-01: one contract per project may be Primary. Enforced by a filtered unique index.
		public bool IsPrimary { get; set; }

		public string Status { get; set; } = ClientContractStatus.Draft;

		public int? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? UpdatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public byte[] ConcurrencyToken { get; set; } = Array.Empty<byte>();
	}

	public static class ClientContractStatus
	{
		public const string Draft = "Draft";
		public const string Active = "Active";
		public const string Suspended = "Suspended";
		public const string Completed = "Completed";
		public const string Closed = "Closed";
		public const string Terminated = "Terminated";

		public static readonly IReadOnlyList<string> All =
			new[] { Draft, Active, Suspended, Completed, Closed, Terminated };

		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// حالة بند الكميات — the commercial state of a BOQ line. 1:1 satellite of BoqItems (see the header note).
	///
	/// This is what makes CR-01 expressible: a BOQ line that must disappear from the working BOQ is
	/// RETIRED here, and its BoqItems row — and therefore its identity — survives, so every certificate,
	/// measurement and issue that points at it still resolves.
	public class BoqLineState
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }

		/// → BoqItems.ID. Unique: exactly one state row per BOQ line.
		public int BoqItemId { get; set; }

		/// D-01: which client contract owns this line. Nullable until the owner runs the backfill.
		public int? ClientContractId { get; set; }

		public string Status { get; set; } = BoqLineStatus.Active;

		/// The revision in force for this line's current commercial values.
		public int? CurrentRevisionId { get; set; }

		public DateTime? RetiredAt { get; set; }
		public int? RetiredBy { get; set; }
		public string? RetiredReason { get; set; }

		public int? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? UpdatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public byte[] ConcurrencyToken { get; set; } = Array.Empty<byte>();
	}

	public static class BoqLineStatus
	{
		/// Part of the working BOQ.
		public const string Active = "Active";

		/// Removed from the working BOQ but PRESERVED, because something references it. Never deleted.
		public const string Retired = "Retired";

		public static readonly IReadOnlyList<string> All = new[] { Active, Retired };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// مراجعة تجارية — an immutable commercial revision (CR-03).
	///
	/// A revision is the ONLY way an approved contractual quantity or rate may change. Approving one
	/// captures the previous and new values of every affected line in CommercialRevisionLines, so a
	/// certificate stamped with a revision id can always be re-derived exactly as it was certified.
	public class CommercialRevision
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }
		public int? ClientContractId { get; set; }

		/// Sequential per (company, project, contract).
		public int RevisionNo { get; set; }

		public string Kind { get; set; } = CommercialRevisionKind.Original;
		public string Source { get; set; } = CommercialRevisionSource.Contract;

		/// Set when Source = Variation — the variation that produced this revision.
		public int? SourceVariationOrderId { get; set; }

		/// The commercial effective date. Distinct from ApprovedAt: a variation approved today may take
		/// effect from an instruction date in the past, and certificates must use the right one.
		public DateTime EffectiveDate { get; set; }

		public string Status { get; set; } = CommercialRevisionStatus.Draft;

		public decimal TotalValueBefore { get; set; }
		public decimal TotalValueAfter { get; set; }
		public decimal ValueImpact { get; set; }

		/// Required to reach Approved. A commercial change with no stated reason is refused.
		public string? Reason { get; set; }

		public int? ApprovedBy { get; set; }
		public DateTime? ApprovedAt { get; set; }
		public int? SupersededByRevisionId { get; set; }

		public int? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; }
		public byte[] ConcurrencyToken { get; set; } = Array.Empty<byte>();

		public List<CommercialRevisionLine> Lines { get; set; } = new();
	}

	public static class CommercialRevisionKind
	{
		public const string Original = "Original";
		public const string Revised = "Revised";
		public static readonly IReadOnlyList<string> All = new[] { Original, Revised };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class CommercialRevisionSource
	{
		public const string Contract = "Contract";
		public const string Variation = "Variation";
		public const string Correction = "Correction";
		public static readonly IReadOnlyList<string> All = new[] { Contract, Variation, Correction };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class CommercialRevisionStatus
	{
		public const string Draft = "Draft";
		public const string Approved = "Approved";
		public const string Superseded = "Superseded";
		public static readonly IReadOnlyList<string> All = new[] { Draft, Approved, Superseded };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// سطر المراجعة التجارية — previous vs new commercial values for one BOQ line, immutable once approved.
	public class CommercialRevisionLine
	{
		public int ID { get; set; }
		public int CommercialRevisionId { get; set; }
		public int CompanyID { get; set; }

		/// → BoqItems.ID. Null only for a line added by this revision before its BOQ row exists.
		public int? BoqItemId { get; set; }

		public string? LineCode { get; set; }
		public string? Description { get; set; }
		public string? Unit { get; set; }

		public decimal PreviousQuantity { get; set; }
		public decimal NewQuantity { get; set; }
		public decimal PreviousRate { get; set; }
		public decimal NewRate { get; set; }

		public decimal QuantityImpact { get; set; }   // New − Previous
		public decimal ValueImpact { get; set; }      // New value − Previous value

		public string ChangeKind { get; set; } = CommercialLineChangeKind.Adjusted;

		public string? Note { get; set; }

		public CommercialRevision? Revision { get; set; }
	}

	public static class CommercialLineChangeKind
	{
		public const string Added = "Added";
		public const string Adjusted = "Adjusted";
		public const string Retired = "Retired";
		public const string Unchanged = "Unchanged";
		public static readonly IReadOnlyList<string> All = new[] { Added, Adjusted, Retired, Unchanged };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// نطاق عقد الباطن — the assigned scope of a subcontract. THIS IS THE CAP (CR-02, D-07).
	///
	/// A subcontractor may be certified up to AssignedQuantity + ApprovedVariationQuantity and no
	/// further. Increasing the cap requires an approved variation, recorded on the scope line.
	public class SubcontractScope
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }
		public int SubcontractId { get; set; }

		/// The client BOQ line this scope is carved out of. Null for a scope line with no BOQ counterpart
		/// (e.g. a lump-sum enabling work), which still carries a quantity and a rate and is still capped.
		public int? BoqItemId { get; set; }

		public string? ScopeCode { get; set; }
		public string? Description { get; set; }
		public string? Unit { get; set; }

		public decimal AssignedQuantity { get; set; }

		/// Additional quantity authorised by approved variations. Only a variation may raise this.
		public decimal ApprovedVariationQuantity { get; set; }

		/// The subcontractor's rate — commercial data (margin vs the client rate).
		public decimal SubRate { get; set; }

		/// The variation that last raised the cap, for traceability.
		public int? LastCapVariationOrderId { get; set; }

		public string Status { get; set; } = SubcontractScopeStatus.Active;

		public int? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? UpdatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public byte[] ConcurrencyToken { get; set; } = Array.Empty<byte>();

		/// The cap, in quantity. Not mapped — derived so the two components can never disagree with it.
		[System.ComponentModel.DataAnnotations.Schema.NotMapped]
		public decimal CappedQuantity => AssignedQuantity + ApprovedVariationQuantity;

		/// The cap, in value.
		[System.ComponentModel.DataAnnotations.Schema.NotMapped]
		public decimal CappedValue => Math.Round(CappedQuantity * SubRate, 2, MidpointRounding.AwayFromZero);
	}

	public static class SubcontractScopeStatus
	{
		public const string Active = "Active";
		public const string Retired = "Retired";
		public static readonly IReadOnlyList<string> All = new[] { Active, Retired };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// سطر مستخلص الباطن — a subcontractor certificate LINE (CR-02).
	///
	/// Today `SubcontractBillings` has no lines at all and its cumulative figure is a free-typed decimal,
	/// which is exactly why certification is unbounded. Every certified amount must now come from lines,
	/// and every line is capped by its scope.
	public class SubcontractCertificateLine
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }

		/// → SubcontractBillings.ID (the existing certificate header).
		public int SubcontractBillingId { get; set; }
		public int SubcontractId { get; set; }

		/// → SubcontractScopes.ID — the cap this line is measured against.
		public int SubcontractScopeId { get; set; }

		public int? BoqItemId { get; set; }

		/// The commercial revision in force when this line was certified. Historical reproduction.
		public int? CommercialRevisionId { get; set; }

		/// The variation that authorised quantity above the original assignment, where applicable.
		public int? ApprovedVariationOrderId { get; set; }

		/// The rate actually used, snapshotted. Never re-read from the scope at report time.
		public decimal RateSnapshot { get; set; }

		public decimal PreviousQuantity { get; set; }
		public decimal PreviousValue { get; set; }
		public decimal CurrentQuantity { get; set; }
		public decimal CurrentValue { get; set; }
		public decimal CumulativeQuantity { get; set; }
		public decimal CumulativeValue { get; set; }

		/// Set when this line is an adjustment/reversal of an earlier certified line rather than new work.
		public int? AdjustsLineId { get; set; }

		public DateTime CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public byte[] ConcurrencyToken { get; set; } = Array.Empty<byte>();
	}

	/// لقطة سطر المستخلص — the commercial snapshot of a CLIENT certificate line (CR-03).
	///
	/// 1:1 satellite of ProgressBillingLines (see the header note on why it is not a set of columns).
	/// With it, a posted certificate can be reproduced exactly even after a later variation changes the
	/// live BOQ values.
	public class CertificateLineSnapshot
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }

		/// → ProgressBillingLines.ID. Unique: one snapshot per certificate line.
		public int ProgressBillingLineId { get; set; }
		public int ProgressBillingId { get; set; }

		public int? BoqItemId { get; set; }
		public int? ClientContractId { get; set; }

		/// The revision the line was certified against. The anchor of historical reproduction.
		public int? CommercialRevisionId { get; set; }
		public int? VariationOrderId { get; set; }

		public decimal ContractedQuantity { get; set; }
		public decimal ContractedRate { get; set; }
		public decimal CertifiedQuantity { get; set; }
		public decimal CertifiedValue { get; set; }

		public DateTime CapturedAt { get; set; }
		public int? CapturedBy { get; set; }
	}

	/// سجل تدقيق الإنشاءات — append-only, field-level history. Never updated, never deleted.
	public class ConstructionAuditEntry
	{
		public long ID { get; set; }
		public int CompanyID { get; set; }
		public int? ProjectId { get; set; }

		public string EntityType { get; set; } = "";
		public int EntityId { get; set; }

		/// The line within the entity, when the change is line-level rather than header-level.
		public int? LineId { get; set; }

		public string? FieldName { get; set; }
		public string? OldValue { get; set; }
		public string? NewValue { get; set; }

		/// Numeric mirrors, so a report can aggregate a change without parsing text.
		public decimal? OldNumeric { get; set; }
		public decimal? NewNumeric { get; set; }

		public string ChangeKind { get; set; } = ConstructionChangeKind.Updated;

		/// Required for any change to a commercial value. Enforced by the writing service.
		public string? Reason { get; set; }

		public int? ActorEmployeeId { get; set; }
		public string? ActorUserId { get; set; }
		public DateTime OccurredAt { get; set; }

		/// Where the change came from — a service/endpoint name, not a screen title.
		public string? SourceContext { get; set; }

		/// One user action producing many rows must be recognisable as ONE action.
		public Guid CorrelationId { get; set; }

		/// The commercial revision in force, where the change has one.
		public int? RevisionId { get; set; }
	}

	public static class ConstructionChangeKind
	{
		public const string Created = "Created";
		public const string Updated = "Updated";
		public const string Retired = "Retired";
		public const string Approved = "Approved";
		public const string Posted = "Posted";
		public const string Reversed = "Reversed";
		public const string Cancelled = "Cancelled";
		public const string CapRaised = "CapRaised";
		public const string Restructured = "Restructured";

		public static readonly IReadOnlyList<string> All =
			new[] { Created, Updated, Retired, Approved, Posted, Reversed, Cancelled, CapRaised, Restructured };

		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// The entity-type names used in ConstructionAuditEntry.EntityType. Constants, so a query and a
	/// writer cannot disagree about spelling.
	public static class ConstructionAuditEntityTypes
	{
		public const string BoqItem = "BoqItem";
		public const string ClientContract = "ClientContract";
		public const string CommercialRevision = "CommercialRevision";
		public const string SubcontractScope = "SubcontractScope";
		public const string SubcontractCertificate = "SubcontractCertificate";
		public const string ClientCertificate = "ClientCertificate";
		public const string VariationOrder = "VariationOrder";
	}

	// ==========================================================================================
	// Model configuration. Kept HERE rather than in CrossDbContext.OnModelCreating so this tab's
	// footprint in the shared context file is one call plus the DbSet declarations.
	// ==========================================================================================
	public static class ConstructionCommercialModel
	{
		public static void Configure(ModelBuilder b)
		{
			// ---- ClientContracts ----
			b.Entity<ClientContract>(e =>
			{
				e.Property(x => x.ConcurrencyToken).IsConcurrencyToken().HasMaxLength(16);
				e.HasIndex(x => new { x.CompanyID, x.ContractNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.Status });
				// One Primary contract per project. A filtered unique index, so several non-primary
				// contracts are fine and two primaries are impossible.
				e.HasIndex(x => new { x.CompanyID, x.ProjectId })
				 .HasFilter("[IsPrimary] = 1")
				 .IsUnique()
				 .HasDatabaseName("UX_ClientContracts_OnePrimaryPerProject");
			});

			// ---- BoqLineStates ----
			b.Entity<BoqLineState>(e =>
			{
				e.Property(x => x.ConcurrencyToken).IsConcurrencyToken().HasMaxLength(16);
				// Exactly one state row per BOQ line.
				e.HasIndex(x => x.BoqItemId).IsUnique();
				e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.Status });
				e.HasIndex(x => new { x.CompanyID, x.ClientContractId });
			});

			// ---- CommercialRevisions ----
			b.Entity<CommercialRevision>(e =>
			{
				e.Property(x => x.ConcurrencyToken).IsConcurrencyToken().HasMaxLength(16);
				e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.ClientContractId, x.RevisionNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.Status });
				e.HasMany(x => x.Lines)
				 .WithOne(l => l.Revision!)
				 .HasForeignKey(l => l.CommercialRevisionId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			b.Entity<CommercialRevisionLine>(e =>
			{
				e.HasIndex(x => new { x.CommercialRevisionId, x.BoqItemId });
			});

			// ---- SubcontractScopes ----
			b.Entity<SubcontractScope>(e =>
			{
				e.Property(x => x.ConcurrencyToken).IsConcurrencyToken().HasMaxLength(16);
				e.HasIndex(x => new { x.CompanyID, x.SubcontractId, x.Status });
				e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.BoqItemId });
			});

			// ---- SubcontractCertificateLines ----
			b.Entity<SubcontractCertificateLine>(e =>
			{
				e.Property(x => x.ConcurrencyToken).IsConcurrencyToken().HasMaxLength(16);
				e.HasIndex(x => new { x.CompanyID, x.SubcontractBillingId });
				e.HasIndex(x => new { x.CompanyID, x.SubcontractScopeId });
			});

			// ---- CertificateLineSnapshots ----
			b.Entity<CertificateLineSnapshot>(e =>
			{
				e.HasIndex(x => x.ProgressBillingLineId).IsUnique();
				e.HasIndex(x => new { x.CompanyID, x.ProgressBillingId });
			});

			// ---- ConstructionAuditEntries ----
			b.Entity<ConstructionAuditEntry>(e =>
			{
				e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId });
				e.HasIndex(x => x.CorrelationId);
				e.HasIndex(x => new { x.CompanyID, x.OccurredAt });
			});
		}
	}
}
