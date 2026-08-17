using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // Stage 2A Batch A — THE PRODUCTION GRANT WRITER.
    //
    // This closes RISK-037. PlatformRoleAssignments has been the designated grant store since Stage 1
    // Batch C and has had NO production writer: grants could only be inserted by hand, so every module
    // that reads it stayed permanently bootstrap-open in practice. A store nobody can write is a
    // security design that exists only on paper.
    //
    // FOUR RULES THIS CLASS IS BUILT AROUND
    //
    //  1. SESSION-FREE. It takes a BusinessContext; it never reads HttpContext, Session or claims. That is
    //     what makes the same writer usable from a controller, a future console, a migration and a test.
    //  2. FAIL CLOSED. An unresolved company, an unresolved actor, an unknown scope or role, an
    //     unsupported principal kind — all deny. Nothing defaults, and there is no company fallback.
    //  3. NO SECOND AUTHORIZATION ENGINE. The privilege ceiling asks the REAL access services what the
    //     actor may do. It never reconstructs permissions from the grant table, because a permission
    //     recomputed from storage is a second engine that will disagree with the first one eventually.
    //  4. THE DATABASE OWNS UNIQUENESS. Duplicate-active and idempotency are unique indexes. Two
    //     concurrent requests both read "not found" and both insert; only the engine can arbitrate.
    // =============================================================================================

    /// <summary>
    /// The actor's Identity roles, resolved from a user id.
    ///
    /// EXTRACTED so the writer does not depend on ASP.NET Identity plumbing. Two reasons, and the second is the
    /// one that matters: a writer that injects <c>UserManager&lt;Users&gt;</c> cannot be constructed in a test
    /// without standing up the whole Identity stack, and a privilege-ceiling rule nobody can test is a rule
    /// nobody has verified. The production implementation is the only place UserManager appears.
    ///
    /// It takes a USER ID, not an HttpContext — the writer stays session-free.
    /// </summary>
    public interface IPlatformAdminIdentity
    {
        Task<IReadOnlyList<string>> RolesAsync(string? userId, CancellationToken cancellationToken = default);
    }

    public sealed class IdentityPlatformAdminIdentity : IPlatformAdminIdentity
    {
        private readonly UserManager<Users> _users;

        public IdentityPlatformAdminIdentity(UserManager<Users> users) => _users = users;

        public async Task<IReadOnlyList<string>> RolesAsync(
            string? userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) return Array.Empty<string>();

            var user = await _users.FindByIdAsync(userId);
            if (user == null) return Array.Empty<string>();

            return (await _users.GetRolesAsync(user)).ToList();
        }
    }

    public interface IPlatformGrantWriter
    {
        Task<PlatformGrantResult> CreateGrantAsync(
            BusinessContext actor, CreatePlatformGrantCommand command, CancellationToken cancellationToken = default);

        Task<PlatformGrantResult> RevokeGrantAsync(
            BusinessContext actor, RevokePlatformGrantCommand command, CancellationToken cancellationToken = default);

        Task<PlatformGrantResult> UpdateValidityAsync(
            BusinessContext actor, UpdateGrantValidityCommand command, CancellationToken cancellationToken = default);

        Task<PlatformGrantResult> GetGrantAsync(
            BusinessContext actor, int grantId, int companyId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PlatformGrantView>> ListDirectGrantsAsync(
            BusinessContext actor, DirectGrantQuery query, CancellationToken cancellationToken = default);
    }

    public sealed class PlatformGrantWriter : IPlatformGrantWriter
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessEventService _events;
        private readonly IPlatformRoleDirectory _roles;
        private readonly IEnumerable<IModuleAccessService> _modules;
        private readonly IPlatformAdminIdentity _identity;
        // Modules publish their role/action vocabulary to the kernel; the kernel never names a module.
        private readonly IPlatformPermissionVocabularyRegistry _vocabulary;
        private readonly ILogger<PlatformGrantWriter> _log;

        public PlatformGrantWriter(
            CrossDbContext db,
            IBusinessEventService events,
            IPlatformRoleDirectory roles,
            IEnumerable<IModuleAccessService> modules,
            IPlatformAdminIdentity identity,
            IPlatformPermissionVocabularyRegistry vocabulary,
            ILogger<PlatformGrantWriter> log)
        {
            _db = db; _events = events; _roles = roles; _modules = modules; _identity = identity; _vocabulary = vocabulary; _log = log;
        }

        // ONE clock per operation, matching PlatformRoleDirectory's policy: two validity comparisons inside one
        // decision must not straddle a tick.
        private static DateTime UtcNow() => DateTime.UtcNow;

        /// <summary>Scopes this writer refuses outright. POS is administered through BranchUserRoles — IMP-001.</summary>
        private static readonly string[] RefusedScopes = { EntityRegistry.ScopePos };

        // =========================================================================================
        // CREATE
        // =========================================================================================

        public async Task<PlatformGrantResult> CreateGrantAsync(
            BusinessContext actor, CreatePlatformGrantCommand command, CancellationToken cancellationToken = default)
        {
            if (command == null) return PlatformGrantResult.Invalid("No command was supplied.");

            var validation = ValidateCreate(command);
            if (validation.Count > 0) return PlatformGrantResult.Invalid(validation.ToArray());

            var authority = await ResolveAuthorityAsync(
                actor, command.CompanyId, command.Scope, GrantAdminActions.CreateGrant, cancellationToken);
            if (!authority.Allowed) return await DenyAsync(actor, command.CompanyId, command.Scope, command.Role,
                GrantAdminActions.CreateGrant, authority.Reason, cancellationToken);

            // ---- the target principal must be a real, active employee of the TARGET company ----
            var principal = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == command.PrincipalId)
                .Select(e => new { e.ID, e.EmpCompanyID, e.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            if (principal == null)
                return PlatformGrantResult.Invalid($"Employee {command.PrincipalId} does not exist.");

            if (principal.EmpCompanyID != command.CompanyId)
                // Deliberately the same shape as "does not exist": confirming that an employee exists in ANOTHER
                // company is a cross-tenant disclosure, and an administrator has no need to learn it.
                return PlatformGrantResult.Invalid($"Employee {command.PrincipalId} does not exist in this company.");

            if (!principal.IsActive)
                return PlatformGrantResult.Invalid(
                    $"Employee {command.PrincipalId} is inactive and cannot receive a new grant.");

            // ---- the company must exist ----
            if (!await _db.Companies.AsNoTracking().AnyAsync(c => c.CompanyID == command.CompanyId, cancellationToken))
                return PlatformGrantResult.Invalid($"Company {command.CompanyId} does not exist.");

            // ---- branch scope must belong to the same company, and must not silently widen ----
            if (command.ScopeBranchId is int branchId)
            {
                var branchCompany = await _db.Branches.AsNoTracking()
                    .Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID)
                    .FirstOrDefaultAsync(cancellationToken);

                if (branchCompany == null || branchCompany.Value != command.CompanyId)
                    return PlatformGrantResult.Invalid($"Branch {branchId} does not exist in this company.");
            }

            // ---- privilege ceiling, through the REAL access services ----
            var ceiling = await CheckCeilingAsync(actor, authority, command.CompanyId, command.Scope, command.Role,
                command.PrincipalId, cancellationToken);
            if (!ceiling.Allowed) return await DenyAsync(actor, command.CompanyId, command.Scope, command.Role,
                GrantAdminActions.CreateGrant, ceiling.Reason, cancellationToken);

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            // ---- idempotent replay ----
            if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
            {
                var prior = await _db.PlatformRoleAssignments
                    .Where(r => r.CompanyID == command.CompanyId && r.IdempotencyKey == command.IdempotencyKey)
                    .FirstOrDefaultAsync(cancellationToken);

                if (prior != null)
                {
                    // Same key, same effective command ⇒ the original result. Same key, DIFFERENT payload ⇒
                    // conflict: silently returning the original would tell the caller their new intent succeeded.
                    if (!SamePayload(prior, command))
                        return PlatformGrantResult.Conflicting(
                            "This idempotency key was already used for a different grant. Use a new key.");

                    return PlatformGrantResult.Replay(ToView(prior));
                }
            }

            // ---- duplicate-active check, then let the DATABASE arbitrate ----
            var existing = await _db.PlatformRoleAssignments
                .Where(r => r.CompanyID == command.CompanyId
                         && r.Scope == command.Scope
                         && r.PrincipalType == command.PrincipalType
                         && r.PrincipalId == command.PrincipalId
                         && r.Role == command.Role
                         && r.ScopeBranchId == command.ScopeBranchId
                         && r.IsActive)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null) return PlatformGrantResult.AlreadyExists(ToView(existing));

            var now = UtcNow();
            var row = new PlatformRoleAssignment
            {
                CompanyID = command.CompanyId,
                Scope = command.Scope,
                PrincipalType = command.PrincipalType,
                PrincipalId = command.PrincipalId,
                Role = command.Role,
                ScopeBranchId = command.ScopeBranchId,
                IsActive = true,
                ValidFrom = command.ValidFrom,
                ValidTo = command.ValidTo,
                CreatedAt = now,
                CreatedBy = actor.EmployeeId,
                Reason = Trim(command.Reason, 400),
                SourceSystem = PlatformGrantSources.GrantWriter,
                IdempotencyKey = Trim(command.IdempotencyKey, 120),
            };

            _db.PlatformRoleAssignments.Add(row);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsConcurrencyLoss(ex))
            {
                // The race the application check cannot win: two concurrent creates both saw "not found".
                // The engine arbitrated; this side reports the truth rather than a spurious failure.
                _db.Entry(row).State = EntityState.Detached;

                _log.LogInformation(
                    "Grant create lost a concurrency race for company {Company} scope {Scope} principal {Principal}.",
                    command.CompanyId, command.Scope, command.PrincipalId);

                await RecordConflictAsync(actor, command, cancellationToken);
                await tx.CommitAsync();

                // B0 FINDING — CONCURRENT IDEMPOTENT REPLAY.
                //
                // The pre-insert replay check above cannot help a retry STORM: every concurrent caller reads
                // "not found" and only one insert survives. Before this, the losers received Conflict — which
                // breaks the guarantee B0.3 states, that the same command and key return the ORIGINAL result.
                // Sequential retries replayed correctly and concurrent ones did not, which is the worst shape for
                // a guarantee to have: it holds in the test you write first.
                //
                // Re-reading by key AFTER the engine has arbitrated turns the loser into a replay, so a client
                // resending a request while the first is still in flight gets the same answer either way.
                if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
                {
                    var winner = await _db.PlatformRoleAssignments.AsNoTracking()
                        .FirstOrDefaultAsync(
                            r => r.CompanyID == command.CompanyId && r.IdempotencyKey == command.IdempotencyKey,
                            cancellationToken);

                    if (winner != null && SamePayload(winner, command))
                        return PlatformGrantResult.Replay(ToView(winner));
                }

                return PlatformGrantResult.Conflicting(
                    "An equivalent grant was created concurrently, or this idempotency key was just used.");
            }

            // In-transaction, immediately before commit, no swallowing catch — the kernel's contract.
            await _events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.PlatformRoleAssignment,
                EntityId = row.ID,
                EventType = BusinessEventTypes.Build(EntityRegistry.PlatformRoleAssignment, "Created"),
                CompanyIdOverride = row.CompanyID,
                BranchIdOverride = row.ScopeBranchId,
                ActorEmployeeIdOverride = actor.EmployeeId,
                CorrelationId = actor.CorrelationId,
                Payload = EventPayload(row, authority.Authority, command.Reason),
            }, cancellationToken);

            await tx.CommitAsync();

            _log.LogInformation(
                "Grant {Id} created: company {Company}, scope {Scope}, role {Role}, principal {Principal}, by {Actor} ({Authority}).",
                row.ID, row.CompanyID, row.Scope, row.Role, row.PrincipalId, actor.EmployeeId, authority.Authority);

            return PlatformGrantResult.Ok(ToView(row), "Grant created.");
        }

        // =========================================================================================
        // REVOKE
        // =========================================================================================

        public async Task<PlatformGrantResult> RevokeGrantAsync(
            BusinessContext actor, RevokePlatformGrantCommand command, CancellationToken cancellationToken = default)
        {
            if (command == null) return PlatformGrantResult.Invalid("No command was supplied.");
            if (command.GrantId <= 0) return PlatformGrantResult.Invalid("A grant id is required.");
            if (string.IsNullOrWhiteSpace(command.Reason))
                return PlatformGrantResult.Invalid("A revocation reason is required.");
            if (command.CompanyId <= 0) return PlatformGrantResult.Invalid("A company is required.");

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            // A LOCKED read inside the transaction. Concurrent revoke/update serialise rather than racing, which
            // is this project's established concurrency idiom — a locked read is a fact. No schema token needed.
            var row = await LoadForUpdateAsync(command.GrantId, command.CompanyId, cancellationToken);
            if (row == null) return PlatformGrantResult.Missing();

            var authority = await ResolveAuthorityAsync(
                actor, row.CompanyID, row.Scope, GrantAdminActions.RevokeGrant, cancellationToken);
            if (!authority.Allowed) return await DenyAsync(actor, row.CompanyID, row.Scope, row.Role,
                GrantAdminActions.RevokeGrant, authority.Reason, cancellationToken);

            var ceiling = await CheckCeilingAsync(actor, authority, row.CompanyID, row.Scope, row.Role,
                row.PrincipalId, cancellationToken);
            if (!ceiling.Allowed) return await DenyAsync(actor, row.CompanyID, row.Scope, row.Role,
                GrantAdminActions.RevokeGrant, ceiling.Reason, cancellationToken);

            // Already revoked is NOT an error and NOT a second revocation. Returning Success would overwrite the
            // original RevokedAt/RevokedBy and destroy who actually did it.
            if (!row.IsActive) return PlatformGrantResult.Revoked(ToView(row));

            var now = UtcNow();
            row.IsActive = false;              // no hard delete, ever — the row IS the audit trail
            row.RevokedAt = now;
            row.RevokedBy = actor.EmployeeId;
            row.Reason = Trim(command.Reason, 400);
            row.UpdatedAt = now;
            row.UpdatedBy = actor.EmployeeId;

            await _db.SaveChangesAsync(cancellationToken);

            await _events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.PlatformRoleAssignment,
                EntityId = row.ID,
                EventType = BusinessEventTypes.Build(EntityRegistry.PlatformRoleAssignment, "Revoked"),
                CompanyIdOverride = row.CompanyID,
                BranchIdOverride = row.ScopeBranchId,
                ActorEmployeeIdOverride = actor.EmployeeId,
                CorrelationId = actor.CorrelationId,
                Payload = EventPayload(row, authority.Authority, command.Reason),
            }, cancellationToken);

            await tx.CommitAsync();

            _log.LogInformation(
                "Grant {Id} revoked by {Actor} ({Authority}): {Reason}",
                row.ID, actor.EmployeeId, authority.Authority, command.Reason);

            return PlatformGrantResult.Ok(ToView(row), "Grant revoked.");
        }

        // =========================================================================================
        // VALIDITY
        // =========================================================================================

        public async Task<PlatformGrantResult> UpdateValidityAsync(
            BusinessContext actor, UpdateGrantValidityCommand command, CancellationToken cancellationToken = default)
        {
            if (command == null) return PlatformGrantResult.Invalid("No command was supplied.");
            if (command.GrantId <= 0) return PlatformGrantResult.Invalid("A grant id is required.");
            if (command.CompanyId <= 0) return PlatformGrantResult.Invalid("A company is required.");
            if (string.IsNullOrWhiteSpace(command.Reason))
                return PlatformGrantResult.Invalid("A reason is required for a validity change.");
            if (command.ValidFrom.HasValue && command.ValidTo.HasValue && command.ValidTo < command.ValidFrom)
                return PlatformGrantResult.Invalid("ValidTo cannot precede ValidFrom.");

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            var row = await LoadForUpdateAsync(command.GrantId, command.CompanyId, cancellationToken);
            if (row == null) return PlatformGrantResult.Missing();

            var authority = await ResolveAuthorityAsync(
                actor, row.CompanyID, row.Scope, GrantAdminActions.UpdateValidity, cancellationToken);
            if (!authority.Allowed) return await DenyAsync(actor, row.CompanyID, row.Scope, row.Role,
                GrantAdminActions.UpdateValidity, authority.Reason, cancellationToken);

            var ceiling = await CheckCeilingAsync(actor, authority, row.CompanyID, row.Scope, row.Role,
                row.PrincipalId, cancellationToken);
            if (!ceiling.Allowed) return await DenyAsync(actor, row.CompanyID, row.Scope, row.Role,
                GrantAdminActions.UpdateValidity, ceiling.Reason, cancellationToken);

            // A revoked grant is not re-dated back to life. Reactivation is a separate, explicit decision with
            // its own audit history; letting a date change do it would be reactivation by side effect.
            if (!row.IsActive)
                return PlatformGrantResult.Conflicting(
                    "This grant is revoked. A validity change cannot reactivate it — that is a separate decision.");

            var now = UtcNow();
            bool wasEffective = IsEffective(row, now);

            // Written plainly on purpose. The first version packed this into one pattern expression and did not
            // compile; a date-window comparison that decides whether authority widens is the last place to be
            // clever with syntax.
            bool startsInTime = command.ValidFrom == null || command.ValidFrom.Value <= now;
            bool endsInTime = command.ValidTo == null || command.ValidTo.Value >= now;
            bool becomesEffective = startsInTime && endsInTime;

            // An EXPIRED grant cannot be made effective by a simple date change. The approved design does not
            // permit reactivation-by-update, so this is refused rather than quietly widening authority.
            if (!wasEffective && becomesEffective)
                return PlatformGrantResult.HasExpired(
                    "This grant is not currently effective. Making it effective again by changing its dates is " +
                    "reactivation, which is a separate decision — create a new grant instead.");

            var previousFrom = row.ValidFrom;
            var previousTo = row.ValidTo;

            row.ValidFrom = command.ValidFrom;
            row.ValidTo = command.ValidTo;
            row.Reason = Trim(command.Reason, 400);
            row.UpdatedAt = now;
            row.UpdatedBy = actor.EmployeeId;

            await _db.SaveChangesAsync(cancellationToken);

            await _events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.PlatformRoleAssignment,
                EntityId = row.ID,
                EventType = BusinessEventTypes.Build(EntityRegistry.PlatformRoleAssignment, "ValidityChanged"),
                CompanyIdOverride = row.CompanyID,
                BranchIdOverride = row.ScopeBranchId,
                ActorEmployeeIdOverride = actor.EmployeeId,
                CorrelationId = actor.CorrelationId,
                Payload = new
                {
                    grantId = row.ID,
                    companyId = row.CompanyID,
                    scope = row.Scope,
                    role = row.Role,
                    principalType = row.PrincipalType,
                    principalId = row.PrincipalId,
                    scopeBranchId = row.ScopeBranchId,
                    previousValidFrom = previousFrom,
                    previousValidTo = previousTo,
                    validFrom = row.ValidFrom,
                    validTo = row.ValidTo,
                    actorEmployeeId = actor.EmployeeId,
                    authority = authority.Authority.ToString(),
                    reason = command.Reason,
                    sourceSystem = row.SourceSystem,
                    migrationBatchId = row.MigrationBatchId,
                    occurredAt = now,
                },
            }, cancellationToken);

            await tx.CommitAsync();

            return PlatformGrantResult.Ok(ToView(row), "Validity updated.");
        }

        // =========================================================================================
        // READ
        // =========================================================================================

        public async Task<PlatformGrantResult> GetGrantAsync(
            BusinessContext actor, int grantId, int companyId, CancellationToken cancellationToken = default)
        {
            if (grantId <= 0 || companyId <= 0) return PlatformGrantResult.Invalid("A grant id and company are required.");

            var authority = await ResolveAuthorityAsync(
                actor, companyId, scope: null, GrantAdminActions.ViewDirectGrants, cancellationToken);
            if (!authority.Allowed) return PlatformGrantResult.Denied(authority.Reason);

            var row = await _db.PlatformRoleAssignments.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ID == grantId && r.CompanyID == companyId, cancellationToken);

            // A foreign-company grant is NotFound, indistinguishable from one that never existed — the company
            // predicate is part of the query, so the writer never learns whether it exists elsewhere either.
            if (row == null) return PlatformGrantResult.Missing();

            // A module administrator sees only their own scope.
            if (authority.Authority == PlatformGrantAuthority.ModuleAdministrator
                && !string.Equals(authority.Scope, row.Scope, StringComparison.Ordinal))
                return PlatformGrantResult.Missing();

            return PlatformGrantResult.Ok(ToView(row));
        }

        public async Task<IReadOnlyList<PlatformGrantView>> ListDirectGrantsAsync(
            BusinessContext actor, DirectGrantQuery query, CancellationToken cancellationToken = default)
        {
            if (query == null || query.CompanyId <= 0) return Array.Empty<PlatformGrantView>();

            var authority = await ResolveAuthorityAsync(
                actor, query.CompanyId, query.Scope, GrantAdminActions.ViewDirectGrants, cancellationToken);
            if (!authority.Allowed) return Array.Empty<PlatformGrantView>();

            var q = _db.PlatformRoleAssignments.AsNoTracking().Where(r => r.CompanyID == query.CompanyId);

            // A module administrator's list is confined to their scope regardless of what they asked for.
            if (authority.Authority == PlatformGrantAuthority.ModuleAdministrator)
                q = q.Where(r => r.Scope == authority.Scope);
            else if (!string.IsNullOrWhiteSpace(query.Scope))
                q = q.Where(r => r.Scope == query.Scope);

            if (query.PrincipalId is > 0) q = q.Where(r => r.PrincipalId == query.PrincipalId);
            if (!string.IsNullOrWhiteSpace(query.Role)) q = q.Where(r => r.Role == query.Role);
            if (query.ScopeBranchId is > 0) q = q.Where(r => r.ScopeBranchId == query.ScopeBranchId);
            if (!query.IncludeInactive) q = q.Where(r => r.IsActive);

            var rows = await q
                .OrderBy(r => r.Scope).ThenBy(r => r.PrincipalId).ThenBy(r => r.Role).ThenByDescending(r => r.ID)
                .Take(500)
                .ToListAsync(cancellationToken);

            return rows.Select(ToView).ToList();
        }

        // =========================================================================================
        // A5 — administration authorization
        // =========================================================================================

        private sealed class AuthorityDecision
        {
            public bool Allowed { get; init; }
            public string Reason { get; init; } = "";
            public PlatformGrantAuthority Authority { get; init; }

            /// <summary>For a module administrator, the one scope they may administer.</summary>
            public string? Scope { get; init; }

            public static AuthorityDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
        }

        /// <summary>
        /// Who the actor is, and whether they may perform this administration on this company and scope.
        ///
        /// THE RULE THAT MATTERS MOST HERE: grant administration must never rest on Bootstrap Open. A module's
        /// access service answers "true" for its manage action when NOTHING is configured for that company —
        /// that is bootstrap-open compatibility, and treating it as administrative authority would mean any
        /// employee of an unconfigured company could grant themselves a role. So a ModuleAdministrator claim is
        /// admissible only when the scope is already configured; the FIRST grant in a company can only be made
        /// by a real Identity administrator. That is A5 rule 6 and it is why RISK-043 does not widen here.
        /// </summary>
        private async Task<AuthorityDecision> ResolveAuthorityAsync(
            BusinessContext actor, int companyId, string? scope, string adminAction, CancellationToken cancellationToken)
        {
            if (actor == null) return AuthorityDecision.Deny("No business context — denied.");
            if (actor.CompanyId <= 0) return AuthorityDecision.Deny("No company resolved for the actor — denied.");
            if (companyId <= 0) return AuthorityDecision.Deny("No target company — denied.");
            if (!GrantAdminActions.IsKnown(adminAction))
                return AuthorityDecision.Deny($"Unknown administration action '{adminAction}' — denied.");

            // A worker or system context is not an administrator. Grant administration is an interactive,
            // attributable act; a context with no employee has nobody to attribute it to.
            if (actor.IsSystem || actor.Source == BusinessContextSource.Worker)
                return AuthorityDecision.Deny("A system or worker context may not administer grants.");
            if (actor.EmployeeId is not > 0)
                return AuthorityDecision.Deny("No resolved employee — denied.");

            // An inactive administrator is denied, whatever roles they still hold.
            var me = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == actor.EmployeeId!.Value)
                .Select(e => new { e.IsActive, e.EmpCompanyID })
                .FirstOrDefaultAsync(cancellationToken);

            if (me == null) return AuthorityDecision.Deny("The acting employee does not exist — denied.");
            if (!me.IsActive) return AuthorityDecision.Deny("The acting employee is inactive — denied.");

            // The actor's own company comes from the RESOLVED context, cross-checked against the employee row.
            // A request-supplied company is validated below, never trusted.
            if (me.EmpCompanyID != actor.CompanyId)
                return AuthorityDecision.Deny("The acting employee's company does not match the resolved context — denied.");

            var identityRoles = await IdentityRolesAsync(actor);

            bool platformSecurity = identityRoles.Any(r =>
                GrantAdminIdentityRoles.PlatformSecurity.Contains(r, StringComparer.OrdinalIgnoreCase));
            bool companyAdmin = identityRoles.Any(r =>
                GrantAdminIdentityRoles.CompanyAdministration.Contains(r, StringComparer.OrdinalIgnoreCase));

            // ---- platform security: any company, any scope ----
            if (platformSecurity)
                return new AuthorityDecision
                {
                    Allowed = true,
                    Authority = PlatformGrantAuthority.PlatformSecurityAdministrator,
                };

            // ---- everything below is confined to the actor's OWN company ----
            if (companyId != actor.CompanyId)
                return AuthorityDecision.Deny(
                    "Cross-company grant administration requires platform security authority.");

            if (companyAdmin)
                return new AuthorityDecision
                {
                    Allowed = true,
                    Authority = PlatformGrantAuthority.CompanyAdministrator,
                };

            // ---- module administrator: needs a scope, and the scope must already be configured ----
            if (string.IsNullOrWhiteSpace(scope))
                return AuthorityDecision.Deny(
                    "Grant administration across scopes requires company or platform administration authority.");

            if (!EntityRegistry.IsKnownScope(scope))
                return AuthorityDecision.Deny($"'{scope}' is not a known permission scope.");

            // A5 rule 6 — role administration must never depend on Bootstrap Open.
            if (!await _roles.AnyConfiguredAsync(companyId, scope, cancellationToken))
                return AuthorityDecision.Deny(
                    $"No role is configured for scope '{scope}' in this company, so module-level authority cannot " +
                    "be established — the module would be answering under bootstrap-open compatibility, which is " +
                    "not administrative authority. The first grant must be made by a company or platform administrator.");

            var module = ModuleFor(scope);
            if (module == null)
                return AuthorityDecision.Deny($"No access service is registered for scope '{scope}'.");

            var manageAction = ManageActionFor(module);
            if (manageAction == null)
                // A6: exact comparability is undefined for this module, so fail safely and require platform authority.
                return AuthorityDecision.Deny(
                    $"Scope '{scope}' publishes no manage-level action, so module-level grant authority cannot be " +
                    "established for it. Platform security authority is required.");

            if (!await module.CanAsync(actor, manageAction, null, cancellationToken))
                return AuthorityDecision.Deny($"You do not hold '{manageAction}' in scope '{scope}'.");

            return new AuthorityDecision
            {
                Allowed = true,
                Authority = PlatformGrantAuthority.ModuleAdministrator,
                Scope = scope,
            };
        }

        // =========================================================================================
        // A6 — privilege ceiling
        // =========================================================================================

        private sealed class CeilingDecision
        {
            public bool Allowed { get; init; }
            public string Reason { get; init; } = "";
            public static CeilingDecision Ok() => new() { Allowed = true };
            public static CeilingDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
        }

        /// <summary>
        /// May this actor confer THIS role, in THIS scope, in THIS company, on THIS principal?
        ///
        /// Resolved through the real access services and the real role directory — never recomputed from the
        /// grant table. RISK-039 is the escalation this closes: without a ceiling, a module administrator can
        /// grant a role wider than their own, and an administrator can grant it to themselves.
        /// </summary>
        private async Task<CeilingDecision> CheckCeilingAsync(
            BusinessContext actor, AuthorityDecision authority, int companyId, string scope, string role,
            int principalId, CancellationToken cancellationToken)
        {
            // Platform security is the ceiling. There is no tier above it to compare against.
            if (authority.Authority == PlatformGrantAuthority.PlatformSecurityAdministrator) return CeilingDecision.Ok();

            // ---- self-escalation ----
            // An administrator below platform security may not grant to themselves. Granting yourself a role is
            // the shortest path from "may administer" to "may do anything", and it needs a second person.
            if (actor.EmployeeId is int actorId && actorId == principalId)
                return CeilingDecision.Deny(
                    "You may not grant a role to yourself. A self-grant requires platform security authority.");

            // ---- a module administrator is confined to their own scope ----
            if (authority.Authority == PlatformGrantAuthority.ModuleAdministrator
                && !string.Equals(authority.Scope, scope, StringComparison.Ordinal))
                return CeilingDecision.Deny(
                    $"Your authority is limited to scope '{authority.Scope}'; you may not administer '{scope}'.");

            // ---- the actor must hold the role they are conferring ----
            //
            // Read through IPlatformRoleDirectory, the ONE reader, so the comparison uses the same active/in-date
            // policy every authorization decision uses. A company administrator is exempt: their authority is
            // company-wide by definition, and requiring them to hold every module role personally would mean an
            // administrator could never configure a module they do not work in.
            if (authority.Authority == PlatformGrantAuthority.ModuleAdministrator)
            {
                var mine = await _roles.RolesAsync(actor, scope, cancellationToken);
                if (!mine.Any(g => string.Equals(g.Role, role, StringComparison.Ordinal)))
                    return CeilingDecision.Deny(
                        $"You do not hold '{role}' in scope '{scope}', so you may not confer it. " +
                        "A grant may never exceed the grantor's own effective rights.");
            }

            return CeilingDecision.Ok();
        }

        // =========================================================================================
        // A4 — command validation
        // =========================================================================================

        private List<string> ValidateCreate(CreatePlatformGrantCommand c)
        {
            var errors = new List<string>();

            if (c.CompanyId <= 0) errors.Add("A company is required. There is no default company.");
            if (c.PrincipalId <= 0) errors.Add("A principal is required.");

            if (string.IsNullOrWhiteSpace(c.Scope)) errors.Add("A scope is required.");
            else if (!EntityRegistry.IsKnownScope(c.Scope)) errors.Add($"'{c.Scope}' is not a known permission scope.");
            else if (RefusedScopes.Contains(c.Scope, StringComparer.Ordinal))
                errors.Add($"Scope '{c.Scope}' is not administered through this writer. POS roles live in BranchUserRoles.");

            if (string.IsNullOrWhiteSpace(c.PrincipalType)) errors.Add("A principal type is required.");
            else if (!PlatformPrincipalTypes.IsKnown(c.PrincipalType))
                errors.Add($"'{c.PrincipalType}' is not a known principal type.");
            else if (!PlatformPrincipalTypes.IsSupported(c.PrincipalType))
                // An unsupported kind DENIES rather than being stored. A grant nobody can evaluate must not exist:
                // "cannot be evaluated" would read as "no such grant" at every call site.
                errors.Add($"Principal type '{c.PrincipalType}' is not supported yet, so a grant for it could never " +
                           "be evaluated. Only 'Employee' may be granted.");

            if (string.IsNullOrWhiteSpace(c.Role)) errors.Add("A role is required.");
            else if (!string.IsNullOrWhiteSpace(c.Scope) && EntityRegistry.IsKnownScope(c.Scope)
                     && !IsKnownRole(c.Scope, c.Role))
                // An unknown role would be a row that matches nothing — a silent no-op grant that looks like
                // configuration and closes bootstrap-open without granting anything.
                errors.Add($"'{c.Role}' is not a role in scope '{c.Scope}'. Known roles: {KnownRoles(c.Scope)}.");

            if (c.ValidFrom.HasValue && c.ValidTo.HasValue && c.ValidTo < c.ValidFrom)
                errors.Add("ValidTo cannot precede ValidFrom.");

            if (c.ScopeBranchId is <= 0) errors.Add("ScopeBranchId must be a real branch when supplied.");

            return errors;
        }

        // ---------------------------------------------------------------------------------------------
        // Role vocabularies, per scope — now RESOLVED, not declared here.
        // ---------------------------------------------------------------------------------------------
        // This block used to hold a per-scope map pointing straight at each module's own role constants. The intent was
        // right — project each module's own published list rather than re-listing it — but naming the module
        // constants made KERNEL source depend on module source, and a kernel-only build could not resolve
        // any of those module role vocabularies at all.
        //
        // The projection still happens; it now happens in the other direction. Each module registers an
        // IPlatformPermissionVocabulary (see BL/ModulePermissions), and this writer asks the registry. The
        // module is still the single source of truth, an unknown role is still REJECTED rather than inferred,
        // and the answers for the four administrable scopes are unchanged.
        private bool IsKnownRole(string scope, string role) => _vocabulary.IsKnownRole(scope, role);

        private string KnownRoles(string scope) => _vocabulary.KnownRoles(scope);

        /// <summary>Scopes this writer can administer at all — the ones with a declared role vocabulary.</summary>
        public IReadOnlyCollection<string> AdministrableScopes => _vocabulary.AdministrableScopes;

        // =========================================================================================
        // helpers
        // =========================================================================================

        private IModuleAccessService? ModuleFor(string scope) =>
            _modules.FirstOrDefault(m => string.Equals(m.Scope, scope, StringComparison.Ordinal));

        /// <summary>
        /// The module's own manage-level action, as the module itself declares it. Still named rather than
        /// guessed from the action list: a heuristic ("the action containing 'manage'") would pick
        /// `attendance-manage` for HR, which is not the module's administrative right. The naming now lives
        /// with the module that owns the right, not in the kernel.
        /// </summary>
        private string? ManageActionFor(IModuleAccessService module) => _vocabulary.ManageActionFor(module.Scope);

        // Session-free: the user id comes from the resolved context, not from HttpContext. That is what lets the
        // same ceiling apply to a console, a migration and a test.
        private Task<IReadOnlyList<string>> IdentityRolesAsync(BusinessContext actor) =>
            _identity.RolesAsync(actor.UserId);

        /// <summary>A tracked, row-locked read inside the ambient transaction.</summary>
        private async Task<PlatformRoleAssignment?> LoadForUpdateAsync(
            int grantId, int companyId, CancellationToken cancellationToken)
        {
            // The company predicate is part of the lookup, so a foreign-company id is NotFound rather than
            // "found, then denied" — the difference is whether the caller learns the row exists.
            var rows = await _db.PlatformRoleAssignments
                .FromSqlRaw(
                    "SELECT * FROM dbo.PlatformRoleAssignments WITH (UPDLOCK, ROWLOCK) WHERE ID = {0} AND CompanyID = {1}",
                    grantId, companyId)
                .ToListAsync(cancellationToken);

            return rows.FirstOrDefault();
        }

        private static bool IsEffective(PlatformRoleAssignment r, DateTime now) =>
            r.IsActive && (r.ValidFrom == null || r.ValidFrom <= now) && (r.ValidTo == null || r.ValidTo >= now);

        private static PlatformGrantView ToView(PlatformRoleAssignment r) => new()
        {
            Id = r.ID,
            CompanyId = r.CompanyID,
            Scope = r.Scope,
            PrincipalType = r.PrincipalType,
            PrincipalId = r.PrincipalId,
            Role = r.Role,
            ScopeBranchId = r.ScopeBranchId,
            IsActive = r.IsActive,
            ValidFrom = r.ValidFrom,
            ValidTo = r.ValidTo,
            CreatedAt = r.CreatedAt,
            CreatedBy = r.CreatedBy,
            UpdatedAt = r.UpdatedAt,
            UpdatedBy = r.UpdatedBy,
            RevokedAt = r.RevokedAt,
            RevokedBy = r.RevokedBy,
            Reason = r.Reason,
            SourceSystem = r.SourceSystem,
            IsEffective = IsEffective(r, DateTime.UtcNow),
        };

        private static bool SamePayload(PlatformRoleAssignment prior, CreatePlatformGrantCommand c) =>
            prior.CompanyID == c.CompanyId
            && string.Equals(prior.Scope, c.Scope, StringComparison.Ordinal)
            && string.Equals(prior.PrincipalType, c.PrincipalType, StringComparison.Ordinal)
            && prior.PrincipalId == c.PrincipalId
            && string.Equals(prior.Role, c.Role, StringComparison.Ordinal)
            && prior.ScopeBranchId == c.ScopeBranchId
            && Nullable.Equals(prior.ValidFrom, c.ValidFrom)
            && Nullable.Equals(prior.ValidTo, c.ValidTo);

        private static object EventPayload(PlatformRoleAssignment r, PlatformGrantAuthority authority, string? reason) => new
        {
            grantId = r.ID,
            companyId = r.CompanyID,
            scope = r.Scope,
            role = r.Role,
            principalType = r.PrincipalType,
            principalId = r.PrincipalId,
            scopeBranchId = r.ScopeBranchId,
            isActive = r.IsActive,
            validFrom = r.ValidFrom,
            validTo = r.ValidTo,
            actorEmployeeId = r.IsActive ? r.CreatedBy : r.RevokedBy,
            authority = authority.ToString(),
            reason,
            sourceSystem = r.SourceSystem,
            migrationBatchId = r.MigrationBatchId,
            occurredAt = r.IsActive ? r.CreatedAt : r.RevokedAt,
        };

        private static string? Trim(string? value, int max) =>
            string.IsNullOrWhiteSpace(value) ? null
            : value.Length <= max ? value.Trim() : value.Trim().Substring(0, max);

        /// <summary>
        /// A LOST RACE, distinguished from any other database failure.
        ///
        ///   2601 / 2627 — duplicate key: another writer inserted the same effective grant or idempotency key.
        ///   1205        — deadlock victim: several writers contending on the same unique index. SQL Server picks
        ///                 a victim rather than failing the insert, and B0's first run produced exactly this as an
        ///                 unhandled exception, once. A concurrency test that fails once and passes twice is a
        ///                 FLAKE, and a flake is not evidence — so the classification was widened rather than the
        ///                 intermittent result being accepted.
        ///
        /// Anything else keeps throwing. Swallowing an unknown database error here would turn a real fault into a
        /// "conflict" the caller retries forever.
        /// </summary>
        private static bool IsConcurrencyLoss(DbUpdateException ex) =>
            ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
            && (sql.Number == 2601 || sql.Number == 2627 || sql.Number == 1205);

        // =========================================================================================
        // A10 — why Denied and Conflict are LOGGED and not evented
        //
        // A10 lists PlatformGrantAdministrationDenied as optional, "only where safe and useful". It is neither,
        // and the reason is structural rather than a matter of taste:
        //
        //   BusinessEventRecord requires a POSITIVE EntityId that names a real entity, and the kernel validates
        //   it. A denied attempt and a lost uniqueness race have NO row — nothing was created. Emitting an event
        //   would mean inventing an id, and an invented id does not sit in a vacuum: the timeline and
        //   notification projections resolve EntityId against the registry, so a fabricated id attaches a
        //   security event to whatever real grant happens to hold that number. That is worse than no event —
        //   it is a false audit line pointing at an innocent record.
        //
        // So both are recorded as STRUCTURED LOGS with every field an investigator needs. When a
        // company-level event entity exists (one that can carry an id of its own), they become events without
        // changing any caller. Recorded as a gap in the delivery report rather than solved by a sentinel.
        // =========================================================================================

        private Task<PlatformGrantResult> DenyAsync(
            BusinessContext actor, int companyId, string? scope, string? role, string adminAction, string reason,
            CancellationToken cancellationToken)
        {
            _log.LogWarning(
                "GRANT ADMINISTRATION DENIED: actor {Actor}, company {Company}, scope {Scope}, role {Role}, " +
                "action {Action} — {Reason}",
                actor?.EmployeeId, companyId, scope, role, adminAction, reason);

            return Task.FromResult(PlatformGrantResult.Denied(reason));
        }

        private Task RecordConflictAsync(
            BusinessContext actor, CreatePlatformGrantCommand command, CancellationToken cancellationToken)
        {
            _log.LogWarning(
                "GRANT CONFLICT DETECTED: actor {Actor}, company {Company}, scope {Scope}, role {Role}, " +
                "principal {Principal}, branch {Branch}, idempotencyKey {Key} — a concurrent writer won the " +
                "uniqueness race, so nothing was created here.",
                actor?.EmployeeId, command.CompanyId, command.Scope, command.Role, command.PrincipalId,
                command.ScopeBranchId, command.IdempotencyKey);

            return Task.CompletedTask;
        }
    }
}
