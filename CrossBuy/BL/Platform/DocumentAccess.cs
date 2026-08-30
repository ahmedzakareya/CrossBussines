using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // CENTRAL DOCUMENT PLATFORM — the shared authorization spine. P0, and deliberately only the spine.
    //
    // THE RULE THIS EXISTS TO ENFORCE: A PATH IS NOT AN AUTHORIZATION.
    //
    // Today a protected business document is reachable by anyone signed in who knows its URL.
    // PrivateFileGate closed the ANONYMOUS hole and says so honestly — it declares a
    // RequireCompanyMatch policy that is applied to no prefix, because the gate runs before
    // UseSession() and therefore cannot resolve a BusinessContext at all. Knowing
    // /uploads/hr-docs/<guid>.pdf is currently equivalent to being allowed to read it, for every
    // authenticated user in every company. GUID names make the store un-enumerable; they do not make
    // it authorized, and a URL that leaks through a forwarded mail or a proxy log grants permanent
    // access with no way to revoke it.
    //
    // The fix cannot live in that middleware. It has to live where a BusinessContext exists, which
    // means a resolved decision taken per document rather than per path. This file is that decision.
    //
    // WHAT IS DELIBERATELY NOT HERE
    //
    //   * No PlatformDocuments schema, no versions, no document types, no metadata engine, no
    //     business events, no approvals. Those are the document DOMAIN and belong to its owner. This
    //     is the security seam they will call.
    //   * No `if (entityType == "Employee")`. The centre never names a module, an entity family or a
    //     module's action vocabulary — see IDocumentOwnerResolver.
    //   * No storage migration. IDocumentStorage exists so the business layer can stop naming
    //     physical paths; nothing is moved onto it in this batch.
    // =============================================================================================

    /// <summary>
    /// What a caller wants to do with a document. Deliberately small and storage-shaped: these are the
    /// verbs a document service performs, not a module's permission vocabulary. Each owning module maps
    /// them onto its own actions through <see cref="IDocumentOwnerResolver.ModuleActionFor"/>.
    /// </summary>
    public enum DocumentAction
    {
        View = 0,
        Download,
        Upload,
        Replace,
        Delete,
        Manage,
    }

    /// <summary>
    /// The confidentiality vocabulary, REUSED rather than reinvented.
    ///
    /// Communication already ships Internal/Confidential/Restricted/System and the product's users
    /// already read those words on a thread. A second, subtly different document enum would give one
    /// deployment two meanings for "Confidential" and force a translation table between them, which is
    /// how two classifications drift until neither can be trusted.
    ///
    /// The VOCABULARY is shared; the DEPENDENCY is not. These are plain strings owned by the platform,
    /// so a document never has to reach into CommThread internals to know what it is classified as.
    /// </summary>
    public static class DocumentConfidentiality
    {
        public const string Internal = "Internal";
        public const string Confidential = "Confidential";
        public const string Restricted = "Restricted";
        public const string System = "System";

        public static readonly IReadOnlyList<string> All = new[] { Internal, Confidential, Restricted, System };

        public static bool IsKnown(string? value) =>
            value != null && All.Contains(value, StringComparer.Ordinal);

        /// <summary>
        /// Above Internal, ordinary read authority is not enough: the caller must hold the owning
        /// module's MANAGE-tier authority over the record. Stated once, here, so no module can quietly
        /// decide that its own confidential documents are ordinary.
        /// </summary>
        public static bool RequiresElevatedAuthority(string? value) =>
            string.Equals(value, Confidential, StringComparison.Ordinal)
            || string.Equals(value, Restricted, StringComparison.Ordinal)
            || string.Equals(value, System, StringComparison.Ordinal);
    }

    /// <summary>The business record a document hangs off. EntityType is an EntityRegistry code.</summary>
    public readonly record struct DocumentOwnerRef(string EntityType, int EntityId);

    /// <summary>
    /// The per-family half of the contract, provided by the module that OWNS the record.
    ///
    /// Two questions only, both of which the centre must not answer for itself:
    ///   * which company owns row N of this family, and
    ///   * what does this module call the authority a given DocumentAction needs.
    ///
    /// Keeping the second question here is what stops the resolver growing a switch over module action
    /// strings. Hr says "employee-view"; Inventory says "read"; the resolver says neither.
    /// </summary>
    public interface IDocumentOwnerResolver
    {
        /// <summary>The EntityRegistry code this resolver answers for.</summary>
        string EntityType { get; }

        /// <summary>
        /// The company that owns this row, or null when the row does not exist. NEVER a default: an
        /// unknown row must be indistinguishable from a foreign one, so both end in the same refusal.
        /// </summary>
        Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default);

        /// <summary>The permission target that represents this record to its own module.</summary>
        PermissionTarget TargetFor(int entityId, int companyId);

        /// <summary>This module's action name for the requested document verb.</summary>
        string ModuleActionFor(DocumentAction action);
    }

    /// <summary>Why a document request was allowed or refused. Carries no document content and no path.</summary>
    public sealed record DocumentAccessDecision(bool Allowed, string ReasonCode)
    {
        public static DocumentAccessDecision Allow() => new(true, DocumentAccessReasons.Allowed);
        public static DocumentAccessDecision Deny(string reason) => new(false, reason);
    }

    public static class DocumentAccessReasons
    {
        public const string Allowed = "allowed";
        public const string CompanyUnresolved = "company_unresolved";
        public const string NoEmployeeIdentity = "no_employee_identity";
        public const string CompanyMismatch = "company_mismatch";
        public const string UnknownEntityType = "unknown_entity_type";
        public const string NoOwnerResolver = "no_owner_resolver";
        public const string OwnerNotFound = "owner_not_found";

        /// <summary>The document claims one company while its owning record belongs to another.</summary>
        public const string RelationMismatch = "relation_mismatch";

        public const string ModuleDenied = "module_denied";
        public const string NoModuleAuthority = "no_module_authority";
        public const string ConfidentialityDenied = "confidentiality_denied";
        public const string UnknownConfidentiality = "unknown_confidentiality";
    }

    /// <summary>
    /// "May this BusinessContext perform ACTION on documents belonging to this business record?"
    ///
    /// The one question the future document platform asks, and the only one it should need to.
    /// </summary>
    public interface IDocumentAccessResolver
    {
        Task<DocumentAccessDecision> AuthorizeAsync(
            BusinessContext? context,
            DocumentOwnerRef owner,
            DocumentAction action,
            int documentCompanyId,
            string? confidentiality = null,
            CancellationToken cancellationToken = default);
    }
}
