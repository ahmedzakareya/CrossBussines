using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-034) — THE DECIDE STEP (modules 13, 19) and the in-app inbox.
    //
    // ORDER OF OPERATIONS, and every step is a gate that can only DROP recipients:
    //
    //   1. template            -> unknown key is refused at decide time, not at delivery time
    //   2. dedupe recipients   -> a person mentioned directly AND via their department is ONE notification
    //   3. drop the actor      -> nobody is told about their own action
    //   4. AUTHORIZE each one  -> a notification is a read of the content by another name
    //   5. preferences         -> per (category, channel), narrowing only
    //   6. idempotency         -> unique (company, dedupKey); a retried transaction adds nothing
    //   7. write               -> one CommNotification + one CommNotificationDelivery per channel
    //
    // STEP 4 IS THE ONE THAT MATTERS. ADR-006's consumer authorizes recipients for exactly this reason, and the
    // rule here is stricter than "can they open the record": a recipient must be able to read the SUBJECT'S
    // VISIBILITY TIER. Mentioning somebody in a Confidential note therefore notifies nobody who cannot read
    // Confidential — the mention row still exists, so the audit records that it was attempted. Skipping this
    // check would make an @-mention a way to leak a note's existence, its author, and an excerpt of its text to
    // somebody the visibility tier was designed to exclude.
    //
    // THIS SERVICE NEVER DELIVERS. It writes Pending delivery rows and returns. Delivery is
    // ICommNotificationDispatcher, called after the transaction commits — because sending an email inside a
    // financial transaction means either holding a database transaction open across an SMTP round trip, or
    // sending mail for a comment that then rolls back. Same reasoning that keeps side effects out of the
    // kernel's RecordAsync.
    // =============================================================================================
    public interface ICommNotificationService
    {
        // ENROLS in the caller's transaction and DOES NOT SAVE — the notification rows must share the comment's
        // fate. The caller's CommTransaction.CommitAsync persists them.
        Task<CommNotificationResult> QueueAsync(
            BusinessContext context, CommNotificationRequest request, CancellationToken cancellationToken = default);

        // The platform's OWN in-app inbox (module 19). See CommInboxItemDto for why it does not write into the
        // product's legacy Notifications table.
        Task<CommPage<CommInboxItemDto>> GetInboxAsync(
            BusinessContext context, bool unreadOnly = false, CommPageRequest? page = null,
            CancellationToken cancellationToken = default);

        Task<int> GetUnreadCountAsync(BusinessContext context, CancellationToken cancellationToken = default);

        Task<int> MarkReadAsync(
            BusinessContext context, IReadOnlyList<long>? notificationIds = null, CancellationToken cancellationToken = default);
    }

    public sealed class CommNotificationService : ICommNotificationService
    {
        private readonly CommDb _db;
        private readonly ICommTemplateCatalog _catalog;
        private readonly ICommTemplateRenderer _renderer;
        private readonly ICommPreferenceResolver _preferences;
        private readonly ICommAccessPolicy _access;
        private readonly ICommActorDirectory _actors;
        private readonly IEntityRegistry _registry;
        private readonly CommunicationPlatformOptions _options;

        public CommNotificationService(
            CrossDbContext db,
            ICommTemplateCatalog catalog,
            ICommTemplateRenderer renderer,
            ICommPreferenceResolver preferences,
            ICommAccessPolicy access,
            ICommActorDirectory actors,
            IEntityRegistry registry,
            IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _catalog = catalog;
            _renderer = renderer;
            _preferences = preferences;
            _access = access;
            _actors = actors;
            _registry = registry;
            _options = options.Value;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommNotificationResult> QueueAsync(
            BusinessContext context, CommNotificationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            var template = _catalog.Get(request.TemplateKey);   // unknown key throws here, at decide time

            var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
            var notificationIds = new List<long>();
            int deliveryRows = 0;

            // ---- 2 + 3: dedupe, then drop the actor.
            var recipients = (request.RecipientEmployeeIds ?? Array.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            int requested = recipients.Count;

            if (_options.ExcludeActorFromOwnNotifications && request.ActorEmployeeId is > 0)
            {
                int removed = recipients.RemoveAll(id => id == request.ActorEmployeeId.Value);
                if (removed > 0) Bump(skipped, CommNotificationResult.SkipReasons.ActorSelf, removed);
            }

            if (recipients.Count == 0)
                return new CommNotificationResult
                {
                    RequestedRecipientCount = requested,
                    NotifiedRecipientCount = 0,
                    DeliveryRowCount = 0,
                    NotificationIds = Array.Empty<long>(),
                    SkippedByReason = skipped,
                };

            // The deep link, resolved ONCE for the whole batch through the registry — the same BuildUrl the
            // kernel's notification projection uses, so a comm notification and an event notification land on
            // the same screen.
            string? url = _registry.IsValid(request.Entity.EntityCode)
                ? _registry.BuildUrl(request.Entity.EntityCode, request.Entity.EntityId)
                : null;

            var rendered = _renderer.Render(template, request.Tokens, url);

            // ---- 6 (part 1): which of these recipients already have a notification for this dedup root?
            // ONE query for the batch. Without it a mention to a 50-person department would issue 50 existence
            // checks.
            var dedupKeys = recipients.ToDictionary(id => id, id => DedupKeyFor(request.DedupKeyPrefix, id));
            var existing = await _db.Notifications.AsNoTracking()
                .Where(n => n.CompanyID == request.CompanyId && dedupKeys.Values.Contains(n.DedupKey))
                .Select(n => n.DedupKey)
                .ToListAsync(cancellationToken);
            var already = new HashSet<string>(existing, StringComparer.Ordinal);

            // ---- 4: authorization, per recipient.
            //
            // The visibility tier is resolved PER RECIPIENT because it depends on their module rights on this
            // entity. That is one permission evaluation per person — the honest cost of not leaking a
            // confidential note, and bounded by MaxGroupMentionRecipients.
            foreach (var employeeId in recipients)
            {
                if (already.Contains(dedupKeys[employeeId]))
                {
                    Bump(skipped, CommNotificationResult.SkipReasons.AlreadyNotified, 1);
                    continue;
                }

                if (!await CanReceiveAsync(context, request, employeeId, cancellationToken))
                {
                    Bump(skipped, CommNotificationResult.SkipReasons.NotVisible, 1);
                    continue;
                }

                // ---- 5: preferences.
                var plan = await _preferences.ResolveAsync(
                    request.CompanyId, employeeId, template, request.Channels, cancellationToken);

                if (!plan.HasAnyChannel)
                {
                    // Distinguish "they switched it off" from "no channel is available at all" — the two lead an
                    // operator to different places.
                    bool preferenceOff = plan.ExcludedChannels.Values
                        .Any(v => v == CommNotificationResult.SkipReasons.PreferenceOff);
                    Bump(skipped, preferenceOff
                        ? CommNotificationResult.SkipReasons.PreferenceOff
                        : CommNotificationResult.SkipReasons.NoChannel, 1);
                    continue;
                }

                // ---- 7: write.
                var notification = new CommNotification
                {
                    CompanyID = request.CompanyId,
                    BranchID = request.BranchId,
                    TemplateKey = template.Key,
                    Category = template.Category,
                    Priority = rendered.Priority,
                    LegacyType = rendered.LegacyNotificationType,
                    EntityType = request.Entity.EntityCode,
                    EntityId = request.Entity.EntityId,
                    ThreadId = request.ThreadId,
                    CommentId = request.CommentId,
                    MentionId = request.MentionId,
                    RecipientEmployeeId = employeeId,
                    ActorEmployeeId = request.ActorEmployeeId,

                    // Rendered text is STORED, both languages. Re-rendering on read would show today's template
                    // wording for a notification sent last year, and would need the token values kept alive
                    // forever to do it.
                    TitleAr = Cap(rendered.TitleAr, Models.Context.Communication.CommunicationModel.TitleLength),
                    TitleEn = Cap(rendered.TitleEn, Models.Context.Communication.CommunicationModel.TitleLength),
                    BodyAr = rendered.BodyAr,
                    BodyEn = rendered.BodyEn,
                    Url = rendered.Url,
                    DedupKey = dedupKeys[employeeId],
                    CreatedBy = request.ActorEmployeeId,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.Notifications.Add(notification);

                // Saved per recipient so notification.Id is assigned for its delivery rows' dedup keys. Inside
                // the caller's transaction, so this is a round trip and not a commit.
                await _db.SaveAsync(cancellationToken);

                foreach (var channel in plan.Channels)
                {
                    _db.Deliveries.Add(new CommNotificationDelivery
                    {
                        CompanyID = request.CompanyId,
                        NotificationId = notification.Id,
                        Channel = channel,
                        Status = CommDeliveryStatus.Pending,
                        Attempts = 0,
                        DedupKey = notification.DedupKey + "|" + channel,
                        CreatedBy = request.ActorEmployeeId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                    deliveryRows++;
                }

                notificationIds.Add(notification.Id);
            }

            return new CommNotificationResult
            {
                RequestedRecipientCount = requested,
                NotifiedRecipientCount = notificationIds.Count,
                DeliveryRowCount = deliveryRows,
                NotificationIds = notificationIds,
                SkippedByReason = skipped,
            };
        }

        // THE AUTHORIZATION GATE. Evaluated as the RECIPIENT, not as the actor — the question is what THEY may
        // read, and a context borrowed from the actor would answer the wrong question entirely.
        //
        // The synthesised context carries the recipient's employee id and NO ROLES, which makes this check
        // deliberately conservative: role-derived elevation (ViewConfidential via a module role) is not
        // reconstructed here, so a recipient who genuinely holds Confidential rights on the entity may still be
        // skipped for a Confidential subject. That direction of error withholds a notification rather than
        // leaking one. Reconstructing full rights would mean resolving another employee's session-free
        // BusinessContext, which IBusinessContextFactory can do only for the current request's identity.
        // Recorded as an open item in CPS-001 §9 rather than papered over.
        private async Task<bool> CanReceiveAsync(
            BusinessContext context, CommNotificationRequest request, int employeeId, CancellationToken cancellationToken)
        {
            // Public and Internal need no elevation beyond opening the record, which participation and mention
            // resolution have already established through company + active-employee checks.
            if (request.SubjectVisibility is CommVisibility.Public or CommVisibility.Internal)
                return true;

            var recipientContext = new BusinessContext
            {
                CompanyId = request.CompanyId,
                BranchId = request.BranchId,
                EmployeeId = employeeId,

                // UserId is deliberately LEFT AT ITS DEFAULT ("") rather than set: there is no identity provider
                // subject for a recipient who is not the current request's user, and inventing one would make
                // BusinessContext.IsAuthenticated true for the wrong reason. EmployeeId alone already makes it
                // true, which is the honest basis here.
                Roles = Array.Empty<string>(),
                CorrelationId = context.CorrelationId,
            };

            var visibilities = await _access.ResolveVisibilitiesAsync(recipientContext, request.Entity, cancellationToken);
            if (!visibilities.Allowed.Contains(request.SubjectVisibility)) return false;

            // Restricted needs the manager grant. The own-author exception cannot apply: the author is the actor,
            // and the actor is not a recipient of their own action.
            if (request.SubjectVisibility == CommVisibility.Restricted && !visibilities.MayReadAnyRestricted)
                return false;

            return true;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<CommPage<CommInboxItemDto>> GetInboxAsync(
            BusinessContext context, bool unreadOnly = false, CommPageRequest? page = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return CommPage<CommInboxItemDto>.Empty();

            int take = _options.ClampPageSize(page?.PageSize);
            long after = page?.AfterId ?? 0;

            var rows = await _db.Notifications.AsNoTracking()
                .Where(n => n.CompanyID == context.CompanyId
                            && n.RecipientEmployeeId == context.EmployeeId.Value
                            && (!unreadOnly || n.ReadAt == null)
                            && (after == 0 || n.Id < after))
                .OrderByDescending(n => n.Id)
                .Take(take + 1)
                .ToListAsync(cancellationToken);

            bool hasMore = rows.Count > take;
            if (hasMore) rows = rows.Take(take).ToList();
            if (rows.Count == 0) return CommPage<CommInboxItemDto>.Empty();

            var actors = await _actors.ResolveAsync(rows.Select(r => r.ActorEmployeeId), cancellationToken);

            // NOT re-authorized per row, and stated openly: every row here was authorized at DECIDE time
            // against this recipient. Re-checking would be defensible, but it would also mean a notification
            // silently disappearing from somebody's inbox when a permission changed — which reads as data loss.
            // The CONTENT is re-checked when they click through to the thread, which is where it matters.
            var items = rows.Select(n => new CommInboxItemDto
            {
                NotificationId = n.Id,
                TemplateKey = n.TemplateKey,
                Category = n.Category,
                Priority = n.Priority,
                Entity = new CommEntityRef(n.EntityType, n.EntityId),
                ThreadId = n.ThreadId,
                CommentId = n.CommentId,
                TitleAr = n.TitleAr,
                TitleEn = n.TitleEn,
                BodyAr = n.BodyAr,
                BodyEn = n.BodyEn,
                Url = n.Url,
                Actor = n.ActorEmployeeId.HasValue ? _actors.Get(actors, n.ActorEmployeeId) : null,
                CreatedAt = n.CreatedAt,
                ReadAt = n.ReadAt,
            }).ToList();

            return new CommPage<CommInboxItemDto>
            {
                Items = items,
                NextCursor = hasMore ? rows[^1].Id : null,
            };
        }

        public async Task<int> GetUnreadCountAsync(BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return 0;

            return await _db.Notifications.AsNoTracking()
                .CountAsync(n => n.CompanyID == context.CompanyId
                                 && n.RecipientEmployeeId == context.EmployeeId.Value
                                 && n.ReadAt == null,
                    cancellationToken);
        }

        public async Task<int> MarkReadAsync(
            BusinessContext context, IReadOnlyList<long>? notificationIds = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.EmployeeId is not > 0) return 0;

            // Anchored on the caller's own recipient id FIRST, so passing somebody else's notification ids
            // changes nothing.
            var query = _db.Notifications
                .Where(n => n.CompanyID == context.CompanyId
                            && n.RecipientEmployeeId == context.EmployeeId.Value
                            && n.ReadAt == null);

            if (notificationIds is { Count: > 0 })
            {
                var ids = notificationIds.ToList();
                query = query.Where(n => ids.Contains(n.Id));
            }

            var rows = await query.ToListAsync(cancellationToken);
            if (rows.Count == 0) return 0;

            var now = DateTime.UtcNow;
            foreach (var row in rows)
            {
                row.ReadAt = now;
                row.updatedBy = context.EmployeeId;
                row.UpdatedAt = now;
            }
            await _db.SaveAsync(cancellationToken);
            return rows.Count;
        }

        // ---------------------------------------------------------------------------------------------
        // Deterministic per (dedup root, recipient). The root comes from the producing row's identity
        // (comment id, mention id), so a retried transaction produces the same key and the unique index
        // absorbs it — the same construction NotificationCommand.DedupKeyPrefix uses off EventUid.
        private static string DedupKeyFor(string prefix, int employeeId)
        {
            var key = (prefix ?? "").Trim() + "|emp:" + employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // The column is bounded, and a silently truncated key would collide with a DIFFERENT notification —
            // turning idempotency into data loss. A hash of the overlong part keeps it unique.
            int max = Models.Context.Communication.CommunicationModel.DedupKeyLength;
            if (key.Length <= max) return key;

            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
            return key.Substring(0, max - 17) + "#" + hash.Substring(0, 16);
        }

        private static void Bump(Dictionary<string, int> counters, string reason, int by)
            => counters[reason] = counters.TryGetValue(reason, out var current) ? current + by : by;

        private static string Cap(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
    }
}
