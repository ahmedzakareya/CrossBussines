using CrossBuy.Models.Platform;

namespace CrossBuy.Models.Context.Platform
{
    // =============================================================================================
    // Stage 2A Batch B — B2: the explicit bootstrap policy store.
    //
    // A POLICY IS NOT A GRANT, and this is a separate table for that reason. A grant says "this employee
    // holds this role"; a policy says "this company has not configured this scope yet, and here is what
    // that permits until it does". Putting them in one table would make every query answer both
    // questions, and the coexistence rule (IMP-001) is that sources are never unioned.
    //
    // WHAT THIS REPLACES. Today three financial services carry `if (no role configured) return true;`
    // before the action switch — Mechanism A. It is invisible, unauditable, cannot exclude a dangerous
    // action, and nobody can tell from a log whether an allow came from a role or from that line. Rows
    // in this table are the same compatibility made explicit, per company, per scope, PER ACTION.
    //
    // THE ONE THING THIS TABLE CANNOT DO: override a Never-Bootstrap-Open classification. That list is
    // NeverBootstrapOpen.All in code, deliberately NOT duplicated as a SQL CHECK — see the SQL slice
    // header for why a second copy would be the more dangerous design.
    // =============================================================================================
    public class BootstrapAccessPolicy
    {
        public int ID { get; set; }

        /// <summary>The isolation key. Never nullable, never defaulted, and there is no company 1 fallback.</summary>
        public int CompanyID { get; set; }

        /// <summary>An EntityRegistry.PermissionScopes value. 'Pos' is refused — POS has no bootstrap behaviour.</summary>
        public string Scope { get; set; } = "";

        /// <summary>
        /// The module's own action string ("post", "read", "doc", …). PER ACTION is the whole point: Mechanism A
        /// could only answer for a whole module, which is why it could not exclude anything.
        /// </summary>
        public string ActionCode { get; set; } = "";

        /// <summary>A <see cref="BootstrapPolicyStates"/> value.</summary>
        public string State { get; set; } = BootstrapPolicyStates.LegacyCompatibility;

        /// <summary>Why this policy exists. A compatibility allowance with no reason cannot be reviewed.</summary>
        public string? Reason { get; set; }

        public DateTime? EnabledAt { get; set; }
        public int? EnabledBy { get; set; }

        /// <summary>
        /// When the allowance stops. MANDATORY for <see cref="BootstrapPolicyStates.Temporary"/> — a time-boxed
        /// exception with no end date is a permanent one wearing a different name, which is RISK-045.
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        public DateTime? ReviewedAt { get; set; }
        public int? ReviewedBy { get; set; }

        /// <summary>Someone has seen this exposure and accepted it. Distinct from having reviewed it.</summary>
        public DateTime? AcknowledgedAt { get; set; }
        public int? AcknowledgedBy { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        /// <summary>Which writer produced the row — <see cref="BootstrapPolicySources"/>.</summary>
        public string? SourceSystem { get; set; }

        /// <summary>Groups the rows one seed run produced, so a run is reversible as a unit.</summary>
        public Guid? MigrationBatchId { get; set; }

        /// <summary>
        /// false = superseded history. Rows are never deleted: the row IS the audit trail of what a company was
        /// permitted and when, and a deleted policy cannot answer "why was this open in March".
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Does this policy permit anything RIGHT NOW — active, permitting state, and not expired?</summary>
        public bool IsEffective(DateTime asOfUtc) =>
            IsActive
            && BootstrapPolicyStates.Permits(State)
            && (ExpiresAt == null || ExpiresAt > asOfUtc);

        /// <summary>
        /// Structural validity, independent of the database. Returns every problem rather than the first, because
        /// a seed reporting one error at a time turns a 41-row plan into 41 round trips.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();

            if (CompanyID <= 0)
                errors.Add("A company is required. There is no default company and no fallback to company 1.");

            if (string.IsNullOrWhiteSpace(Scope)) errors.Add("A scope is required.");
            else if (!BL.Platform.EntityRegistry.IsKnownScope(Scope))
                errors.Add($"'{Scope}' is not a known permission scope.");
            else if (string.Equals(Scope, BL.Platform.EntityRegistry.ScopePos, StringComparison.Ordinal))
                errors.Add("POS has no bootstrap behaviour and may never receive a policy. Its roles live in " +
                           "BranchUserRoles and PosAccessService fails closed without them.");

            if (string.IsNullOrWhiteSpace(ActionCode)) errors.Add("An action code is required.");

            if (!BootstrapPolicyStates.IsKnown(State))
                errors.Add($"'{State}' is not a known bootstrap policy state. A policy nobody can evaluate must " +
                           "not exist — 'cannot be evaluated' reads as 'no policy', and no policy reads as open.");

            if (BootstrapPolicyStates.RequiresExpiry(State) && ExpiresAt == null)
                errors.Add($"State '{BootstrapPolicyStates.Temporary}' requires ExpiresAt. A temporary exception " +
                           "with no end date is a permanent one under another name.");

            if (ExpiresAt.HasValue && EnabledAt.HasValue && ExpiresAt.Value < EnabledAt.Value)
                errors.Add("ExpiresAt cannot precede EnabledAt.");

            // The Never override, enforced at the storage boundary as well as in the reader. Belt and braces on
            // purpose: a row that CLAIMS to permit a Never action would be a lie sitting in an audit table even if
            // no reader ever honoured it.
            if (BootstrapPolicyStates.Permits(State) && NeverBootstrapOpen.Contains(Scope, ActionCode))
                errors.Add($"'{Scope}.{ActionCode}' is Never-Bootstrap-Open and cannot be represented by a " +
                           $"permitting policy. Reason: {NeverBootstrapOpen.Find(Scope, ActionCode)?.Reason}");

            return errors;
        }
    }

    /// <summary>Where a policy row came from. Closed vocabulary so a later query can filter reliably.</summary>
    public static class BootstrapPolicySources
    {
        /// <summary>The behaviour-preserving seed (B3) — the only producer in this increment.</summary>
        public const string BehaviourPreservingSeed = "BehaviourPreservingSeed";

        /// <summary>Reserved for the B10 policy writer. Unused here.</summary>
        public const string PolicyWriter = "PolicyWriter";

        public static readonly IReadOnlyList<string> Known = new[] { BehaviourPreservingSeed, PolicyWriter };
    }
}