using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // WAVE 3B-B1 — THE COMMUNICATION EXTERNAL ACTOR. Security matrix C01-C30.
    //
    // These run the REAL CommExternalAccessPolicy over a real (SQLite) database with real rows. The
    // portal context is constructed directly rather than resolved through a cookie, because the
    // authentication half was closed and proven in 3A-0B; what is under test here is what the
    // Communication platform does with an identity that has already been established.
    //
    // THE CONTROL THAT KEEPS THE NEGATIVES HONEST. Nineteen of these assert a REFUSAL. A policy that
    // refused everything would pass all of them, so C19 and the employee-regression tests assert the
    // ALLOW: each denial below is a decision, not silence. That is the specific trap this session
    // already walked into once — a negative authorization test passing because no principal existed
    // at all — so the control is written first and referenced by the tests that depend on it.
    // =============================================================================================

    internal sealed class ExternalActorHost : IDisposable
    {
        public const int CompanyA = 1, CompanyB = 2;
        public const int CustomerAcme = 100, CustomerBeta = 200;
        public const int PortalAcme = 10, PortalBeta = 20, PortalOtherCo = 30;

        public readonly PlatformTestHost Platform = new(companyId: null);

        public CrossBuy.Models.Context.CrossDbContext Db => Platform.Seed;

        public CommExternalAccessPolicy Policy() => new(Db);

        public PortalContext Portal(int portalUserId = PortalAcme, int customerId = CustomerAcme, int companyId = CompanyA)
            => new(companyId, customerId, portalUserId, PortalCapabilities.Default);

        /// A quotation for a customer, and the thread anchored to it. Returns the thread.
        public CommThread SeedPublicThread(int customerId, int companyId, string visibility = CommVisibility.Public)
        {
            var quotation = new Quotation
            {
                CompanyID = companyId,
                CustomerId = customerId,
                Status = "Sent",
                QuoteNo = $"Q-{companyId}-{customerId}-{Guid.NewGuid():N}".Substring(0, 20),
                QuoteDate = DateTime.UtcNow.AddDays(-1),
                GrandTotal = 100m,
            };
            Db.Quotations.Add(quotation);
            Db.SaveChanges();

            var thread = new CommThread
            {
                CompanyID = companyId,
                EntityType = "Quotation",
                EntityId = quotation.ID,
                Kind = CommThreadKind.Discussion,
                ThreadKey = "",
                Visibility = visibility,
                CreatedAt = DateTime.UtcNow,
            };
            Db.Set<CommThread>().Add(thread);
            Db.SaveChanges();
            return thread;
        }

        public CommParticipant AddPortalParticipant(CommThread thread, int portalUserId, bool removed = false)
        {
            var row = new CommParticipant
            {
                CompanyID = thread.CompanyID,
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                Actor = CommActor.PortalUser(portalUserId),
                Role = CommParticipantRole.Participant,
                Source = CommParticipationSource.Explicit,
                RemovedAt = removed ? DateTime.UtcNow : null,
                CreatedAt = DateTime.UtcNow,
            };
            Db.Set<CommParticipant>().Add(row);
            Db.SaveChanges();
            return row;
        }

        public CommParticipant AddEmployeeParticipant(CommThread thread, int employeeId)
        {
            var row = new CommParticipant
            {
                CompanyID = thread.CompanyID,
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                Actor = CommActor.Employee(employeeId),
                Role = CommParticipantRole.Participant,
                Source = CommParticipationSource.Author,
                CreatedAt = DateTime.UtcNow,
            };
            Db.Set<CommParticipant>().Add(row);
            Db.SaveChanges();
            return row;
        }

        public CommComment SeedComment(CommThread thread, CommActor author, string visibility)
        {
            var c = new CommComment
            {
                CompanyID = thread.CompanyID,
                ThreadId = thread.Id,
                EntityType = thread.EntityType,
                EntityId = thread.EntityId,
                Body = "body",
                BodyFormat = CommBodyFormat.PlainText,
                Visibility = visibility,
                Author = author,
                CreatedAt = DateTime.UtcNow,
            };
            Db.Set<CommComment>().Add(c);
            Db.SaveChanges();
            return c;
        }

        public void Dispose() => Platform.Dispose();
    }

    // =============================================================================================
    public class CommExternalActorTests
    {
        // ---- THE ACTOR TYPE -----------------------------------------------------------------------

        [Fact] // C02
        public void C02_portal_actor_is_representable_without_an_employee_id()
        {
            var actor = CommActor.PortalUser(7);

            Assert.True(actor.IsPortalUser);
            Assert.Null(actor.EmployeeId);
            Assert.Equal(7, actor.PortalUserId);
        }

        [Fact] // C01
        public void C01_employee_actor_still_works()
        {
            var actor = CommActor.Employee(7);

            Assert.True(actor.IsEmployee);
            Assert.Equal(7, actor.EmployeeId);
            Assert.Null(actor.PortalUserId);
        }

        [Fact] // C03
        public void C03_an_actor_with_neither_identity_is_rejected()
        {
            Assert.Throws<InvalidOperationException>(() => CommActor.FromColumns(null, null));
            Assert.Null(CommActor.TryFromColumns(null, null));

            // And 0 is not an identity — the whole point of the wave.
            Assert.Throws<InvalidOperationException>(() => CommActor.FromColumns(0, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => CommActor.Employee(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => CommActor.PortalUser(0));
        }

        [Fact] // C04
        public void C04_an_actor_with_both_identities_is_rejected()
        {
            Assert.Throws<InvalidOperationException>(() => CommActor.FromColumns(7, 7));
            Assert.Throws<InvalidOperationException>(() => CommActor.FromColumns(7, 9));
            Assert.Null(CommActor.TryFromColumns(7, 9));
        }

        [Fact] // C05
        public void C05_a_portal_id_never_becomes_an_employee_id()
        {
            var portal = CommActor.PortalUser(7);

            Assert.Null(portal.EmployeeId);

            // There is no conversion, implicit or explicit, and no property that would yield the id
            // without naming its kind. Reflection is the honest way to assert an absence of API.
            var conversions = typeof(CommActor).GetMethods()
                .Where(m => m.Name is "op_Implicit" or "op_Explicit")
                .ToList();
            Assert.Empty(conversions);

            // Writing the actor onto a row sets ONE column and clears the other.
            var row = new CommParticipant { Actor = portal };
            Assert.Null(row.EmployeeId);
            Assert.Equal(7, row.PortalUserId);
        }

        [Fact] // C06
        public void C06_employee_7_and_portal_user_7_are_different_principals()
        {
            var employee = CommActor.Employee(7);
            var portal = CommActor.PortalUser(7);

            Assert.NotEqual(employee, portal);
            Assert.False(employee == portal);
            Assert.NotEqual(employee.GetHashCode(), portal.GetHashCode());
            Assert.Equal("Employee:7", employee.ToString());
            Assert.Equal("PortalUser:7", portal.ToString());
        }

        [Fact] // C22 — the actor of a write comes from the context, never from an argument
        public void C22_request_supplied_portal_user_cannot_impersonate_another()
        {
            var context = BusinessContext.ForExternalPortal(
                companyId: ExternalActorHost.CompanyA, userId: "u1", portalUserId: 10);

            Assert.Equal(CommActor.PortalUser(10), CommActor.FromContext(context));

            // An ExternalPortal context never yields an employee actor, whatever else is true of it.
            Assert.False(CommActor.FromContext(context).IsEmployee);

            // And an unresolved identity is a refusal, not employee 0.
            Assert.Throws<CommActorUnresolvedException>(
                () => CommActor.FromContext(PlatformTestHost.DefaultContext(companyId: 1, employeeId: null)));
        }

        // ---- ROWS -------------------------------------------------------------------------------

        [Fact] // C07
        public void C07_a_portal_comment_author_is_stored_without_an_employee_id()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            var comment = host.SeedComment(thread, CommActor.PortalUser(ExternalActorHost.PortalAcme), CommVisibility.Public);

            var stored = host.Db.Set<CommComment>().AsNoTracking().Single(c => c.Id == comment.Id);
            Assert.Null(stored.AuthorEmployeeId);
            Assert.Equal(ExternalActorHost.PortalAcme, stored.AuthorPortalUserId);
            Assert.Equal(CommActor.PortalUser(ExternalActorHost.PortalAcme), stored.Author);
        }

        [Fact] // C08
        public void C08_a_comment_with_no_author_cannot_be_read_as_valid()
        {
            var orphan = new CommComment { AuthorEmployeeId = null, AuthorPortalUserId = null };
            Assert.Throws<InvalidOperationException>(() => _ = orphan.Author);
        }

        [Fact] // C09
        public void C09_a_portal_read_receipt_is_stored_without_an_employee_id()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            var receipt = new CommReadReceipt
            {
                CompanyID = thread.CompanyID,
                ThreadId = thread.Id,
                Reader = CommActor.PortalUser(ExternalActorHost.PortalAcme),
                LastReadCommentId = 5,
                LastReadAt = DateTime.UtcNow,
            };
            host.Db.Set<CommReadReceipt>().Add(receipt);
            host.Db.SaveChanges();

            var stored = host.Db.Set<CommReadReceipt>().AsNoTracking().Single(r => r.Id == receipt.Id);
            Assert.Null(stored.EmployeeId);
            Assert.Equal(ExternalActorHost.PortalAcme, stored.PortalUserId);
        }

        [Fact] // C10
        public void C10_employee_and_portal_read_receipts_do_not_collide()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            // The SAME numeric id, two different principals, one thread. Both rows must exist, and each
            // must keep its OWN watermark — a shared key would let one inherit the other's position.
            host.Db.Set<CommReadReceipt>().Add(new CommReadReceipt
            { CompanyID = thread.CompanyID, ThreadId = thread.Id, Reader = CommActor.Employee(7), LastReadCommentId = 11 });
            host.Db.Set<CommReadReceipt>().Add(new CommReadReceipt
            { CompanyID = thread.CompanyID, ThreadId = thread.Id, Reader = CommActor.PortalUser(7), LastReadCommentId = 22 });
            host.Db.SaveChanges();

            var rows = host.Db.Set<CommReadReceipt>().AsNoTracking().Where(r => r.ThreadId == thread.Id).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(11, rows.Single(r => r.EmployeeId == 7).LastReadCommentId);
            Assert.Equal(22, rows.Single(r => r.PortalUserId == 7).LastReadCommentId);
        }

        [Fact] // C11 + C12
        public void C11_C12_attachment_uploader_carries_no_employee_zero()
        {
            // The column is nullable and 0 is not a value the model can produce: CommActor refuses it
            // at construction, and the property setter writes NULL for the kind that is absent.
            var portalUpload = new CommCommentAttachment { UploadedBy = CommActor.PortalUser(10) };
            Assert.Null(portalUpload.UploadedByEmployeeId);
            Assert.Equal(10, portalUpload.UploadedByPortalUserId);

            var employeeUpload = new CommCommentAttachment { UploadedBy = CommActor.Employee(7) };
            Assert.Equal(7, employeeUpload.UploadedByEmployeeId);
            Assert.Null(employeeUpload.UploadedByPortalUserId);

            // C12: the source of the old defect is gone — no write path can produce a 0 uploader.
            Assert.Throws<ArgumentOutOfRangeException>(() => CommActor.Employee(0));
        }

        [Fact] // C12 — the ownership predicate no longer treats "unresolved" as an owner
        public void C12_unresolved_identity_owns_nothing()
        {
            var unresolved = PlatformTestHost.DefaultContext(companyId: 1, employeeId: null);

            // A row authored by employee 0 cannot exist any more, but even if one were read from
            // legacy data, an unresolved caller is not its author: both sides must resolve to an actor.
            Assert.Null(CommActor.TryFromContext(unresolved));
            Assert.Null(CommActor.TryFromColumns(0, null));
        }

        // ---- THE EXTERNAL POLICY ------------------------------------------------------------------

        [Fact] // C19 — THE NON-VACUITY CONTROL. Read this before trusting any denial below.
        public async Task C19_public_comment_passes_when_every_condition_holds()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);

            Assert.True(access.CanRead, "If this fails every refusal in this file is vacuous.");
            Assert.False(access.CanComment);   // external authoring is 3B-C

            var pub = host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            Assert.True(host.Policy().CanReadComment(access, pub));
        }

        [Fact] // C14
        public async Task C14_company_A_portal_user_cannot_reach_a_company_B_thread()
        {
            using var host = new ExternalActorHost();
            var threadB = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyB);

            // Membership is granted deliberately, so the ONLY thing refusing is the company predicate.
            host.AddPortalParticipant(threadB, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(
                host.Portal(companyId: ExternalActorHost.CompanyA), threadB);

            Assert.False(access.CanRead);
        }

        [Fact] // C15
        public async Task C15_customer_A_portal_user_cannot_reach_customer_B_thread()
        {
            using var host = new ExternalActorHost();
            var threadBeta = host.SeedPublicThread(ExternalActorHost.CustomerBeta, ExternalActorHost.CompanyA);

            // Same company, and membership granted — so only the CUSTOMER anchor can be refusing.
            host.AddPortalParticipant(threadBeta, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(
                host.Portal(portalUserId: ExternalActorHost.PortalAcme, customerId: ExternalActorHost.CustomerAcme),
                threadBeta);

            Assert.False(access.CanRead);
        }

        [Fact] // C16
        public async Task C16_a_non_participant_portal_user_cannot_reach_the_thread()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            // Right company, right customer, Public thread — and NO participant row. Being the customer
            // on the quotation is not membership of the conversation about it.
            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);

            Assert.False(access.CanRead);
        }

        [Fact] // C16 — a removed participant is not a participant
        public async Task C16_a_removed_participant_cannot_reach_the_thread()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme, removed: true);

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);

            Assert.False(access.CanRead);
        }

        // -----------------------------------------------------------------------------------------
        // C14 IS DEFENDED TWICE, so it is proven twice.
        //
        // Company is checked at the THREAD row and again inside the anchor query. That is good design
        // and a bad test: removing either one alone leaves C14 passing, so a layer could be deleted
        // with the suite still green. These two isolate one layer each by making the OTHER layer
        // consistent — which is the only way to show that both are load-bearing.
        // -----------------------------------------------------------------------------------------

        [Fact] // C14a — the thread-row company comparison, isolated
        public async Task C14a_a_thread_row_in_another_company_is_refused_even_when_its_anchor_is_mine()
        {
            using var host = new ExternalActorHost();

            // A quotation that genuinely belongs to this portal identity...
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            // ...but the THREAD row claims another company. Only the thread-level comparison refuses:
            // the anchor query would pass, because the quotation really is company A / customer Acme.
            thread.CompanyID = ExternalActorHost.CompanyB;
            host.Db.SaveChanges();

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);

            Assert.False(access.CanRead);
        }

        [Fact] // C14b — the anchor company predicate, isolated
        public async Task C14b_an_anchor_in_another_company_is_refused_even_when_the_thread_row_agrees_with_me()
        {
            using var host = new ExternalActorHost();

            // The thread row says company A and the participant is live in company A, so the
            // thread-level comparison passes. The quotation it points at is in company B.
            var threadB = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyB);
            threadB.CompanyID = ExternalActorHost.CompanyA;
            host.Db.SaveChanges();
            host.AddPortalParticipant(threadB, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), threadB);

            Assert.False(access.CanRead);
        }

        [Fact] // C11 at the SERVICE, not the model — the level the old defect actually lived at
        public async Task C11_the_attachment_service_never_writes_an_employee_zero_uploader()
        {
            using var host = new CommunicationTestHost();

            var thread = new CommThread
            {
                CompanyID = 1, EntityType = "SalesInvoice", EntityId = 1,
                Kind = CommThreadKind.Discussion, ThreadKey = "", Visibility = CommVisibility.Internal,
                CreatedAt = DateTime.UtcNow,
            };
            host.Db.Set<CommThread>().Add(thread);
            host.Db.SaveChanges();

            var comment = new CommComment
            {
                CompanyID = 1, ThreadId = thread.Id, EntityType = thread.EntityType, EntityId = thread.EntityId,
                Body = "b", BodyFormat = CommBodyFormat.PlainText, Visibility = CommVisibility.Internal,
                Author = CommActor.Employee(7), CreatedAt = DateTime.UtcNow,
            };
            host.Db.Set<CommComment>().Add(comment);
            host.Db.SaveChanges();

            // A context with NO resolved identity — exactly what used to become employee 0.
            var unresolved = PlatformTestHost.DefaultContext(companyId: 1, employeeId: null);

            await Assert.ThrowsAsync<CommActorUnresolvedException>(() =>
                host.Attachments().AttachAsync(unresolved, thread, comment,
                    new[] { new CommAttachmentRequest { FileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 10, StorageKey = "k1" } }));

            // Nothing was written — the refusal happens before any row is built.
            Assert.Empty(host.Db.Set<CommCommentAttachment>().Where(a => a.CommentId == comment.Id));
        }

        [Fact] // C17
        public async Task C17_a_disabled_portal_user_never_produces_a_context_to_pass_here()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            // PortalContextAccessor returns NULL for an inactive PortalUsers row, so a disabled user
            // arrives here as a null context rather than as a context that fails a check. This asserts
            // the policy refuses that, which is the shape the disabled case actually takes.
            var access = await host.Policy().ResolveThreadAccessAsync(null!, thread);

            Assert.False(access.CanRead);
        }

        [Fact] // C18
        public async Task C18_an_internal_comment_is_denied_to_a_portal_reader()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);
            Assert.True(access.CanRead);   // the thread IS reachable — so only visibility can refuse

            foreach (var tier in new[] { CommVisibility.Internal, CommVisibility.Confidential, CommVisibility.Restricted })
            {
                var comment = host.SeedComment(thread, CommActor.Employee(7), tier);
                Assert.False(host.Policy().CanReadComment(access, comment), $"tier {tier} leaked externally");
            }
        }

        [Fact] // C18 — an internal-ceiling THREAD is not externally readable at all
        public async Task C18_an_internal_thread_is_not_externally_readable()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(
                ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA, visibility: CommVisibility.Internal);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);

            Assert.False(access.CanRead);
        }

        [Theory] // C20 + C21 — the context is the authority; nothing in the request can move it
        [InlineData(ExternalActorHost.CompanyB, ExternalActorHost.CustomerAcme)]
        [InlineData(ExternalActorHost.CompanyA, ExternalActorHost.CustomerBeta)]
        [InlineData(ExternalActorHost.CompanyB, ExternalActorHost.CustomerBeta)]
        public async Task C20_C21_a_forged_company_or_customer_cannot_widen_access(int companyId, int customerId)
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            // The policy takes the PortalContext and NOTHING else — there is no company or customer
            // parameter for a request to supply. Handing it a context naming other values models the
            // strongest possible forgery: an attacker who somehow chose the whole context still gets
            // refused, because the ANCHOR ROW decides, not the claim.
            var access = await host.Policy().ResolveThreadAccessAsync(
                host.Portal(portalUserId: ExternalActorHost.PortalAcme, customerId: customerId, companyId: companyId),
                thread);

            Assert.False(access.CanRead);
        }

        [Fact] // C22 — a portal user cannot read another portal user's thread by naming them
        public async Task C22_another_portal_users_membership_does_not_admit_me()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            // Portal user 10 is the member. Portal user 20, same company and same customer, is not.
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(
                host.Portal(portalUserId: ExternalActorHost.PortalBeta, customerId: ExternalActorHost.CustomerAcme),
                thread);

            Assert.False(access.CanRead);
        }

        [Fact] // C16 — an employee participant row does not admit a portal user with the same number
        public async Task C16_an_employee_membership_does_not_admit_the_same_numbered_portal_user()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            // Employee 10 is a participant. Portal user 10 is a different person and must not inherit it.
            host.AddEmployeeParticipant(thread, ExternalActorHost.PortalAcme);

            var access = await host.Policy().ResolveThreadAccessAsync(
                host.Portal(portalUserId: ExternalActorHost.PortalAcme), thread);

            Assert.False(access.CanRead);
        }

        [Fact] // the anchor allow-list is an allow-list
        public async Task An_entity_type_outside_the_allow_list_is_never_externally_anchorable()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);
            host.AddPortalParticipant(thread, ExternalActorHost.PortalAcme);

            // Everything else holds; only the entity type changes.
            thread.EntityType = "SalesInvoice";
            host.Db.SaveChanges();

            var access = await host.Policy().ResolveThreadAccessAsync(host.Portal(), thread);

            Assert.False(access.CanRead);
            Assert.DoesNotContain("SalesInvoice", host.Policy().ExternallyAnchorableEntityCodes);
            Assert.Contains("Quotation", host.Policy().ExternallyAnchorableEntityCodes);
        }

        // ---- BOUNDARIES THIS WAVE MUST NOT MOVE ---------------------------------------------------

        [Fact] // C24
        public void C24_the_employee_module_gate_is_not_weakened()
        {
            // Gate 7 is asserted by SOURCE, because its whole value is that it is unconditional: a
            // behavioural test could pass against a version that had grown an exception for portals.
            var source = System.IO.File.ReadAllText(RepoPath("CrossBuy/BL/Platform/ModuleAccessServiceBase.cs"));

            Assert.Contains("gate 7", source);
            Assert.Contains("if (context.EmployeeId is not > 0)", source);

            // And nothing in the gate knows what a portal is.
            var gateStart = source.IndexOf("gate 7", StringComparison.Ordinal);
            var gateRegion = source.Substring(gateStart, Math.Min(500, source.Length - gateStart));
            Assert.DoesNotContain("Portal", gateRegion, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // C23
        public void C23_external_portal_still_has_no_task_authority()
        {
            var portal = BusinessContext.ForExternalPortal(companyId: 1, userId: "u1", portalUserId: 10);

            Assert.Null(portal.EmployeeId);
            Assert.True(portal.IsExternalPortal);
            Assert.False(portal.IsSystem);

            // The external policy exposes READ only, and nothing in this platform hands a portal
            // identity to a task service — asserted structurally, by the absence of the reference.
            var policy = System.IO.File.ReadAllText(RepoPath("CrossBuy/BL/Communication/CommExternalAccessPolicy.cs"));
            Assert.DoesNotContain("ITaskService", policy);
            Assert.DoesNotContain("TasksAccessService", policy);
        }

        [Fact] // C28 + C29
        public void C28_C29_portal_reactions_and_mentions_are_unavailable()
        {
            // Reactions keep a non-nullable employee id: there is no portal reaction to represent.
            var reaction = typeof(CommReaction).GetProperty(nameof(CommReaction.EmployeeId))!;
            Assert.Equal(typeof(int), reaction.PropertyType);
            Assert.Null(typeof(CommReaction).GetProperty("PortalUserId"));

            // Mentions likewise, and the recipient table too.
            Assert.Null(typeof(CommMention).GetProperty("MentionedByPortalUserId"));
            Assert.Null(typeof(CommMentionRecipient).GetProperty("PortalUserId"));
        }

        [Fact] // C30
        public void C30_the_external_policy_needs_no_notification_platform()
        {
            var policy = System.IO.File.ReadAllText(RepoPath("CrossBuy/BL/Communication/CommExternalAccessPolicy.cs"));

            // No dependency on Central Notifications, the event bridge, or the mapper — 3B-E owns those.
            Assert.DoesNotContain("INotificationService", policy);
            Assert.DoesNotContain("NotificationRecipient", policy);
            Assert.DoesNotContain("IBusinessEventService", policy);

            // CommActor likewise: the convention is shared with NotificationRecipient, the TYPE is not.
            var actor = System.IO.File.ReadAllText(RepoPath("CrossBuy/Models/Communication/CommActor.cs"));
            Assert.DoesNotContain("using CrossBuy.Models.Context.Admin", actor);
        }

        [Fact] // the business-event bridge stays off
        public void The_communication_business_event_bridge_remains_off_by_default()
        {
            Assert.False(new CommunicationPlatformOptions().BridgeToBusinessEvents);
        }

        // ---- EMPLOYEE REGRESSION (C25, C26, C27) ---------------------------------------------------

        [Fact] // C25
        public void C25_an_employee_comment_is_stored_exactly_as_before()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            var comment = host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);

            var stored = host.Db.Set<CommComment>().AsNoTracking().Single(c => c.Id == comment.Id);
            Assert.Equal(7, stored.AuthorEmployeeId);
            Assert.Null(stored.AuthorPortalUserId);
        }

        [Fact] // C25 — the classic employee predicate still selects exactly the employee rows
        public void C25_nullable_columns_do_not_broaden_employee_queries()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);
            host.SeedComment(thread, CommActor.PortalUser(7), CommVisibility.Public);

            // THE POINT OF THE SPLIT-COLUMN MODEL: this predicate was written before portal rows
            // existed and is CORRECT UNCHANGED, because `NULL = 7` is UNKNOWN and excludes them.
            int me = 7;
            var mine = host.Db.Set<CommComment>().AsNoTracking()
                .Where(c => c.ThreadId == thread.Id && c.AuthorEmployeeId == me)
                .ToList();

            Assert.Single(mine);
            Assert.Null(mine[0].AuthorPortalUserId);
        }

        [Fact] // C26
        public void C26_employee_read_receipt_behaviour_is_unchanged()
        {
            using var host = new ExternalActorHost();
            var thread = host.SeedPublicThread(ExternalActorHost.CustomerAcme, ExternalActorHost.CompanyA);

            host.Db.Set<CommReadReceipt>().Add(new CommReadReceipt
            { CompanyID = thread.CompanyID, ThreadId = thread.Id, Reader = CommActor.Employee(7), LastReadCommentId = 3 });
            host.Db.SaveChanges();

            var row = host.Db.Set<CommReadReceipt>().AsNoTracking()
                .Single(r => r.ThreadId == thread.Id && r.EmployeeId == 7);
            Assert.Equal(3, row.LastReadCommentId);
            Assert.Null(row.PortalUserId);
        }

        [Fact] // C27
        public void C27_employee_attachment_upload_is_unchanged()
        {
            var row = new CommCommentAttachment { UploadedBy = CommActor.Employee(7) };
            Assert.Equal(7, row.UploadedByEmployeeId);
            Assert.Null(row.UploadedByPortalUserId);
            Assert.Equal(CommActor.Employee(7), row.UploadedBy);
        }

        [Fact] // C01 — the employee participation API is unchanged in signature and behaviour
        public void C01_the_employee_participation_overload_still_exists()
        {
            var employeeOverload = typeof(ICommParticipationService).GetMethod(
                nameof(ICommParticipationService.EnsureAsync));
            Assert.NotNull(employeeOverload);
            Assert.Contains(employeeOverload!.GetParameters(), p => p.ParameterType == typeof(int) && p.Name == "employeeId");

            var actorOverload = typeof(ICommParticipationService).GetMethod(
                nameof(ICommParticipationService.EnsureActorAsync));
            Assert.NotNull(actorOverload);
            Assert.Contains(actorOverload!.GetParameters(), p => p.ParameterType == typeof(CommActor));
        }

        [Fact] // C13
        public void C13_portal_attachment_delete_is_not_granted_by_this_wave()
        {
            // The external policy exposes READ and nothing else. CanComment is false, and there is no
            // delete surface at all — so "portal may delete an attachment" is not expressible.
            var access = CommExternalAccess.Allow("test");
            Assert.True(access.CanRead);
            Assert.False(access.CanComment);

            var members = typeof(CommExternalAccess).GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("CanDelete", members);
            Assert.DoesNotContain("CanModerate", members);
        }

        // ---------------------------------------------------------------------------------------------
        private static string RepoPath(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "CrossBuy", "BL")))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }
    }
}
