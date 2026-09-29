using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Portal;
using CrossBuy.Controllers;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // WAVE 3B-C — PORTAL ↔ COMMUNICATION AUTHORIZATION. Security matrix C31-C60.
    //
    // These exercise the REAL CommPortalService over a real SQLite database, with the REAL
    // CommExternalAccessPolicy underneath it. Nothing about the authority decision is stubbed — the
    // only thing constructed by hand is the PortalContext, because the authentication half was
    // proven in 3A-0B and what is under test here is what Communication does with an identity that
    // has already been established.
    //
    // NINETEEN OF THESE ASSERT A REFUSAL, so the controls come first and the rest depend on them.
    // C31 and C39 assert the ALLOW: a service that returned nothing to everyone would pass every
    // denial in this file and prove nothing. This session has already shipped one authorization test
    // that passed because no principal existed at all; the controls exist so that cannot recur.
    // =============================================================================================

    internal sealed class PortalConversationHost : IDisposable
    {
        public const int CompanyA = 1, CompanyB = 2;
        public const int CustomerAcme = 100, CustomerBeta = 200;
        public const int PortalAcme = 10, PortalBeta = 20;

        private readonly ExternalActorHost _inner = new();

        public CrossBuy.Models.Context.CrossDbContext Db => _inner.Db;

        public CommPortalService Service() => new(
            Db,
            new CommExternalAccessPolicy(Db),
            new CommBodyPolicy(Options.Create(new CommunicationPlatformOptions())),
            Options.Create(new CommunicationPlatformOptions()));

        public PortalContext Portal(int portalUserId = PortalAcme, int customerId = CustomerAcme, int companyId = CompanyA)
            => new(companyId, customerId, portalUserId, PortalCapabilities.Default);

        public CommThread SeedThread(int customerId, int companyId, string visibility = CommVisibility.Public)
            => _inner.SeedPublicThread(customerId, companyId, visibility);

        public CommParticipant AddPortal(CommThread t, int portalUserId, bool removed = false)
            => _inner.AddPortalParticipant(t, portalUserId, removed);

        public CommParticipant AddEmployee(CommThread t, int employeeId)
            => _inner.AddEmployeeParticipant(t, employeeId);

        public CommComment SeedComment(CommThread t, CommActor author, string visibility)
            => _inner.SeedComment(t, author, visibility);

        /// A thread this portal user may fully use: right company, right customer, live participant,
        /// Public. Every refusal test below starts from this and breaks exactly ONE condition.
        public CommThread SeedUsableThread()
        {
            var t = SeedThread(CustomerAcme, CompanyA);
            AddPortal(t, PortalAcme);
            return t;
        }

        public void Dispose() => _inner.Dispose();
    }

    // =============================================================================================
    public class CommPortalServiceTests
    {
        // ---- CONTROLS. Read these before trusting any refusal below. -------------------------------

        [Fact] // C31
        public async Task C31_a_valid_portal_participant_lists_the_permitted_thread()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();

            var list = await host.Service().ListConversationsAsync(host.Portal());

            var one = Assert.Single(list);
            Assert.Equal(thread.Id, one.ThreadId);
            Assert.Equal("Quotation", one.EntityType);
            Assert.True(one.CanComment);
        }

        [Fact] // C39
        public async Task C39_an_external_comment_is_readable()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);

            var comments = await host.Service().ListCommentsAsync(host.Portal(), thread.Id);

            var one = Assert.Single(comments);
            Assert.False(one.IsMine);           // an employee wrote it
            Assert.Equal("body", one.Body);
        }

        // ---- LIST / OPEN ---------------------------------------------------------------------------

        [Fact] // C32
        public async Task C32_same_company_but_another_customer_is_not_listed_or_openable()
        {
            using var host = new PortalConversationHost();
            var beta = host.SeedThread(PortalConversationHost.CustomerBeta, PortalConversationHost.CompanyA);
            host.AddPortal(beta, PortalConversationHost.PortalAcme);   // membership granted deliberately

            Assert.Empty(await host.Service().ListConversationsAsync(host.Portal()));
            Assert.Null(await host.Service().GetConversationAsync(host.Portal(), beta.Id));
        }

        [Fact] // C33
        public async Task C33_the_right_customer_but_no_participation_is_not_listed_or_openable()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedThread(PortalConversationHost.CustomerAcme, PortalConversationHost.CompanyA);
            // no participant row: being the customer on the quotation is not membership

            Assert.Empty(await host.Service().ListConversationsAsync(host.Portal()));
            Assert.Null(await host.Service().GetConversationAsync(host.Portal(), thread.Id));
        }

        [Fact] // C34
        public async Task C34_a_removed_participant_is_denied()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedThread(PortalConversationHost.CustomerAcme, PortalConversationHost.CompanyA);
            host.AddPortal(thread, PortalConversationHost.PortalAcme, removed: true);

            Assert.Empty(await host.Service().ListConversationsAsync(host.Portal()));
            Assert.Null(await host.Service().GetConversationAsync(host.Portal(), thread.Id));
        }

        [Fact] // C35
        public async Task C35_another_company_is_denied()
        {
            using var host = new PortalConversationHost();
            var threadB = host.SeedThread(PortalConversationHost.CustomerAcme, PortalConversationHost.CompanyB);
            host.AddPortal(threadB, PortalConversationHost.PortalAcme);

            Assert.Empty(await host.Service().ListConversationsAsync(host.Portal()));
            Assert.Null(await host.Service().GetConversationAsync(host.Portal(), threadB.Id));
        }

        [Fact] // C36 — the list is not an authorization cache
        public async Task C36_direct_thread_id_access_rechecks_authority_on_every_call()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);

            // It IS reachable first, so the change below is what causes the refusal.
            Assert.NotNull(await host.Service().GetConversationAsync(host.Portal(), thread.Id));

            // Membership is revoked AFTER the caller has already seen it listed. Every subsequent
            // entry point must refuse — "it appeared in the list" is not authorization.
            var row = host.Db.Set<CommParticipant>().Single(p => p.ThreadId == thread.Id);
            row.RemovedAt = DateTime.UtcNow;
            host.Db.SaveChanges();

            var portal = host.Portal();
            Assert.Null(await host.Service().GetConversationAsync(portal, thread.Id));
            Assert.Empty(await host.Service().ListCommentsAsync(portal, thread.Id));
            Assert.Empty(await host.Service().ListAttachmentsAsync(portal, thread.Id));
            Assert.Null(await host.Service().PostCommentAsync(portal, thread.Id, "hello"));
            Assert.False(await host.Service().MarkReadAsync(portal, thread.Id));
        }

        [Fact] // C37
        public async Task C37_an_internal_thread_is_denied()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedThread(
                PortalConversationHost.CustomerAcme, PortalConversationHost.CompanyA, CommVisibility.Internal);
            host.AddPortal(thread, PortalConversationHost.PortalAcme);

            Assert.Empty(await host.Service().ListConversationsAsync(host.Portal()));
            Assert.Null(await host.Service().GetConversationAsync(host.Portal(), thread.Id));
        }

        // ---- COMMENTS ------------------------------------------------------------------------------

        [Fact] // C38
        public async Task C38_internal_comments_are_omitted_from_an_externally_readable_thread()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();

            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Confidential);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Restricted);

            var comments = await host.Service().ListCommentsAsync(host.Portal(), thread.Id);

            // Exactly the Public one. The others are not filtered out of the result — they are never
            // materialised, because the predicate is in SQL.
            Assert.Single(comments);
        }

        [Fact] // C40 + C41
        public async Task C40_C41_a_portal_post_writes_the_portal_actor_and_no_employee_id()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();

            var posted = await host.Service().PostCommentAsync(host.Portal(), thread.Id, "from the customer");

            Assert.NotNull(posted);
            Assert.True(posted!.IsMine);

            var row = host.Db.Set<CommComment>().AsNoTracking().Single(c => c.Id == posted.CommentId);
            Assert.Equal(PortalConversationHost.PortalAcme, row.AuthorPortalUserId);
            Assert.Null(row.AuthorEmployeeId);                       // C41 — and NOT zero
            Assert.Equal(CommActor.PortalUser(PortalConversationHost.PortalAcme), row.Author);
        }

        [Fact] // C45
        public async Task C45_a_portal_post_cannot_carry_internal_visibility()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();

            var posted = await host.Service().PostCommentAsync(host.Portal(), thread.Id, "hello");

            var row = host.Db.Set<CommComment>().AsNoTracking().Single(c => c.Id == posted!.CommentId);
            Assert.Equal(CommVisibility.Public, row.Visibility);

            // STRUCTURAL, not merely observed: there is no visibility parameter to pass.
            var post = typeof(ICommPortalService).GetMethod(nameof(ICommPortalService.PostCommentAsync))!;
            Assert.DoesNotContain(post.GetParameters(), p =>
                p.Name!.Contains("visib", StringComparison.OrdinalIgnoreCase));
        }

        [Fact] // C40 — posting also maintains the thread counters, as the internal path does
        public async Task C40_a_portal_post_maintains_the_thread_counters()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            int before = thread.CommentCount;

            await host.Service().PostCommentAsync(host.Portal(), thread.Id, "hello");

            var stored = host.Db.Set<CommThread>().AsNoTracking().Single(t => t.Id == thread.Id);
            Assert.Equal(before + 1, stored.CommentCount);
            Assert.NotNull(stored.LastActivityAt);
        }

        // ---- IDENTITY CANNOT COME FROM THE REQUEST -------------------------------------------------

        [Fact] // C42, C43, C44 — asserted structurally: there is nothing to forge
        public void C42_C43_C44_no_service_or_endpoint_accepts_an_identity_parameter()
        {
            var forbidden = new[] { "companyid", "customerid", "portaluserid", "employeeid" };

            foreach (var method in typeof(ICommPortalService).GetMethods())
                foreach (var p in method.GetParameters())
                    Assert.DoesNotContain(forbidden, f =>
                        string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase));

            foreach (var action in typeof(ClientPortalConversationsController)
                         .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (var p in action.GetParameters())
                    Assert.DoesNotContain(forbidden, f =>
                        string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase));
        }

        [Fact] // C42 — one portal user cannot act as another even holding a valid context of their own
        public async Task C42_a_portal_user_cannot_act_as_a_different_portal_user()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();          // portal 10 is the member

            // Portal 20: same company, same customer, valid context — but not a participant.
            var other = host.Portal(portalUserId: PortalConversationHost.PortalBeta);

            Assert.Null(await host.Service().GetConversationAsync(other, thread.Id));
            Assert.Null(await host.Service().PostCommentAsync(other, thread.Id, "hello"));
            Assert.False(await host.Service().MarkReadAsync(other, thread.Id));
        }

        [Theory] // C43 + C44 — a context naming other values is the strongest possible forgery
        [InlineData(PortalConversationHost.CompanyB, PortalConversationHost.CustomerAcme)]
        [InlineData(PortalConversationHost.CompanyA, PortalConversationHost.CustomerBeta)]
        [InlineData(PortalConversationHost.CompanyB, PortalConversationHost.CustomerBeta)]
        public async Task C43_C44_a_forged_company_or_customer_grants_nothing(int companyId, int customerId)
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();

            var forged = host.Portal(
                portalUserId: PortalConversationHost.PortalAcme, customerId: customerId, companyId: companyId);

            Assert.Empty(await host.Service().ListConversationsAsync(forged));
            Assert.Null(await host.Service().GetConversationAsync(forged, thread.Id));
            Assert.Null(await host.Service().PostCommentAsync(forged, thread.Id, "hello"));
        }

        // ---- READ RECEIPTS -------------------------------------------------------------------------

        [Fact] // C48
        public async Task C48_mark_read_writes_the_portal_actor()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);

            Assert.True(await host.Service().MarkReadAsync(host.Portal(), thread.Id));

            var receipt = host.Db.Set<CommReadReceipt>().AsNoTracking().Single(r => r.ThreadId == thread.Id);
            Assert.Equal(PortalConversationHost.PortalAcme, receipt.PortalUserId);
            Assert.Null(receipt.EmployeeId);
        }

        [Fact] // C49
        public async Task C49_mark_read_requires_thread_authority()
        {
            using var host = new PortalConversationHost();
            var stranger = host.SeedThread(PortalConversationHost.CustomerBeta, PortalConversationHost.CompanyA);

            Assert.False(await host.Service().MarkReadAsync(host.Portal(), stranger.Id));

            // And no row was created — a refusal that still wrote would confirm the thread exists.
            Assert.Empty(host.Db.Set<CommReadReceipt>().Where(r => r.ThreadId == stranger.Id));
        }

        [Fact] // C47
        public async Task C47_employee_7_and_portal_7_keep_separate_watermarks()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedThread(PortalConversationHost.CustomerAcme, PortalConversationHost.CompanyA);
            host.AddPortal(thread, 7);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);

            // The employee's own receipt, written directly as the internal path would.
            host.Db.Set<CommReadReceipt>().Add(new CommReadReceipt
            {
                CompanyID = thread.CompanyID, ThreadId = thread.Id,
                Reader = CommActor.Employee(7), LastReadCommentId = 999,
            });
            host.Db.SaveChanges();

            Assert.True(await host.Service().MarkReadAsync(host.Portal(portalUserId: 7), thread.Id));

            var rows = host.Db.Set<CommReadReceipt>().AsNoTracking().Where(r => r.ThreadId == thread.Id).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(999, rows.Single(r => r.EmployeeId == 7).LastReadCommentId);   // untouched
            Assert.NotNull(rows.Single(r => r.PortalUserId == 7).LastReadCommentId);
        }

        [Fact] // unread is counted over what the EXTERNAL reader may actually read
        public async Task Unread_counts_only_externally_readable_comments()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();

            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Confidential);

            var conversation = await host.Service().GetConversationAsync(host.Portal(), thread.Id);

            // One, not three. A badge counting comments the reader can never open cannot be cleared.
            Assert.Equal(1, conversation!.UnreadCount);
        }

        // ---- ATTACHMENTS ---------------------------------------------------------------------------

        [Fact] // C50
        public async Task C50_attachment_metadata_requires_thread_authority()
        {
            using var host = new PortalConversationHost();
            var stranger = host.SeedThread(PortalConversationHost.CustomerBeta, PortalConversationHost.CompanyA);
            var comment = host.SeedComment(stranger, CommActor.Employee(7), CommVisibility.Public);
            host.Db.Set<CommCommentAttachment>().Add(new CommCommentAttachment
            {
                CompanyID = stranger.CompanyID, ThreadId = stranger.Id, CommentId = comment.Id,
                FileName = "secret.pdf", ContentType = "application/pdf", SizeBytes = 1,
                StorageKey = "k-secret", PreviewKind = CommPreviewKind.Pdf,
                UploadedBy = CommActor.Employee(7),
            });
            host.Db.SaveChanges();

            Assert.Empty(await host.Service().ListAttachmentsAsync(host.Portal(), stranger.Id));
        }

        [Fact] // C53 — there is no static path to bypass, because no key is ever emitted
        public async Task C53_no_storage_key_crosses_the_external_boundary()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            var comment = host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            host.Db.Set<CommCommentAttachment>().Add(new CommCommentAttachment
            {
                CompanyID = thread.CompanyID, ThreadId = thread.Id, CommentId = comment.Id,
                FileName = "quote.pdf", ContentType = "application/pdf", SizeBytes = 10,
                StorageKey = "k-visible", PreviewKind = CommPreviewKind.Pdf,
                UploadedBy = CommActor.Employee(7),
            });
            host.Db.SaveChanges();

            var list = await host.Service().ListAttachmentsAsync(host.Portal(), thread.Id);
            Assert.Single(list);

            // The DTO has no member that could carry a key, path or URL — asserted on the TYPE, so a
            // future field cannot quietly reintroduce one.
            var names = typeof(PortalAttachmentDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
            Assert.DoesNotContain(names, n => n.Contains("storage") || n.Contains("key")
                                              || n.Contains("path") || n.Contains("url") || n.Contains("uri"));
        }

        [Fact] // C38 — an attachment on an INTERNAL comment is unreachable from a readable thread
        public async Task An_attachment_on_an_internal_comment_is_not_externally_listed()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            var internalComment = host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);
            host.Db.Set<CommCommentAttachment>().Add(new CommCommentAttachment
            {
                CompanyID = thread.CompanyID, ThreadId = thread.Id, CommentId = internalComment.Id,
                FileName = "internal.pdf", ContentType = "application/pdf", SizeBytes = 1,
                StorageKey = "k-internal", PreviewKind = CommPreviewKind.Pdf,
                UploadedBy = CommActor.Employee(7),
            });
            host.Db.SaveChanges();

            Assert.Empty(await host.Service().ListAttachmentsAsync(host.Portal(), thread.Id));
        }

        [Fact] // C51 + C52
        public void C51_C52_the_external_surface_offers_no_upload_and_no_delete()
        {
            var members = typeof(ICommPortalService).GetMethods().Select(m => m.Name).ToList();

            // C52: delete is not expressible, matching 3B-B1's deliberate absence of CanDelete.
            Assert.DoesNotContain(members, m => m.Contains("Delete", StringComparison.Ordinal));
            Assert.DoesNotContain(members, m => m.Contains("Remove", StringComparison.Ordinal));

            // C51: upload is DEFERRED and the reason is structural — this platform receives no bytes
            // (CommAttachmentService refuses a request without a StorageKey that already exists in the
            // deployment's file store), and giving the portal a byte-ingress path is a Client Drive
            // decision, not a conversation one.
            Assert.DoesNotContain(members, m => m.Contains("Upload", StringComparison.Ordinal));
            Assert.DoesNotContain(members, m => m.Contains("Attach", StringComparison.Ordinal)
                                                && !m.Contains("ListAttachments", StringComparison.Ordinal));

            // No moderation is exposed either.
            Assert.DoesNotContain(members, m => m.Contains("Lock", StringComparison.Ordinal));
            Assert.DoesNotContain(members, m => m.Contains("Moderate", StringComparison.Ordinal));
        }

        // ---- BOUNDARIES THIS WAVE MUST NOT MOVE ----------------------------------------------------

        [Fact] // C46
        public async Task C46_employee_communication_is_unchanged()
        {
            using var host = new PortalConversationHost();
            var thread = host.SeedUsableThread();
            host.AddEmployee(thread, 7);
            var employeeComment = host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);

            // The portal service cannot see it...
            Assert.Empty(await host.Service().ListCommentsAsync(host.Portal(), thread.Id));

            // ...and the row itself is untouched: employee author, no portal id, internal tier intact.
            var row = host.Db.Set<CommComment>().AsNoTracking().Single(c => c.Id == employeeComment.Id);
            Assert.Equal(7, row.AuthorEmployeeId);
            Assert.Null(row.AuthorPortalUserId);
            Assert.Equal(CommVisibility.Internal, row.Visibility);
        }

        [Fact] // C54 + C55
        public void C54_C55_no_task_authority_and_gate_7_unchanged()
        {
            var service = System.IO.File.ReadAllText(Repo("CrossBuy/BL/Communication/CommPortalService.cs"));
            var controller = System.IO.File.ReadAllText(Repo("CrossBuy/Controllers/ClientPortalConversationsController.cs"));

            foreach (var source in new[] { service, controller })
            {
                Assert.DoesNotContain("ITaskService", source);
                Assert.DoesNotContain("TasksAccessService", source);
                Assert.DoesNotContain("TaskOrchestration", source);
            }

            // C55: gate 7 is untouched and still knows nothing about portals.
            var gate = System.IO.File.ReadAllText(Repo("CrossBuy/BL/Platform/ModuleAccessServiceBase.cs"));
            Assert.Contains("if (context.EmployeeId is not > 0)", gate);
            int i = gate.IndexOf("gate 7", StringComparison.Ordinal);
            Assert.DoesNotContain("Portal", gate.Substring(i, Math.Min(500, gate.Length - i)),
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // C58 + C59 + C60
        public void C58_C59_C60_no_notifications_no_event_bridge_no_client_drive()
        {
            var service = System.IO.File.ReadAllText(Repo("CrossBuy/BL/Communication/CommPortalService.cs"));
            var controller = System.IO.File.ReadAllText(Repo("CrossBuy/Controllers/ClientPortalConversationsController.cs"));

            foreach (var source in new[] { service, controller })
            {
                // C58 — no Central Notification dependency of any kind.
                Assert.DoesNotContain("INotificationService", source);
                Assert.DoesNotContain("NotificationRecipient", source);
                Assert.DoesNotContain("NotificationsHub", source);
                // C59 — no business event production.
                Assert.DoesNotContain("IBusinessEventService", source);
                Assert.DoesNotContain("ICommBusinessEventBridge", source);
                // C60 — no Client Drive coupling, and no file-store access at all.
                Assert.DoesNotContain("IFileManagerService", source);
                Assert.DoesNotContain("ClientDrive", source);
            }

            // C59 — and the bridge default itself is still off.
            Assert.False(new CommunicationPlatformOptions().BridgeToBusinessEvents);
        }

        [Fact] // C56 + C57
        public void C56_C57_mentions_and_reactions_remain_deferred()
        {
            var members = typeof(ICommPortalService).GetMethods().Select(m => m.Name).ToList();
            Assert.DoesNotContain(members, m => m.Contains("Mention", StringComparison.Ordinal));
            Assert.DoesNotContain(members, m => m.Contains("React", StringComparison.Ordinal));

            // The portal post path records no mention row — the service never calls the mention service.
            var service = System.IO.File.ReadAllText(Repo("CrossBuy/BL/Communication/CommPortalService.cs"));
            Assert.DoesNotContain("ICommMentionService", service);
            Assert.DoesNotContain("ICommReactionService", service);

            // And the old zero-coercion has not come back anywhere in the platform.
            foreach (var file in System.IO.Directory.GetFiles(Repo("CrossBuy/BL/Communication"), "*.cs"))
            {
                var text = System.IO.File.ReadAllText(file);
                Assert.DoesNotContain("MentionedByEmployeeId = context.EmployeeId ?? 0", text);
            }
        }

        // ---- THE ENDPOINT CONTRACT -----------------------------------------------------------------

        [Fact]
        public void The_external_endpoints_are_portal_authorized_and_anti_forgery_protected()
        {
            var type = typeof(ClientPortalConversationsController);

            // Portal policy, not bare [Authorize] — the 3A-0B defect, which would admit staff.
            var authorize = type.GetCustomAttributes(inherit: true).OfType<AuthorizeAttribute>().ToList();
            Assert.Contains(authorize, a => a is PortalAuthorizeAttribute);
            Assert.All(authorize, a => Assert.Equal(PortalAuthenticationDefaults.PolicyName, a.Policy));
            Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));

            // EVERY mutating action carries anti-forgery.
            var posts = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<HttpPostAttribute>() != null)
                .ToList();
            Assert.Equal(2, posts.Count);
            Assert.All(posts, m => Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));

            // And none of them is declared anonymous-by-design.
            Assert.All(posts, m => Assert.Null(m.GetCustomAttribute<AnonymousByDesignAttribute>()));
        }

        // ---------------------------------------------------------------------------------------------
        private static string Repo(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "CrossBuy", "BL")))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }
    }
}
