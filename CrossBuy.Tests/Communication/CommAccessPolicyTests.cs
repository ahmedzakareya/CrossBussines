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
    // Communication Platform — CONVERSATION AND THREAD PERMISSIONS (modules 23, 24).
    //
    // THE INVARIANT THIS FILE EXISTS TO DEFEND:
    //
    //     A communication permission can never widen access to a business record.
    //
    // Entity-level View is asked of IPlatformPermissionProvider FIRST. Only then does thread state
    // (participation, grants, visibility) decide whether the caller may read or write this conversation. Every
    // test below is a way that ordering could be broken.
    // =============================================================================================
    public class CommAccessPolicyTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();
        public void Dispose() => _host.Dispose();

        // A stub that denies EVERYTHING, including View — the caller cannot open the invoice at all.
        private static StubPermissionProvider DeniesEverything() => new();

        private static StubPermissionProvider ViewOnly() => new(PlatformActions.View);

        private static StubPermissionProvider ViewAndConfidential() =>
            new(PlatformActions.View, PlatformActions.ViewConfidential);

        private static StubPermissionProvider Manager() =>
            new(PlatformActions.View, PlatformActions.ViewConfidential, PlatformActions.ViewRestricted);

        // ============================================================================================
        // The entity gate comes first
        // ============================================================================================

        // If the caller cannot View the invoice, nothing about the conversation is reachable. This is the test
        // that would fail if somebody ever "optimised" the entity check away for a caller who is a participant.
        [Fact]
        public async Task No_entity_view_means_no_thread_at_all()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            // Arrange the thread while View is granted.
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity });

            // Now ask as a caller who cannot open the record.
            var denied = _host.Access(permissions: DeniesEverything());
            var access = await denied.ResolveThreadAccessAsync(context, thread);

            Assert.False(access.CanRead);
            Assert.False(access.CanComment);
            Assert.False(access.CanModerate);
            Assert.Contains("entity View denied", access.Reason);
        }

        // THE CENTRAL CLAIM, stated as a test: a thread grant is additive WITHIN the entity, never a way past it.
        // A Moderate grant on the thread must still be worthless to somebody who cannot open the invoice.
        [Fact]
        public async Task A_thread_grant_cannot_substitute_for_entity_view()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads(permissions: Manager()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity });

            await _host.Threads(permissions: Manager()).GrantAsync(
                context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Employee, Id = CommunicationTestHost.Colleague },
                CommPermissionLevel.Moderate);

            // The colleague holds Moderate on the thread — and cannot open the invoice.
            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var access = await _host.Access(permissions: DeniesEverything())
                .ResolveThreadAccessAsync(colleagueContext, thread);

            Assert.False(access.CanRead);
            Assert.False(access.CanModerate);
        }

        // ============================================================================================
        // Thread grants (modules 23, 24) — what they DO do
        // ============================================================================================

        // A grant's purpose: open a thread above the caller's own visibility tier, for somebody who can already
        // open the record. Without a grant, an ordinary viewer cannot read a Confidential thread.
        [Fact]
        public async Task A_read_grant_opens_a_confidential_thread_to_a_caller_who_can_open_the_record()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads(permissions: Manager()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity, Visibility = CommVisibility.Confidential });

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);

            // Before the grant: can open the invoice, cannot read the confidential thread.
            var before = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(colleagueContext, thread);
            Assert.False(before.CanRead);

            await _host.Threads(permissions: Manager()).GrantAsync(
                context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Employee, Id = CommunicationTestHost.Colleague },
                CommPermissionLevel.Read);

            var after = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(colleagueContext, thread);
            Assert.True(after.CanRead);
            Assert.False(after.CanComment);      // Read is Read — it does not imply Comment
        }

        [Fact]
        public async Task A_comment_grant_implies_read_and_a_moderate_grant_implies_both()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var threads = _host.Threads(permissions: Manager());

            var thread = await threads.GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity, Visibility = CommVisibility.Confidential });

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);

            await threads.GrantAsync(context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Employee, Id = CommunicationTestHost.Colleague },
                CommPermissionLevel.Comment);

            var withComment = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(colleagueContext, thread);
            Assert.True(withComment.CanRead);
            Assert.True(withComment.CanComment);
            Assert.False(withComment.CanModerate);

            // Raising the level UPDATES the existing row rather than adding a second one, so grant resolution
            // never has to reconcile two rows for one principal.
            await threads.GrantAsync(context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Employee, Id = CommunicationTestHost.Colleague },
                CommPermissionLevel.Moderate);

            var withModerate = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(colleagueContext, thread);
            Assert.True(withModerate.CanModerate);

            using var fresh = _host.NewContext();
            Assert.Equal(1, await fresh.Set<CommThreadPermission>()
                .CountAsync(p => p.ThreadId == thread.Id && p.RevokedAt == null));
        }

        // A GROUP grant is expanded through the SAME resolver mentions use. That shared expansion is what stops
        // a department grant and a department mention from disagreeing about who is in the department.
        [Fact]
        public async Task A_department_grant_reaches_a_member_of_a_sub_department()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var threads = _host.Threads(permissions: Manager());

            var thread = await threads.GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity, Visibility = CommVisibility.Confidential });

            await threads.GrantAsync(context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Department, Id = CommunicationTestHost.SalesDepartmentNode },
                CommPermissionLevel.Read);

            // Outsider sits in the SUB-department, so only a node walk reaches them.
            var subDepartmentMember = CommunicationTestHost.Context(CommunicationTestHost.Outsider);
            var access = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(subDepartmentMember, thread);

            Assert.True(access.CanRead);
        }

        // The org tree carries no CompanyID — IOrgHierarchy's own comment says a raw walk can return another
        // company's employees. This is the test that proves the resolver's intersection actually excludes them.
        [Fact]
        public async Task A_department_grant_does_not_reach_another_companys_employee_in_the_same_node()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();
            var threads = _host.Threads(permissions: Manager());

            var thread = await threads.GetOrCreateAsync(
                context, new CommThreadRequest { Entity = entity, Visibility = CommVisibility.Confidential });

            await threads.GrantAsync(context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Department, Id = CommunicationTestHost.SalesDepartmentNode },
                CommPermissionLevel.Read);

            var expansion = await _host.Principals().ExpandAsync(
                CommPrincipalKind.Department, CommunicationTestHost.SalesDepartmentNode, null, CommunicationTestHost.CompanyId);

            Assert.Contains(CommunicationTestHost.Author, expansion.EmployeeIds);
            Assert.DoesNotContain(CommunicationTestHost.OtherCompanyEmployee, expansion.EmployeeIds);
        }

        // A grant to a principal nothing can expand is a grant to NOBODY, and it must be refused rather than
        // stored: a stored grant that never matches looks, to whoever granted it, exactly like a working one.
        // This is where an unwired @role principal is caught.
        [Fact]
        public async Task A_grant_to_an_unresolvable_role_principal_is_refused_rather_than_stored()
        {
            var context = CommunicationTestHost.Context();
            var thread = await _host.Threads(permissions: Manager()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });

            var ex = await Assert.ThrowsAsync<CommValidationException>(() =>
                _host.Threads(permissions: Manager()).GrantAsync(
                    context, thread.Id,
                    new CommPrincipalRef { Kind = CommPrincipalKind.Role, Key = "InventoryManager" },
                    CommPermissionLevel.Read));

            Assert.Equal(CommValidationException.Codes.PrincipalKindInvalid, ex.Code);

            using var fresh = _host.NewContext();
            Assert.Equal(0, await fresh.Set<CommThreadPermission>().CountAsync(p => p.ThreadId == thread.Id));
        }

        // Revoked, not deleted: "who could read what when the decision was taken" is a fact an investigation
        // needs. The row leaves the filtered index and stops affecting decisions.
        [Fact]
        public async Task Revoking_a_grant_keeps_the_row_and_removes_the_access()
        {
            var context = CommunicationTestHost.Context();
            var threads = _host.Threads(permissions: Manager());

            var thread = await threads.GetOrCreateAsync(context, new CommThreadRequest
            {
                Entity = CommunicationTestHost.Invoice(), Visibility = CommVisibility.Confidential,
            });

            var grant = await threads.GrantAsync(context, thread.Id,
                new CommPrincipalRef { Kind = CommPrincipalKind.Employee, Id = CommunicationTestHost.Colleague },
                CommPermissionLevel.Read);

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            Assert.True((await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(colleagueContext, thread)).CanRead);

            Assert.True(await threads.RevokeAsync(context, grant.PermissionId));

            Assert.False((await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(colleagueContext, thread)).CanRead);

            using var fresh = _host.NewContext();
            var row = await fresh.Set<CommThreadPermission>().AsNoTracking().SingleAsync(p => p.Id == grant.PermissionId);
            Assert.NotNull(row.RevokedAt);
        }

        // Who can read a thread is itself sensitive — the participant list of a restricted HR note is a fact
        // about an investigation. So listing permissions needs Moderate, not Read.
        [Fact]
        public async Task Listing_thread_permissions_requires_moderate_not_read()
        {
            var context = CommunicationTestHost.Context();
            var thread = await _host.Threads(permissions: Manager()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });

            await Assert.ThrowsAsync<CommAccessDeniedException>(() =>
                _host.Threads(permissions: ViewOnly()).ListPermissionsAsync(context, thread.Id));

            var asManager = await _host.Threads(permissions: Manager()).ListPermissionsAsync(context, thread.Id);
            Assert.NotNull(asManager);
        }

        // ============================================================================================
        // Visibility — the set/flag split the kernel's timeline learned the hard way
        // ============================================================================================

        // A single value collapsing "what the SQL filter may pass" with "may I read other people's restricted
        // rows" grants every viewer every restricted comment. The two are separate here; this proves it.
        [Fact]
        public async Task An_ordinary_caller_reads_their_own_restricted_comment_but_not_someone_elses()
        {
            var entity = CommunicationTestHost.Invoice();
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);
            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);

            var comments = _host.Comments(permissions: ViewOnly());

            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                authorContext, new CommThreadRequest
                {
                    Entity = entity, Kind = CommThreadKind.Notes, Visibility = CommVisibility.Restricted,
                });

            var mine = await comments.AddAsync(authorContext, new CommCommentRequest
            {
                Entity = entity, ThreadId = thread.Id, Body = "my private note",
                Visibility = CommVisibility.Restricted,
            });

            // The author reads their own restricted note.
            var asAuthor = await comments.ListAsync(authorContext, thread.Id);
            Assert.Contains(asAuthor.Items, c => c.CommentId == mine.CommentId);

            // A colleague with the same rights cannot — they are not the actor, not a participant, and hold no
            // ViewRestricted. They cannot even reach the thread, because its ceiling is Restricted.
            await Assert.ThrowsAsync<CommAccessDeniedException>(() =>
                comments.ListAsync(colleagueContext, thread.Id));
        }

        // A module manager (ViewRestricted) reads everything on the record. This is the grant the kernel's
        // timeline calls `mayReadAnyRestricted`, and it is the ONLY thing that opens somebody else's restricted
        // content.
        [Fact]
        public async Task A_manager_reads_another_employees_restricted_comment()
        {
            var entity = CommunicationTestHost.Invoice();
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);

            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                authorContext, new CommThreadRequest
                {
                    Entity = entity, Kind = CommThreadKind.Notes, Visibility = CommVisibility.Restricted,
                });

            var mine = await _host.Comments(permissions: ViewOnly()).AddAsync(authorContext, new CommCommentRequest
            {
                Entity = entity, ThreadId = thread.Id, Body = "private", Visibility = CommVisibility.Restricted,
            });

            var managerContext = CommunicationTestHost.Context(CommunicationTestHost.Manager);
            var asManager = await _host.Comments(permissions: Manager()).ListAsync(managerContext, thread.Id);

            Assert.Contains(asManager.Items, c => c.CommentId == mine.CommentId);
        }

        // A caller who cannot read Confidential must not be OFFERED it as a posting option — they would write
        // something and immediately lose sight of it.
        [Fact]
        public async Task Author_visibilities_exclude_a_tier_the_caller_cannot_read()
        {
            var context = CommunicationTestHost.Context();
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });

            var ordinary = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(context, thread);
            Assert.DoesNotContain(CommVisibility.Confidential, ordinary.AuthorVisibilities);
            Assert.Contains(CommVisibility.Internal, ordinary.AuthorVisibilities);
            Assert.Contains(CommVisibility.Restricted, ordinary.AuthorVisibilities);   // a private note is normal

            var privileged = await _host.Access(permissions: ViewAndConfidential()).ResolveThreadAccessAsync(context, thread);
            Assert.Contains(CommVisibility.Confidential, privileged.AuthorVisibilities);
        }

        // The thread's ceiling is the OTHER bound: a Confidential thread may not carry an Internal comment.
        // Together with the test above, this is the pair that a single "max" value could not express.
        [Fact]
        public async Task Author_visibilities_exclude_tiers_more_open_than_the_thread()
        {
            var context = CommunicationTestHost.Context();
            var thread = await _host.Threads(permissions: Manager()).GetOrCreateAsync(
                context, new CommThreadRequest
                {
                    Entity = CommunicationTestHost.Invoice(), Visibility = CommVisibility.Confidential,
                });

            var access = await _host.Access(permissions: Manager()).ResolveThreadAccessAsync(context, thread);

            Assert.DoesNotContain(CommVisibility.Public, access.AuthorVisibilities);
            Assert.DoesNotContain(CommVisibility.Internal, access.AuthorVisibilities);
            Assert.Contains(CommVisibility.Confidential, access.AuthorVisibilities);
            Assert.Equal(CommVisibility.Confidential, access.MostOpenAuthorVisibility);
        }

        // An employee who opens a Restricted note must be able to read the thread they just created. Opening a
        // thread does not by itself make somebody a participant, so without the own-actor exception on the
        // thread row they would be locked out of their own note.
        [Fact]
        public async Task The_creator_of_a_restricted_thread_can_read_it()
        {
            var context = CommunicationTestHost.Context();
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                context, new CommThreadRequest
                {
                    Entity = CommunicationTestHost.Invoice(), Kind = CommThreadKind.Notes,
                    Visibility = CommVisibility.Restricted,
                });

            var access = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(context, thread);

            Assert.True(access.CanRead);
            Assert.True(access.CanComment);
        }

        // ============================================================================================
        // Cross-company
        // ============================================================================================

        // These tables carry NO global query filter, so this explicit comparison is the only thing between two
        // tenants. A filter is not an authorization control.
        [Fact]
        public async Task A_thread_from_another_company_is_never_readable()
        {
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                CommunicationTestHost.Context(), new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });

            var otherContext = CommunicationTestHost.Context(
                CommunicationTestHost.OtherCompanyEmployee, CommunicationTestHost.OtherCompanyId);

            var access = await _host.Access(permissions: Manager()).ResolveThreadAccessAsync(otherContext, thread);

            Assert.False(access.CanRead);
            Assert.Contains("another company", access.Reason);
        }

        // Fail closed. CLAUDE.md: "An unresolved company scope reads no company-scoped data and writes none."
        [Fact]
        public async Task An_unresolved_company_scope_reads_nothing()
        {
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                CommunicationTestHost.Context(), new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });

            var unresolved = new BusinessContext
            {
                CompanyId = 0, EmployeeId = CommunicationTestHost.Author, Roles = Array.Empty<string>(),
            };

            var decision = await _host.Access(permissions: Manager())
                .CanAccessEntityAsync(unresolved, CommunicationTestHost.Invoice(), CommCapabilities.Comments);

            Assert.False(decision.Allowed);
            Assert.Contains("no company scope", decision.Reason);
        }

        // ============================================================================================
        // Comment-level rights
        // ============================================================================================
        [Fact]
        public async Task An_author_may_edit_and_delete_their_own_comment_but_not_restore_it()
        {
            var context = CommunicationTestHost.Context();
            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context, _host.CommentOn(CommunicationTestHost.Invoice(), "mine"));

            var read = await _host.Comments(permissions: ViewOnly()).GetAsync(context, added.CommentId);

            Assert.True(read.Capabilities.CanEdit);
            Assert.True(read.Capabilities.CanDelete);
            Assert.False(read.Capabilities.CanRestore);      // nothing to restore, and only a moderator ever may
            Assert.True(read.Capabilities.CanReact);
            Assert.True(read.Capabilities.CanReply);
        }

        // Being the thread's CREATOR is not moderation: a thread is created implicitly by the first commenter, so
        // creator-moderates would hand moderation to whoever happened to type first.
        [Fact]
        public async Task Creating_a_thread_does_not_confer_moderation()
        {
            var context = CommunicationTestHost.Context();
            var thread = await _host.Threads(permissions: ViewOnly()).GetOrCreateAsync(
                context, new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });

            var access = await _host.Access(permissions: ViewOnly()).ResolveThreadAccessAsync(context, thread);

            Assert.True(access.CanRead);
            Assert.False(access.CanModerate);
        }

        [Fact]
        public async Task A_colleague_cannot_edit_another_employees_comment_without_moderation()
        {
            var authorContext = CommunicationTestHost.Context(CommunicationTestHost.Author);
            var added = await _host.Comments(permissions: ViewOnly()).AddAsync(
                authorContext, _host.CommentOn(CommunicationTestHost.Invoice(), "not yours"));

            var colleagueContext = CommunicationTestHost.Context(CommunicationTestHost.Colleague);

            await Assert.ThrowsAsync<CommAccessDeniedException>(() =>
                _host.Comments(permissions: ViewOnly()).EditAsync(colleagueContext, new CommCommentEditRequest
                {
                    CommentId = added.CommentId, Body = "hijacked",
                }));
        }

        // ============================================================================================
        // Denials are audited
        // ============================================================================================

        // A read that returns nothing and a read that was refused look identical in a log otherwise — and "why
        // can this user not see the thread" is a real support question.
        [Fact]
        public async Task A_denied_thread_creation_writes_an_audit_row()
        {
            var context = CommunicationTestHost.Context();

            await Assert.ThrowsAsync<CommAccessDeniedException>(() =>
                _host.Threads(permissions: DeniesEverything()).GetOrCreateAsync(
                    context, new CommThreadRequest { Entity = CommunicationTestHost.Invoice() }));

            using var fresh = _host.NewContext();
            var audit = await fresh.Set<CommAuditEntry>().AsNoTracking()
                .Where(a => a.Action == CommAuditActions.AccessDenied)
                .ToListAsync();

            var row = Assert.Single(audit);
            Assert.Equal(CommunicationTestHost.Author, row.ActorEmployeeId);
            Assert.Equal(EntityRegistry.SalesInvoice, row.EntityType);
        }
    }
}
