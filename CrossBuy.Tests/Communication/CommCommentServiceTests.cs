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
    // Communication Platform — COMMENTS: the engine, its edit history, its soft delete, its idempotency.
    //
    // Covers modules 1 (Comments Engine), 2 (Discussion Threads), 8/9 (Internal + Public Notes),
    // 28 (Version History), 29 (Soft Delete), 30 (Edit History), and the ONE-TRANSACTION invariant that the
    // whole platform rests on.
    //
    // Every assertion RE-READS FROM THE DATABASE through a fresh context where it matters, rather than trusting
    // the entity that wrote it — CLAUDE.md: "Prove from a new context."
    // =============================================================================================
    public class CommCommentServiceTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();
        public void Dispose() => _host.Dispose();

        // ---------------------------------------------------------------------------------------------
        // Posting
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task Adding_a_comment_creates_the_thread_and_stores_the_body_verbatim()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var dto = await _host.Comments().AddAsync(context, _host.CommentOn(entity, "**check** the totals"));

            Assert.True(dto.CommentId > 0);
            Assert.Equal("**check** the totals", dto.Body);      // stored as AUTHORED, never pre-rendered
            Assert.Equal(CommBodyFormat.Markdown, dto.BodyFormat);
            Assert.Equal(CommunicationTestHost.Author, dto.Author.EmployeeId);
            Assert.Equal(1, dto.Depth);

            // Prove from a NEW context, not from the DTO the writer produced.
            using var fresh = _host.NewContext();
            var stored = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == dto.CommentId);
            Assert.Equal("**check** the totals", stored.Body);
            Assert.Equal(CommunicationTestHost.CompanyId, stored.CompanyID);
            Assert.Equal(entity.EntityCode, stored.EntityType);
            Assert.Equal(entity.EntityId, stored.EntityId);
        }

        // The thread's UNIQUE anchor index is what makes get-or-create safe. Two comments on one record must
        // share one thread, or the conversation is permanently split in half.
        [Fact]
        public async Task Two_comments_on_the_same_record_share_one_thread()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var first = await comments.AddAsync(context, _host.CommentOn(entity, "one"));
            var second = await comments.AddAsync(context, _host.CommentOn(entity, "two"));

            Assert.Equal(first.ThreadId, second.ThreadId);

            using var fresh = _host.NewContext();
            var threads = await fresh.Set<CommThread>().AsNoTracking()
                .Where(t => t.EntityType == entity.EntityCode && t.EntityId == entity.EntityId)
                .ToListAsync();
            Assert.Single(threads);
            Assert.Equal(2, threads[0].CommentCount);
        }

        // Module 2: a NAMED thread key is a second, independent conversation on the same record — which is how
        // discussion threads exist without a second table.
        [Fact]
        public async Task A_named_thread_key_creates_a_second_independent_thread_on_the_same_record()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var main = await comments.AddAsync(context, _host.CommentOn(entity, "general"));

            var topic = await comments.AddAsync(context, new CommCommentRequest
            {
                Entity = entity,
                ThreadKind = CommThreadKind.Discussion,
                ThreadKey = "pricing-dispute",
                Body = "about the discount",
            });

            Assert.NotEqual(main.ThreadId, topic.ThreadId);
        }

        // Modules 8 and 9: internal and public notes are the SAME table, separated by Visibility.
        [Fact]
        public async Task Internal_and_public_notes_are_one_table_separated_by_visibility()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var thread = await _host.Threads().GetOrCreateAsync(context, new CommThreadRequest
            {
                Entity = entity,
                Kind = CommThreadKind.Notes,

                // The thread's CEILING is Public, so both tiers are postable in it.
                Visibility = CommVisibility.Public,
            });

            var publicNote = await comments.AddAsync(context, new CommCommentRequest
            {
                Entity = entity, ThreadId = thread.Id, Body = "visible to the customer",
                Visibility = CommVisibility.Public,
            });
            var internalNote = await comments.AddAsync(context, new CommCommentRequest
            {
                Entity = entity, ThreadId = thread.Id, Body = "staff only",
                Visibility = CommVisibility.Internal,
            });

            Assert.Equal(CommVisibility.Public, publicNote.Visibility);
            Assert.Equal(CommVisibility.Internal, internalNote.Visibility);
            Assert.Equal(thread.Id, publicNote.ThreadId);
            Assert.Equal(thread.Id, internalNote.ThreadId);
        }

        // A comment may never be MORE OPEN than its thread: the ceiling is the promise made to whoever opened
        // the thread at that tier.
        [Fact]
        public async Task A_comment_more_open_than_its_thread_is_refused()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads().GetOrCreateAsync(context, new CommThreadRequest
            {
                Entity = entity, Kind = CommThreadKind.Notes, Visibility = CommVisibility.Restricted,
            });

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                _host.Comments().AddAsync(context, new CommCommentRequest
                {
                    Entity = entity, ThreadId = thread.Id, Body = "oops",
                    Visibility = CommVisibility.Public,
                }));

            Assert.Equal(CommValidationException.Codes.VisibilityInvalid, ex.Code);
        }

        // A caller could otherwise post to any thread in their company by id while naming a different anchor,
        // leaving the comment's denormalised EntityType/EntityId disagreeing with its thread's.
        [Fact]
        public async Task A_thread_id_from_a_different_record_is_refused()
        {
            var context = CommunicationTestHost.Context();
            var invoiceThread = await _host.Threads().GetOrCreateAsync(
                context, new CommThreadRequest { Entity = CommunicationTestHost.Invoice(1001) });

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                _host.Comments().AddAsync(context, new CommCommentRequest
                {
                    Entity = CommunicationTestHost.Invoice(9999),   // a DIFFERENT record
                    ThreadId = invoiceThread.Id,
                    Body = "grafted",
                }));

            Assert.Equal(CommValidationException.Codes.EntityRefInvalid, ex.Code);
        }

        [Fact]
        public async Task Supplying_both_a_thread_id_and_a_thread_kind_is_refused_rather_than_guessed()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var thread = await _host.Threads().GetOrCreateAsync(context, new CommThreadRequest { Entity = entity });

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                _host.Comments().AddAsync(context, new CommCommentRequest
                {
                    Entity = entity, ThreadId = thread.Id, ThreadKind = CommThreadKind.Notes, Body = "x",
                }));

            Assert.Equal(CommValidationException.Codes.ThreadKindInvalid, ex.Code);
        }

        // ---------------------------------------------------------------------------------------------
        // The collaboration surface (ADR-031)
        // ---------------------------------------------------------------------------------------------

        // PosOrder does NOT carry SupportsComments in IEntityRegistry. It works here purely because the
        // deployment listed it in EnabledEntityCodes — which is the whole mechanism behind modules 36-43.
        [Fact]
        public async Task An_entity_onboarded_only_by_configuration_accepts_comments()
        {
            var context = CommunicationTestHost.Context();

            var dto = await _host.Comments().AddAsync(
                context, _host.CommentOn(CommunicationTestHost.PosOrder(), "customer complained"));

            Assert.True(dto.CommentId > 0);

            // And the platform REPORTS the divergence rather than hiding it: the registry flag is still false.
            var gap = _host.Surface().GetOnboardingGap();
            Assert.Contains(gap, g => g.EntityCode == EntityRegistry.PosOrder);
        }

        [Fact]
        public async Task An_entity_that_is_neither_registered_nor_configured_is_refused()
        {
            var context = CommunicationTestHost.Context();

            var ex = await Assert.ThrowsAsync<CommEntityNotSupportedException>(() =>
                _host.Comments().AddAsync(context, _host.CommentOn(new CommEntityRef("Item", 5), "x")));

            Assert.Equal("Item", ex.EntityCode);
        }

        [Fact]
        public async Task An_unregistered_entity_code_is_refused_even_when_configuration_lists_it()
        {
            // Configuration may ONBOARD a registered entity; it may never invent one. That is the ADR-002 rule
            // the allow-list does not get to override.
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                EnabledEntityCodes = new List<string> { "TotallyMadeUp" },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            await Assert.ThrowsAsync<CommEntityNotSupportedException>(() =>
                host.Comments().AddAsync(
                    CommunicationTestHost.Context(), host.CommentOn(new CommEntityRef("TotallyMadeUp", 1), "x")));
        }

        [Fact]
        public async Task A_blocked_entity_is_refused_even_when_the_registry_supports_comments()
        {
            // Deny wins outright, so a deployment can pull one entity out of the surface without a code change.
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                BlockedEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            await Assert.ThrowsAsync<CommEntityNotSupportedException>(() =>
                host.Comments().AddAsync(
                    CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "x")));
        }

        // ---------------------------------------------------------------------------------------------
        // Idempotency (module 1's mobile-client requirement)
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_retried_post_with_the_same_dedup_key_returns_the_original_comment()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var first = await comments.AddAsync(context, _host.CommentOn(entity, "once", dedupKey: "req-abc"));
            var retry = await comments.AddAsync(context, _host.CommentOn(entity, "once", dedupKey: "req-abc"));

            Assert.Equal(first.CommentId, retry.CommentId);

            using var fresh = _host.NewContext();
            Assert.Equal(1, await fresh.Set<CommComment>().CountAsync(c => c.DedupKey == "req-abc"));
        }

        // ---------------------------------------------------------------------------------------------
        // Replies
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_reply_records_its_parent_and_its_depth()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var parent = await comments.AddAsync(context, _host.CommentOn(entity, "question?"));
            var reply = await comments.AddAsync(
                context, _host.CommentOn(entity, "answer", threadId: parent.ThreadId, parentCommentId: parent.CommentId));

            Assert.Equal(parent.CommentId, reply.ParentCommentId);
            Assert.Equal(2, reply.Depth);

            using var fresh = _host.NewContext();
            var storedParent = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == parent.CommentId);
            Assert.Equal(1, storedParent.ReplyCount);
        }

        [Fact]
        public async Task A_reply_beyond_the_depth_limit_is_refused()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                MaxReplyDepth = 2,
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = host.Comments();

            var root = await comments.AddAsync(context, host.CommentOn(entity, "one"));
            var reply = await comments.AddAsync(
                context, host.CommentOn(entity, "two", threadId: root.ThreadId, parentCommentId: root.CommentId));

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                comments.AddAsync(
                    context, host.CommentOn(entity, "three", threadId: root.ThreadId, parentCommentId: reply.CommentId)));

            Assert.Equal(CommValidationException.Codes.ReplyDepthExceeded, ex.Code);
        }

        // A reply grafted onto a comment in ANOTHER thread would make the reply's thread-level authorization
        // answer about the wrong thread.
        [Fact]
        public async Task A_reply_to_a_comment_in_another_thread_is_refused()
        {
            var context = CommunicationTestHost.Context();
            var comments = _host.Comments();

            var a = await comments.AddAsync(context, _host.CommentOn(CommunicationTestHost.Invoice(1001), "a"));
            var b = await comments.AddAsync(context, _host.CommentOn(CommunicationTestHost.Invoice(1002), "b"));

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                comments.AddAsync(context, new CommCommentRequest
                {
                    Entity = CommunicationTestHost.Invoice(1002),
                    ThreadId = b.ThreadId,
                    ParentCommentId = a.CommentId,       // a comment from the OTHER thread
                    Body = "grafted",
                }));

            Assert.Equal(CommValidationException.Codes.ReplyParentMismatch, ex.Code);
        }

        // ---------------------------------------------------------------------------------------------
        // Edit history (modules 28, 30)
        // ---------------------------------------------------------------------------------------------

        // THE REVISION HOLDS THE PREVIOUS TEXT. Storing the new body would duplicate the current comment and
        // leave the original unrecoverable — the opposite of an edit history.
        [Fact]
        public async Task Editing_writes_a_revision_holding_the_previous_body()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "original text"));
            var edited = await comments.EditAsync(context, new CommCommentEditRequest
            {
                CommentId = added.CommentId, Body = "corrected text", Reason = "typo",
            });

            Assert.Equal("corrected text", edited.Body);
            Assert.Equal(1, edited.RevisionCount);
            Assert.NotNull(edited.EditedAt);

            var revisions = await comments.GetRevisionsAsync(context, added.CommentId);
            var revision = Assert.Single(revisions);
            Assert.Equal(1, revision.RevisionNo);
            Assert.Equal("original text", revision.Body);        // the PREVIOUS body
            Assert.Equal("typo", revision.Reason);
        }

        [Fact]
        public async Task A_second_edit_appends_a_second_revision_and_the_chain_reconstructs_the_history()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "v1"));
            await comments.EditAsync(context, new CommCommentEditRequest { CommentId = added.CommentId, Body = "v2" });
            var final = await comments.EditAsync(context, new CommCommentEditRequest { CommentId = added.CommentId, Body = "v3" });

            var revisions = await comments.GetRevisionsAsync(context, added.CommentId);

            Assert.Equal(2, revisions.Count);
            Assert.Equal(new[] { "v1", "v2" }, revisions.Select(r => r.Body).ToArray());
            Assert.Equal("v3", final.Body);
            Assert.Equal(2, final.RevisionCount);
        }

        // A no-op edit must write NOTHING. Otherwise an accidental double-submit inflates the history with
        // identical entries and makes the real edits harder to find.
        [Fact]
        public async Task An_edit_that_changes_nothing_writes_no_revision()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "same"));
            var again = await comments.EditAsync(context, new CommCommentEditRequest
            {
                CommentId = added.CommentId, Body = "same",
            });

            Assert.Equal(0, again.RevisionCount);
            Assert.Empty(await comments.GetRevisionsAsync(context, added.CommentId));
        }

        // A visibility change is an edit that matters MORE than a wording change, so it must be recorded in the
        // same place — an auditor reading the history needs to see it.
        [Fact]
        public async Task Changing_only_the_visibility_is_recorded_as_a_revision()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "text", CommVisibility.Internal));
            var edited = await comments.EditAsync(context, new CommCommentEditRequest
            {
                CommentId = added.CommentId, Body = "text", Visibility = CommVisibility.Restricted,
            });

            Assert.Equal(CommVisibility.Restricted, edited.Visibility);
            Assert.Equal(1, edited.RevisionCount);

            var revision = Assert.Single(await comments.GetRevisionsAsync(context, added.CommentId));
            Assert.Equal("text", revision.Body);
        }

        // THE EDIT WINDOW. After it closes, editing is a moderator act — so the change is attributable to a
        // role rather than being a silent rewrite of something people relied on.
        [Fact]
        public async Task An_author_cannot_edit_after_the_window_closes()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                AuthorEditWindowMinutes = 0,          // the window is closed the instant the comment exists
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            var context = CommunicationTestHost.Context();
            var added = await host.Comments().AddAsync(
                context, host.CommentOn(CommunicationTestHost.Invoice(), "original"));

            // Backdate so "now - created" is unambiguously past a zero-minute window.
            using (var arrange = host.NewContext())
            {
                var row = await arrange.Set<CommComment>().SingleAsync(c => c.Id == added.CommentId);
                row.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
                await arrange.SaveChangesAsync();
            }

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                host.Comments().EditAsync(context, new CommCommentEditRequest
                {
                    CommentId = added.CommentId, Body = "sneaky rewrite",
                }));

            Assert.Equal(CommValidationException.Codes.EditWindowClosed, ex.Code);
        }

        // ---------------------------------------------------------------------------------------------
        // Soft delete (module 29)
        // ---------------------------------------------------------------------------------------------

        // "Reverse, never delete" governs FINANCIAL history. A comment's correction primitive is a revision plus
        // a soft-delete marker — both append-only, and the row survives so revisions and replies stay intact.
        [Fact]
        public async Task Deleting_a_comment_keeps_the_row_and_its_revisions()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "v1"));
            await comments.EditAsync(context, new CommCommentEditRequest { CommentId = added.CommentId, Body = "v2" });

            Assert.True(await comments.DeleteAsync(context, added.CommentId, "off topic"));

            using var fresh = _host.NewContext();
            var row = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == added.CommentId);
            Assert.NotNull(row.DeletedAt);
            Assert.Equal("v2", row.Body);        // the text is still there — the row is marked, not erased
            Assert.Equal(1, await fresh.Set<CommCommentRevision>().CountAsync(r => r.CommentId == added.CommentId));
        }

        // An ordinary reader must not see a gap where a row used to be; a moderator must see the row. Both
        // follow from the same query, which is why the moderator flag is in the predicate.
        [Fact]
        public async Task A_deleted_comment_is_hidden_from_a_reader_and_visible_but_blanked_to_a_moderator()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "secret wording"));
            await comments.DeleteAsync(context, added.CommentId);

            // Ordinary reader: the row is not in the page at all.
            var asReader = await comments.ListAsync(context, added.ThreadId);
            Assert.DoesNotContain(asReader.Items, c => c.CommentId == added.CommentId);

            // Moderator (holds ViewRestricted, which is what grants moderation): the row appears, BLANKED.
            var moderator = _host.Comments(permissions: new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewRestricted));
            var asModerator = await moderator.ListAsync(context, added.ThreadId);

            var seen = Assert.Single(asModerator.Items, c => c.CommentId == added.CommentId);
            Assert.True(seen.IsDeleted);
            Assert.Equal("", seen.Body);          // existence, not content — the text is in the revisions
        }

        // Only a moderator restores. An author who could restore their own deletion could use delete to hide a
        // comment from a review and bring it back afterwards.
        [Fact]
        public async Task Only_a_moderator_can_restore_a_deleted_comment()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "text"));
            await comments.DeleteAsync(context, added.CommentId);

            await Assert.ThrowsAsync<CommAccessDeniedException>(() =>
                comments.RestoreAsync(context, added.CommentId));

            var moderator = _host.Comments(permissions: new StubPermissionProvider(
                PlatformActions.View, PlatformActions.ViewRestricted));
            Assert.True(await moderator.RestoreAsync(context, added.CommentId));

            using var fresh = _host.NewContext();
            var row = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == added.CommentId);
            Assert.Null(row.DeletedAt);
        }

        // An author may always RETRACT their own comment, window or not: withdrawing something you said is not
        // the same act as rewriting it.
        [Fact]
        public async Task An_author_may_delete_their_own_comment_after_the_edit_window_closes()
        {
            using var host = new CommunicationTestHost(new CommunicationPlatformOptions
            {
                AuthorEditWindowMinutes = 0,
                EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
                EnabledChannels = new List<string> { CommChannel.InApp },
            });

            var context = CommunicationTestHost.Context();
            var added = await host.Comments().AddAsync(
                context, host.CommentOn(CommunicationTestHost.Invoice(), "retract me"));

            using (var arrange = host.NewContext())
            {
                var row = await arrange.Set<CommComment>().SingleAsync(c => c.Id == added.CommentId);
                row.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
                await arrange.SaveChangesAsync();
            }

            Assert.True(await host.Comments().DeleteAsync(context, added.CommentId));
        }

        // ---------------------------------------------------------------------------------------------
        // Locking
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_locked_thread_still_reads_but_accepts_no_new_comment()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            var added = await comments.AddAsync(context, _host.CommentOn(entity, "before the lock"));

            var moderatorPermissions = new StubPermissionProvider(PlatformActions.View, PlatformActions.ViewRestricted);
            await _host.Threads(permissions: moderatorPermissions)
                .SetLockedAsync(context, added.ThreadId, locked: true, reason: "resolved");

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                comments.AddAsync(context, _host.CommentOn(entity, "after the lock", threadId: added.ThreadId)));
            Assert.Equal(CommValidationException.Codes.ThreadLocked, ex.Code);

            // Reading still works — locking ends a conversation, it does not hide one.
            var page = await comments.ListAsync(context, added.ThreadId);
            Assert.Contains(page.Items, c => c.CommentId == added.CommentId);
        }

        // ---------------------------------------------------------------------------------------------
        // Company isolation — these tables carry NO global query filter, so the explicit predicate is the
        // only thing standing between two tenants.
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_comment_is_invisible_to_another_company()
        {
            var entity = CommunicationTestHost.Invoice();

            var added = await _host.Comments().AddAsync(
                CommunicationTestHost.Context(), _host.CommentOn(entity, "company one only"));

            // A real second request scope: its own holder, its own context — the arrangement that makes this a
            // genuine cross-tenant read rather than a passed-in company id.
            var other = _host.Request(CommunicationTestHost.OtherCompanyId);
            var otherContext = CommunicationTestHost.Context(
                CommunicationTestHost.OtherCompanyEmployee, CommunicationTestHost.OtherCompanyId);

            // Absent and other-company answer IDENTICALLY, so a thread id cannot be used as an existence oracle.
            await Assert.ThrowsAsync<CommNotFoundException>(() =>
                _host.Comments(other.Db).GetAsync(otherContext, added.CommentId));

            await Assert.ThrowsAsync<CommNotFoundException>(() =>
                _host.Comments(other.Db).ListAsync(otherContext, added.ThreadId));

            other.Db.Dispose();
        }

        // ---------------------------------------------------------------------------------------------
        // Body analysis is stored, not recomputed on read (module 10)
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task The_body_analysis_is_stored_at_write_time_and_returned_on_read()
        {
            var context = CommunicationTestHost.Context();
            var body = "# Title\n\n[link](https://x.test) ![img](https://y.test/a.png)\n\n```\ncode\n```";

            var added = await _host.Comments().AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), body));

            var read = await _host.Comments().GetAsync(context, added.CommentId);

            Assert.NotNull(read.BodyAnalysis);
            Assert.Equal(1, read.BodyAnalysis!.LinkCount);
            Assert.Equal(1, read.BodyAnalysis.ImageCount);
            Assert.Equal(1, read.BodyAnalysis.CodeBlockCount);
            Assert.True(read.BodyAnalysis.HasHeading);
        }

        // ---------------------------------------------------------------------------------------------
        // Paging
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_thread_pages_oldest_first_with_a_stable_cursor()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var comments = _host.Comments();

            long threadId = 0;
            for (int i = 1; i <= 5; i++)
            {
                var dto = await comments.AddAsync(context, _host.CommentOn(entity, "comment " + i, threadId: threadId == 0 ? null : threadId));
                threadId = dto.ThreadId;
            }

            var first = await comments.ListAsync(context, threadId, new CommPageRequest { PageSize = 2 });
            Assert.Equal(2, first.Items.Count);
            Assert.True(first.HasMore);
            Assert.Equal("comment 1", first.Items[0].Body);   // a conversation reads downwards

            var second = await comments.ListAsync(
                context, threadId, new CommPageRequest { PageSize = 2, AfterId = first.NextCursor });
            Assert.Equal("comment 3", second.Items[0].Body);
            Assert.DoesNotContain(second.Items, c => first.Items.Any(f => f.CommentId == c.CommentId));
        }
    }
}
