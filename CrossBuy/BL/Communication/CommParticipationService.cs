using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §4) — FOLLOWERS AND WATCHERS (modules 4, 5).
    //
    // THE FOLLOWER/WATCHER DISTINCTION IS LOAD-BEARING, not cosmetic:
    //
    //   Follower — asked to be told. Notified per activity.
    //   Watcher  — asked to keep an eye. NOT notified per activity; the thread appears in their watching list.
    //
    // It exists because auto-follow is otherwise unusable. Auto-following the record's owner sounds helpful
    // until a busy invoice generates forty notifications for somebody who never opted in, at which point the
    // whole feature gets switched off. So automatic participation lands at WATCHER, and only an explicit act
    // (pressing Follow, being mentioned, writing a comment) reaches FOLLOWER. See
    // CommParticipantRole.NotifiedPerActivity — one method decides it, in one place.
    //
    // ROLE ESCALATION IS ONE-WAY WITHIN AN OPERATION: EnsureAsync never LOWERS a role. An author who was
    // already an Owner does not become a Participant by commenting again. Lowering is an explicit act
    // (SetRoleAsync), because silently demoting somebody's notification level is indistinguishable from a bug.
    // =============================================================================================
    public interface ICommParticipationService
    {
        // Adds or upgrades a participation. Idempotent, and never lowers an existing role.
        // DOES NOT SAVE — it enrols in the caller's CommTransaction so participation, the comment that caused
        // it and the audit row share one fate.
        Task<CommParticipant> EnsureAsync(
            BusinessContext context, CommThread thread, int employeeId, string role, string source,
            CancellationToken cancellationToken = default);

        // Explicit follow/unfollow by the caller for themselves.
        Task<CommParticipantDto> FollowAsync(
            BusinessContext context, long threadId, string role, CancellationToken cancellationToken = default);

        Task<bool> UnfollowAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);

        Task<bool> SetMutedAsync(
            BusinessContext context, long threadId, bool muted, CancellationToken cancellationToken = default);

        // Moderator adds/removes somebody else.
        Task<CommParticipantDto> AddAsync(
            BusinessContext context, long threadId, int employeeId, string role, CancellationToken cancellationToken = default);

        Task<bool> RemoveAsync(
            BusinessContext context, long threadId, int employeeId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommParticipantDto>> ListAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);

        // The notification audience for a thread: live, unmuted participants whose role earns a per-activity
        // notification. Excludes the actor when the deployment says so.
        //
        // TAKES A BusinessContext, like every other method here, and that is not symmetry for its own sake.
        // Without it this method was a threadId-in / employee-ids-out primitive with NO company scope — the
        // Comm tables are not covered by the platform's global query filters (they are not pilot entities), so
        // nothing else would have constrained it. Its only caller passes an already-authorized thread, so there
        // was no live leak; but a public interface method that enumerates people across tenants for any id a
        // future caller supplies is a latent cross-company enumeration primitive, and the cost of closing it is
        // one parameter. Found by CommSecurityBoundaryTests.Service_entry_points_take_a_resolved_business_context.
        Task<IReadOnlyList<int>> ResolveNotifiableAsync(
            BusinessContext context, long threadId, int? exceptEmployeeId,
            CancellationToken cancellationToken = default);

        // "What am I following" — the personal read (module 4/5 surfaced to the user).
        Task<IReadOnlyList<CommParticipantDto>> ListMineAsync(
            BusinessContext context, CommPageRequest? page = null, CancellationToken cancellationToken = default);
    }

    public sealed class CommParticipationService : ICommParticipationService
    {
        private readonly CommDb _db;
        private readonly ICommThreadService _threads;
        private readonly ICommAccessPolicy _access;
        private readonly ICommEventPublisher _events;
        private readonly ICommActorDirectory _actors;
        private readonly CommunicationPlatformOptions _options;

        public CommParticipationService(
            CrossDbContext db,
            ICommThreadService threads,
            ICommAccessPolicy access,
            ICommEventPublisher events,
            ICommActorDirectory actors,
            Microsoft.Extensions.Options.IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _threads = threads;
            _access = access;
            _events = events;
            _actors = actors;
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommParticipant> EnsureAsync(
            BusinessContext context, CommThread thread, int employeeId, string role, string source,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(thread);

            if (!CommParticipantRole.IsValid(role))
                throw new CommValidationException(
                    CommValidationException.Codes.ParticipantRoleInvalid,
                    $"Participant role '{role}' is not one of {string.Join(" | ", CommParticipantRole.Values)}.");
            if (!CommParticipationSource.IsValid(source))
                throw new CommValidationException(
                    CommValidationException.Codes.ParticipantRoleInvalid,
                    $"Participation source '{source}' is not one of {string.Join(" | ", CommParticipationSource.Values)}.");
            if (employeeId <= 0)
                throw new CommValidationException(
                    CommValidationException.Codes.ActorRequired, "A participant needs a positive employee id.");

            var existing = await _db.Participants
                .Where(p => p.ThreadId == thread.Id && p.EmployeeId == employeeId && p.RemovedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null)
            {
                // Upgrade only. See the header: silently demoting a notification level is indistinguishable
                // from a bug, so lowering requires SetRoleAsync.
                if (RoleRank(role) > RoleRank(existing.Role))
                {
                    existing.Role = role;
                    existing.Source = source;
                    existing.updatedBy = context.EmployeeId;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                return existing;
            }

            var participant = new CommParticipant
            {
                CompanyID = thread.CompanyID,
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                EmployeeId = employeeId,
                Role = role,
                Source = source,
                CreatedBy = context.EmployeeId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Participants.Add(participant);

            // Counter maintained in the SAME transaction as the row it counts, so it is never a lagging cache.
            thread.ParticipantCount += 1;
            thread.UpdatedAt = DateTime.UtcNow;

            return participant;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommParticipantDto> FollowAsync(
            BusinessContext context, long threadId, string role, CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0)
                throw new CommValidationException(
                    CommValidationException.Codes.ActorRequired, "Following requires a resolved employee.");

            // Following is a self-service act, but only for a thread the caller may READ. Following something
            // you cannot read would create a notification subscription to content you can never open.
            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            // Owner is not self-assignable: it carries moderation weight in some deployments, and anybody could
            // otherwise promote themselves by pressing Follow.
            if (role != CommParticipantRole.Follower && role != CommParticipantRole.Watcher)
                throw new CommValidationException(
                    CommValidationException.Codes.ParticipantRoleInvalid,
                    "Self-service participation may be Follower or Watcher only.");

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            var participant = await EnsureAsync(
                context, thread, context.EmployeeId.Value, role, CommParticipationSource.Explicit, cancellationToken);

            await PublishParticipantEventAsync(context, thread, participant, CommEventTypes.ParticipantAdded, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(new[] { participant.EmployeeId }, cancellationToken);
            return ToDto(participant, actors);
        }

        public async Task<bool> UnfollowAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0) return false;
            return await RemoveCoreAsync(context, threadId, context.EmployeeId.Value, requireModerate: false, cancellationToken);
        }

        public async Task<bool> SetMutedAsync(
            BusinessContext context, long threadId, bool muted, CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0) return false;

            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            var participant = await _db.Participants
                .Where(p => p.ThreadId == threadId && p.EmployeeId == context.EmployeeId.Value && p.RemovedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            // Muting a thread you do not participate in is meaningful — it is how you pre-empt being pulled in
            // by a mention. So a Watcher row is created rather than the request being refused.
            if (participant == null)
            {
                await using var createTx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);
                participant = await EnsureAsync(
                    context, thread, context.EmployeeId.Value, CommParticipantRole.Watcher,
                    CommParticipationSource.Explicit, cancellationToken);
                participant.MutedAt = muted ? DateTime.UtcNow : null;
                await createTx.CommitAsync(cancellationToken);
                return true;
            }

            if ((participant.MutedAt != null) == muted) return true;   // idempotent

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);
            participant.MutedAt = muted ? DateTime.UtcNow : null;
            participant.updatedBy = context.EmployeeId;
            participant.UpdatedAt = DateTime.UtcNow;
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommParticipantDto> AddAsync(
            BusinessContext context, long threadId, int employeeId, string role, CancellationToken cancellationToken = default)
        {
            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);

            // Adding SOMEBODY ELSE is a moderator act: it subscribes another person to content and, for a
            // restricted thread, is effectively a grant of attention. Self-service goes through FollowAsync.
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Moderate, cancellationToken);

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            var participant = await EnsureAsync(
                context, thread, employeeId, role, CommParticipationSource.Explicit, cancellationToken);

            await PublishParticipantEventAsync(context, thread, participant, CommEventTypes.ParticipantAdded, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(new[] { employeeId }, cancellationToken);
            return ToDto(participant, actors);
        }

        public Task<bool> RemoveAsync(
            BusinessContext context, long threadId, int employeeId, CancellationToken cancellationToken = default)
            => RemoveCoreAsync(context, threadId, employeeId, requireModerate: employeeId != (context.EmployeeId ?? 0), cancellationToken);

        private async Task<bool> RemoveCoreAsync(
            BusinessContext context, long threadId, int employeeId, bool requireModerate, CancellationToken cancellationToken)
        {
            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(
                context, thread,
                requireModerate ? CommPermissionLevel.Moderate : CommPermissionLevel.Read,
                cancellationToken);

            var participant = await _db.Participants
                .Where(p => p.ThreadId == threadId && p.EmployeeId == employeeId && p.RemovedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (participant == null) return false;

            await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);

            // Marked removed, not deleted: "who was following this thread when the decision was taken" is a
            // fact an audit needs, and the row leaves the filtered unique index so a re-follow works.
            participant.RemovedAt = DateTime.UtcNow;
            participant.RemovedBy = context.EmployeeId;
            participant.updatedBy = context.EmployeeId;
            participant.UpdatedAt = DateTime.UtcNow;

            thread.ParticipantCount = Math.Max(0, thread.ParticipantCount - 1);
            thread.UpdatedAt = DateTime.UtcNow;

            await PublishParticipantEventAsync(context, thread, participant, CommEventTypes.ParticipantRemoved, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<CommParticipantDto>> ListAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);
            await _access.RequireThreadAsync(context, thread, CommPermissionLevel.Read, cancellationToken);

            var rows = await _db.Participants.AsNoTracking()
                .Where(p => p.ThreadId == threadId && p.CompanyID == context.CompanyId && p.RemovedAt == null)
                .OrderBy(p => p.Id)
                .Take(_options.MaxPageSize)
                .ToListAsync(cancellationToken);

            var actors = await _actors.ResolveAsync(rows.Select(r => r.EmployeeId), cancellationToken);
            return rows.Select(r => ToDto(r, actors)).ToList();
        }

        public async Task<IReadOnlyList<int>> ResolveNotifiableAsync(
            BusinessContext context, long threadId, int? exceptEmployeeId,
            CancellationToken cancellationToken = default)
        {
            // Fail closed on an unresolved company rather than reading every tenant's participants. The platform
            // rule this inherits: "an unresolved company scope reads no company-scoped data and writes none."
            if (context is null || context.CompanyId <= 0) return Array.Empty<int>();

            var rows = await _db.Participants.AsNoTracking()
                .Where(p => p.CompanyID == context.CompanyId
                            && p.ThreadId == threadId && p.RemovedAt == null && p.MutedAt == null)
                .Select(p => new { p.EmployeeId, p.Role })
                .ToListAsync(cancellationToken);

            // The role filter is applied IN MEMORY through CommParticipantRole.NotifiedPerActivity rather than
            // as a SQL IN-list, so the follower/watcher rule lives in exactly one place and a future role
            // cannot be added to the vocabulary without this query learning about it.
            var ids = rows
                .Where(r => CommParticipantRole.NotifiedPerActivity(r.Role))
                .Select(r => r.EmployeeId)
                .Distinct()
                .ToList();

            if (_options.ExcludeActorFromOwnNotifications && exceptEmployeeId is > 0)
                ids.RemoveAll(id => id == exceptEmployeeId.Value);

            return ids;
        }

        public async Task<IReadOnlyList<CommParticipantDto>> ListMineAsync(
            BusinessContext context, CommPageRequest? page = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return Array.Empty<CommParticipantDto>();

            int take = _options.ClampPageSize(page?.PageSize);
            long after = page?.AfterId ?? 0;

            var rows = await _db.Participants.AsNoTracking()
                .Where(p => p.CompanyID == context.CompanyId
                            && p.EmployeeId == context.EmployeeId.Value
                            && p.RemovedAt == null
                            && (after == 0 || p.Id < after))
                .OrderByDescending(p => p.Id)
                .Take(take)
                .ToListAsync(cancellationToken);

            // NOT re-authorized per row, and that is stated rather than hidden: this is the caller's OWN
            // participation list, and a row here is a record of something they were already given access to.
            // Navigating to the thread re-checks access at that point, which is where a since-revoked
            // permission takes effect.
            var actors = await _actors.ResolveAsync(new[] { context.EmployeeId.Value }, cancellationToken);
            return rows.Select(r => ToDto(r, actors)).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        private Task PublishParticipantEventAsync(
            BusinessContext context, CommThread thread, CommParticipant participant, string eventType,
            CancellationToken cancellationToken)
            => _events.PublishAsync(new CommEvent
            {
                EventType = eventType,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                ThreadId = thread.Id,
                CompanyId = thread.CompanyID,
                BranchId = thread.BranchID,
                ActorEmployeeId = context.EmployeeId,
                Visibility = thread.Visibility,
                Payload = new CommParticipantEventPayload
                {
                    ThreadId = thread.Id,
                    EmployeeId = participant.EmployeeId,
                    Role = participant.Role,
                    Source = participant.Source,
                },

                // Ticks are in the key because add/remove/add is a legitimate sequence and each occurrence is
                // its own fact. Without them the second add would deduplicate against the first and vanish.
                DedupKey = $"participant:{thread.Id}:{participant.EmployeeId}:{eventType}:{DateTime.UtcNow.Ticks}",
                CorrelationId = context.CorrelationId,
            }, cancellationToken);

        private CommParticipantDto ToDto(CommParticipant row, IReadOnlyDictionary<int, CommActorDto> actors)
            => new()
            {
                ParticipantId = row.Id,
                ThreadId = row.ThreadId,
                Employee = _actors.Get(actors, row.EmployeeId),
                Role = row.Role,
                Source = row.Source,
                IsMuted = row.MutedAt != null,
                CreatedAt = row.CreatedAt,
            };

        // Owner > Follower > Participant > Watcher.
        //
        // FOLLOWER OUTRANKS PARTICIPANT DELIBERATELY: rank here orders NOTIFICATION APPETITE, not authority.
        // Someone who explicitly pressed Follow has asked for more than someone who merely happens to have
        // commented once, so an Ensure(Participant) call must not quietly undo their Follow.
        private static int RoleRank(string role) => role switch
        {
            CommParticipantRole.Owner => 4,
            CommParticipantRole.Follower => 3,
            CommParticipantRole.Participant => 2,
            CommParticipantRole.Watcher => 1,
            _ => 0,
        };
    }
}
