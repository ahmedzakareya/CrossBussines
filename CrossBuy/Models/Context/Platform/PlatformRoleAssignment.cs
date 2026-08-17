namespace CrossBuy.Models.Context.Platform
{
    // Stage 1 Batch C — the ONE shared module role assignment.
    //
    // Structure and reasoning live in deploy/sql/platform_role_assignments.sql. This class exists so EF can
    // read the table; nothing writes it in Batch C (assignment administration is Batch D / a later batch).
    //
    // Nothing outside IPlatformRoleDirectory may query this entity. That is the rule that lets the four
    // legacy role tables be folded in later as a DATA move rather than a code change — see ADR-026.
    public class PlatformRoleAssignment
    {
        public int ID { get; set; }

        // The isolation key. Never nullable, never defaulted — POS's company-less shape is deliberately
        // not copied here (see the script's header).
        public int CompanyID { get; set; }

        // EntityRegistry.PermissionScopes value. Validated at startup, not at query time.
        public string Scope { get; set; } = "";

        // PlatformPrincipalTypes value. 'Employee' is the only kind Batch C assigns or evaluates.
        public string PrincipalType { get; set; } = PlatformPrincipalTypes.Employee;
        public int PrincipalId { get; set; }

        // The module's own role name (HrManager, PayrollOfficer, ProjectManager, …).
        public string Role { get; set; } = "";

        // NULL = company-wide. Mirrors InventoryUserRoles.ScopeBranchId.
        public int? ScopeBranchId { get; set; }

        public bool IsActive { get; set; } = true;

        // Temporal validity, NOT a delegation record — there is no DelegatedFrom/To, reason, approver or
        // revocation trail here, and pretending otherwise would overstate what the column proves.
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // ---- Stage 2A Batch A: the production Grant Writer's audit and idempotency fields ----
        // Storage: deploy/sql/platform_role_assignments_slice_002.sql. All nullable, because slice-1 rows
        // predate them and inventing values for those rows would be fabricated history.

        // WHEN and BY WHOM a grant was revoked. IsActive alone records THAT a grant is off but not when, by
        // whom, or why — an audit line that cannot answer the only question ever asked of it.
        // CK_PlatformRoleAssignments_Revocation enforces revoked ⇒ inactive, so the authorization outcome and
        // the audit record can never disagree.
        public DateTime? RevokedAt { get; set; }
        public int? RevokedBy { get; set; }

        // Why the grant was created, revoked or re-dated.
        public string? Reason { get; set; }

        // Which writer produced this row. Batch A stamps PlatformGrantSources.GrantWriter. It exists now so
        // that when the legacy role tables are eventually folded in, a migrated row stays distinguishable from
        // one an administrator created — without it the fold-in is irreversible the moment it runs.
        public string? SourceSystem { get; set; }

        // Groups the rows one migration run produced. NOTHING writes it in Batch A — no migration occurs.
        public Guid? MigrationBatchId { get; set; }

        // The caller's request id, for idempotent create. Uniqueness is a DATABASE constraint
        // (UX_PlatformRoleAssignments_Idempotency, per company): two concurrent retries both read "not found"
        // and both insert, so application-level checking alone cannot make create idempotent.
        public string? IdempotencyKey { get; set; }
    }

    // Where a grant row came from. A short closed vocabulary rather than free text, so a later migration can
    // filter on it reliably instead of matching strings.
    public static class PlatformGrantSources
    {
        // An administrator acting through IPlatformGrantWriter — the only producer in Batch A.
        public const string GrantWriter = "GrantWriter";

        // Reserved for the later fold-in of the legacy role tables. Unused in Batch A.
        public const string LegacyAccountingUserRoles = "LegacyAccountingUserRoles";
        public const string LegacyInventoryUserRoles = "LegacyInventoryUserRoles";
        public const string LegacyCrmUserRoles = "LegacyCrmUserRoles";

        public static readonly IReadOnlyList<string> Known = new[]
        {
            GrantWriter, LegacyAccountingUserRoles, LegacyInventoryUserRoles, LegacyCrmUserRoles,
        };
    }

    // The principal kinds the platform is structurally ready for.
    //
    // Only Employee is SUPPORTED in Batch C: the others exist so that adding External Collaboration or a
    // Customer Portal later does not mean altering the primary access path of every module at once. They are
    // validated centrally (IsSupported) and an unsupported kind DENIES rather than being ignored — a grant
    // nobody can evaluate must not be silently skipped, because "skipped" reads as "no such grant".
    public static class PlatformPrincipalTypes
    {
        public const string Employee = "Employee";

        // Structurally known, NOT supported at runtime in Batch C. Assigning one is refused.
        public const string CustomerContact = "CustomerContact";
        public const string VendorContact = "VendorContact";
        public const string PartnerContact = "PartnerContact";
        public const string ServiceAccount = "ServiceAccount";
        public const string IntegrationClient = "IntegrationClient";

        // Mirrors CK_PlatformRoleAssignments_PrincipalType. A value outside this set is a data defect.
        public static readonly IReadOnlyList<string> Known = new[]
        {
            Employee, CustomerContact, VendorContact, PartnerContact, ServiceAccount, IntegrationClient,
        };

        // What an access service will actually evaluate today.
        public static readonly IReadOnlyList<string> Supported = new[] { Employee };

        public static bool IsKnown(string? type) => type != null && Known.Contains(type, StringComparer.Ordinal);
        public static bool IsSupported(string? type) => type != null && Supported.Contains(type, StringComparer.Ordinal);
    }
}
