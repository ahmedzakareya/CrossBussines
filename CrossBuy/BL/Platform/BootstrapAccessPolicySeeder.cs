using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // CrossBusiness Platform — Stage 2A Batch B — B3: the behaviour-preserving bootstrap seed.
    //
    // WHAT IT IS FOR. When B6 replaces Mechanism A, the three financial services stop treating "no role
    // configured" as an implicit allow and start asking IBootstrapAccessPolicyReader instead. The reader
    // DENIES when no policy exists. So without this seed, B6 would close Accounting, Inventory and CRM
    // reads on every unconfigured company the moment it shipped. This writes today's implicit openness
    // down, explicitly, per company, BEFORE B6 can consume it.
    //
    // THE PLAN IS FOUR ENTRIES, AND THAT IS A CORRECTION.
    //
    // The frozen matrix recorded 41. The live action vocabularies yield 32 bootstrap-eligible actions. The
    // seed implements FOUR, by owner decision, and the reasoning is worth stating because the number looks
    // surprisingly small:
    //
    //   B3 is not a catalogue of every bootstrap-eligible action. It preserves behaviour at the sites B6
    //   actually converts. HR, Projects, Tasks and Communication already run through
    //   ModuleAccessServiceBase, which is action-aware; B6 does not touch them and they never consult this
    //   reader. Manufacturing delegates to Inventory's roles and has no access service of its own. Seeding
    //   any of them would create policy rows that no production decision path reads — misleading
    //   configuration and future cleanup debt.
    //
    //   41 = superseded historical planning figure.
    //   32 = complete live-source eligibility inventory (recorded, not seeded).
    //    4 = implemented seed set, each reconciled to an exact B6 call site below.
    //
    // NOTHING HERE CHANGES AUTHORIZATION TODAY. B6 is inactive, so these rows are inert: the three
    // services still carry Mechanism A and never ask the reader.
    // =============================================================================================

    public enum SeedExecutionMode { DryRun = 0, Execute = 1 }

    public enum SeedResultCode
    {
        Success = 0,
        DryRun,
        ValidationFailed,
        Forbidden,
        Conflict,
        CompanyMismatch,
        CompanyNotFound,
        InvalidPlan,
        PartialFailure,
        AlreadyApplied,
    }

    /// <summary>One planned policy, with the evidence that justifies it and the B6 site that will read it.</summary>
    public sealed class BootstrapSeedPlanEntry
    {
        public string Scope { get; init; } = "";
        public string ActionCode { get; init; } = "";
        public string PolicyState { get; init; } = BootstrapPolicyStates.LegacyCompatibility;
        public string Reason { get; init; } = "";

        /// <summary>The exact production line B6 will convert to consult this policy. Reconciliation, not prose.</summary>
        public string B6CallSite { get; init; } = "";

        public string SourceEvidence { get; init; } = "";
        public bool IsCompatibilityRead { get; init; }
        public bool IsWarehouseScope { get; init; }
        public bool IsOwnerScope { get; init; }

        /// <summary>Whether a human must look at this before it is relied on permanently.</summary>
        public bool ReviewExpected { get; init; } = true;

        /// <summary>LegacyCompatibility is unbounded by design — it records what IS, not a time-boxed exception.</summary>
        public bool ExpiryExpected { get; init; }

        public string Identity => Scope + "." + ActionCode;
    }

    public sealed class BootstrapSeedCommand
    {
        public int CompanyId { get; init; }
        public int ActorEmployeeId { get; init; }
        public string SourceSystem { get; init; } = BootstrapPolicySources.BehaviourPreservingSeed;
        public string Reason { get; init; } = "";
        public Guid? MigrationBatchId { get; init; }
        public SeedExecutionMode Mode { get; init; } = SeedExecutionMode.DryRun;
    }

    public sealed class BootstrapSeedResult
    {
        public SeedResultCode ResultCode { get; init; }
        public int CompanyID { get; init; }
        public string SourceSystem { get; init; } = "";
        public Guid? MigrationBatchId { get; init; }
        public bool IsDryRun { get; init; }

        public int PlannedCount { get; init; }
        public int InsertedCount { get; init; }
        public int ExistingEquivalentCount { get; init; }
        public int SkippedNeverCount { get; init; }
        public int SkippedPosCount { get; init; }
        public int ConflictCount { get; init; }
        public int ValidationFailureCount { get; init; }

        public IReadOnlyList<string> PlannedIdentities { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> InsertedIdentities { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> SkippedNeverIdentities { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> SkippedPosIdentities { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ValidationFailures { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = "";
        public bool Succeeded => ResultCode is SeedResultCode.Success or SeedResultCode.DryRun
            or SeedResultCode.AlreadyApplied;
    }

    public interface IBootstrapAccessPolicySeeder
    {
        /// <summary>The plan. Deterministic, versioned, source-derived — no database access.</summary>
        IReadOnlyList<BootstrapSeedPlanEntry> GetPlan();

        /// <summary>Structural validation of the plan itself, independent of any company.</summary>
        IReadOnlyList<string> ValidatePlan();

        /// <summary>What Execute would do, without writing. Same code path, mode flipped.</summary>
        Task<BootstrapSeedResult> PreviewAsync(
            BusinessContext context, BootstrapSeedCommand command, CancellationToken cancellationToken = default);

        Task<BootstrapSeedResult> ApplyAsync(
            BusinessContext context, BootstrapSeedCommand command, CancellationToken cancellationToken = default);
    }

    public sealed class BootstrapAccessPolicySeeder : IBootstrapAccessPolicySeeder
    {
        /// <summary>Bumped whenever the plan changes, so a seeded company's provenance is identifiable.</summary>
        public const string PlanVersion = "B3.v1";

        private readonly CrossDbContext _db;
        private readonly ILogger<BootstrapAccessPolicySeeder> _log;

        public BootstrapAccessPolicySeeder(CrossDbContext db, ILogger<BootstrapAccessPolicySeeder> log)
        { _db = db; _log = log; }

        private static DateTime UtcNow() => DateTime.UtcNow;

        // =========================================================================================
        // THE PLAN — four entries, each reconciled to the B6 call site that will read it
        // =========================================================================================

        private static readonly IReadOnlyList<BootstrapSeedPlanEntry> Plan = new[]
        {
            new BootstrapSeedPlanEntry
            {
                Scope = "Accounting", ActionCode = "read",
                PolicyState = BootstrapPolicyStates.LegacyCompatibility,
                Reason = "Preserves the implicit bootstrap-open read that AccountingAccessService grants today " +
                         "when no accounting role is configured for the company.",
                B6CallSite = "AccountingAccessService.cs:60 — if (!await AnyRoleConfiguredAsync(...)) return true;",
                SourceEvidence = "Action switch line 29: \"read\" => true — any authenticated user in this company " +
                                 "may view. The bootstrap return at :60 precedes the switch, so read is open on an " +
                                 "unconfigured company and stays open once roles exist.",
                IsCompatibilityRead = true, ReviewExpected = true, ExpiryExpected = false,
            },
            new BootstrapSeedPlanEntry
            {
                Scope = "Inventory", ActionCode = "read",
                PolicyState = BootstrapPolicyStates.LegacyCompatibility,
                Reason = "Preserves the implicit bootstrap-open read that InventoryAccessService grants today " +
                         "when no inventory role is configured for the company.",
                B6CallSite = "InventoryAccessService.cs:48 — if (!await AnyRoleConfiguredAsync(...)) return true;",
                SourceEvidence = "Action switch line 18: \"read\" => true. The SECOND Inventory bootstrap site " +
                                 "(:91, warehouse scope) governs warehouse-access, which is Never-Bootstrap-Open " +
                                 "and therefore receives NO policy row.",
                IsCompatibilityRead = true, ReviewExpected = true, ExpiryExpected = false,
            },
            new BootstrapSeedPlanEntry
            {
                Scope = "Crm", ActionCode = "read",
                PolicyState = BootstrapPolicyStates.LegacyCompatibility,
                Reason = "Preserves the implicit bootstrap-open read that CrmAccessService grants today when no " +
                         "CRM role is configured for the company.",
                B6CallSite = "CrmAccessService.cs:59 — if (!await AnyRoleConfiguredAsync(...)) return true;",
                SourceEvidence = "Action switch line 29: \"read\" => true.",
                IsCompatibilityRead = true, ReviewExpected = true, ExpiryExpected = false,
            },
            new BootstrapSeedPlanEntry
            {
                Scope = "Crm", ActionCode = "edit",
                PolicyState = BootstrapPolicyStates.LegacyCompatibility,
                Reason = "Preserves today's behaviour exactly: with no CRM role configured, the bootstrap return " +
                         "at :59 grants 'edit' before the action switch is reached. NOT a read — flagged for " +
                         "review because it is the one MUTATING action in this plan.",
                B6CallSite = "CrmAccessService.cs:59 — if (!await AnyRoleConfiguredAsync(...)) return true;",
                SourceEvidence = "Action switch line 30: \"edit\" => mgr || rep || mkt, commented 'CrmViewer (or " +
                                 "no role) -> read-only'. That comment describes the CONFIGURED path; the " +
                                 "bootstrap return at :59 short-circuits before it, so an unconfigured company " +
                                 "does permit edit. Seeding it is what makes B6 behaviour-preserving rather than " +
                                 "a silent tightening.",
                IsCompatibilityRead = false, IsOwnerScope = true, ReviewExpected = true, ExpiryExpected = false,
            },
        };

        public IReadOnlyList<BootstrapSeedPlanEntry> GetPlan() => Plan;

        /// <summary>
        /// Validates the plan itself. Runs before any company is touched, so a bad plan can never write a row.
        ///
        /// The Never and POS checks consult the AUTHORITATIVE sources — NeverBootstrapOpen.All and
        /// EntityRegistry.ScopePos — never a copy. Mutation D injects a Never entry into the plan to prove these
        /// two checks are load-bearing rather than decorative.
        /// </summary>
        public IReadOnlyList<string> ValidatePlan()
        {
            var errors = new List<string>();

            foreach (var entry in Plan)
            {
                if (!EntityRegistry.IsKnownScope(entry.Scope))
                    errors.Add($"{entry.Identity}: '{entry.Scope}' is not a known permission scope.");

                if (string.Equals(entry.Scope, EntityRegistry.ScopePos, StringComparison.Ordinal))
                    errors.Add($"{entry.Identity}: POS may never receive a bootstrap policy.");

                if (!BootstrapPolicyStates.IsKnown(entry.PolicyState))
                    errors.Add($"{entry.Identity}: '{entry.PolicyState}' is not a known policy state.");

                if (BootstrapPolicyStates.Permits(entry.PolicyState)
                    && NeverBootstrapOpen.Contains(entry.Scope, entry.ActionCode))
                    errors.Add($"{entry.Identity}: is Never-Bootstrap-Open and must not receive an allowing " +
                               $"policy. {NeverBootstrapOpen.Find(entry.Scope, entry.ActionCode)?.Reason}");

                if (BootstrapPolicyStates.RequiresExpiry(entry.PolicyState) && !entry.ExpiryExpected)
                    errors.Add($"{entry.Identity}: state '{entry.PolicyState}' requires an expiry.");

                if (string.IsNullOrWhiteSpace(entry.Reason))
                    errors.Add($"{entry.Identity}: a reason is required.");

                if (string.IsNullOrWhiteSpace(entry.B6CallSite))
                    errors.Add($"{entry.Identity}: every seeded policy must name the B6 call site that reads it. " +
                               "A policy no production path consumes is misleading configuration.");
            }

            var duplicates = Plan.GroupBy(e => e.Identity, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var duplicate in duplicates)
                errors.Add($"{duplicate}: appears more than once in the plan.");

            return errors;
        }

        // =========================================================================================
        // PREVIEW / APPLY — one code path, mode-switched, so a dry run cannot diverge from the real thing
        // =========================================================================================

        /// <summary>
        /// Preview forces DryRun regardless of what the caller put in the command, so "preview" can never write
        /// because a mode field was set wrong. The command is copied rather than mutated — these are init-only
        /// properties on a class, not a record, so it is written out plainly.
        /// </summary>
        public Task<BootstrapSeedResult> PreviewAsync(
            BusinessContext context, BootstrapSeedCommand command, CancellationToken cancellationToken = default)
        {
            if (command == null) return RunAsync(context, null, cancellationToken);

            var forced = new BootstrapSeedCommand
            {
                CompanyId = command.CompanyId,
                ActorEmployeeId = command.ActorEmployeeId,
                SourceSystem = command.SourceSystem,
                Reason = command.Reason,
                MigrationBatchId = command.MigrationBatchId,
                Mode = SeedExecutionMode.DryRun,
            };

            return RunAsync(context, forced, cancellationToken);
        }

        public Task<BootstrapSeedResult> ApplyAsync(
            BusinessContext context, BootstrapSeedCommand command, CancellationToken cancellationToken = default) =>
            RunAsync(context, command, cancellationToken);

        private async Task<BootstrapSeedResult> RunAsync(
            BusinessContext? context, BootstrapSeedCommand? command, CancellationToken cancellationToken)
        {
            if (command == null)
                return Fail(SeedResultCode.ValidationFailed, 0, "No command supplied.");

            bool dryRun = command.Mode == SeedExecutionMode.DryRun;

            // ---- the plan must be sound before any company is looked at ----
            var planErrors = ValidatePlan();
            if (planErrors.Count > 0)
                return new BootstrapSeedResult
                {
                    ResultCode = SeedResultCode.InvalidPlan,
                    CompanyID = command.CompanyId,
                    IsDryRun = dryRun,
                    ValidationFailureCount = planErrors.Count,
                    ValidationFailures = planErrors,
                    Message = "The seed plan is invalid; nothing was written.",
                };

            // ---- context, fail closed. No CompanyID = 1 fallback anywhere. ----
            if (context == null || context.CompanyId <= 0)
                return Fail(SeedResultCode.Forbidden, command.CompanyId, "No resolved company context.");

            if (command.CompanyId <= 0)
                return Fail(SeedResultCode.ValidationFailed, 0,
                    "A company is required. There is no default company and no fallback to company 1.");

            if (command.CompanyId != context.CompanyId)
                return Fail(SeedResultCode.CompanyMismatch, command.CompanyId,
                    "The requested company does not match the resolved context. A request-supplied company is " +
                    "validated, never trusted.");

            if (string.IsNullOrWhiteSpace(command.Reason))
                return Fail(SeedResultCode.ValidationFailed, command.CompanyId, "A reason is required.");

            if (!BootstrapPolicySources.Known.Contains(command.SourceSystem, StringComparer.Ordinal))
                return Fail(SeedResultCode.ValidationFailed, command.CompanyId,
                    $"'{command.SourceSystem}' is not a known source system.");

            if (!await _db.Companies.AsNoTracking()
                    .AnyAsync(c => c.CompanyID == command.CompanyId, cancellationToken))
                return Fail(SeedResultCode.CompanyNotFound, command.CompanyId, "The company does not exist.");

            // ---- the actor must be a real, active employee of this company ----
            var actor = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == command.ActorEmployeeId)
                .Select(e => new { e.IsActive, e.EmpCompanyID })
                .FirstOrDefaultAsync(cancellationToken);

            if (actor == null || !actor.IsActive || actor.EmpCompanyID != command.CompanyId)
                return Fail(SeedResultCode.Forbidden, command.CompanyId,
                    "The acting employee must be a real, active employee of the target company.");

            // ---- classify every plan entry ----
            var never = new List<string>();
            var pos = new List<string>();
            var seedable = new List<BootstrapSeedPlanEntry>();

            foreach (var entry in Plan)
            {
                // Both filters read the authoritative sources. Mutation D proves they are load-bearing.
                if (NeverBootstrapOpen.Contains(entry.Scope, entry.ActionCode)) { never.Add(entry.Identity); continue; }
                if (string.Equals(entry.Scope, EntityRegistry.ScopePos, StringComparison.Ordinal))
                { pos.Add(entry.Identity); continue; }
                seedable.Add(entry);
            }

            var existing = await _db.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.CompanyID == command.CompanyId)
                .Select(p => new { p.Scope, p.ActionCode, p.State, p.IsActive })
                .ToListAsync(cancellationToken);

            var inserted = new List<string>();
            var equivalent = new List<string>();
            var conflicts = new List<string>();
            var toInsert = new List<BootstrapAccessPolicy>();
            var now = UtcNow();

            foreach (var entry in seedable)
            {
                var active = existing.FirstOrDefault(p =>
                    p.IsActive
                    && string.Equals(p.Scope, entry.Scope, StringComparison.Ordinal)
                    && string.Equals(p.ActionCode, entry.ActionCode, StringComparison.Ordinal));

                if (active != null)
                {
                    // Equivalent = same state. Different state = a deliberate decision somebody made, and the seed
                    // must not overwrite it. Reporting it as a conflict is the honest outcome: re-running a seed
                    // should never silently revert an administrator's change.
                    if (string.Equals(active.State, entry.PolicyState, StringComparison.Ordinal))
                        equivalent.Add(entry.Identity);
                    else
                        conflicts.Add($"{entry.Identity}: an active policy already exists in state " +
                                      $"'{active.State}' but the plan specifies '{entry.PolicyState}'. " +
                                      "The seed does not overwrite a deliberate decision.");
                    continue;
                }

                // A DISABLED historical row must not be silently reactivated. Its existence means somebody turned
                // this compatibility off on purpose, and re-running the seed is not a reason to undo that.
                bool disabledHistory = existing.Any(p =>
                    !p.IsActive
                    && string.Equals(p.Scope, entry.Scope, StringComparison.Ordinal)
                    && string.Equals(p.ActionCode, entry.ActionCode, StringComparison.Ordinal));

                if (disabledHistory)
                {
                    conflicts.Add($"{entry.Identity}: a disabled historical policy exists. The seed will not " +
                                  "silently reactivate compatibility that was deliberately turned off.");
                    continue;
                }

                var row = new BootstrapAccessPolicy
                {
                    CompanyID = command.CompanyId,
                    Scope = entry.Scope,
                    ActionCode = entry.ActionCode,
                    State = entry.PolicyState,
                    Reason = Truncate(entry.Reason + " [" + PlanVersion + "] " + command.Reason, 400),
                    EnabledAt = now,
                    EnabledBy = command.ActorEmployeeId,
                    ExpiresAt = null,                      // LegacyCompatibility records what IS; it is unbounded
                    CreatedAt = now,
                    CreatedBy = command.ActorEmployeeId,
                    SourceSystem = command.SourceSystem,
                    MigrationBatchId = command.MigrationBatchId,
                    IsActive = true,
                };

                // The entity's own validation, again, on the constructed row. Belt and braces: the plan was
                // validated, but the row is what reaches the database.
                var rowErrors = row.Validate();
                if (rowErrors.Count > 0)
                {
                    conflicts.AddRange(rowErrors.Select(e => entry.Identity + ": " + e));
                    continue;
                }

                toInsert.Add(row);
                inserted.Add(entry.Identity);
            }

            // The single condition that decides whether anything reaches the database. Computed once and used
            // both for the reported counts and for the control flow below, so the two cannot disagree.
            bool willCommit = !dryRun && conflicts.Count == 0 && toInsert.Count > 0;

            var summary = new BootstrapSeedResult
            {
                CompanyID = command.CompanyId,
                SourceSystem = command.SourceSystem,
                MigrationBatchId = command.MigrationBatchId,
                IsDryRun = dryRun,
                PlannedCount = Plan.Count,
                PlannedIdentities = Plan.Select(e => e.Identity).ToList(),
                SkippedNeverCount = never.Count,
                SkippedNeverIdentities = never,
                SkippedPosCount = pos.Count,
                SkippedPosIdentities = pos,
                ExistingEquivalentCount = equivalent.Count,
                ConflictCount = conflicts.Count,
                Conflicts = conflicts,
                // DEFECT FOUND BY THE CONFLICT TEST, and worth naming: the first version reported
                // `inserted.Count` here, so an aborted run said "3 inserted" while writing nothing. A result
                // whose counts contradict the committed rows is worse than a failure — it is a false receipt.
                // InsertedCount is now non-zero ONLY on the path that actually commits.
                InsertedCount = willCommit ? inserted.Count : 0,
                InsertedIdentities = willCommit ? inserted : Array.Empty<string>(),
                ResultCode = dryRun ? SeedResultCode.DryRun
                    : conflicts.Count > 0 ? SeedResultCode.Conflict
                    : inserted.Count == 0 ? SeedResultCode.AlreadyApplied
                    : SeedResultCode.Success,
                Message = dryRun
                    ? $"Dry run: {inserted.Count} policy row(s) would be inserted. Nothing was written."
                    : $"{inserted.Count} policy row(s) inserted.",
            };

            if (dryRun)
            {
                _log.LogInformation(
                    "Bootstrap seed DRY RUN for company {Company}: {Would} would insert, {Existing} equivalent, " +
                    "{Never} Never skipped, {Conflicts} conflicts. Nothing written.",
                    command.CompanyId, inserted.Count, equivalent.Count, never.Count, conflicts.Count);
                return summary;
            }

            if (conflicts.Count > 0)
            {
                // Nothing is written when any conflict exists. A half-seeded company is worse than an unseeded
                // one: B6 would then find compatibility for some actions and not others, and the difference would
                // look deliberate.
                _log.LogWarning(
                    "Bootstrap seed for company {Company} ABORTED with {Count} conflict(s); nothing written.",
                    command.CompanyId, conflicts.Count);
                return summary;
            }

            if (toInsert.Count == 0) return summary;

            // ---- one transaction per company. All rows commit together or none do. ----
            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
            try
            {
                _db.BootstrapAccessPolicies.AddRange(toInsert);
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync();
            }
            catch (DbUpdateException ex) when (IsConcurrencyLoss(ex))
            {
                // Two concurrent seeds for the same company. The unique index arbitrated; this side reports the
                // truth instead of a spurious failure, and its rows are rolled back by the transaction.
                foreach (var row in toInsert) _db.Entry(row).State = EntityState.Detached;

                _log.LogInformation(
                    "Bootstrap seed for company {Company} lost a concurrency race; another seed applied first.",
                    command.CompanyId);

                return new BootstrapSeedResult
                {
                    ResultCode = SeedResultCode.Conflict,
                    CompanyID = summary.CompanyID,
                    SourceSystem = summary.SourceSystem,
                    MigrationBatchId = summary.MigrationBatchId,
                    IsDryRun = false,
                    PlannedCount = summary.PlannedCount,
                    PlannedIdentities = summary.PlannedIdentities,
                    SkippedNeverCount = summary.SkippedNeverCount,
                    SkippedNeverIdentities = summary.SkippedNeverIdentities,
                    SkippedPosCount = summary.SkippedPosCount,
                    SkippedPosIdentities = summary.SkippedPosIdentities,
                    ExistingEquivalentCount = summary.ExistingEquivalentCount,
                    InsertedCount = 0,
                    ConflictCount = 1,
                    Conflicts = new[] { "A concurrent seed applied first; this run wrote nothing." },
                    Message = "A concurrent seed applied first; this run wrote nothing.",
                };
            }

            _log.LogInformation(
                "Bootstrap seed APPLIED for company {Company}: {Count} policy row(s), batch {Batch}, plan {Version}. " +
                "These rows are INERT until B6 is enabled — the three services still carry Mechanism A.",
                command.CompanyId, toInsert.Count, command.MigrationBatchId, PlanVersion);

            return summary;
        }

        // =========================================================================================

        private static BootstrapSeedResult Fail(SeedResultCode code, int companyId, string message) => new()
        {
            ResultCode = code,
            CompanyID = companyId,
            ValidationFailureCount = 1,
            ValidationFailures = new[] { message },
            Message = message,
        };

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max);

        /// <summary>
        /// A lost race, matching the Grant Writer's classification: duplicate key (2601/2627) or deadlock victim
        /// (1205). Anything else keeps throwing — swallowing an unknown database error would turn a real fault
        /// into a conflict the caller retries forever.
        /// </summary>
        private static bool IsConcurrencyLoss(DbUpdateException ex) =>
            ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
            && (sql.Number == 2601 || sql.Number == 2627 || sql.Number == 1205);
    }
}
