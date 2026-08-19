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
    // Communication Platform — TIMELINE AGGREGATION (modules 12, 32, 35), DELIVERY (17-21),
    // REACTIONS (6), READ STATUS (14), ATTACHMENTS + PREVIEW (7, 11), AUDIT (31), EVENTS (26, 27).
    // =============================================================================================
    public class CommTimelineAndDispatchTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();
        public void Dispose() => _host.Dispose();

        private static StubPermissionProvider ViewOnly() => new(PlatformActions.View);
        private static StubPermissionProvider Manager() =>
            new(PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted);

        // ============================================================================================
        // TIMELINE
        // ============================================================================================
        [Fact]
        public async Task The_timeline_merges_comments_and_mentions_newest_first_and_reports_every_source()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(entity, $"first @employee:{CommunicationTestHost.Colleague}"));

            var result = await _host.Timeline(permissions: ViewOnly()).GetAsync(
                context, new CommTimelineQuery { Entity = entity });

            Assert.Contains(result.Items, i => i.Source == CommTimelineSourceKinds.Comment);
            Assert.Contains(result.Items, i => i.Source == CommTimelineSourceKinds.Mention);

            // Newest first, so a scan starts at the most recent activity.
            var timestamps = result.Items.Select(i => i.OccurredAt).ToList();
            Assert.Equal(timestamps.OrderByDescending(t => t).ToList(), timestamps);

            // EVERY source reports, contributing or not. A merged timeline that silently returns nothing from one
            // source is indistinguishable from a source that had nothing to say.
            Assert.Equal(3, result.SourceReports.Count);
            Assert.All(result.SourceReports, r => Assert.NotNull(r.Note ?? "ok"));
        }

        // A comment and its mention are written in ONE transaction and can share a timestamp to the tick. Without
        // a deterministic tie-break the two would swap places between pages and the cursor would skip or repeat
        // one of them.
        [Fact]
        public async Task Items_sharing_a_timestamp_are_ordered_deterministically_by_item_key()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(entity, $"@employee:{CommunicationTestHost.Colleague}"));

            var first = await _host.Timeline(permissions: ViewOnly()).GetAsync(
                context, new CommTimelineQuery { Entity = entity });
            var second = await _host.Timeline(permissions: ViewOnly()).GetAsync(
                context, new CommTimelineQuery { Entity = entity });

            Assert.Equal(
                first.Items.Select(i => i.ItemKey).ToList(),
                second.Items.Select(i => i.ItemKey).ToList());
        }

        // A caller who cannot open the record gets an EMPTY widget plus a reason, not an exception a controller
        // has to translate — a record timeline is a widget on a page.
        [Fact]
        public async Task A_caller_denied_the_entity_gets_an_empty_timeline_with_a_reason()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(entity, "hidden"));

            var result = await _host.Timeline(permissions: new StubPermissionProvider()).GetAsync(
                context, new CommTimelineQuery { Entity = entity });

            Assert.Empty(result.Items);
            var report = Assert.Single(result.SourceReports);
            Assert.StartsWith("denied", report.Note);
        }

        // ONE BROKEN CONTRIBUTOR MUST NOT BLANK A RECORD'S HISTORY. The failure is reported and the other
        // sources still render.
        [Fact]
        public async Task A_throwing_source_is_reported_and_the_other_sources_still_render()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(entity, "survives"));

            var access = _host.Access(permissions: ViewOnly());
            var aggregator = _host.Timeline(sources: new ICommTimelineSource[]
            {
                new ThrowingTimelineSource(),
                new CommCommentTimelineSource(_host.Db, access, _host.Actors(), _host.BodyPolicy()),
            }, permissions: ViewOnly());

            var result = await aggregator.GetAsync(context, new CommTimelineQuery { Entity = entity });

            Assert.Contains(result.Items, i => i.Source == CommTimelineSourceKinds.Comment);
            var broken = Assert.Single(result.SourceReports, r => r.Source == CommTimelineSourceKinds.BusinessEvent);
            Assert.False(broken.Contributed);
            Assert.StartsWith("error:", broken.Note);
        }

        // The KERNEL source is applied only where the registry says SupportsTimeline. ITimelineProjectionService
        // THROWS otherwise, so asking first is the difference between "not applicable" and an exception on most
        // records in the product.
        [Fact]
        public async Task The_kernel_source_reports_not_applicable_for_an_entity_with_no_kernel_timeline()
        {
            var context = CommunicationTestHost.Context();

            // PosOrder is onboarded HERE by configuration, but its registry SupportsTimeline is false.
            var entity = CommunicationTestHost.PosOrder();
            await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(entity, "note"));

            var access = _host.Access(permissions: ViewOnly());
            var kernel = new TimelineProjectionService(
                _host.Db, _host.Registry(), ViewOnly(), Array.Empty<ILegacyTimelineAdapter>());

            var aggregator = _host.Timeline(sources: new ICommTimelineSource[]
            {
                new PlatformEventTimelineSource(kernel, _host.Registry()),
                new CommCommentTimelineSource(_host.Db, access, _host.Actors(), _host.BodyPolicy()),
            }, permissions: ViewOnly());

            var result = await aggregator.GetAsync(context, new CommTimelineQuery { Entity = entity });

            var kernelReport = Assert.Single(result.SourceReports, r => r.Source == CommTimelineSourceKinds.BusinessEvent);
            Assert.False(kernelReport.Contributed);
            Assert.Contains("SupportsTimeline is false", kernelReport.Note);

            // And the comment stream still rendered.
            Assert.Contains(result.Items, i => i.Source == CommTimelineSourceKinds.Comment);
        }

        [Fact]
        public async Task A_confidential_comment_is_absent_from_an_ordinary_callers_timeline()
        {
            var authorContext = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads(permissions: Manager()).GetOrCreateAsync(
                authorContext, new CommThreadRequest
                {
                    Entity = entity, Kind = CommThreadKind.Notes, Visibility = CommVisibility.Confidential,
                });

            await _host.Comments(permissions: Manager()).AddAsync(authorContext, new CommCommentRequest
            {
                Entity = entity, ThreadId = thread.Id, Body = "margin is 2%",
                Visibility = CommVisibility.Confidential,
            });

            var ordinary = await _host.Timeline(permissions: ViewOnly()).GetAsync(
                authorContext, new CommTimelineQuery { Entity = entity });
            Assert.DoesNotContain(ordinary.Items, i => i.Body != null && i.Body.Contains("margin"));

            var privileged = await _host.Timeline(permissions: Manager()).GetAsync(
                authorContext, new CommTimelineQuery { Entity = entity });
            Assert.Contains(privileged.Items, i => i.Body != null && i.Body.Contains("margin"));
        }

        // Moderation acts appear on the record so a reader can understand a gap in the conversation. Reactions,
        // participation and read state are audited but deliberately NOT surfaced here.
        [Fact]
        public async Task A_moderation_act_appears_on_the_timeline_but_a_reaction_does_not()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(entity, "text"));
            await _host.Reactions(permissions: ViewOnly()).AddAsync(context, added.CommentId, CommReactionKeys.Like);
            await _host.Comments(permissions: Manager()).DeleteAsync(context, added.CommentId, "off topic");

            var result = await _host.Timeline(permissions: Manager()).GetAsync(
                context, new CommTimelineQuery { Entity = entity, IncludeDeleted = true });

            Assert.Contains(result.Items,
                i => i.Source == CommTimelineSourceKinds.CommAudit && i.ItemType == CommEventTypes.CommentDeleted);
            Assert.DoesNotContain(result.Items, i => i.ItemType == CommEventTypes.ReactionAdded);
        }

        [Fact]
        public async Task Requesting_a_subset_of_sources_skips_the_others_entirely()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(entity, $"@employee:{CommunicationTestHost.Colleague}"));

            var result = await _host.Timeline(permissions: ViewOnly()).GetAsync(context, new CommTimelineQuery
            {
                Entity = entity,
                Sources = new[] { CommTimelineSourceKinds.Comment },
            });

            Assert.All(result.Items, i => Assert.Equal(CommTimelineSourceKinds.Comment, i.Source));
            Assert.Single(result.SourceReports);
        }

        // ============================================================================================
        // DELIVERY (modules 17-21)
        // ============================================================================================
        [Fact]
        public async Task Dispatching_delivers_an_in_app_row_and_audits_it()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var report = await _host.Dispatcher().DispatchPendingAsync();

            Assert.True(report.Claimed > 0);
            Assert.True(report.Delivered > 0);
            Assert.Equal(report.Delivered, report.ByChannel[CommChannel.InApp]);

            using var fresh = _host.NewContext();
            var inApp = await fresh.Set<CommNotificationDelivery>().AsNoTracking()
                .Where(d => d.Channel == CommChannel.InApp).ToListAsync();
            Assert.All(inApp, d =>
            {
                Assert.Equal(CommDeliveryStatus.Sent, d.Status);
                Assert.NotNull(d.SentAt);
                Assert.Null(d.ClaimedAt);            // released on completion
            });

            Assert.NotEmpty(await fresh.Set<CommAuditEntry>().AsNoTracking()
                .Where(a => a.Action == CommAuditActions.NotificationDelivered).ToListAsync());
        }

        // A channel in the VOCABULARY with no registered adapter is parked as Skipped WITH A REASON — never left
        // Pending forever, and never retried five times for a permanent condition.
        [Fact]
        public async Task A_channel_with_no_registered_adapter_is_parked_as_skipped_with_a_reason()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            // Only the in-app adapter is supplied; the mention template also asks for Email.
            var report = await _host.Dispatcher(channels: new ICommNotificationChannel[]
            {
                new InAppCommNotificationChannel(),
            }).DispatchPendingAsync();

            Assert.True(report.Skipped > 0);

            using var fresh = _host.NewContext();
            var email = await fresh.Set<CommNotificationDelivery>().AsNoTracking()
                .Where(d => d.Channel == CommChannel.Email).ToListAsync();

            Assert.All(email, d =>
            {
                Assert.Equal(CommDeliveryStatus.Skipped, d.Status);
                Assert.Contains("no adapter", d.Error);
            });
        }

        // A DISABLED adapter parks its rows too — a channel configured without credentials shows an operator a
        // reason rather than a growing Pending queue.
        [Fact]
        public async Task A_disabled_channel_parks_its_rows_as_skipped()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            await _host.Dispatcher(channels: new ICommNotificationChannel[]
            {
                new InAppCommNotificationChannel(),
                UnwiredCommNotificationChannel.Email(),
            }).DispatchPendingAsync();

            using var fresh = _host.NewContext();
            var email = await fresh.Set<CommNotificationDelivery>().AsNoTracking()
                .SingleAsync(d => d.Channel == CommChannel.Email);

            Assert.Equal(CommDeliveryStatus.Skipped, email.Status);
            Assert.Contains("no email adapter", email.Error);
        }

        // A THROWING adapter is a FAILURE — retryable. An SMTP timeout is not a permanent condition, and one bad
        // row must not abort the batch.
        [Fact]
        public async Task A_throwing_channel_becomes_a_retryable_failed_row_and_the_batch_continues()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var report = await _host.Dispatcher(channels: new ICommNotificationChannel[]
            {
                new InAppCommNotificationChannel(),
                new ThrowingCommChannel(CommChannel.Email),
            }).DispatchPendingAsync();

            Assert.True(report.Failed > 0);
            Assert.True(report.Delivered > 0);       // the in-app row still went

            using var fresh = _host.NewContext();
            var email = await fresh.Set<CommNotificationDelivery>().AsNoTracking()
                .SingleAsync(d => d.Channel == CommChannel.Email);

            Assert.Equal(CommDeliveryStatus.Failed, email.Status);
            Assert.Equal(1, email.Attempts);
            Assert.Contains("transport exploded", email.Error);
            Assert.Null(email.ClaimedAt);            // released, so the next pass can reclaim it
        }

        [Fact]
        public async Task A_failed_row_is_retried_until_the_attempt_cap_and_then_reported_as_abandoned()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                MaxDeliveryAttempts = 2,
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            var context = CommunicationTestHost.Context();
            await host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var channels = new ICommNotificationChannel[] { new ThrowingCommChannel(CommChannel.InApp) };

            var first = await host.Dispatcher(channels: channels).DispatchPendingAsync();
            Assert.Equal(0, first.Abandoned);

            var second = await host.Dispatcher(channels: channels).DispatchPendingAsync();
            Assert.Equal(1, second.Abandoned);

            // A third pass finds nothing: the row is over the cap, so the claim predicate excludes it.
            var third = await host.Dispatcher(channels: channels).DispatchPendingAsync();
            Assert.Equal(0, third.Claimed);
        }

        // THE CLAIM IS BY STATUS ONLY — never a cursor, never MAX(Id). A Sent row must never be reclaimed, or a
        // recipient gets the same email twice.
        [Fact]
        public async Task A_sent_row_is_never_reclaimed()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var channel = new RecordingCommChannel(CommChannel.InApp);
            var dispatcher = _host.Dispatcher(channels: new ICommNotificationChannel[] { channel });

            await dispatcher.DispatchPendingAsync();
            int afterFirst = channel.Sent.Count;

            await dispatcher.DispatchPendingAsync();

            Assert.Equal(afterFirst, channel.Sent.Count);
        }

        // The delivery row's dedup key is handed to the downstream provider, so a redelivered row cannot produce
        // a second email or push at the far end either.
        [Fact]
        public async Task Each_delivery_carries_a_deterministic_dedup_key_to_the_channel()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var channel = new RecordingCommChannel(CommChannel.InApp);
            await _host.Dispatcher(channels: new ICommNotificationChannel[] { channel }).DispatchPendingAsync();

            var message = Assert.Single(channel.Sent);
            Assert.Contains(CommChannel.InApp, message.DedupKey);
            Assert.Contains("emp:" + CommunicationTestHost.Colleague, message.DedupKey);
            Assert.False(string.IsNullOrWhiteSpace(message.Rendered.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(message.Rendered.TitleAr));
        }

        [Fact]
        public async Task An_empty_queue_reports_nothing_claimed()
        {
            var report = await _host.Dispatcher().DispatchPendingAsync();

            Assert.Equal(0, report.Claimed);
            Assert.Contains("queue empty", report.Notes);
        }

        // ============================================================================================
        // IN-APP INBOX (module 19)
        // ============================================================================================
        [Fact]
        public async Task The_inbox_lists_a_recipients_own_notifications_and_marks_them_read()
        {
            var authorContext = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                authorContext,
                _host.CommentOn(CommunicationTestHost.Invoice(), $"@employee:{CommunicationTestHost.Colleague}"));

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var notifications = _host.Notifications(permissions: ViewOnly());

            var inbox = await notifications.GetInboxAsync(colleagueContext);
            var item = Assert.Single(inbox.Items);
            Assert.False(item.IsRead);
            Assert.Equal(CommTemplateKeys.MentionedInComment, item.TemplateKey);

            // The deep link is resolved through IEntityRegistry.BuildUrl, so a comm notification and an event
            // notification land on the same screen.
            Assert.Equal("/Accounting/SalesInvoiceDetail?id=1001", item.Url);

            Assert.Equal(1, await notifications.GetUnreadCountAsync(colleagueContext));
            Assert.Equal(1, await notifications.MarkReadAsync(colleagueContext));
            Assert.Equal(0, await notifications.GetUnreadCountAsync(colleagueContext));

            // The AUTHOR's inbox is empty: nobody is told about their own action.
            Assert.Empty((await notifications.GetInboxAsync(authorContext)).Items);
        }

        // ============================================================================================
        // REACTIONS (module 6)
        // ============================================================================================
        [Fact]
        public async Task Reactions_are_idempotent_and_summarised_per_key()
        {
            var context = CommunicationTestHost.Context();
            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), "good point"));

            var reactions = _host.Reactions(permissions: ViewOnly());

            await reactions.AddAsync(context, added.CommentId, CommReactionKeys.Like);
            var summary = await reactions.AddAsync(context, added.CommentId, CommReactionKeys.Like);   // double-click

            var like = Assert.Single(summary, s => s.ReactionKey == CommReactionKeys.Like);
            Assert.Equal(1, like.Count);
            Assert.True(like.Mine);

            using var fresh = _host.NewContext();
            Assert.Equal(1, await fresh.Set<CommReaction>().CountAsync(r => r.CommentId == added.CommentId));
        }

        [Fact]
        public async Task A_reaction_key_outside_the_frozen_set_is_refused()
        {
            var context = CommunicationTestHost.Context();
            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), "x"));

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                _host.Reactions(permissions: ViewOnly()).AddAsync(context, added.CommentId, "🎉"));

            Assert.Equal(CommValidationException.Codes.ReactionKeyInvalid, ex.Code);
        }

        [Fact]
        public async Task Removing_a_reaction_clears_it_and_leaves_an_audit_row()
        {
            var context = CommunicationTestHost.Context();
            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), "x"));

            var reactions = _host.Reactions(permissions: ViewOnly());
            await reactions.AddAsync(context, added.CommentId, CommReactionKeys.Concern);
            var after = await reactions.RemoveAsync(context, added.CommentId, CommReactionKeys.Concern);

            Assert.Empty(after);

            // The reaction ROW is gone — the one hard delete in this platform, because a withdrawn reaction is
            // not history anybody needs. The AUDIT row records that it happened, which is the part that matters.
            using var fresh = _host.NewContext();
            Assert.Equal(0, await fresh.Set<CommReaction>().CountAsync(r => r.CommentId == added.CommentId));
            Assert.NotEmpty(await fresh.Set<CommAuditEntry>().AsNoTracking()
                .Where(a => a.Action == CommAuditActions.ReactionRemoved).ToListAsync());
        }

        // ============================================================================================
        // READ STATUS (module 14)
        // ============================================================================================

        // Unread is counted against what the caller MAY READ and excludes their own comments: a badge that can
        // never be cleared is worse than no badge.
        [Fact]
        public async Task Unread_counts_only_readable_comments_by_other_people()
        {
            var authorContext = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments(permissions: ViewOnly());

            var mine = await comments.AddAsync(authorContext, _host.CommentOn(entity, "mine"));
            await comments.AddAsync(
                CommunicationTestHost.Context(CommunicationTestHost.Colleague),
                _host.CommentOn(entity, "theirs", threadId: mine.ThreadId));

            var status = await _host.ReadStatus(permissions: ViewOnly()).GetAsync(authorContext, mine.ThreadId);

            Assert.Equal(1, status.UnreadCount);        // only the colleague's comment
        }

        // THE WATERMARK ONLY MOVES FORWARD. A stale page reporting an older comment id must not resurrect
        // notifications the user has already dismissed.
        [Fact]
        public async Task The_read_watermark_never_moves_backwards()
        {
            var authorContext = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments(permissions: ViewOnly());

            var first = await comments.AddAsync(authorContext, _host.CommentOn(entity, "one"));
            var second = await comments.AddAsync(
                CommunicationTestHost.Context(CommunicationTestHost.Colleague),
                _host.CommentOn(entity, "two", threadId: first.ThreadId));

            var readStatus = _host.ReadStatus(permissions: ViewOnly());

            var forward = await readStatus.MarkReadAsync(authorContext, first.ThreadId, second.CommentId);
            Assert.Equal(second.CommentId, forward.LastReadCommentId);

            var backward = await readStatus.MarkReadAsync(authorContext, first.ThreadId, first.CommentId);
            Assert.Equal(second.CommentId, backward.LastReadCommentId);
        }

        // ============================================================================================
        // ATTACHMENTS AND PREVIEW (modules 7, 11)
        // ============================================================================================
        [Fact]
        public async Task An_attachment_is_stored_by_reference_and_classified_for_preview()
        {
            var context = CommunicationTestHost.Context();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(
                CommunicationTestHost.Invoice(), "see attached",
                attachments: new[]
                {
                    new CommAttachmentRequest
                    {
                        FileName = "scan.pdf", ContentType = "application/pdf",
                        SizeBytes = 1024, StorageKey = "files/2026/scan.pdf",
                    },
                }));

            var attachment = Assert.Single(added.Attachments);
            Assert.Equal("scan.pdf", attachment.FileName);
            Assert.Equal("files/2026/scan.pdf", attachment.StorageKey);
            Assert.Equal(CommPreviewKind.Pdf, attachment.Preview.Kind);
            Assert.True(attachment.Preview.CanInline);
        }

        // An SVG is an XML document that can carry script. It classifies as an image so a UI shows an image icon,
        // but inlining one from a comment is stored XSS — so CanInline is false with the reason recorded.
        [Fact]
        public void An_svg_classifies_as_an_image_but_is_never_inlineable()
        {
            var preview = new DefaultCommFilePreviewProvider().Classify("logo.svg", "image/svg+xml", 2048);

            Assert.Equal(CommPreviewKind.Image, preview.Kind);
            Assert.False(preview.CanInline);
            Assert.Contains("script", preview.Reason);
        }

        // Fail closed: an unrecognised content type is exactly the case where guessing is most likely to inline
        // something executable.
        [Fact]
        public void An_unknown_content_type_is_not_inlineable()
        {
            var preview = new DefaultCommFilePreviewProvider().Classify("thing.bin", "application/x-msdownload", 10);

            Assert.Equal(CommPreviewKind.None, preview.Kind);
            Assert.False(preview.CanInline);
        }

        // A display name like "..\..\web.config" is a phishing aid and must never be mistaken for a path.
        [Fact]
        public async Task An_attachment_file_name_is_stripped_of_path_separators()
        {
            var context = CommunicationTestHost.Context();

            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(
                CommunicationTestHost.Invoice(), "x",
                attachments: new[]
                {
                    new CommAttachmentRequest
                    {
                        FileName = @"..\..\web.config", ContentType = "text/plain",
                        SizeBytes = 10, StorageKey = "k",
                    },
                }));

            var attachment = Assert.Single(added.Attachments);
            Assert.DoesNotContain(@"\", attachment.FileName);
            Assert.DoesNotContain("/", attachment.FileName);
        }

        [Fact]
        public async Task An_attachment_with_no_storage_key_is_refused_because_this_platform_receives_no_bytes()
        {
            var context = CommunicationTestHost.Context();

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                _host.Comments(permissions: ViewOnly()).AddAsync(context, _host.CommentOn(
                    CommunicationTestHost.Invoice(), "x",
                    attachments: new[]
                    {
                        new CommAttachmentRequest
                        {
                            FileName = "a.txt", ContentType = "text/plain", SizeBytes = 1, StorageKey = "",
                        },
                    })));

            Assert.Equal(CommValidationException.Codes.AttachmentStorageKeyRequired, ex.Code);
        }

        // ============================================================================================
        // AUDIT (module 31) AND EVENTS (26, 27)
        // ============================================================================================

        // A StorageKey is capability-bearing, and an audit row is read by more people than the file is. The
        // writer ENFORCES this rather than documenting it.
        [Fact]
        public void An_audit_detail_containing_a_storage_key_or_a_body_is_refused()
        {
            var audit = _host.Audit();

            Assert.Throws<InvalidOperationException>(() => audit.Append(new CommAuditRequest
            {
                Action = CommAuditActions.AttachmentAdded,
                Entity = CommunicationTestHost.Invoice(),
                CompanyId = CommunicationTestHost.CompanyId,
                Detail = new { storageKey = "files/secret.pdf" },
            }));

            Assert.Throws<InvalidOperationException>(() => audit.Append(new CommAuditRequest
            {
                Action = CommAuditActions.CommentAdded,
                Entity = CommunicationTestHost.Invoice(),
                CompanyId = CommunicationTestHost.CompanyId,
                Detail = new { body = "the whole comment" },
            }));
        }

        [Fact]
        public void An_audit_row_with_an_unknown_action_is_refused()
        {
            Assert.Throws<InvalidOperationException>(() => _host.Audit().Append(new CommAuditRequest
            {
                Action = "NotAnAction",
                Entity = CommunicationTestHost.Invoice(),
                CompanyId = CommunicationTestHost.CompanyId,
            }));
        }

        // Every CommEventTypes value must have an audit action. An unmapped one THROWS rather than defaulting, so
        // adding an event without deciding how it is audited fails at the first call.
        [Fact]
        public void Every_communication_event_type_maps_to_an_audit_action()
        {
            foreach (var eventType in CommEventTypes.Values)
            {
                var action = CommAuditWriter.ActionForEvent(eventType);
                Assert.True(CommAuditActions.IsValid(action), $"{eventType} maps to '{action}'");
            }
        }

        // The bridge is OFF by default, so nothing is forwarded — but the audit row is ALWAYS written. That
        // split is what makes the platform usable without the kernel's schema.
        [Fact]
        public async Task With_the_bridge_off_nothing_is_forwarded_but_everything_is_audited()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), "text"));

            Assert.Empty(_host.Bridge.Forwarded);

            using var fresh = _host.NewContext();
            Assert.NotEmpty(await fresh.Set<CommAuditEntry>().AsNoTracking()
                .Where(a => a.Action == CommAuditActions.CommentAdded).ToListAsync());
        }

        // With the bridge ON, a bridgeable event is forwarded and a deliberately-unbridgeable one is not.
        [Fact]
        public async Task With_the_bridge_on_only_bridgeable_events_are_forwarded()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                BridgeToBusinessEvents = true,
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            var context = CommunicationTestHost.Context();
            var added = await host.Comments(permissions: ViewOnly()).AddAsync(
                context, host.CommentOn(CommunicationTestHost.Invoice(), "text"));

            await host.Reactions(permissions: ViewOnly()).AddAsync(context, added.CommentId, CommReactionKeys.Like);

            var forwarded = host.Bridge.Forwarded.Select(e => e.EventType).ToList();

            Assert.Contains(CommEventTypes.CommentAdded, forwarded);
            Assert.Contains(CommEventTypes.ThreadCreated, forwarded);

            // A reaction is a signal, not a business fact — bridging thousands a day into the largest table in
            // the database buys nothing.
            Assert.DoesNotContain(CommEventTypes.ReactionAdded, forwarded);
        }

        // Public collapses to Internal when bridged: the kernel has no external tier, and narrowing is the safe
        // direction. Adding a value to the kernel's frozen set from outside the kernel is not an option.
        [Fact]
        public void Communication_visibility_maps_downward_onto_the_kernels_frozen_set()
        {
            Assert.Equal(BusinessEventVisibility.Internal, CommVisibility.ToBusinessEventVisibility(CommVisibility.Public));
            Assert.Equal(BusinessEventVisibility.Internal, CommVisibility.ToBusinessEventVisibility(CommVisibility.Internal));
            Assert.Equal(BusinessEventVisibility.Confidential, CommVisibility.ToBusinessEventVisibility(CommVisibility.Confidential));
            Assert.Equal(BusinessEventVisibility.Restricted, CommVisibility.ToBusinessEventVisibility(CommVisibility.Restricted));

            // Every mapped value must be one the kernel actually accepts.
            foreach (var visibility in CommVisibility.Values)
                Assert.True(BusinessEventVisibility.IsValid(CommVisibility.ToBusinessEventVisibility(visibility)));
        }

        // A bridged event type must satisfy the kernel's own grammar validator: "<EntityCode>.<PascalAction>".
        // If it did not, RecordAsync would throw inside the caller's transaction.
        [Fact]
        public void Every_bridgeable_event_produces_a_kernel_event_type_the_kernel_accepts()
        {
            foreach (var eventType in CommEventTypes.Values.Where(CommEventTypes.IsBridgeable))
            {
                var action = CommEventTypes.KernelActionFor(eventType)!;
                var kernelType = BusinessEventTypes.Build(EntityRegistry.SalesInvoice, action);

                Assert.True(
                    BusinessEventTypes.TryValidate(kernelType, EntityRegistry.SalesInvoice, out var error),
                    $"{eventType} -> {kernelType}: {error}");
            }
        }

        // A correlation id links a comm audit row to the kernel event row from the same request, so an
        // investigation can follow one operation across both logs.
        [Fact]
        public async Task Audit_rows_carry_the_requests_correlation_id()
        {
            var context = CommunicationTestHost.Context();
            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), "text"));

            using var fresh = _host.NewContext();
            var rows = await fresh.Set<CommAuditEntry>().AsNoTracking().ToListAsync();

            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(context.CorrelationId, r.CorrelationId));
        }
    }

    // A source that fails, to prove one broken contributor does not blank a record's history.
    internal sealed class ThrowingTimelineSource : ICommTimelineSource
    {
        public string Source => CommTimelineSourceKinds.BusinessEvent;

        public Task<CommTimelineSourceResult> GetAsync(
            BusinessContext context, CommEntityRef entity, DateTime? before, int take, bool includeDeleted,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("source is broken");
    }
}
