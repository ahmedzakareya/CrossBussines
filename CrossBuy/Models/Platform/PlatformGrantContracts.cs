namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // Stage 2A Batch A — the Platform Grant Writer contract.
    //
    // Typed throughout, deliberately. A bool return cannot distinguish "you may not do that" from
    // "that grant already exists" from "the same request already succeeded" — and a caller that
    // cannot tell them apart either retries a forbidden operation forever or treats a duplicate as a
    // failure. Every outcome a caller must branch on is a named value.
    // =============================================================================================

    /// <summary>What happened. Every value is something a caller must be able to act on differently.</summary>
    public enum PlatformGrantOutcome
    {
        /// <summary>The grant was created, revoked or re-dated.</summary>
        Success = 0,

        /// <summary>
        /// The same IdempotencyKey and the same effective command had already succeeded. The ORIGINAL grant is
        /// returned. Distinct from Success because a caller counting created grants must not count this twice.
        /// </summary>
        IdempotentReplay,

        /// <summary>A domain rule rejected the command. Maps to 400.</summary>
        ValidationFailed,

        /// <summary>The actor may not perform this administration. Maps to 403.</summary>
        Forbidden,

        /// <summary>
        /// No such grant, or it belongs to another company. The two are deliberately the same outcome — see
        /// the API contract on information disclosure. Maps to 404.
        /// </summary>
        NotFound,

        /// <summary>An equivalent ACTIVE grant already exists. Maps to 409.</summary>
        Duplicate,

        /// <summary>
        /// The same IdempotencyKey was reused with a DIFFERENT payload, or a concurrent writer changed the row
        /// first. Maps to 409.
        /// </summary>
        Conflict,

        /// <summary>The grant's validity window has passed, so the operation cannot take effect.</summary>
        Expired,

        /// <summary>Revocation of an already-revoked grant. Not an error, and not a second revocation.</summary>
        AlreadyRevoked,
    }

    /// <summary>A grant as an administrator sees it. Never the entity — the entity is the writer's business.</summary>
    public sealed class PlatformGrantView
    {
        public int Id { get; init; }
        public int CompanyId { get; init; }
        public string Scope { get; init; } = "";
        public string PrincipalType { get; init; } = "";
        public int PrincipalId { get; init; }
        public string Role { get; init; } = "";
        public int? ScopeBranchId { get; init; }
        public bool IsActive { get; init; }
        public DateTime? ValidFrom { get; init; }
        public DateTime? ValidTo { get; init; }
        public DateTime CreatedAt { get; init; }
        public int? CreatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public int? UpdatedBy { get; init; }
        public DateTime? RevokedAt { get; init; }
        public int? RevokedBy { get; init; }
        public string? Reason { get; init; }
        public string? SourceSystem { get; init; }

        /// <summary>Whether the grant can decide anything RIGHT NOW: active and inside its window.</summary>
        public bool IsEffective { get; init; }
    }

    /// <summary>
    /// The result of a write. Carries the grant on success so a caller never has to re-query to learn the id it
    /// just created — a re-query would also be a second authorization decision on the same operation.
    /// </summary>
    public sealed class PlatformGrantResult
    {
        public PlatformGrantOutcome Outcome { get; init; }

        /// <summary>Safe to show an administrator. Never names another company's data.</summary>
        public string Message { get; init; } = "";

        public PlatformGrantView? Grant { get; init; }

        /// <summary>Field-level validation detail, for a 400 body. Empty for every other outcome.</summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        public bool Succeeded => Outcome is PlatformGrantOutcome.Success or PlatformGrantOutcome.IdempotentReplay;

        public static PlatformGrantResult Ok(PlatformGrantView grant, string message = "") =>
            new() { Outcome = PlatformGrantOutcome.Success, Grant = grant, Message = message };

        public static PlatformGrantResult Replay(PlatformGrantView grant) =>
            new()
            {
                Outcome = PlatformGrantOutcome.IdempotentReplay,
                Grant = grant,
                Message = "This request had already been applied; the original grant is returned unchanged.",
            };

        public static PlatformGrantResult Invalid(params string[] errors) =>
            new()
            {
                Outcome = PlatformGrantOutcome.ValidationFailed,
                Message = "The grant request is not valid.",
                Errors = errors,
            };

        public static PlatformGrantResult Denied(string message) =>
            new() { Outcome = PlatformGrantOutcome.Forbidden, Message = message };

        public static PlatformGrantResult Missing() =>
            new() { Outcome = PlatformGrantOutcome.NotFound, Message = "No such grant." };

        public static PlatformGrantResult AlreadyExists(PlatformGrantView existing) =>
            new()
            {
                Outcome = PlatformGrantOutcome.Duplicate,
                Grant = existing,
                Message = "An equivalent active grant already exists.",
            };

        public static PlatformGrantResult Conflicting(string message) =>
            new() { Outcome = PlatformGrantOutcome.Conflict, Message = message };

        public static PlatformGrantResult HasExpired(string message) =>
            new() { Outcome = PlatformGrantOutcome.Expired, Message = message };

        public static PlatformGrantResult Revoked(PlatformGrantView grant) =>
            new()
            {
                Outcome = PlatformGrantOutcome.AlreadyRevoked,
                Grant = grant,
                Message = "This grant was already revoked; nothing was changed.",
            };
    }

    // ---------------------------------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Create one grant.
    ///
    /// CompanyId is REQUIRED and is validated against the actor's resolved BusinessContext — never trusted, and
    /// never defaulted. A request that omits it is refused rather than falling back to the context, because a
    /// silent fallback is how a cross-company grant becomes an accident.
    /// </summary>
    public sealed class CreatePlatformGrantCommand
    {
        public int CompanyId { get; init; }
        public string Scope { get; init; } = "";
        public string PrincipalType { get; init; } = PlatformPrincipalTypesRef.Employee;
        public int PrincipalId { get; init; }
        public string Role { get; init; } = "";
        public int? ScopeBranchId { get; init; }
        public DateTime? ValidFrom { get; init; }
        public DateTime? ValidTo { get; init; }
        public string? Reason { get; init; }

        /// <summary>Optional. When supplied, a retry returns the original grant instead of creating a second.</summary>
        public string? IdempotencyKey { get; init; }
    }

    public sealed class RevokePlatformGrantCommand
    {
        public int GrantId { get; init; }
        public int CompanyId { get; init; }

        /// <summary>Required. A revocation with no reason is an audit line that answers nothing.</summary>
        public string Reason { get; init; } = "";
    }

    public sealed class UpdateGrantValidityCommand
    {
        public int GrantId { get; init; }
        public int CompanyId { get; init; }
        public DateTime? ValidFrom { get; init; }
        public DateTime? ValidTo { get; init; }
        public string Reason { get; init; } = "";
    }

    /// <summary>Filter for the administration list. Company is taken from the authorization decision, not from here.</summary>
    public sealed class DirectGrantQuery
    {
        public int CompanyId { get; init; }
        public string? Scope { get; init; }
        public int? PrincipalId { get; init; }
        public string? Role { get; init; }
        public int? ScopeBranchId { get; init; }

        /// <summary>false (default) returns active grants only; true includes revoked and expired history.</summary>
        public bool IncludeInactive { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Administration vocabulary
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The administration rights, SEPARATED as A5 requires. Viewing who holds what is a different right from
    /// changing it, and viewing effective permissions is a different right again — it discloses the outcome of
    /// every rule, which is more than the grant list shows.
    /// </summary>
    public static class GrantAdminActions
    {
        public const string ViewDirectGrants = "grant-view";
        public const string CreateGrant = "grant-create";
        public const string RevokeGrant = "grant-revoke";
        public const string UpdateValidity = "grant-validity";
        public const string ViewEffectivePermissions = "grant-effective-view";
        public const string ViewMigrationDiagnostics = "grant-migration-view";

        public static readonly IReadOnlyList<string> All = new[]
        {
            ViewDirectGrants, CreateGrant, RevokeGrant, UpdateValidity,
            ViewEffectivePermissions, ViewMigrationDiagnostics,
        };

        public static bool IsKnown(string? action) => action != null && All.Contains(action, StringComparer.Ordinal);
    }

    /// <summary>
    /// What kind of administrator the actor is. Ordered by breadth so a ceiling comparison is a comparison.
    ///
    /// These are NOT new stored roles. They are resolved from mechanisms that already exist — Identity roles for
    /// the two administrative tiers, and a module's own access service for the module tier — because IMP-001
    /// decided Identity roles remain separate and coexistence never unions sources.
    /// </summary>
    public enum PlatformGrantAuthority
    {
        /// <summary>Not an administrator. Denies.</summary>
        None = 0,

        /// <summary>
        /// Holds a module's own manage-level right, proven by that module's REAL access service. May administer
        /// grants only in that one scope, only in their own company, and never a role they do not themselves hold.
        /// </summary>
        ModuleAdministrator = 1,

        /// <summary>An Identity administrator. Their own company, any module scope.</summary>
        CompanyAdministrator = 2,

        /// <summary>Platform security. Any company, any scope — the only tier that may act cross-company.</summary>
        PlatformSecurityAdministrator = 3,
    }

    /// <summary>
    /// The Identity roles behind the two administrative tiers.
    ///
    /// Deliberately reusing the vocabulary <see cref="CrossBuy.Models.PlatformOpsAttribute"/> already enforces,
    /// rather than inventing a parallel set: two lists of admin role names would drift, and the one that drifts
    /// is always the one nobody is looking at.
    /// </summary>
    public static class GrantAdminIdentityRoles
    {
        /// <summary>Cross-company authority. The narrowest tier, because it is the widest power.</summary>
        public static readonly IReadOnlyList<string> PlatformSecurity = new[] { "SuperAdmin", "PlatformOps" };

        /// <summary>Own-company authority across module scopes.</summary>
        public static readonly IReadOnlyList<string> CompanyAdministration = new[] { "Admin", "Administrator" };
    }

    /// <summary>
    /// Mirror of <c>Models.Context.Platform.PlatformPrincipalTypes.Employee</c>, so a command DTO in this
    /// namespace does not have to reference the persistence namespace. Asserted equal by a test.
    /// </summary>
    internal static class PlatformPrincipalTypesRef
    {
        internal const string Employee = "Employee";
    }
}
