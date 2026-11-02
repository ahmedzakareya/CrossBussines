using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Approvals
{
	// =================================================================================================
	// The approval READ platform's module-neutral contracts.
	//
	// WHY THESE EXIST. The unified approvals inbox was a three-way inline union inside
	// ApprovalsController.Index: each silo queried with its own predicates, its own ordering, and its
	// own hardcoded Url.Action pair. Nothing was reusable, so a second consumer (Workspace) could only
	// have re-implemented the same three queries — including their company boundaries, which is exactly
	// the code that has already needed two security fixes (96cb210, 87ec8fa).
	//
	// The shape here is deliberately POOR IN BEHAVIOUR and RICH IN FACTS:
	//
	//   * No EF entities. A row must be safe to hand to any consumer without dragging a DbContext,
	//     a lazy-loading proxy, or a navigation property that would re-query on access.
	//   * No mutation delegates, actions or callbacks. This is a read platform; approve/reject stay with
	//     the module services that own the workflow. A row that could act would make every consumer a
	//     potential approver.
	//   * No localized strings. Silo labels, icons, colours and formatted period text are presentation,
	//     and a BL service has no business holding an IStringLocalizer. Rows carry the Arabic/English
	//     pairs and the raw parts; the caller formats. This is what keeps the existing view byte-identical.
	//
	// Each module owns its own company boundary and fills these rows itself — the aggregator never
	// queries a module table, so it cannot get a boundary wrong on a module's behalf.
	// =================================================================================================

	/// <summary>The silo keys. Match the values ApprovalsController.Index has always emitted, because the
	/// view and its CSS switch on them.</summary>
	public static class ApprovalSilos
	{
		public const string Leave = "Leave";
		public const string Request = "Request";
		public const string Inventory = "Inventory";
		public const string ProjectBilling = "ProjectBilling";   // المستخلص awaiting approval
	}

	// =================================================================================================
	// CANONICAL ENTITY IDENTITY FOR AN APPROVAL ROW.
	//
	// A SILO KEY IS NOT AN ENTITY CODE, and the difference is the whole reason this map exists. "Request"
	// is a presentation grouping the inbox view switches its CSS on; "EmployeeRequest" is the platform
	// identity IEntityRegistry resolves, authorizes and links. They are different vocabularies that
	// happen to describe the same rows, and a consumer that treated one as the other would be guessing.
	//
	// THE MAPPING IS VERIFIED, NOT ASSUMED. For each silo below, the id the row carries was checked
	// against the id the registry's own ResolveAsync arm looks up:
	//
	//   Leave          → LeaveRequest       row EntityId = LeaveRequest.ID       ← registry: LeaveRequests.ID
	//   Request        → EmployeeRequest    row EntityId = EmployeeRequest.ID    ← registry: EmployeeRequests.ID
	//   Inventory      → InventoryApproval  row EntityId = InventoryApproval.ID  ← registry: InventoryApprovals.ID
	//   ProjectBilling → ProjectBilling     row EntityId = ProgressBilling.ID    ← registry: ProgressBillings.ID
	//
	// All four registry codes are registered in BOTH required places - a definition AND a ResolveAsync
	// arm - which matters because a definition without an arm resolves nothing and reads as a denial
	// rather than as a wiring gap.
	//
	// NUMERIC IDS DO NOT TRAVEL BETWEEN SILOS. Leave 42, employee request 42, inventory approval 42 and
	// progress billing 42 are four unrelated records. Nothing here pairs a row with an entity because
	// the numbers match: the pairing is the silo's OWN primary key, carried from the module that
	// produced the row, and the EntityType is what stops a consumer from losing that distinction.
	//
	// AN UNKNOWN SILO RESOLVES TO NOTHING, DELIBERATELY. A future silo added without a verified mapping
	// gets null here and is reported as unresolved. Inventing a plausible-looking code for it would be
	// the one failure mode this contract exists to prevent, because an invented code resolves against a
	// REAL table and would silently name the wrong record.
	// =================================================================================================
	public static class ApprovalEntityMap
	{
		private static readonly IReadOnlyDictionary<string, string> BySilo =
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				[ApprovalSilos.Leave] = CrossBuy.BL.Platform.EntityRegistry.LeaveRequest,
				[ApprovalSilos.Request] = CrossBuy.BL.Platform.EntityRegistry.EmployeeRequest,
				[ApprovalSilos.Inventory] = CrossBuy.BL.Platform.EntityRegistry.InventoryApproval,
				[ApprovalSilos.ProjectBilling] = CrossBuy.BL.Platform.EntityRegistry.ProjectBilling,
			};

		/// <summary>The canonical entity code for a silo, or NULL when the silo has no verified mapping.</summary>
		public static string? CanonicalEntityType(string? silo)
			=> silo != null && BySilo.TryGetValue(silo, out var code) ? code : null;

		/// <summary>True when this silo can be addressed by canonical entity identity.</summary>
		public static bool IsSupported(string? silo) => CanonicalEntityType(silo) != null;

		/// <summary>The silos that currently support canonical identity, for a consumer that wants to ask
		/// before it queries rather than discovering an empty result and guessing why.</summary>
		public static IReadOnlyCollection<string> SupportedSilos => (IReadOnlyCollection<string>)BySilo.Keys;

		/// <summary>The canonical codes, in the same order. Published so a consumer can validate an
		/// entityType argument BEFORE calling, instead of sending an unknown code and reading nothing back.</summary>
		public static IReadOnlyCollection<string> SupportedEntityTypes => (IReadOnlyCollection<string>)BySilo.Values;
	}

	/// <summary>
	/// Where a consumer should send the approver to act on this row.
	///
	/// The owning module supplies it, so neither the aggregator nor Workspace hardcodes module routes —
	/// the three pairs (People/Leaves, People/Requests, Inventory/Approvals) used to be string literals
	/// in the controller. Resolving this to a URL stays in the presentation layer: a BL service has no
	/// IUrlHelper and should not pretend to.
	/// </summary>
	/// <summary>
	/// Where the owning module wants this row opened. RouteValues is OPTIONAL and defaulted, so the three
	/// silos that need no identifiers are unchanged; project billing needs them because its screen is
	/// addressed by project AND billing, and the inbox must reach the REAL document rather than a list.
	/// </summary>
	public sealed record ApprovalNavigationTarget(
		string Controller,
		string Action,
		IReadOnlyDictionary<string, string?>? RouteValues = null);

	/// <summary>
	/// One pending approval, as the owning module sees it. Every field is either an identity, a fact, or
	/// a bilingual pair — never a rendered string.
	/// </summary>
	public sealed record PendingApprovalRow
	{
		/// <summary>Which silo produced this row. One of <see cref="ApprovalSilos"/>.</summary>
		public required string Silo { get; init; }

		/// <summary>The module's own row id (LeaveRequest.ID, EmployeeRequest.ID, InventoryApproval.ID).</summary>
		public required int EntityId { get; init; }

		/// <summary>
		/// The pending approval STEP, where the silo models one. Leave and employee requests advance
		/// through numbered steps; inventory has no step table. Carried because a stable approval
		/// identity needs it — "leave 42" is not the same act as "leave 42, level 2".
		/// </summary>
		public int? StepId { get; init; }

		/// <summary>
		/// Stable cross-silo identity: "<silo>:<entityId>" plus the step when there is one. Consumers can
		/// key, dedupe and correlate on this without parsing anything module-specific.
		/// </summary>
		public string Reference => StepId.HasValue
			? $"{Silo}:{EntityId}:{StepId.Value}"
			: $"{Silo}:{EntityId}";

		/// <summary>The module's own type discriminator: leave-type id, "Letter"/"Permission", DocType.</summary>
		public string? ApprovalType { get; init; }

		/// <summary>What is being approved, in each language. Either may be null when the module has only one.</summary>
		public string? TitleAr { get; init; }
		public string? TitleEn { get; init; }

		/// <summary>Who asked. The id is always set where the module records one; the names may be null,
		/// in which case a caller that needs them resolves them once for the whole page.</summary>
		public int? RequesterEmployeeId { get; init; }
		public string? RequesterNameAr { get; init; }
		public string? RequesterNameEn { get; init; }

		/// <summary>When it was submitted. Also the ordering key — see <see cref="OrderKey"/>.</summary>
		public DateTime? SubmittedAt { get; init; }

		/// <summary>The module's own status string, already filtered to "pending" by the reader.</summary>
		public required string Status { get; init; }

		/// <summary>Monetary value where the silo has one (inventory). Null elsewhere — absent, not zero.</summary>
		public decimal? Amount { get; init; }

		// ---- raw period parts, for silos that describe a span rather than a value ----
		public DateTime? PeriodStart { get; init; }
		public DateTime? PeriodEnd { get; init; }
		public int? Days { get; init; }
		public DateTime? OnDate { get; init; }
		public TimeSpan? FromTime { get; init; }
		public TimeSpan? ToTime { get; init; }

		/// <summary>Module-supplied navigation. Required: a row nobody can act on is not an inbox row.</summary>
		public required ApprovalNavigationTarget Navigation { get; init; }

		// ---- canonical platform identity -------------------------------------------------------------
		//
		// STAMPED CENTRALLY BY THE AGGREGATOR, never by a module. Two reasons, and both are about keeping
		// one answer rather than four: the silo→code mapping is a property of the approvals platform and
		// belongs with it, and a field each module filled in for itself is a field four modules can
		// disagree about. Modules keep filling the facts they own; identity is derived from the silo they
		// already declared.
		//
		// OPTIONAL AND DEFAULTED so every existing consumer and every existing module reader compiles and
		// behaves exactly as before. This is additive: nothing was renamed and nothing was removed.

		/// <summary>
		/// The canonical <c>IEntityRegistry</c> code this row is about - "LeaveRequest", "EmployeeRequest",
		/// "InventoryApproval", "ProjectBilling".
		///
		/// NULL MEANS UNRESOLVED, and that is a real answer rather than a missing one: the silo has no
		/// verified mapping, so this contract declines to name an entity instead of inventing a code that
		/// would resolve against some real table and point at the wrong record.
		/// </summary>
		public string? EntityType { get; init; }

		/// <summary>
		/// True when this row can be addressed by canonical entity identity. A consumer that needs a
		/// platform entity MUST test this rather than assume - see <see cref="EntityType"/>.
		/// </summary>
		public bool HasCanonicalEntity => EntityType != null;

		/// <summary>
		/// The company this row was READ UNDER - the resolved BusinessContext's company, stamped once by
		/// the aggregator.
		///
		/// IT IS PROVENANCE, NOT A SECOND AUTHORITY. The tenant boundary is enforced by each module reader
		/// before the row exists and by the aggregator's own fail-closed guard; this field records which
		/// context produced the row so a consumer holding rows from more than one call cannot mix them up.
		/// Because exactly one line sets it, from the same context the readers were given, it cannot drift
		/// away from the boundary that was actually applied - which is the failure mode a per-module copy
		/// of the company would have introduced.
		/// </summary>
		public int CompanyId { get; init; }

		/// <summary>
		/// Canonical cross-silo key: "<c>&lt;EntityType&gt;:&lt;EntityId&gt;</c>", plus the step where the
		/// silo models one. Null when the entity is unresolved.
		///
		/// DISTINCT FROM <see cref="Reference"/>, which is keyed on the SILO and stays exactly as it was
		/// for existing consumers. Two keys because they answer two questions: Reference says "which inbox
		/// row", this says "which business record".
		/// </summary>
		public string? EntityReference => EntityType == null
			? null
			: StepId.HasValue ? $"{EntityType}:{EntityId}:{StepId.Value}" : $"{EntityType}:{EntityId}";

		/// <summary>
		/// Deterministic ordering metadata. Newest first is the existing inbox behaviour, and ties are
		/// broken by silo then entity id so a merge across three sources is STABLE rather than dependent
		/// on which reader happened to return first. Without the tiebreak, two rows submitted in the same
		/// tick could swap places between requests.
		/// </summary>
		public (long Ticks, string Silo, int EntityId) OrderKey
			=> (-(SubmittedAt ?? DateTime.MinValue).Ticks, Silo, -EntityId);
	}

	/// <summary>
	/// Reads the pending approvals a given approver may act on, within one company. Implemented by the
	/// module that owns the silo, so the company boundary stays with the code that understands the rows.
	/// </summary>
	public interface IPendingApprovalReader
	{
		/// <summary>
		/// Pending approvals awaiting <paramref name="approverEmployeeId"/> in
		/// <paramref name="context"/>'s company. Read-only. Fails CLOSED: an unresolved company or
		/// approver yields an empty list, never an unfiltered one.
		/// </summary>
		Task<IReadOnlyList<PendingApprovalRow>> PendingForApproverAsync(
			BusinessContext context, int approverEmployeeId, CancellationToken cancellationToken = default);
	}
}
