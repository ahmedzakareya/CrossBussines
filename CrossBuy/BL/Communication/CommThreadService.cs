using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §4) — THREADS (modules 1, 2, 34, 35).
    //
    // A thread is the unit a conversation, its participants, its permissions and its read state all hang from.
    // Comments never attach directly to a business record — always to a thread on that record — because
    // participation, locking, a visibility ceiling and per-thread read state have nowhere to live otherwise.
    // =============================================================================================
    public interface ICommThreadService
    {
        // Returns the existing thread or creates it. The COMMON path: a screen showing a record's discussion
        // does not care whether one exists yet.
        Task<CommThread> GetOrCreateAsync(
            BusinessContext context, CommThreadRequest request, CancellationToken cancellationToken = default);

        // Loads a thread by id, company-checked. Throws CommNotFoundException for absent, other-company and
        // soft-deleted alike — see CommNotFoundException for why the three must not be distinguishable.
        Task<CommThread> RequireAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);

        // Every thread on a record the caller may read, newest activity first.
        Task<IReadOnlyList<CommThreadDto>> ListForEntityAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default);

        Task<CommThreadDto> GetDtoAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);

        // Locking is a MODERATOR act. A locked thread still reads — locking ends a conversation, it does not
        // hide it.
        Task<CommThreadDto> SetLockedAsync(
            BusinessContext context, long threadId, bool locked, string? reason, CancellationToken cancellationToken = default);

        // Thread-level grants (modules 23, 24).
        Task<CommThreadPermissionDto> GrantAsync(
            BusinessContext context, long threadId, CommPrincipalRef principal, string level, CancellationToken cancellationToken = default);

        Task<bool> RevokeAsync(
            BusinessContext context, long permissionId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommThreadPermissionDto>> ListPermissionsAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);
    }

    public sealed class CommThreadService : ICommThreadService
    {
        private readonly CommDb _db;
        private readonly ICommEntitySurface _surface;
        private readonly ICommAccessPolicy _access;
        private readonly ICommEventPublisher _events;
        private readonly ICommAuditWriter _audit;
        private readonly ICommActorDirectory _actors;
        private readonly ICommPrincipalResolver _principals;
        private readonly CommunicationPlatformOptions _options;

        public CommThreadService(
            CrossDbContext db,
            ICommEntitySurface surface,
            ICommAccessPolicy access,
            ICommEventPublisher events,
            ICommAuditWriter audit,
            ICommActorDirectory actors,
            ICommPrincipalResolver principals,
            IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _surface = surface;
            _access = access;
            _events = events;
            _audit = audit;
            _actors = actors;
            _principals = principals;
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommThread> GetOrCreateAsync(
            BusinessContext context, CommThreadRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            var definition = await _surface.RequireAsync(request.Entity, CommCapabilities.Comments);

            if (!CommThreadKind.IsValid(request.Kind))
                throw new CommValidationException(
                    CommValidationException.Codes.ThreadKindInvalid,
                    $"Thread kind '{request.Kind}' is not one of {string.Join(" | ", CommThreadKind.Values)}.");

            if (!CommVisibility.IsValid(request.Visibility))
                throw new CommValidationException(
                    CommValidationException.Codes.VisibilityInvalid,
                    $"Visibility '{request.Visibility}' is not one of {string.Join(" | ", CommVisibility.Values)}.");

            var entityDecision = await _access.CanAccessEntityAsync(
                context, request.Entity, CommCapabilities.Comments, cancellationToken);
            if (!entityDecision.Allowed)
            {
                // A denial is AUDITED. A read that returns nothing and a read that was refused look identical
                // in a log otherwise, and "why can this user not see the thread" is a real support question.
                _audit.Append(new CommAuditRequest
                {
                    Action = CommAuditActions.AccessDenied,
                    Entity = request.Entity,
                    CompanyId = context.CompanyId,
                    BranchId = context.BranchId,
                    ActorEmployeeId = context.EmployeeId,
                    Detail = new { operation = "GetOrCreateThread", reason = entityDecision.Reason },
                    CorrelationId = context.CorrelationId,
                });
                await _db.SaveAsync(cancellationToken);

                throw new CommAccessDeniedException(
                    CommPermissionLevel.Read, request.Entity, entityDecision.Reason);
            }

            var threadKey = (request.ThreadKey ?? "").Trim();

            var existing = await FindAsync(context.CompanyId, request.Entity, request.Kind, threadKey, cancellationToken);
            if (existing != null) return existing;

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            var thread = new CommThread
            {
                CompanyID = context.CompanyId,
                BranchID = context.BranchId,
                EntityType = definition.Code,
                EntityId = request.Entity.EntityId,
                Kind = request.Kind,
                ThreadKey = threadKey,
                SubjectAr = Trim(request.SubjectAr, Models.Context.Communication.CommunicationModel.SubjectLength),
                SubjectEn = Trim(request.SubjectEn, Models.Context.Communication.CommunicationModel.SubjectLength),
                Visibility = request.Visibility,
                IsLocked = false,
                CommentCount = 0,
                ParticipantCount = 0,
                LastActivityAt = DateTime.UtcNow,
                CreatedBy = context.EmployeeId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Threads.Add(thread);

            // Saved BEFORE the event so thread.Id is assigned — the event payload and the audit row both carry
            // it, and an audit row pointing at thread 0 is useless.
            await _db.SaveAsync(cancellationToken);

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.ThreadCreated,
                Entity = request.Entity,
                ThreadId = thread.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = thread.Visibility,
                Payload = new CommThreadEventPayload
                {
                    ThreadId = thread.Id, Kind = thread.Kind, ThreadKey = thread.ThreadKey,
                    Visibility = thread.Visibility,
                },
                DedupKey = $"thread:{thread.Id}:created",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            try
            {
                await tx.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Lost the race on UX_CommThreads_Anchor: another request created the same thread first. That is
                // exactly what the unique index is for — the alternative was two threads on one record and a
                // conversation permanently split in half. Return theirs.
                var winner = await FindAsync(context.CompanyId, request.Entity, request.Kind, threadKey, cancellationToken);
                if (winner != null) return winner;
                throw;
            }

            return thread;
        }

        private Task<CommThread?> FindAsync(
            int companyId, CommEntityRef entity, string kind, string threadKey, CancellationToken cancellationToken)
            => _db.Threads
                .Where(t => t.CompanyID == companyId
                            && t.EntityType == entity.EntityCode
                            && t.EntityId == entity.EntityId
                            && t.Kind == kind
                            && t.ThreadKey == threadKey
                            && t.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

        // ---------------------------------------------------------------------------------------------
        public async Task<CommThread> RequireAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var thread = await _db.Threads
                .Where(t => t.Id == threadId && t.CompanyID == context.CompanyId && t.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            // Absent, other-company and deleted all land here. Identical answers, deliberately.
            if (thread == null) throw new CommNotFoundException("Thread", threadId);
            return thread;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<CommThreadDto>> ListForEntityAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var decision = await _access.CanAccessEntityAsync(context, entity, CommCapabilities.Comments, cancellationToken);
            if (!decision.Allowed) return Array.Empty<CommThreadDto>();

            var threads = await _db.Threads.AsNoTracking()
                .Where(t => t.CompanyID == context.CompanyId
                            && t.EntityType == entity.EntityCode
                            && t.EntityId == entity.EntityId
                            && t.DeletedAt == null)
                .OrderByDescending(t => t.LastActivityAt)
                .ThenByDescending(t => t.Id)
                .Take(_options.MaxPageSize)
                .ToListAsync(cancellationToken);

            var results = new List<CommThreadDto>(threads.Count);
            var actors = await _actors.ResolveAsync(threads.Select(t => t.CreatedBy), cancellationToken);

            foreach (var thread in threads)
            {
                var access = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);
                if (!access.CanRead) continue;   // filtered, not hidden — the row never becomes a DTO
                results.Add(ToDto(thread, access, actors));
            }
            return results;
        }

        public async Task<CommThreadDto> GetDtoAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            var thread = await RequireAsync(context, threadId, cancellationToken);
            var access = await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);
            var actors = await _actors.ResolveAsync(new[] { thread.CreatedBy }, cancellationToken);
            return ToDto(thread, access, actors);
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommThreadDto> SetLockedAsync(
            BusinessContext context, long threadId, bool locked, string? reason, CancellationToken cancellationToken = default)
        {
            var thread = await RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Moderate, cancellationToken);

            // Idempotent: locking a locked thread is not an error, and returning the current state avoids a
            // second audit row for a no-op.
            if (thread.IsLocked == locked)
            {
                var currentAccess = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);
                var currentActors = await _actors.ResolveAsync(new[] { thread.CreatedBy }, cancellationToken);
                return ToDto(thread, currentAccess, currentActors);
            }

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            thread.IsLocked = locked;
            thread.LockedReason = locked ? Trim(reason, Models.Context.Communication.CommunicationModel.SubjectLength) : null;
            thread.LockedBy = locked ? context.EmployeeId : null;
            thread.LockedAt = locked ? DateTime.UtcNow : null;
            thread.updatedBy = context.EmployeeId;
            thread.UpdatedAt = DateTime.UtcNow;

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            await _events.PublishAsync(new CommEvent
            {
                EventType = locked ? CommEventTypes.ThreadLocked : CommEventTypes.ThreadUnlocked,
                Entity = entity,
                ThreadId = thread.Id,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = thread.Visibility,
                Payload = new CommThreadEventPayload
                {
                    ThreadId = thread.Id, Kind = thread.Kind, ThreadKey = thread.ThreadKey,
                    Visibility = thread.Visibility, Reason = thread.LockedReason,
                },

                // The lock COUNT is in the key, so locking, unlocking and locking again each record their own
                // event instead of the second lock deduplicating against the first.
                DedupKey = $"thread:{thread.Id}:lock:{(locked ? "on" : "off")}:{DateTime.UtcNow.Ticks}",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await tx.CommitAsync(cancellationToken);

            var access = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);
            var actors = await _actors.ResolveAsync(new[] { thread.CreatedBy }, cancellationToken);
            return ToDto(thread, access, actors);
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommThreadPermissionDto> GrantAsync(
            BusinessContext context, long threadId, CommPrincipalRef principal, string level, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principal);

            var thread = await RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Moderate, cancellationToken);

            if (!CommPrincipalKind.IsValid(principal.Kind))
                throw new CommValidationException(
                    CommValidationException.Codes.PrincipalKindInvalid,
                    $"Principal kind '{principal.Kind}' is not one of {string.Join(" | ", CommPrincipalKind.Values)}.");

            if (!CommPermissionLevel.IsValid(level))
                throw new CommValidationException(
                    CommValidationException.Codes.PermissionLevelInvalid,
                    $"Permission level '{level}' is not one of {string.Join(" | ", CommPermissionLevel.Values)}.");

            // A grant to a principal nothing can expand is a grant to nobody. Refused rather than stored,
            // because a stored grant that never matches looks — to whoever granted it — exactly like a working
            // one. This is where an unwired @role principal is caught.
            var expansion = await _principals.ExpandAsync(
                principal.Kind, principal.Id, principal.Key, context.CompanyId, cancellationToken);
            if (!expansion.Supported)
                throw new CommValidationException(
                    CommValidationException.Codes.PrincipalKindInvalid,
                    $"Principal '{principal.Kind}' cannot be resolved: {expansion.Reason} " +
                    "A grant that resolves to nobody would look identical to a working one.");

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // An existing live grant is RAISED or LOWERED in place rather than duplicated, so
            // ResolveGrantLevelAsync never has to reconcile two rows for one principal.
            var row = await _db.ThreadPermissions
                .Where(p => p.ThreadId == threadId
                            && p.CompanyID == context.CompanyId
                            && p.PrincipalKind == principal.Kind
                            && p.PrincipalId == principal.Id
                            && p.PrincipalKey == principal.Key
                            && p.RevokedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null)
            {
                row = new CommThreadPermission
                {
                    CompanyID = context.CompanyId,
                    ThreadId = threadId,
                    PrincipalKind = principal.Kind,
                    PrincipalId = principal.Id,
                    PrincipalKey = Trim(principal.Key, Models.Context.Communication.CommunicationModel.PrincipalKeyLength),
                    Level = level,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.ThreadPermissions.Add(row);
            }
            else
            {
                row.Level = level;
                row.updatedBy = context.EmployeeId;
                row.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveAsync(cancellationToken);

            var entity = new CommEntityRef(thread.EntityType, thread.EntityId);
            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.PermissionGranted,
                Entity = entity,
                ThreadId = threadId,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = thread.Visibility,
                Payload = new
                {
                    threadId,
                    principalKind = principal.Kind,
                    principalId = principal.Id,
                    principalKey = principal.Key,
                    level,
                    resolvedCount = expansion.EmployeeIds.Count,
                },
                DedupKey = $"perm:{row.Id}:{level}",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await tx.CommitAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(new[] { row.CreatedBy }, cancellationToken);
            return ToDto(row, principal, expansion, actors);
        }

        public async Task<bool> RevokeAsync(
            BusinessContext context, long permissionId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var row = await _db.ThreadPermissions
                .Where(p => p.Id == permissionId && p.CompanyID == context.CompanyId && p.RevokedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (row == null) return false;

            var thread = await RequireAsync(context, row.ThreadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Moderate, cancellationToken);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // Revoked, not deleted. A grant that existed is a fact about who could read what, and an
            // investigation needs it — the row leaves the filtered index and stops affecting decisions.
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedBy = context.EmployeeId;
            row.updatedBy = context.EmployeeId;
            row.UpdatedAt = DateTime.UtcNow;

            await _events.PublishAsync(new CommEvent
            {
                EventType = CommEventTypes.PermissionRevoked,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = row.ThreadId,
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ActorEmployeeId = context.EmployeeId,
                Visibility = thread.Visibility,
                Payload = new
                {
                    threadId = row.ThreadId,
                    principalKind = row.PrincipalKind,
                    principalId = row.PrincipalId,
                    level = row.Level,
                },
                DedupKey = $"perm:{row.Id}:revoked",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        }

        public async Task<IReadOnlyList<CommThreadPermissionDto>> ListPermissionsAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            var thread = await RequireAsync(context, threadId, cancellationToken);

            // Who can read a thread is itself sensitive: the participant list of a restricted HR note is a fact
            // about an investigation. Moderate, not Read.
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Moderate, cancellationToken);

            var rows = await _db.ThreadPermissions.AsNoTracking()
                .Where(p => p.ThreadId == threadId && p.CompanyID == context.CompanyId && p.RevokedAt == null)
                .OrderBy(p => p.Id)
                .ToListAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(rows.Select(r => r.CreatedBy), cancellationToken);

            var results = new List<CommThreadPermissionDto>(rows.Count);
            foreach (var row in rows)
            {
                var (labelAr, labelEn) = await _principals.LabelAsync(
                    row.PrincipalKind, row.PrincipalId, row.PrincipalKey, context.CompanyId, cancellationToken);

                results.Add(new CommThreadPermissionDto
                {
                    PermissionId = row.Id,
                    ThreadId = row.ThreadId,
                    Principal = new CommPrincipalRef
                    {
                        Kind = row.PrincipalKind, Id = row.PrincipalId, Key = row.PrincipalKey,
                        Label = labelEn ?? labelAr,
                    },
                    Level = row.Level,
                    GrantedBy = _actors.Get(actors, row.CreatedBy),
                    GrantedAt = row.CreatedAt,
                });
            }
            return results;
        }

        // ---------------------------------------------------------------------------------------------
        private CommThreadDto ToDto(CommThread thread, CommThreadAccess access, IReadOnlyDictionary<int, CommActorDto> actors)
            => new()
            {
                ThreadId = thread.Id,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                Kind = thread.Kind,
                ThreadKey = thread.ThreadKey,
                SubjectAr = thread.SubjectAr,
                SubjectEn = thread.SubjectEn,
                Visibility = thread.Visibility,
                IsLocked = thread.IsLocked,
                LockedReason = thread.LockedReason,
                CommentCount = thread.CommentCount,
                ParticipantCount = thread.ParticipantCount,
                LastActivityAt = thread.LastActivityAt,
                CreatedAt = thread.CreatedAt,
                CreatedBy = thread.CreatedBy.HasValue ? _actors.Get(actors, thread.CreatedBy) : null,
                Capabilities = access.ToCapabilities(),
            };

        private CommThreadPermissionDto ToDto(
            CommThreadPermission row, CommPrincipalRef principal, CommPrincipalExpansion expansion,
            IReadOnlyDictionary<int, CommActorDto> actors)
            => new()
            {
                PermissionId = row.Id,
                ThreadId = row.ThreadId,
                Principal = new CommPrincipalRef
                {
                    Kind = principal.Kind, Id = principal.Id, Key = principal.Key,
                    Label = expansion.LabelEn ?? expansion.LabelAr ?? principal.Label,
                },
                Level = row.Level,
                GrantedBy = _actors.Get(actors, row.CreatedBy),
                GrantedAt = row.CreatedAt,
            };

        // Truncation happens HERE, not at the database. A column-length overflow is a 500 with an
        // unintelligible provider message; a trimmed subject is a subject.
        private static string? Trim(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max);
        }
    }
}
