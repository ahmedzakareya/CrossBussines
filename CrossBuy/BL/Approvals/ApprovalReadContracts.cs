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
	}

	/// <summary>
	/// Where a consumer should send the approver to act on this row.
	///
	/// The owning module supplies it, so neither the aggregator nor Workspace hardcodes module routes —
	/// the three pairs (People/Leaves, People/Requests, Inventory/Approvals) used to be string literals
	/// in the controller. Resolving this to a URL stays in the presentation layer: a BL service has no
	/// IUrlHelper and should not pretend to.
	/// </summary>
	public sealed record ApprovalNavigationTarget(string Controller, string Action);

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
