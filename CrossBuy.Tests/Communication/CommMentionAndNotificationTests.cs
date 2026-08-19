using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Communication Platform — MENTIONS (modules 3, 13, 15) and NOTIFICATIONS (16-22).
    //
    // THE REQUIREMENT THIS FILE VERIFIES LITERALLY:
    //
    //     "Every Mention must create: Mention Entity / Notification / Timeline Entry / Audit Entry"
    //
    // All four, in one transaction. The first test asserts all four artefacts from a fresh context; the rest
    // pull on the ways that could go wrong — a group mention notifying nobody, a mention leaking a confidential
    // note, a retried transaction notifying twice.
    // =============================================================================================
    public class CommMentionAndNotificationTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();
        public void Dispose() => _host.Dispose();

        private static StubPermissionProvider ViewOnly() => new(PlatformActions.View);
        private static StubPermissionProvider Manager() =>
            new(PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted);

        // ============================================================================================
        // THE FOUR ARTEFACTS
        // ============================================================================================
        [Fact]
        public async Task A_mention_creates_a_mention_entity_a_notification_a_timeline_entry_and_an_audit_entry()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(entity, $"please check @employee:{CommunicationTestHost.Colleague}"));

            using var fresh = _host.NewContext();

            // 1 — MENTION ENTITY, plus its resolved recipient row.
            var mention = Assert.Single(await fresh.Set<CommMention>().AsNoTracking()
                .Where(m => m.CommentId == added.CommentId).ToListAsync());
            Assert.Equal(CommMentionTargetKind.Employee, mention.TargetKind);
            Assert.Equal(CommunicationTestHost.Colleague, mention.TargetId);
            Assert.Equal(1, mention.ResolvedRecipientCount);

            var recipient = Assert.Single(await fresh.Set<CommMentionRecipient>().AsNoTracking()
                .Where(r => r.MentionId == mention.Id).ToListAsync());
            Assert.Equal(CommunicationTestHost.Colleague, recipient.EmployeeId);

            // 2 — NOTIFICATION, addressed to the mentioned employee, with a delivery row per channel.
            var notification = Assert.Single(await fresh.Set<CommNotification>().AsNoTracking()
                .Where(n => n.CommentId == added.CommentId
                            && n.TemplateKey == CommTemplateKeys.MentionedInComment).ToListAsync());
            Assert.Equal(CommunicationTestHost.Colleague, notification.RecipientEmployeeId);
            Assert.Equal(CommNotificationCategories.Mentions, notification.Category);

            var deliveries = await fresh.Set<CommNotificationDelivery>().AsNoTracking()
                .Where(d => d.NotificationId == notification.Id).ToListAsync();
            Assert.NotEmpty(deliveries);
            Assert.All(deliveries, d => Assert.Equal(CommDeliveryStatus.Pending, d.Status));

            // 3 — AUDIT ENTRY for the mention event.
            var audit = await fresh.Set<CommAuditEntry>().AsNoTracking()
                .Where(a => a.Action == CommAuditActions.MentionCreated && a.MentionId == mention.Id)
                .ToListAsync();
            Assert.Single(audit);

            // 4 — TIMELINE ENTRY. Not a fifth table: the aggregator's Mention source reads the facts above.
            // Materialising a separate timeline row would be a second copy of the same truth, free to disagree.
            var timeline = await _host.Timeline(permissions: ViewOnly()).GetAsync(
                context, new CommTimelineQuery { Entity = entity });
            Assert.Contains(timeline.Items,
                i => i.Source == CommTimelineSourceKinds.Mention && i.CommentId == added.CommentId);
        }

        // ============================================================================================
        // Group mentions
        // ============================================================================================

        // @team resolves through IOrgHierarchy — the existing, verified, cycle-guarded walk. "Team" has no table
        // in this product, and inventing one for a collaboration feature would be new master data.
        [Fact]
        public async Task A_team_mention_reaches_the_managers_direct_and_indirect_reports()
        {
            var managerContext = CommunicationTestHost.Context(CommunicationTestHost.Manager);
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                managerContext, _host.CommentOn(entity, $"team, please review @team:{CommunicationTestHost.Manager}"));

            using var fresh = _host.NewContext();
            var recipients = await fresh.Set<CommMentionRecipient>().AsNoTracking()
                .Where(r => r.CommentId == added.CommentId)
                .Select(r => r.EmployeeId)
                .ToListAsync();

            Assert.Contains(CommunicationTestHost.Author, recipients);
            Assert.Contains(CommunicationTestHost.Colleague, recipients);

            // The manager is the ACTOR, so they are not a recipient of their own mention.
            Assert.DoesNotContain(CommunicationTestHost.Manager, recipients);

            // And the other company's employee, who sits under the same org node, is excluded — the tree carries
            // no CompanyID, so this is the intersection doing its job.
            Assert.DoesNotContain(CommunicationTestHost.OtherCompanyEmployee, recipients);
        }

        [Fact]
        public async Task A_department_mention_reaches_sub_department_members_and_records_the_label_it_resolved()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(entity, $"@department:{CommunicationTestHost.SalesDepartmentNode} heads up"));

            using var fresh = _host.NewContext();
            var mention = await fresh.Set<CommMention>().AsNoTracking().SingleAsync(m => m.CommentId == added.CommentId);

            // The label is FROZEN at authoring time: a department renamed next month was not the department that
            // was mentioned, and re-resolving on read would rewrite history.
            Assert.Equal("Sales", mention.LabelEn);
            Assert.Equal("المبيعات", mention.LabelAr);

            var recipients = await fresh.Set<CommMentionRecipient>().AsNoTracking()
                .Where(r => r.MentionId == mention.Id).Select(r => r.EmployeeId).ToListAsync();

            Assert.Contains(CommunicationTestHost.Outsider, recipients);   // in the SUB-department
            Assert.DoesNotContain(CommunicationTestHost.OtherCompanyEmployee, recipients);
        }

        // REFUSE rather than truncate. Truncation is the worse failure: the author believes the whole department
        // was notified and half of it was not, and nothing in the UI can tell them otherwise.
        [Fact]
        public async Task A_group_mention_over_the_recipient_cap_is_refused_not_truncated()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                MaxGroupMentionRecipients = 1,
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            var context = CommunicationTestHost.Context();

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                host.Comments(permissions: ViewOnly()).AddAsync(
                    context,
                    host.CommentOn(CommunicationTestHost.Invoice(),
                        $"@department:{CommunicationTestHost.SalesDepartmentNode}")));

            Assert.Equal(CommValidationException.Codes.GroupMentionTooLarge, ex.Code);
        }

        // @role parses, and the service RECORDS WHY it reached nobody instead of silently dropping it. An author
        // who typed @role deserves an answer.
        [Fact]
        public async Task An_unwired_role_mention_writes_no_mention_row_and_reports_a_reason()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity });

            var comment = new CommComment
            {
                Id = 0, CompanyID = CommunicationTestHost.CompanyId, ThreadId = thread.Id,
                EntityType = entity.EntityCode, EntityId = entity.EntityId,
                Body = "x", BodyFormat = CommBodyFormat.Markdown, Visibility = CommVisibility.Internal,
                AuthorEmployeeId = CommunicationTestHost.Author, CreatedAt = DateTime.UtcNow,
            };
            _host.Db.Set<CommComment>().Add(comment);
            await _host.Db.SaveChangesAsync();

            var outcome = await _host.Mentions().RecordAsync(
                context, thread, comment,
                new[] { new CommMentionToken(CommMentionTargetKind.Role, null, "InventoryManager", null) });

            Assert.Empty(outcome.Mentions);
            Assert.Empty(outcome.RecipientEmployeeIds);
            // The reason names the missing SOURCE, which is the actionable fact: the options flag exists, and
            // turning it on still would not make role mentions work until a provider is registered (ADR-032 §5).
            var note = Assert.Single(outcome.UnresolvedNotes);
            Assert.Contains("Role", note);
            Assert.Contains("ICommPrincipalSource", note);
        }

        // A mention of a target that resolves to NOBODY still writes the mention row (the author did mention it)
        // and reports that it reached no one.
        [Fact]
        public async Task A_mention_of_an_absent_employee_reaches_nobody_and_says_so()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(entity, "@employee:99999 hello"));

            using var fresh = _host.NewContext();
            var mention = await fresh.Set<CommMention>().AsNoTracking().SingleAsync(m => m.CommentId == added.CommentId);

            Assert.Equal(0, mention.ResolvedRecipientCount);
            Assert.Empty(await fresh.Set<CommMentionRecipient>().AsNoTracking()
                .Where(r => r.MentionId == mention.Id).ToListAsync());
        }

        // ============================================================================================
        // A MENTION IS NOT A GRANT — the most important security property in this file
        // ============================================================================================

        // Mentioning somebody in a Confidential note must not tell them it exists, who wrote it, or what it says.
        // The MENTION row still exists, so the audit records that the author tried — which is the honest outcome.
        [Fact]
        public async Task A_mention_in_a_confidential_note_does_not_notify_someone_who_cannot_read_it()
        {
            var entity = CommunicationTestHost.Invoice();
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);

            // The AUTHOR is privileged (can read Confidential); the mentioned colleague is not. The notification
            // service evaluates the RECIPIENT's rights, not the actor's.
            var privileged = Manager();

            var thread = await _host.Threads(permissions: privileged).GetOrCreateAsync(
                authorContext, new CommThreadRequest
                {
                    Entity = entity, Kind = CommThreadKind.Notes, Visibility = CommVisibility.Confidential,
                });

            // The permission stub answers the same for every employee, so to make the RECIPIENT unprivileged the
            // notification service is given a View-only provider while the thread work uses the privileged one.
            var comments = new CommCommentService(
                _host.Db,
                _host.Threads(permissions: privileged),
                _host.Access(permissions: privileged),
                _host.BodyPolicy(),
                _host.Mentions(),
                _host.Participation(permissions: privileged),
                _host.Notifications(permissions: ViewOnly()),      // the RECIPIENT gate: View only
                _host.Attachments(),
                _host.Events(),
                _host.Actors(),
                _host.Surface(),
                _host.Opt);

            var added = await comments.AddAsync(authorContext, new CommCommentRequest
            {
                Entity = entity, ThreadId = thread.Id,
                Body = $"margin is thin @employee:{CommunicationTestHost.Colleague}",
                Visibility = CommVisibility.Confidential,
            });

            using var fresh = _host.NewContext();

            // The mention row EXISTS — the attempt is on the record.
            Assert.Single(await fresh.Set<CommMention>().AsNoTracking()
                .Where(m => m.CommentId == added.CommentId).ToListAsync());

            // The notification does NOT. A notification is a read of the content by another name.
            Assert.Empty(await fresh.Set<CommNotification>().AsNoTracking()
                .Where(n => n.CommentId == added.CommentId
                            && n.RecipientEmployeeId == CommunicationTestHost.Colleague).ToListAsync());
        }

        [Fact]
        public async Task The_notification_result_reports_why_a_recipient_was_dropped()
        {
            var context = CommunicationTestHost.Context();

            var result = await _host.Notifications(permissions: ViewOnly()).QueueAsync(context, new CommNotificationRequest
            {
                TemplateKey = CommTemplateKeys.MentionedInComment,
                Entity = CommunicationTestHost.Invoice(),
                CompanyId = CommunicationTestHost.CompanyId,
                ActorEmployeeId = CommunicationTestHost.Author,

                // The actor mentions themselves — nobody is told about their own action.
                RecipientEmployeeIds = new[] { CommunicationTestHost.Author },
                SubjectVisibility = CommVisibility.Internal,
                Tokens = new Dictionary<string, string>
                {
                    [CommTemplateTokens.ActorName] = "Ahmed",
                    [CommTemplateTokens.EntityLabel] = "INV-1",
                },
                DedupKeyPrefix = "test:self",
            });

            Assert.Equal(0, result.NotifiedRecipientCount);
            Assert.Equal(1, result.SkippedByReason[CommNotificationResult.SkipReasons.ActorSelf]);
        }

        // ============================================================================================
        // Idempotency and dedup
        // ============================================================================================

        // A person mentioned DIRECTLY and via their DEPARTMENT is one notification, not two.
        [Fact]
        public async Task A_recipient_reached_twice_by_one_comment_is_notified_once()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(entity,
                    $"@employee:{CommunicationTestHost.Colleague} and @department:{CommunicationTestHost.SalesDepartmentNode}"));

            using var fresh = _host.NewContext();
            var notifications = await fresh.Set<CommNotification>().AsNoTracking()
                .Where(n => n.CommentId == added.CommentId
                            && n.RecipientEmployeeId == CommunicationTestHost.Colleague
                            && n.TemplateKey == CommTemplateKeys.MentionedInComment)
                .ToListAsync();

            Assert.Single(notifications);
        }

        // TRUE idempotency, not the legacy service's unread-noise guard: a second queue with the same dedup root
        // adds ZERO rows, whether or not the first has been read.
        [Fact]
        public async Task Queueing_the_same_notification_twice_produces_one_row()
        {
            var context = CommunicationTestHost.Context();
            var notifications = _host.Notifications(permissions: ViewOnly());

            var request = new CommNotificationRequest
            {
                TemplateKey = CommTemplateKeys.MentionedInComment,
                Entity = CommunicationTestHost.Invoice(),
                CompanyId = CommunicationTestHost.CompanyId,
                ActorEmployeeId = CommunicationTestHost.Author,
                RecipientEmployeeIds = new[] { CommunicationTestHost.Colleague },
                SubjectVisibility = CommVisibility.Internal,
                Tokens = new Dictionary<string, string>
                {
                    [CommTemplateTokens.ActorName] = "Ahmed",
                    [CommTemplateTokens.EntityLabel] = "INV-1",
                },
                DedupKeyPrefix = "comment:1:mention",
            };

            var first = await notifications.QueueAsync(context, request);
            await _host.Db.SaveChangesAsync();
            var second = await notifications.QueueAsync(context, request);
            await _host.Db.SaveChangesAsync();

            Assert.Equal(1, first.NotifiedRecipientCount);
            Assert.Equal(0, second.NotifiedRecipientCount);
            Assert.Equal(1, second.SkippedByReason[CommNotificationResult.SkipReasons.AlreadyNotified]);
        }

        // ============================================================================================
        // Templates and preferences (modules 16, 22)
        // ============================================================================================
        [Fact]
        public void An_unknown_template_key_is_refused_at_decide_time()
        {
            var ex = Assert.Throws<CommValidationException>(() => _host.Catalog().Get("comm.not_a_template"));
            Assert.Equal(CommValidationException.Codes.TemplateUnknown, ex.Code);
        }

        // A missing token would render literally in somebody's inbox. Refused when the notification is DECIDED,
        // not discovered when it is delivered.
        [Fact]
        public void A_missing_required_token_is_refused_rather_than_rendered_literally()
        {
            var template = _host.Catalog().Get(CommTemplateKeys.MentionedInComment);

            var ex = Assert.Throws<CommValidationException>(() => _host.Renderer().Render(
                template,
                new Dictionary<string, string> { [CommTemplateTokens.ActorName] = "Ahmed" },   // no entity token
                url: null));

            Assert.Equal(CommValidationException.Codes.TemplateTokenMissing, ex.Code);
        }

        [Fact]
        public void Rendering_substitutes_both_languages_and_drops_an_unsupplied_optional_token()
        {
            var template = _host.Catalog().Get(CommTemplateKeys.MentionedInComment);

            var rendered = _host.Renderer().Render(template, new Dictionary<string, string>
            {
                [CommTemplateTokens.ActorName] = "Ahmed",
                [CommTemplateTokens.EntityLabel] = "INV-42",
            }, url: "/Accounting/SalesInvoiceDetail?id=42");

            Assert.Contains("Ahmed", rendered.TitleEn);
            Assert.Contains("أشار", rendered.TitleAr);
            Assert.Contains("INV-42", rendered.BodyEn);

            // {excerpt} was not supplied: it renders as EMPTY, never as a literal "{excerpt}" — which would look
            // like a bug to a recipient who cannot know it was optional.
            Assert.DoesNotContain("{", rendered.BodyEn);
            Assert.Equal("/Accounting/SalesInvoiceDetail?id=42", rendered.Url);
        }

        // A token VALUE must never be re-scanned: an employee whose display name contains "{entity}" would
        // otherwise have it substituted, which is a small injection into other people's notifications.
        [Fact]
        public void A_token_value_containing_a_placeholder_is_not_substituted_again()
        {
            var template = _host.Catalog().Get(CommTemplateKeys.MentionedInComment);

            var rendered = _host.Renderer().Render(template, new Dictionary<string, string>
            {
                [CommTemplateTokens.ActorName] = "{entity}",
                [CommTemplateTokens.EntityLabel] = "INV-42",
            }, url: null);

            Assert.Contains("{entity}", rendered.TitleEn);        // preserved verbatim, not expanded
        }

        // Every template's resource keys are PUBLISHED, so localizing the platform is a list rather than a grep.
        [Fact]
        public void Every_template_publishes_the_resource_keys_a_localized_deployment_needs()
        {
            var catalog = _host.Catalog();
            var keys = catalog.RequiredResourceKeys();

            Assert.Equal(catalog.All().Count * 4, keys.Count);
            Assert.All(keys, k => Assert.StartsWith("Comm.Notification.", k));
        }

        // The channel plan is an INTERSECTION. A preference of Off removes the channel; the deployment's
        // EnabledChannels removes it too. Neither can ADD one.
        [Fact]
        public async Task A_preference_of_off_removes_a_channel_from_the_plan()
        {
            var context = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var preferences = _host.Preferences();

            await preferences.SetAsync(context, new CommPreferenceUpdateRequest
            {
                EmployeeId = CommunicationTestHost.Colleague,
                Category = CommNotificationCategories.Mentions,
                Channel = CommChannel.Email,
                Mode = CommPreferenceMode.Off,
            });

            var plan = await preferences.ResolveAsync(
                CommunicationTestHost.CompanyId, CommunicationTestHost.Colleague,
                _host.Catalog().Get(CommTemplateKeys.MentionedInComment), requestedChannels: null);

            Assert.Contains(CommChannel.InApp, plan.Channels);
            Assert.DoesNotContain(CommChannel.Email, plan.Channels);
            Assert.Equal(CommNotificationResult.SkipReasons.PreferenceOff, plan.ExcludedChannels[CommChannel.Email]);
        }

        // ABSENCE OF A ROW IS NOT "OFF" — it means "use the deployment default". Otherwise a channel an operator
        // switches on would reach only the handful of people who had opened the preferences screen.
        [Fact]
        public async Task A_recipient_with_no_stored_preference_inherits_the_deployment_default()
        {
            var plan = await _host.Preferences().ResolveAsync(
                CommunicationTestHost.CompanyId, CommunicationTestHost.Colleague,
                _host.Catalog().Get(CommTemplateKeys.MentionedInComment), requestedChannels: null);

            Assert.Contains(CommChannel.InApp, plan.Channels);
            Assert.Contains(CommChannel.Email, plan.Channels);

            var stored = await _host.Preferences().GetAsync(
                CommunicationTestHost.CompanyId, CommunicationTestHost.Colleague);
            Assert.All(stored, p => Assert.True(p.IsDefault));
        }

        // A channel the deployment has not enabled produces NO delivery row at all — a stronger statement than
        // parking one as Skipped.
        [Fact]
        public async Task A_channel_the_deployment_has_not_enabled_never_reaches_the_plan()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                EnabledChannels = new List<string> { CommChannel.InApp },       // no Email
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
            });

            var plan = await host.Preferences().ResolveAsync(
                CommunicationTestHost.CompanyId, CommunicationTestHost.Colleague,
                host.Catalog().Get(CommTemplateKeys.MentionedInComment), requestedChannels: null);

            Assert.DoesNotContain(CommChannel.Email, plan.Channels);
            Assert.Contains("EnabledChannels", plan.ExcludedChannels[CommChannel.Email]);
        }

        // Digest is RESOLVED HONESTLY: it behaves as Immediate for in-app (the inbox IS the digest) and is
        // excluded elsewhere, because no batching worker exists. Treating it as Immediate for email would email
        // somebody who explicitly asked for a daily summary.
        [Fact]
        public async Task Digest_is_honoured_for_in_app_and_excluded_where_no_batching_worker_exists()
        {
            var context = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var preferences = _host.Preferences();

            foreach (var channel in new[] { CommChannel.InApp, CommChannel.Email })
                await preferences.SetAsync(context, new CommPreferenceUpdateRequest
                {
                    EmployeeId = CommunicationTestHost.Colleague,
                    Category = CommNotificationCategories.Mentions,
                    Channel = channel,
                    Mode = CommPreferenceMode.Digest,
                });

            var plan = await preferences.ResolveAsync(
                CommunicationTestHost.CompanyId, CommunicationTestHost.Colleague,
                _host.Catalog().Get(CommTemplateKeys.MentionedInComment), requestedChannels: null);

            Assert.Contains(CommChannel.InApp, plan.Channels);
            Assert.DoesNotContain(CommChannel.Email, plan.Channels);
            Assert.Contains("digest batching worker", plan.ExcludedChannels[CommChannel.Email]);
        }

        // A preference is PERSONAL. There is no administrative override, deliberately: an administrator who can
        // switch somebody else's notifications off can silence a mention they were meant to see.
        [Fact]
        public async Task Nobody_can_change_another_employees_preference()
        {
            var managerContext = CommunicationTestHost.Context(CommunicationTestHost.Manager);

            await Assert.ThrowsAsync<CommAccessDeniedException>(() =>
                _host.Preferences().SetAsync(managerContext, new CommPreferenceUpdateRequest
                {
                    EmployeeId = CommunicationTestHost.Colleague,     // somebody else
                    Category = CommNotificationCategories.Mentions,
                    Channel = CommChannel.InApp,
                    Mode = CommPreferenceMode.Off,
                }));
        }

        // ============================================================================================
        // Mention history (module 15)
        // ============================================================================================
        [Fact]
        public async Task Mention_history_lists_direct_and_group_mentions_with_the_route_they_arrived_by()
        {
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments(permissions: ViewOnly());

            await comments.AddAsync(authorContext,
                _host.CommentOn(entity, $"direct @employee:{CommunicationTestHost.Colleague}"));
            await comments.AddAsync(authorContext,
                _host.CommentOn(entity, $"group @department:{CommunicationTestHost.SalesDepartmentNode}", threadId: null));

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var history = await _host.Mentions().GetHistoryAsync(colleagueContext);

            Assert.Equal(2, history.Items.Count);
            Assert.Contains(history.Items, i => i.ViaKind == CommMentionTargetKind.Employee);
            Assert.Contains(history.Items, i => i.ViaKind == CommMentionTargetKind.Department);
            Assert.All(history.Items, i => Assert.Equal(CommunicationTestHost.Author, i.MentionedBy.EmployeeId));

            // An EXCERPT, never the whole body: a mention list must not become a way to read comments.
            Assert.All(history.Items, i => Assert.False(string.IsNullOrEmpty(i.Excerpt)));
        }

        [Fact]
        public async Task Unread_mention_count_clears_when_marked_read()
        {
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                authorContext,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var mentions = _host.Mentions();

            Assert.Equal(1, await mentions.GetUnreadCountAsync(colleagueContext));
            Assert.Equal(1, await mentions.MarkReadAsync(colleagueContext));
            Assert.Equal(0, await mentions.GetUnreadCountAsync(colleagueContext));
        }

        // Marking read is anchored on the CALLER's own recipient rows first, so passing somebody else's mention
        // ids changes nothing.
        [Fact]
        public async Task Marking_read_cannot_clear_another_employees_mentions()
        {
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                authorContext,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            using var fresh = _host.NewContext();
            var mentionId = await fresh.Set<CommMention>().AsNoTracking().Select(m => m.Id).FirstAsync();

            // The MANAGER tries to clear a mention addressed to the colleague.
            var managerContext = CommunicationTestHost.Context(CommunicationTestHost.Manager);
            Assert.Equal(0, await _host.Mentions().MarkReadAsync(managerContext, new[] { mentionId }));

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            Assert.Equal(1, await _host.Mentions().GetUnreadCountAsync(colleagueContext));
        }

        // ============================================================================================
        // Auto-participation (modules 4, 5)
        // ============================================================================================

        // Being NAMED is a stronger signal of interest than having commented, so a mentioned employee lands at
        // Follower while the author lands at Participant.
        [Fact]
        public async Task The_author_becomes_a_participant_and_a_mentioned_employee_becomes_a_follower()
        {
            var context = CommunicationTestHost.Context();
            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            using var fresh = _host.NewContext();
            var participants = await fresh.Set<CommParticipant>().AsNoTracking()
                .Where(p => p.ThreadId == added.ThreadId && p.RemovedAt == null).ToListAsync();

            var author = Assert.Single(participants, p => p.EmployeeId == CommunicationTestHost.Author);
            Assert.Equal(CommParticipantRole.Participant, author.Role);
            Assert.Equal(CommParticipationSource.Author, author.Source);

            var mentioned = Assert.Single(participants, p => p.EmployeeId == CommunicationTestHost.Colleague);
            Assert.Equal(CommParticipantRole.Follower, mentioned.Role);
            Assert.Equal(CommParticipationSource.Mention, mentioned.Source);
        }

        // A follower gets ONE notification per comment, and a mentioned person gets the MENTION template rather
        // than both — a mention and a "new comment on a thread you follow" for one comment is one too many.
        [Fact]
        public async Task A_follower_who_is_also_mentioned_gets_only_the_mention_notification()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments(permissions: ViewOnly());

            var first = await comments.AddAsync(context,
                _host.CommentOn(entity, $"@employee:{CommunicationTestHost.Colleague} start"));

            // The colleague is now a Follower. Mention them again in a second comment.
            await comments.AddAsync(context,
                _host.CommentOn(entity, $"@employee:{CommunicationTestHost.Colleague} again", threadId: first.ThreadId));

            using var fresh = _host.NewContext();
            var templates = await fresh.Set<CommNotification>().AsNoTracking()
                .Where(n => n.RecipientEmployeeId == CommunicationTestHost.Colleague)
                .Select(n => n.TemplateKey)
                .ToListAsync();

            Assert.Equal(2, templates.Count);
            Assert.All(templates, t => Assert.Equal(CommTemplateKeys.MentionedInComment, t));
            Assert.DoesNotContain(CommTemplateKeys.CommentOnFollowedThread, templates);
        }

        // A WATCHER is deliberately NOT notified per activity — that is the whole reason the role exists, and it
        // is what makes auto-follow usable rather than a source of spam.
        [Fact]
        public async Task A_watcher_is_not_notified_per_activity_but_a_follower_is()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments(permissions: ViewOnly());

            var seed = await comments.AddAsync(context, _host.CommentOn(entity, "opening"));

            // Adding SOMEBODY ELSE is a moderator act — self-service goes through FollowAsync — so the
            // arrangement is made with moderator rights.
            var participation = _host.Participation(permissions: Manager());
            await participation.AddAsync(context, seed.ThreadId, CommunicationTestHost.Colleague, CommParticipantRole.Watcher);
            await participation.AddAsync(context, seed.ThreadId, CommunicationTestHost.Manager, CommParticipantRole.Follower);

            var notifiable = await participation.ResolveNotifiableAsync(context, seed.ThreadId, CommunicationTestHost.Author);

            Assert.Contains(CommunicationTestHost.Manager, notifiable);
            Assert.DoesNotContain(CommunicationTestHost.Colleague, notifiable);
        }

        [Fact]
        public async Task A_muted_participant_is_not_notified()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments(permissions: ViewOnly());

            var seed = await comments.AddAsync(context, _host.CommentOn(entity, "opening"));

            var participation = _host.Participation(permissions: Manager());
            await participation.AddAsync(context, seed.ThreadId, CommunicationTestHost.Manager, CommParticipantRole.Follower);

            // Muting is SELF-service, so it needs no elevated right — only Read on the thread.
            var managerContext = CommunicationTestHost.Context(CommunicationTestHost.Manager);
            await _host.Participation(permissions: ViewOnly()).SetMutedAsync(managerContext, seed.ThreadId, muted: true);

            var notifiable = await participation.ResolveNotifiableAsync(context, seed.ThreadId, CommunicationTestHost.Author);
            Assert.DoesNotContain(CommunicationTestHost.Manager, notifiable);
        }

        // EnsureAsync must never LOWER a role: silently demoting somebody's notification level is
        // indistinguishable from a bug.
        [Fact]
        public async Task Participation_is_upgraded_but_never_downgraded()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var seed = await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(entity, "one"));

            var participation = _host.Participation(permissions: Manager());
            await participation.AddAsync(context, seed.ThreadId, CommunicationTestHost.Colleague, CommParticipantRole.Follower);

            // A later comment by the colleague would Ensure(Participant), which is LOWER than Follower.
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                CommunicationTestHost.Context(CommunicationTestHost.Colleague),
                _host.CommentOn(entity, "two", threadId: seed.ThreadId));

            using var fresh = _host.NewContext();
            var row = await fresh.Set<CommParticipant>().AsNoTracking()
                .SingleAsync(p => p.ThreadId == seed.ThreadId
                                  && p.EmployeeId == CommunicationTestHost.Colleague
                                  && p.RemovedAt == null);

            Assert.Equal(CommParticipantRole.Follower, row.Role);
        }
    }
}
