using CrossBuy.BL;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Communication;

// =================================================================================================
// PHASE 3 — Communication ↔ Task Management.
//
// WHAT IS REAL HERE AND WHAT IS DOUBLED, because it decides what these tests are worth.
//
//   REAL: the whole Communication side (threads, comments, access policy, audit), AND the real
//         TaskService over the real CrossDbContext. A task created by these tests is a genuine
//         TaskItem row written by the Tasks module's own service, with its own validation.
//
//   DOUBLED: ITasksAccessService only. Authorization is doubled because the REFUSALS are what need
//         testing and the real service needs role and hierarchy plumbing to say "no" for the reason
//         a test means. Nothing that decides COMPANY isolation is doubled — that is the real
//         comment service, the real thread service and a real company predicate.
//
// The adapter never takes a Tasks type in its constructor, so "the Tasks module is absent" is a real
// state this suite can create by handing it an empty service provider.
// =================================================================================================
public sealed class CommTaskIntegrationTests : IDisposable
{
    private readonly CommunicationTestHost _host = new();
    private readonly StubTasksAccess _access = new();

    public void Dispose() => _host.Dispose();

    private ITaskService RealTasks()
    {
        var accessor = new StubContextAccessor(CommunicationTestHost.Context());
        var events = new BusinessEventService(
            _host.Db, new EntityRegistry(_host.Db), accessor, NullLogger<BusinessEventService>.Instance);
        return new TaskService(
            _host.Db, new TaskCalendarEventPublisher(events),
            new TaskNotificationService(_host.Db, new UatRecordingNotificationService(_host.Db)));
    }

    // Isolated test storage for the link relationship. The production default is
    // UnprovisionedCommTaskLinkStore because CommTaskLinks does not exist in any database yet; this
    // in-memory store lets the CONTRACT — authorization, company isolation, idempotency — be tested now,
    // and it implements the same interface EfCommTaskLinkStore does, so the tests move across unchanged
    // when the table is provisioned.
    private readonly InMemoryCommTaskLinkStore _links = new();

    private ICommTaskIntegrationService Adapter(
        bool tasksAvailable = true, ICommTaskLinkStore? links = null,
        IPlatformPermissionProvider? permissions = null)
    {
        var services = new StubServices();
        // The REAL registry, so eligibility runs the full TaskEntityLinkPolicy path rather than its
        // narrower registry-is-null fallback.
        services.Add<IEntityRegistry>(new EntityRegistry(_host.Db));
        if (tasksAvailable)
        {
            services.Add<ITaskService>(RealTasks());
            services.Add<ITasksAccessService>(_access);
        }

        return new CommTaskIntegrationService(
            _host.Db, _host.Threads(), _host.Comments(), _host.Audit(),
            _host.Access(permissions: permissions), links ?? _links, services,
            NullLogger<CommTaskIntegrationService>.Instance);
    }

    // The conversation's anchor must be one TaskService will actually store, or the task comes back
    // unlinked — Customer is on its allow-list, so it is the honest default for these tests.
    private static CommEntityRef Customer(int id = 5001) => new(EntityRegistry.Customer, id);

    private async Task<long> SeedMessageAsync(CommEntityRef entity, string body = "please follow this up")
    {
        var added = await _host.Comments().AddAsync(
            CommunicationTestHost.Context(), _host.CommentOn(entity, body));
        return added.CommentId;
    }

    private static CommTaskCreateRequest Request(long commentId, int? assignee = null, string? dedupKey = null) => new()
    {
        CommentId = commentId,
        Title = "تابع الفاتورة",
        TitleEn = "Follow up the invoice",
        Description = "raised from a conversation",
        AssigneeEmployeeId = assignee ?? CommunicationTestHost.Author,
        Priority = "High",
        DueDate = DateTime.UtcNow.Date.AddDays(3),
        DedupKey = dedupKey,
    };

    // ---------------------------------------------------------------------------------------------
    // A. CREATE TASK FROM MESSAGE — the authorized, same-company path.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_authorized_caller_turns_a_message_into_a_task_carrying_the_conversations_record()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());

        var result = await Adapter().CreateFromMessageAsync(CommunicationTestHost.Context(), Request(commentId));

        Assert.True(result.Ok, result.Error);
        Assert.True(result.TaskId > 0);
        Assert.Null(result.AnchorWarning);

        using var fresh = _host.NewContext();
        var task = await fresh.Set<TaskItem>().AsNoTracking().SingleAsync(t => t.ID == result.TaskId);

        Assert.Equal(CommunicationTestHost.CompanyId, task.CompanyId);
        Assert.Equal("تابع الفاتورة", task.Title);
        Assert.Equal("Follow up the invoice", task.TitleEn);      // the bilingual twin survives the hop
        Assert.Equal("High", task.Priority);
        Assert.Equal(CommunicationTestHost.Author, task.AssigneeEmployeeId);
        Assert.NotNull(task.DueDate);

        // The task is about the RECORD the conversation hangs on, not about the comment row.
        Assert.Equal(EntityRegistry.Customer, task.EntityType);
        Assert.Equal(5001, task.EntityId);
    }

    // The message reference has no column anywhere, so the audit row is the only durable record of
    // which message a task came from. If this stops being written, that link is simply gone.
    [Fact]
    public async Task The_originating_message_is_recorded_in_the_audit_trail()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());

        var result = await Adapter().CreateFromMessageAsync(CommunicationTestHost.Context(), Request(commentId));

        using var fresh = _host.NewContext();
        var row = await fresh.Set<CrossBuy.Models.Context.Communication.CommAuditEntry>().AsNoTracking()
            .SingleAsync(a => a.Action == CommAuditActions.TaskCreatedFromMessage);

        Assert.Equal(commentId, row.CommentId);
        Assert.Equal(CommunicationTestHost.CompanyId, row.CompanyID);
        Assert.Equal(EntityRegistry.Customer, row.EntityType);
        Assert.Contains(result.TaskId.ToString(), row.DetailJson);
    }

    // ---------------------------------------------------------------------------------------------
    // §5 SECURITY
    // ---------------------------------------------------------------------------------------------

    // Cross-company. The id is real and the caller is real; only the company differs. The refusal must
    // be the SAME one a non-existent message gets, or the error itself tells an attacker the message
    // is there.
    [Fact]
    public async Task A_message_in_another_company_cannot_become_a_task_and_is_indistinguishable_from_absent()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());

        var intruder = CommunicationTestHost.Context(
            employeeId: CommunicationTestHost.OtherCompanyEmployee,
            companyId: CommunicationTestHost.OtherCompanyId);

        var real = await Adapter().CreateFromMessageAsync(intruder, Request(commentId));
        var absent = await Adapter().CreateFromMessageAsync(intruder, Request(commentId + 9999));

        Assert.False(real.Ok);
        Assert.Equal("message_not_found", real.Error);
        Assert.Equal(absent.Error, real.Error);

        using var fresh = _host.NewContext();
        Assert.Empty(await fresh.Set<TaskItem>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_caller_without_the_create_authority_is_refused_and_writes_nothing()
    {
        // Deliberately NOT allowed: _access grants nothing.
        long commentId = await SeedMessageAsync(Customer());

        var result = await Adapter().CreateFromMessageAsync(CommunicationTestHost.Context(), Request(commentId));

        Assert.False(result.Ok);
        Assert.Equal("not_authorized_to_create_task", result.Error);

        using var fresh = _host.NewContext();
        Assert.Empty(await fresh.Set<TaskItem>().AsNoTracking().ToListAsync());
    }

    // Assigning work to somebody else is a separate authority from raising your own task. Create alone
    // must not be enough, or "create" quietly becomes "assign".
    [Fact]
    public async Task Create_alone_does_not_let_a_caller_assign_the_task_to_someone_else()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());

        var toSomeoneElse = await Adapter().CreateFromMessageAsync(
            CommunicationTestHost.Context(), Request(commentId, assignee: CommunicationTestHost.Colleague));

        Assert.False(toSomeoneElse.Ok);
        Assert.Equal("not_authorized_to_assign", toSomeoneElse.Error);

        // With the assign authority the same call succeeds — so the refusal above is about the
        // authority and not about the assignee being a different person.
        _access.Allow(TasksActions.Assign);
        var allowed = await Adapter().CreateFromMessageAsync(
            CommunicationTestHost.Context(), Request(commentId, assignee: CommunicationTestHost.Colleague));
        Assert.True(allowed.Ok, allowed.Error);
    }

    [Fact]
    public async Task A_caller_with_no_resolved_company_is_refused_before_anything_is_read()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());

        var noCompany = CommunicationTestHost.Context(companyId: 0);
        var result = await Adapter().CreateFromMessageAsync(noCompany, Request(commentId));

        Assert.False(result.Ok);
        Assert.Equal("no_company_scope", result.Error);
    }

    // A deleted message is not a source of work. Soft-deleted rows survive for the audit trail, so
    // without this the comment would still be readable by id.
    [Fact]
    public async Task A_deleted_message_cannot_become_a_task()
    {
        _access.Allow(TasksActions.Create);
        var context = CommunicationTestHost.Context();
        long commentId = await SeedMessageAsync(Customer());

        await _host.Comments().DeleteAsync(context, commentId);

        var result = await Adapter().CreateFromMessageAsync(context, Request(commentId));

        Assert.False(result.Ok);
        Assert.Equal("message_not_found", result.Error);
    }

    // The Tasks module absent is a deployment state, not an error. The adapter must say so rather than
    // throw — this is the mirror of TasksController's CommunicationUnavailable().
    [Fact]
    public async Task When_the_Tasks_module_is_absent_the_adapter_reports_it_instead_of_throwing()
    {
        long commentId = await SeedMessageAsync(Customer());

        var result = await Adapter(tasksAvailable: false)
            .CreateFromMessageAsync(CommunicationTestHost.Context(), Request(commentId));

        Assert.False(result.Ok);
        Assert.Equal("tasks_unavailable", result.Error);

        // And the read paths degrade to empty rather than failing the screen around them.
        Assert.Empty(await Adapter(tasksAvailable: false)
            .RelatedTasksAsync(CommunicationTestHost.Context(), 1));
    }

    // Duplicate submission. A double-clicked dialog must not raise two tasks.
    [Fact]
    public async Task A_repeated_submission_with_the_same_key_returns_the_first_task_instead_of_a_second()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());
        var context = CommunicationTestHost.Context();

        var first = await Adapter().CreateFromMessageAsync(context, Request(commentId, dedupKey: "dialog-42"));
        var retry = await Adapter().CreateFromMessageAsync(context, Request(commentId, dedupKey: "dialog-42"));

        Assert.True(first.Ok, first.Error);
        Assert.True(retry.Ok, retry.Error);
        Assert.Equal(first.TaskId, retry.TaskId);

        using var fresh = _host.NewContext();
        Assert.Single(await fresh.Set<TaskItem>().AsNoTracking().ToListAsync());
    }

    // ...and a DIFFERENT key is a different task, so the test above is about the key and not about
    // the adapter refusing every second call.
    [Fact]
    public async Task A_different_key_raises_a_second_task()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(Customer());
        var context = CommunicationTestHost.Context();

        var first = await Adapter().CreateFromMessageAsync(context, Request(commentId, dedupKey: "dialog-1"));
        var second = await Adapter().CreateFromMessageAsync(context, Request(commentId, dedupKey: "dialog-2"));

        Assert.NotEqual(first.TaskId, second.TaskId);

        using var fresh = _host.NewContext();
        Assert.Equal(2, await fresh.Set<TaskItem>().AsNoTracking().CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // C. RELATED TASKS
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Related_tasks_are_the_tasks_about_this_conversations_record_and_no_others()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long ours = await SeedMessageAsync(Customer(5001));
        var mine = await Adapter().CreateFromMessageAsync(context, Request(ours));

        // A task about a DIFFERENT customer, raised from its own conversation.
        long other = await SeedMessageAsync(Customer(5002), "unrelated");
        var elsewhere = await Adapter().CreateFromMessageAsync(context, Request(other));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });

        var related = await Adapter().RelatedTasksAsync(context, thread.Id);

        // Asymmetric: a count alone cannot say WHICH task survived the filter.
        Assert.Contains(related, t => t.TaskId == mine.TaskId);
        Assert.DoesNotContain(related, t => t.TaskId == elsewhere.TaskId);
        Assert.All(related, t => Assert.Equal("/Tasks/Detail/" + t.TaskId, t.Route));
    }

    [Fact]
    public async Task Related_tasks_require_the_task_read_authority()
    {
        _access.Allow(TasksActions.Create);
        var context = CommunicationTestHost.Context();
        long commentId = await SeedMessageAsync(Customer());
        await Adapter().CreateFromMessageAsync(context, Request(commentId));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer() });

        // Read is deliberately NOT granted.
        Assert.Empty(await Adapter().RelatedTasksAsync(context, thread.Id));

        _access.Allow(TasksActions.Read);
        Assert.NotEmpty(await Adapter().RelatedTasksAsync(context, thread.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // D. TASK → CONVERSATION
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_task_exposes_its_own_conversation_and_the_record_it_is_about()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer());
        var created = await Adapter().CreateFromMessageAsync(context, Request(commentId));

        var conversation = await Adapter().ConversationForTaskAsync(context, created.TaskId);

        Assert.NotNull(conversation);
        Assert.Equal(created.TaskId, conversation!.TaskId);
        Assert.True(conversation.ThreadId > 0);
        Assert.Equal(EntityRegistry.Customer, conversation.AnchorEntityType);
        Assert.Equal(5001, conversation.AnchorEntityId);

        // It really is the thread anchored to the TASK, not to the customer.
        using var fresh = _host.NewContext();
        var thread = await fresh.Set<CrossBuy.Models.Context.Communication.CommThread>()
            .AsNoTracking().SingleAsync(t => t.Id == conversation.ThreadId);
        Assert.Equal(EntityRegistry.Task, thread.EntityType);
        Assert.Equal(created.TaskId, thread.EntityId);
    }

    [Fact]
    public async Task Another_companys_task_exposes_no_conversation()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer());
        var created = await Adapter().CreateFromMessageAsync(context, Request(commentId));

        var intruder = CommunicationTestHost.Context(
            employeeId: CommunicationTestHost.OtherCompanyEmployee,
            companyId: CommunicationTestHost.OtherCompanyId);

        Assert.Null(await Adapter().ConversationForTaskAsync(intruder, created.TaskId));
    }

    // ---------------------------------------------------------------------------------------------
    // THE ANCHOR TaskService WILL NOT STORE.
    //
    // TaskService.SaveAsync keeps a hardcoded link allow-list and silently nulls anything outside it.
    // A conversation on a PurchaseInvoice is perfectly ordinary, and a task raised from it would come
    // back unlinked with no indication why. The adapter reports it instead.
    // ---------------------------------------------------------------------------------------------
    // ELIGIBILITY COMES FROM TaskEntityLinkPolicy, NOT FROM A COPY HERE.
    //
    // This test used to assert that a PurchaseInvoice anchor could not be stored. TAB-5 then opted the
    // four Communication anchors in, and the assertion kept passing only because the adapter still held
    // its own stale list — a test proving this file's belief rather than the Tasks module's behaviour.
    // It now asserts the real contract from both sides.
    [Fact]
    public async Task An_anchor_the_Tasks_module_now_accepts_is_carried_through()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(new CommEntityRef(EntityRegistry.PurchaseInvoice, 7001));

        var result = await Adapter().CreateFromMessageAsync(CommunicationTestHost.Context(), Request(commentId));

        Assert.True(result.Ok, result.Error);
        Assert.Null(result.AnchorWarning);

        using var fresh = _host.NewContext();
        var task = await fresh.Set<TaskItem>().AsNoTracking().SingleAsync(t => t.ID == result.TaskId);
        Assert.Equal(EntityRegistry.PurchaseInvoice, task.EntityType);
        Assert.Equal(7001, task.EntityId);
    }

    // PurchaseOrder is commentable but deliberately NOT opted into task linking, so it is the honest
    // negative case: the task is still created, the caller is TOLD the anchor was not stored, and the
    // record it came from survives in the audit row rather than vanishing.
    [Fact]
    public async Task An_anchor_outside_the_opt_in_set_is_reported_rather_than_silently_dropped()
    {
        _access.Allow(TasksActions.Create);
        long commentId = await SeedMessageAsync(new CommEntityRef(EntityRegistry.PurchaseOrder, 8001));

        var result = await Adapter().CreateFromMessageAsync(CommunicationTestHost.Context(), Request(commentId));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("anchor_not_linkable:" + EntityRegistry.PurchaseOrder, result.AnchorWarning);

        using var fresh = _host.NewContext();
        var task = await fresh.Set<TaskItem>().AsNoTracking().SingleAsync(t => t.ID == result.TaskId);
        Assert.Null(task.EntityType);

        var audit = await fresh.Set<CrossBuy.Models.Context.Communication.CommAuditEntry>().AsNoTracking()
            .SingleAsync(a => a.Action == CommAuditActions.TaskCreatedFromMessage);
        Assert.Equal(EntityRegistry.PurchaseOrder, audit.EntityType);
        Assert.Equal(8001, audit.EntityId);
    }

    // The adapter must agree with the policy across the WHOLE published set, not just the two cases
    // above — otherwise the next opt-in drifts the same way the deleted copy did.
    [Fact]
    public void The_adapter_asks_the_published_policy_and_keeps_no_list_of_its_own()
    {
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "Communication",
            "CommTaskIntegration.cs"));

        // COMMENTS ARE STRIPPED FIRST. The first version of this guard scanned raw source and failed on
        // the word "Task" inside a prose sentence — a grep-shaped test reporting on English rather than
        // on code. What it must police is executable text.
        var code = StripComments(raw);

        // No restated entity codes. A literal here would be a second source of truth, which is exactly
        // the drift this guard exists to prevent.
        // Bound to the REGISTRY FLAG, not to TaskEntityLinkPolicy's helper shape. That helper has been a
        // property, then a method over a registry, then a property again while its owner iterates; a
        // guard that tracked its shape would break on their refactors instead of on a real defect.
        // EntityDefinition.EligibleForTaskLink is the thing both sides actually agree on.
        var eligibleCodes = new EntityRegistry(_host.Db).GetDefinitions()
            .Where(d => d.EligibleForTaskLink).Select(d => d.Code).ToList();
        Assert.NotEmpty(eligibleCodes);

        foreach (var eligible in eligibleCodes)
            Assert.DoesNotContain($"\"{eligible}\"", code, StringComparison.Ordinal);

        Assert.Contains("TaskEntityLinkPolicy.IsEligible", code, StringComparison.Ordinal);
    }

    // Removes // line comments and /* */ blocks. Not a C# parser — it does not need to be. It needs to
    // stop prose being mistaken for code, and a guard that over-strips would fail closed, which is the
    // safe direction for a test whose job is to forbid something.
    private static string StripComments(string source)
    {
        source = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var kept = source
            .Split('\n')
            .Select(line =>
            {
                int at = line.IndexOf("//", StringComparison.Ordinal);
                return at >= 0 ? line[..at] : line;
            });
        return string.Join("\n", kept);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ---------------------------------------------------------------------------------------------
    // THE TEST A MUTANT DEMANDED.
    //
    // Mutating RelatedTasksAsync's `t.CompanyId == context.CompanyId` to `t.CompanyId >= 0` changed NO
    // test result, because every fixture above creates tasks in one company only — so the tenant filter
    // had nothing to exclude and its removal was invisible. A conversation panel that leaked another
    // tenant's task titles would have shipped with a green suite.
    //
    // The other company's task is seeded DIRECTLY rather than through the adapter, because the adapter
    // correctly refuses to create it. The point is a row that already exists in the other tenant.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Related_tasks_never_include_another_companys_task_on_the_same_record()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var mine = await Adapter().CreateFromMessageAsync(context, Request(commentId));

        // Same entity code, same entity id, different company — the realistic case, because customer ids
        // are per company and 5001 exists in both.
        _host.Db.Set<TaskItem>().Add(new TaskItem
        {
            CompanyId = CommunicationTestHost.OtherCompanyId,
            Title = "مهمة شركة أخرى",
            AssigneeEmployeeId = CommunicationTestHost.OtherCompanyEmployee,
            CreatedByEmployeeId = CommunicationTestHost.OtherCompanyEmployee,
            Priority = "Normal",
            Status = "New",
            EntityType = EntityRegistry.Customer,
            EntityId = 5001,
            CreatedAt = DateTime.UtcNow,
        });
        await _host.Db.SaveChangesAsync();

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });

        var related = await Adapter().RelatedTasksAsync(context, thread.Id);

        Assert.Contains(related, t => t.TaskId == mine.TaskId);
        Assert.All(related, t => Assert.DoesNotContain("شركة أخرى", t.Title));
        Assert.Single(related);
    }

    // ---------------------------------------------------------------------------------------------
    // B. LINK AN EXISTING TASK — contract, authorization and isolation, against isolated storage.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Production_reports_linking_as_unprovisioned_rather_than_failing_at_the_database()
    {
        var production = Adapter(links: new UnprovisionedCommTaskLinkStore());

        Assert.False(production.LinkingAvailable);

        var result = await production.LinkExistingAsync(
            CommunicationTestHost.Context(), new CommTaskLinkRequest { ThreadId = 1, TaskId = 1 });

        Assert.False(result.Ok);
        Assert.Equal("link_persistence_not_provisioned", result.Error);
    }

    [Fact]
    public async Task An_authorized_caller_links_an_existing_task_without_touching_its_own_anchor()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        // A task that is already about customer 5001.
        long seedComment = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seedComment));

        // A DIFFERENT conversation, on a different record.
        long otherComment = await SeedMessageAsync(Customer(5002), "discuss here");
        var otherThread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        var linked = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = otherThread.Id, TaskId = task.TaskId, CommentId = otherComment,
        });

        Assert.True(linked.Ok, linked.Error);
        Assert.NotNull(linked.LinkId);

        // THE ANCHOR IS UNTOUCHED. This is the whole reason the link is a row and not a column.
        using var fresh = _host.NewContext();
        var stored = await fresh.Set<TaskItem>().AsNoTracking().SingleAsync(t => t.ID == task.TaskId);
        Assert.Equal(EntityRegistry.Customer, stored.EntityType);
        Assert.Equal(5001, stored.EntityId);

        // And the link is reported as an EXPLICIT one, carrying its message provenance.
        var links = await Adapter().LinkedTasksAsync(context, otherThread.Id);
        var row = Assert.Single(links);
        Assert.Equal(CommTaskRelationship.ExplicitLink, row.Relationship);
        Assert.Equal(otherComment, row.CommentId);
        Assert.Equal(task.TaskId, row.TaskId);
    }

    // Shared context and an explicit link are different claims and must never be merged.
    [Fact]
    public async Task Shared_business_context_is_never_reported_as_an_explicit_link()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        await Adapter().CreateFromMessageAsync(context, Request(commentId));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });

        var related = await Adapter().RelatedTasksAsync(context, thread.Id);
        var linked = await Adapter().LinkedTasksAsync(context, thread.Id);

        Assert.All(related, t =>
        {
            Assert.Equal(CommTaskRelationship.SharedBusinessContext, t.Relationship);
            Assert.Null(t.LinkId);
        });
        Assert.Empty(linked);   // nobody linked anything
    }

    [Fact]
    public async Task Linking_requires_the_edit_authority_and_a_visible_task()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(commentId));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        // Edit is NOT granted.
        var refused = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        Assert.False(refused.Ok);
        Assert.Equal("not_authorized_to_link_task", refused.Error);

        _access.Allow(TasksActions.Edit);
        var allowed = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        Assert.True(allowed.Ok, allowed.Error);
    }

    [Fact]
    public async Task A_task_from_another_company_cannot_be_linked_and_is_indistinguishable_from_absent()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });

        _host.Db.Set<TaskItem>().Add(new TaskItem
        {
            CompanyId = CommunicationTestHost.OtherCompanyId,
            Title = "theirs",
            AssigneeEmployeeId = CommunicationTestHost.OtherCompanyEmployee,
            CreatedByEmployeeId = CommunicationTestHost.OtherCompanyEmployee,
            Priority = "Normal", Status = "New", CreatedAt = DateTime.UtcNow,
        });
        await _host.Db.SaveChangesAsync();

        using var peek = _host.NewContext();
        int theirTaskId = await peek.Set<TaskItem>().AsNoTracking()
            .Where(t => t.CompanyId == CommunicationTestHost.OtherCompanyId).Select(t => t.ID).SingleAsync();

        var real = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = theirTaskId });
        var absent = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = 999999 });

        Assert.False(real.Ok);
        Assert.Equal("task_not_found", real.Error);
        Assert.Equal(absent.Error, real.Error);

        Assert.Empty(await Adapter().LinkedTasksAsync(context, thread.Id));
    }

    [Fact]
    public async Task A_message_from_a_different_conversation_cannot_be_used_as_the_links_provenance()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long elsewhere = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(elsewhere));

        long here = await SeedMessageAsync(Customer(5002), "this conversation");
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        var result = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id, TaskId = task.TaskId, CommentId = elsewhere,
        });

        Assert.False(result.Ok);
        Assert.Equal("message_not_in_conversation", result.Error);

        // The honest control: the message that IS in this conversation is accepted.
        var ok = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id, TaskId = task.TaskId, CommentId = here,
        });
        Assert.True(ok.Ok, ok.Error);
    }

    // L4. The Read gate before the Edit gate is not redundant — it decides WHICH refusal a caller gets.
    // Somebody who may edit a task but may not see it must be told the task is absent, not that they lack
    // an authority, because the second answer confirms the task exists. A mutation run showed no test
    // distinguished the two, because every fixture granted Read and Edit together.
    [Fact]
    public async Task A_task_the_caller_cannot_read_is_reported_as_absent_rather_than_as_forbidden()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(commentId));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        // Edit WITHOUT Read: the caller may change tasks but may not see this one.
        var editOnly = new StubTasksAccess();
        editOnly.Allow(TasksActions.Edit);
        var adapter = new CommTaskIntegrationService(
            _host.Db, _host.Threads(), _host.Comments(), _host.Audit(), _host.Access(), _links,
            ServicesWith(editOnly), NullLogger<CommTaskIntegrationService>.Instance);

        var result = await adapter.LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });

        Assert.False(result.Ok);
        Assert.Equal("task_not_found", result.Error);
        Assert.NotEqual("not_authorized_to_link_task", result.Error);
    }

    // L2. The company predicate when FOLLOWING a link is defence against a bad link row, not against a
    // bad caller — so only a deliberately wrong store can exercise it. Without this the adapter would
    // trust a pointer and hand back another tenant's task.
    [Fact]
    public async Task A_link_row_pointing_at_another_companys_task_is_not_followed()
    {
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        await SeedMessageAsync(Customer(5001));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });

        _host.Db.Set<TaskItem>().Add(new TaskItem
        {
            CompanyId = CommunicationTestHost.OtherCompanyId,
            Title = "مهمة شركة أخرى",
            AssigneeEmployeeId = CommunicationTestHost.OtherCompanyEmployee,
            CreatedByEmployeeId = CommunicationTestHost.OtherCompanyEmployee,
            Priority = "Normal", Status = "New", CreatedAt = DateTime.UtcNow,
        });
        await _host.Db.SaveChangesAsync();

        using var peek = _host.NewContext();
        int theirTaskId = await peek.Set<TaskItem>().AsNoTracking()
            .Where(t => t.CompanyId == CommunicationTestHost.OtherCompanyId).Select(t => t.ID).SingleAsync();

        // A store that hands back a link, in OUR company, pointing at THEIR task.
        var rogue = new RogueLinkStore(thread.Id, theirTaskId);
        var adapter = Adapter(links: rogue);

        Assert.Empty(await adapter.LinkedTasksAsync(context, thread.Id));
    }

    [Fact]
    public async Task Linking_the_same_task_twice_produces_one_link()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(commentId));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        var request = new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId };
        var first = await Adapter().LinkExistingAsync(context, request);
        var retry = await Adapter().LinkExistingAsync(context, request);

        Assert.Equal(first.LinkId, retry.LinkId);
        Assert.Single(await Adapter().LinkedTasksAsync(context, thread.Id));
    }

    [Fact]
    public async Task One_task_can_be_linked_to_several_messages_in_the_same_conversation()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var first = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "first"));
        var second = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "second"));

        var a = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = first.CommentId });
        var b = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = second.CommentId });

        Assert.True(a.Ok, a.Error);
        Assert.True(b.Ok, b.Error);
        Assert.NotEqual(a.LinkId, b.LinkId);             // two distinct relationships
        Assert.Null(a.LinkWarning);                      // neither was downgraded
        Assert.Null(b.LinkWarning);

        var links = await Adapter().LinkedTasksAsync(context, thread.Id);
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.CommentId == first.CommentId);
        Assert.Contains(links, l => l.CommentId == second.CommentId);
    }

    [Fact]
    public async Task One_message_can_be_linked_to_several_tasks()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seedA = await SeedMessageAsync(Customer(5001));
        var taskOne = await Adapter().CreateFromMessageAsync(context, Request(seedA, dedupKey: "one"));
        var taskTwo = await Adapter().CreateFromMessageAsync(context, Request(seedA, dedupKey: "two"));
        Assert.NotEqual(taskOne.TaskId, taskTwo.TaskId);

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "one message"));

        var a = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = taskOne.TaskId, CommentId = message.CommentId });
        var b = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = taskTwo.TaskId, CommentId = message.CommentId });

        Assert.True(a.Ok, a.Error);
        Assert.True(b.Ok, b.Error);

        var links = await Adapter().LinkedTasksAsync(context, thread.Id);
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(message.CommentId, l.CommentId));
    }

    // A conversation-level link and a message-level link on the SAME pair are different
    // relationships. The two filtered indexes partition on CommentId IS NULL / IS NOT NULL, so
    // neither displaces the other.
    [Fact]
    public async Task A_conversation_level_link_coexists_with_message_level_links()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "here"));

        var conversationLevel = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        var messageLevel = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = message.CommentId });

        Assert.NotEqual(conversationLevel.LinkId, messageLevel.LinkId);
        Assert.Null(messageLevel.LinkWarning);           // no downgrade - it got what it asked for

        var links = await Adapter().LinkedTasksAsync(context, thread.Id);
        Assert.Equal(2, links.Count);
        Assert.Single(links, l => l.CommentId == null);
        Assert.Single(links, l => l.CommentId == message.CommentId);
    }

    // Idempotency survives the migration - for the EXACT same relationship, which now includes the
    // message. Asserted alongside a different message, so it cannot pass by refusing everything.
    [Fact]
    public async Task The_exact_same_relationship_is_still_idempotent()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "m"));
        var other = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "other"));

        var request = new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = message.CommentId };

        var first = await Adapter().LinkExistingAsync(context, request);
        var retry = await Adapter().LinkExistingAsync(context, request);
        Assert.Equal(first.LinkId, retry.LinkId);        // same message -> same relationship

        var different = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = other.CommentId });
        Assert.NotEqual(first.LinkId, different.LinkId); // different message -> different one

        Assert.Equal(2, (await Adapter().LinkedTasksAsync(context, thread.Id)).Count);
    }

    // Each relationship is removable on its own - the requirement that made the migration necessary.
    [Fact]
    public async Task Unlinking_one_message_relationship_leaves_the_others_intact()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var first = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "first"));
        var second = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "second"));

        var a = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = first.CommentId });
        var b = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = second.CommentId });

        Assert.True((await Adapter().UnlinkAsync(context, a.LinkId!.Value)).Ok);

        var remaining = await Adapter().LinkedTasksAsync(context, thread.Id);
        var kept = Assert.Single(remaining);
        Assert.Equal(b.LinkId, kept.LinkId);
        Assert.Equal(second.CommentId, kept.CommentId);

        // And the removed one can be linked again - the indexes are filtered to live rows.
        var relinked = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = first.CommentId });
        Assert.True(relinked.Ok, relinked.Error);
        Assert.NotEqual(a.LinkId, relinked.LinkId);      // a NEW row, not a resurrection
        Assert.Equal(2, (await Adapter().LinkedTasksAsync(context, thread.Id)).Count);
    }

    // =============================================================================================
    // ORIGIN FIDELITY - the release blocker.
    //
    // A task raised FROM a message and a task ATTACHED to a conversation afterwards are different
    // events, and the link row is the only place that difference is ever recorded. Origin was
    // hardcoded to LinkedExisting, so CreatedFromMessage was written nowhere in the system and the
    // provenance of every task-from-message was quietly lost at the moment it was created.
    //
    // WHY THE CALLER DECLARES IT. Creating the task and writing the link are two acts: the task is
    // created by CreateFromMessageAsync, and the link is written by the controller immediately after
    // through LinkExistingAsync. From inside the service that second write is indistinguishable from
    // somebody attaching an existing task, so the origin cannot be inferred - only declared.
    // =============================================================================================

    // The load-bearing test. Both origins are driven in ONE test against ONE store, so it cannot pass
    // with the value hardcoded either way: reverting to a fixed LinkedExisting fails the first
    // assertion, and a fixed CreatedFromMessage fails the second.
    [Fact]
    public async Task A_task_raised_from_a_message_is_recorded_as_CreatedFromMessage_and_an_attached_one_is_not()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var raised = await Adapter().CreateFromMessageAsync(context, Request(seed));
        var attached = await Adapter().CreateFromMessageAsync(context, Request(seed, dedupKey: "other"));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "raise one"));

        // What the controller does after creating the task from this message.
        var fromMessage = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id,
            TaskId = raised.TaskId,
            CommentId = message.CommentId,
            Origin = CommTaskLinkOrigin.CreatedFromMessage,
        });

        // What attaching an already-existing task to the same conversation does.
        var ordinary = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id,
            TaskId = attached.TaskId,
            CommentId = message.CommentId,
            Origin = CommTaskLinkOrigin.LinkedExisting,
        });

        Assert.True(fromMessage.Ok, fromMessage.Error);
        Assert.True(ordinary.Ok, ordinary.Error);

        var rows = await _links.ForThreadAsync(context.CompanyId, thread.Id);
        Assert.Equal(
            CommTaskLinkOrigin.CreatedFromMessage,
            rows.Single(r => r.Id == fromMessage.LinkId).Origin);
        Assert.Equal(
            CommTaskLinkOrigin.LinkedExisting,
            rows.Single(r => r.Id == ordinary.LinkId).Origin);
    }

    // Attaching is the common case and the safe default: a caller that says nothing must not have a
    // creation provenance invented for it.
    [Fact]
    public async Task Attaching_an_existing_task_records_LinkedExisting_without_the_caller_saying_so()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        var link = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });

        Assert.True(link.Ok, link.Error);
        var row = Assert.Single(await _links.ForThreadAsync(context.CompanyId, thread.Id));
        Assert.Equal(CommTaskLinkOrigin.LinkedExisting, row.Origin);
    }

    // The service refuses an unknown origin ITSELF, proven against a store that would happily accept
    // one. The real store and the CK_CommTaskLinks_Origin check constraint both refuse it too - this
    // asserts the outermost of the three, so removing it is a failure rather than a silent demotion
    // to a database error at the end of somebody's action.
    [Fact]
    public async Task An_origin_outside_the_vocabulary_is_refused_before_the_store_is_asked()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        var permissive = new PermissiveOriginLinkStore();
        var result = await Adapter(links: permissive).LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id,
            TaskId = task.TaskId,
            Origin = "Imported",            // plausible, and not in the vocabulary
        });

        Assert.False(result.Ok);
        Assert.Equal(CommTaskErrors.LinkRejected, result.Error);
        Assert.False(permissive.WasAsked);   // refused BEFORE the write was attempted

        // The control: the same store, the same pair, a valid origin - so the refusal above is about
        // the origin and not about anything else in the request.
        var valid = await Adapter(links: permissive).LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id,
            TaskId = task.TaskId,
            Origin = CommTaskLinkOrigin.CreatedFromMessage,
        });
        Assert.True(valid.Ok, valid.Error);
        Assert.True(permissive.WasAsked);
    }

    // ONE user action, ONE link row. The controller writes the link after creating the task; if this
    // service also wrote one, the same action would produce two rows with two different origins and
    // the panel would show the task twice.
    [Fact]
    public async Task Creating_a_task_from_a_message_writes_no_link_of_its_own()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        Assert.True(task.Ok, task.Error);
        Assert.Empty(await _links.ForTaskAsync(context.CompanyId, task.TaskId));
    }

    // THE PARTIAL-FAILURE RETRY, driven as the controller actually performs it.
    //
    // Creating the task and linking it are two writes, so the window between them is real: the task can
    // exist with no link. The controller's recovery is to re-send the SAME request, and that recovery is
    // only safe if both halves are idempotent — otherwise retrying a half-completed action leaves either
    // two tasks or two links.
    //
    // The two properties are asserted separately elsewhere. This drives the SEQUENCE, because that is
    // what a user hitting the button again does, and composing two individually-safe steps is not by
    // itself a proof that the pair is safe.
    [Fact]
    public async Task Retrying_after_the_link_half_failed_completes_the_pair_without_duplicating_either()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "do this"));

        // FIRST ATTEMPT: the task is created, and the link write does not happen - the partial state.
        var first = await Adapter().CreateFromMessageAsync(context, Request(seed, dedupKey: "retry-me"));
        Assert.True(first.Ok, first.Error);
        Assert.Empty(await _links.ForTaskAsync(context.CompanyId, first.TaskId));

        // THE RETRY: the identical request, both halves this time.
        var second = await Adapter().CreateFromMessageAsync(context, Request(seed, dedupKey: "retry-me"));
        Assert.Equal(first.TaskId, second.TaskId);          // DedupKey returned the SAME task

        var link = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id,
            TaskId = second.TaskId,
            CommentId = message.CommentId,
            Origin = CommTaskLinkOrigin.CreatedFromMessage,
        });
        Assert.True(link.Ok, link.Error);

        // A SECOND retry, now that the pair is complete, must still change nothing.
        var third = await Adapter().CreateFromMessageAsync(context, Request(seed, dedupKey: "retry-me"));
        var relink = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        {
            ThreadId = thread.Id,
            TaskId = third.TaskId,
            CommentId = message.CommentId,
            Origin = CommTaskLinkOrigin.CreatedFromMessage,
        });

        Assert.Equal(first.TaskId, third.TaskId);
        Assert.Equal(link.LinkId, relink.LinkId);           // the SAME link row, not a second

        var rows = await _links.ForTaskAsync(context.CompanyId, first.TaskId);
        var only = Assert.Single(rows);
        Assert.Equal(message.CommentId, only.CommentId);    // provenance survived the retry
        Assert.Equal(CommTaskLinkOrigin.CreatedFromMessage, only.Origin);
    }

    // A store that records whatever it is handed. It exists so the SERVICE's own origin check can be
    // observed on its own, rather than passing because something further down also refuses.
    private sealed class PermissiveOriginLinkStore : ICommTaskLinkStore
    {
        private readonly List<CommTaskLinkRow> _rows = new();
        private long _next = 1;

        public bool WasAsked { get; private set; }
        public bool IsProvisioned => true;

        public Task<CommTaskLinkRow?> AddAsync(int companyId, int taskId, long threadId, long? commentId,
            string origin, int? createdBy, CancellationToken cancellationToken = default)
        {
            WasAsked = true;
            var row = new CommTaskLinkRow
            {
                Id = _next++, TaskId = taskId, ThreadId = threadId, CommentId = commentId,
                Origin = origin, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
            };
            _rows.Add(row);
            return Task.FromResult<CommTaskLinkRow?>(row);
        }

        public Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(int companyId, long threadId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(
                _rows.Where(l => l.ThreadId == threadId).ToList());

        public Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(int companyId, int taskId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(
                _rows.Where(l => l.TaskId == taskId).ToList());

        public Task<CommTaskLinkRow?> GetAsync(int companyId, long linkId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.FirstOrDefault(l => l.Id == linkId));

        public Task<bool> RemoveAsync(int companyId, long linkId, int? removedBy,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.RemoveAll(l => l.Id == linkId) > 0);
    }

    [Fact]
    public async Task Unlinking_removes_it_and_a_second_unlink_reports_it_is_gone()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(commentId));
        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });

        var linked = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });

        var removed = await Adapter().UnlinkAsync(context, linked.LinkId!.Value);
        Assert.True(removed.Ok, removed.Error);
        Assert.Empty(await Adapter().LinkedTasksAsync(context, thread.Id));

        var again = await Adapter().UnlinkAsync(context, linked.LinkId!.Value);
        Assert.False(again.Ok);
        Assert.Equal("link_not_found", again.Error);
    }

    // ---------------------------------------------------------------------------------------------
    private sealed class InMemoryCommTaskLinkStore : ICommTaskLinkStore
    {
        private readonly List<CommTaskLink> _rows = new();
        private long _next = 1;

        public bool IsProvisioned => true;

        public Task<CommTaskLinkRow?> AddAsync(int companyId, int taskId, long threadId, long? commentId,
            string origin, int? createdBy, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || taskId <= 0 || threadId <= 0) return Task.FromResult<CommTaskLinkRow?>(null);
            if (!CommTaskLinkOrigin.IsValid(origin)) return Task.FromResult<CommTaskLinkRow?>(null);

            // Mirrors the real store and the TWO filtered unique indexes: CommentId is part of the
            // live identity, and NULL is a value — a conversation-level link and a message-level link
            // on the same pair are different relationships that coexist.
            var existing = _rows.FirstOrDefault(l => l.CompanyID == companyId && l.TaskId == taskId
                && l.ThreadId == threadId && l.CommentId == commentId && l.RemovedAt == null);
            if (existing != null) return Task.FromResult<CommTaskLinkRow?>(Map(existing));

            var row = new CommTaskLink
            {
                Id = _next++, CompanyID = companyId, TaskId = taskId, ThreadId = threadId,
                CommentId = commentId, Origin = origin, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
            };
            _rows.Add(row);
            return Task.FromResult<CommTaskLinkRow?>(Map(row));
        }

        public Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(int companyId, long threadId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(_rows
                .Where(l => l.CompanyID == companyId && l.ThreadId == threadId && l.RemovedAt == null)
                .Select(Map).ToList());

        public Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(int companyId, int taskId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(_rows
                .Where(l => l.CompanyID == companyId && l.TaskId == taskId && l.RemovedAt == null)
                .Select(Map).ToList());

        public Task<CommTaskLinkRow?> GetAsync(int companyId, long linkId,
            CancellationToken cancellationToken = default)
        {
            var row = _rows.FirstOrDefault(l => l.CompanyID == companyId && l.Id == linkId && l.RemovedAt == null);
            return Task.FromResult(row == null ? null : Map(row));
        }

        public Task<bool> RemoveAsync(int companyId, long linkId, int? removedBy,
            CancellationToken cancellationToken = default)
        {
            var row = _rows.FirstOrDefault(l => l.CompanyID == companyId && l.Id == linkId && l.RemovedAt == null);
            if (row == null) return Task.FromResult(false);
            row.RemovedAt = DateTime.UtcNow; row.RemovedBy = removedBy;
            return Task.FromResult(true);
        }

        private static CommTaskLinkRow Map(CommTaskLink l) => new()
        {
            Id = l.Id, TaskId = l.TaskId, ThreadId = l.ThreadId, CommentId = l.CommentId,
            Origin = l.Origin, CreatedAt = l.CreatedAt, CreatedBy = l.CreatedBy,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // §B — THE SECURE ENTITY LOOKUP.
    // ---------------------------------------------------------------------------------------------

    // The mistake this adapter exists to prevent. The obvious composition for a host screen is
    // GetOrCreateAsync, which WRITES a thread row every time somebody opens the record. Rendering a
    // panel is a read.
    [Fact]
    public async Task Opening_a_record_with_no_conversation_creates_nothing()
    {
        _access.Allow(TasksActions.Read);
        var panel = await Adapter().PanelForEntityAsync(CommunicationTestHost.Context(), Customer(5555));

        Assert.NotNull(panel);
        Assert.Null(panel!.ThreadId);           // no conversation yet, and none invented
        Assert.Empty(panel.SharedContext);
        Assert.Empty(panel.ExplicitLinks);

        using var fresh = _host.NewContext();
        Assert.Empty(await fresh.Set<CommThread>().AsNoTracking()
            .Where(t => t.EntityId == 5555).ToListAsync());
    }

    // THE CANONICAL THREAD, NOT THE MOST RECENTLY ACTIVE ONE.
    //
    // ListForEntityAsync returns every kind of thread on the record, newest activity first. A record can
    // carry a Notes or Review thread alongside its discussion, and if that one is busier it sorts first —
    // so "take the first" quietly returns a different conversation under the same name. The panel must
    // select by IDENTITY (Discussion + empty ThreadKey), which is what UX_CommThreads_Anchor keys on.
    [Fact]
    public async Task The_panel_returns_the_discussion_thread_even_when_another_kind_is_more_recent()
    {
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        // The discussion, created first and then left quiet.
        var discussion = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });

        // A Notes thread on the SAME record, created afterwards so it is the more recent one.
        var notes = await _host.Threads().GetOrCreateAsync(context, new CommThreadRequest
        {
            Entity = Customer(5001),
            Kind = CommThreadKind.Notes,
        });

        Assert.NotEqual(discussion.Id, notes.Id);

        var panel = await Adapter().PanelForEntityAsync(context, Customer(5001));

        Assert.NotNull(panel);
        Assert.Equal(discussion.Id, panel!.ThreadId);
        Assert.NotEqual(notes.Id, panel.ThreadId);
    }

    [Fact]
    public async Task The_panel_resolves_the_existing_conversation_and_both_relationships()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(commentId));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5001) });
        await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });

        var panel = await Adapter().PanelForEntityAsync(context, Customer(5001));

        Assert.NotNull(panel);
        Assert.Equal(thread.Id, panel!.ThreadId);
        Assert.Equal(EntityRegistry.Customer, panel.EntityType);
        Assert.Equal(5001, panel.EntityId);
        Assert.True(panel.LinkingAvailable);    // the in-memory store is provisioned

        // The two relationships arrive SEPARATELY and labelled, never merged into one list.
        Assert.All(panel.SharedContext,
            t => Assert.Equal(CommTaskRelationship.SharedBusinessContext, t.Relationship));
        Assert.All(panel.ExplicitLinks,
            t => Assert.Equal(CommTaskRelationship.ExplicitLink, t.Relationship));
        Assert.Contains(panel.ExplicitLinks, t => t.TaskId == task.TaskId);
    }

    // P0 DIAGNOSIS — WHY THE LINK ACTION IS ABSENT ON EVERY RECORD SCREEN.
    //
    // The panel is what the screens draw from, and the button's visibility rests on exactly one field
    // of it: canLink() in _EntityConversation.cshtml requires state.linkingAvailable === true, and
    // syncMessageLinkButtons() gates the per-message controls on the SAME call. So one false flag
    // removes both the conversation-level and the message-level action, silently and everywhere.
    //
    // TWO ADAPTERS OVER ONE DATASET, which is the point: identical company, identical caller,
    // identical permissions, identical rows — only the STORE differs. If the flag followed
    // authorization or the data, both answers would agree. It follows provisioning alone.
    //
    // AND THE REST OF THE PANEL STILL ANSWERS. That is the reported symptom exactly: the conversation
    // renders, the messages are there, the related work is listed, and only the action is gone. A
    // test that merely asserted False would also pass against a panel that returned nothing at all,
    // which is a different defect with a different fix.
    [Fact]
    public async Task An_unprovisioned_store_hides_linking_while_the_rest_of_the_panel_still_answers()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long commentId = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(commentId));

        // POSITIVE CONTROL, same data: with a provisioned store the action is offered.
        var provisioned = await Adapter().PanelForEntityAsync(context, Customer(5001));
        Assert.NotNull(provisioned);
        Assert.True(provisioned!.LinkingAvailable);

        var unprovisioned = await Adapter(links: new UnprovisionedCommTaskLinkStore())
            .PanelForEntityAsync(context, Customer(5001));

        Assert.NotNull(unprovisioned);
        Assert.False(unprovisioned!.LinkingAvailable);

        // The conversation and its related work are UNAFFECTED — same thread, same shared context.
        Assert.Equal(provisioned.ThreadId, unprovisioned.ThreadId);
        Assert.True(unprovisioned.ThreadId > 0);
        Assert.Contains(unprovisioned.SharedContext, t => t.TaskId == task.TaskId);

        // Explicit links read as empty rather than throwing: an unprovisioned store has no table to
        // query, and "none" is the honest answer for a relationship that cannot be recorded.
        Assert.Empty(unprovisioned.ExplicitLinks);
    }

    // BOTH LEVELS FAIL THE SAME WAY, AND THEY SAY WHY.
    //
    // The UI half of this is TAB-1's: the panel currently renders nothing at all when linking is
    // unprovisioned, which is what made the 2026-10-27 disappearance read as a defect. The SERVICE
    // half is pinned here — whatever the panel chooses to draw, the server must hand it a reason it
    // can draw, and the same reason for both link levels.
    //
    // A GENERIC FAILURE WOULD BE THE REGRESSION. "link_rejected" or an exception from the driver
    // would both be true statements and both useless: the first blames the request, the second
    // blames the database. Only link_persistence_not_provisioned names the thing an operator can fix.
    [Fact]
    public async Task Both_link_levels_refuse_with_the_provisioning_code_when_the_store_is_unprovisioned()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        _access.Allow(TasksActions.Edit);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "here"));

        var unprovisioned = Adapter(links: new UnprovisionedCommTaskLinkStore());

        var conversationLevel = await unprovisioned.LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        var messageLevel = await unprovisioned.LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = message.CommentId });

        Assert.False(conversationLevel.Ok);
        Assert.False(messageLevel.Ok);
        Assert.Equal(CommTaskErrors.LinkPersistenceNotProvisioned, conversationLevel.Error);
        Assert.Equal(CommTaskErrors.LinkPersistenceNotProvisioned, messageLevel.Error);

        // Not a denial, and not a bad request. Those are the two codes this must never be mistaken
        // for, because each sends somebody to fix the wrong thing.
        Assert.NotEqual(CommTaskErrors.NotAuthorizedToLinkTask, conversationLevel.Error);
        Assert.NotEqual(CommTaskErrors.LinkRejected, conversationLevel.Error);

        // And the panel reports it ONCE, as availability, so the UI never has to infer the state
        // from a failed write it has not attempted yet.
        var panel = await unprovisioned.PanelForEntityAsync(context, Customer(5002));
        Assert.NotNull(panel);
        Assert.False(panel!.LinkingAvailable);

        // POSITIVE CONTROL: the same two calls on a provisioned store both succeed, so neither
        // assertion above can be passing because the requests were malformed.
        var okConversation = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        var okMessage = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = message.CommentId });
        Assert.True(okConversation.Ok, okConversation.Error);
        Assert.True(okMessage.Ok, okMessage.Error);
    }

    // PROVISIONING IS NOT A PERMISSION.
    //
    // Turning the flag on restores a path that was returning 503 to everybody, and the thing to be
    // sure of is that it comes back AUTHORIZED rather than merely back. The provisioning check is
    // deliberately the first refusal in LinkExistingAsync, so it is exactly the kind of guard whose
    // removal could be mistaken for "the feature now works".
    //
    // Both levels, because they are separate requests through the same method and a guard that
    // covered only the conversation case would leave message-level linking open.
    [Fact]
    public async Task Provisioning_does_not_bypass_authorization_for_either_link_level()
    {
        _access.Allow(TasksActions.Create);
        _access.Allow(TasksActions.Read);
        var context = CommunicationTestHost.Context();

        long seed = await SeedMessageAsync(Customer(5001));
        var task = await Adapter().CreateFromMessageAsync(context, Request(seed));

        var thread = await _host.Threads().GetOrCreateAsync(
            context, new CommThreadRequest { Entity = Customer(5002) });
        var message = await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5002), "here"));

        // The store is provisioned throughout. Edit is NOT granted yet.
        var refusedConversation = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        var refusedMessage = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = message.CommentId });

        Assert.False(refusedConversation.Ok);
        Assert.False(refusedMessage.Ok);
        Assert.Equal(CommTaskErrors.NotAuthorizedToLinkTask, refusedConversation.Error);
        Assert.Equal(CommTaskErrors.NotAuthorizedToLinkTask, refusedMessage.Error);

        // Nothing was written while the caller was unauthorized.
        Assert.Empty(await Adapter().LinkedTasksAsync(context, thread.Id));

        // Granting the authority is what opens it — both levels, and they remain distinct rows.
        _access.Allow(TasksActions.Edit);
        var allowedConversation = await Adapter().LinkExistingAsync(
            context, new CommTaskLinkRequest { ThreadId = thread.Id, TaskId = task.TaskId });
        var allowedMessage = await Adapter().LinkExistingAsync(context, new CommTaskLinkRequest
        { ThreadId = thread.Id, TaskId = task.TaskId, CommentId = message.CommentId });

        Assert.True(allowedConversation.Ok, allowedConversation.Error);
        Assert.True(allowedMessage.Ok, allowedMessage.Error);
        Assert.NotEqual(allowedConversation.LinkId, allowedMessage.LinkId);

        var links = await Adapter().LinkedTasksAsync(context, thread.Id);
        Assert.Equal(2, links.Count);
        Assert.Single(links, l => l.CommentId == null);
        Assert.Single(links, l => l.CommentId == message.CommentId);
    }

    // The anchor comes from a URL, so it is attacker-controlled.
    [Fact]
    public async Task An_anchor_the_caller_may_not_converse_on_yields_no_panel()
    {
        _access.Allow(TasksActions.Read);

        // A caller with NO permission on the record. My first attempt used an entity code I believed
        // was un-onboarded; the test host onboards it, so the test was asserting my assumption rather
        // than the guard. Permission is the real boundary and this exercises it.
        Assert.Null(await Adapter(permissions: new StubPermissionProvider())
            .PanelForEntityAsync(CommunicationTestHost.Context(), Customer(5001)));

        // The control: the same record IS visible to a caller who may view it.
        Assert.NotNull(await Adapter().PanelForEntityAsync(
            CommunicationTestHost.Context(), Customer(5001)));

        // An unresolved company gets nothing regardless of the anchor.
        Assert.Null(await Adapter().PanelForEntityAsync(
            CommunicationTestHost.Context(companyId: 0), Customer(5001)));
    }

    [Fact]
    public async Task Another_companys_caller_never_sees_this_companys_conversation_through_the_panel()
    {
        _access.Allow(TasksActions.Read);
        await SeedMessageAsync(Customer(5001));

        var intruder = CommunicationTestHost.Context(
            employeeId: CommunicationTestHost.OtherCompanyEmployee,
            companyId: CommunicationTestHost.OtherCompanyId);

        var theirs = await Adapter().PanelForEntityAsync(intruder, Customer(5001));

        // Either refused outright, or their OWN empty panel — never our thread.
        if (theirs != null)
        {
            Assert.Null(theirs.ThreadId);
            Assert.Empty(theirs.SharedContext);
            Assert.Empty(theirs.ExplicitLinks);
        }

        // The control: our caller DOES see it, so the emptiness above is the boundary and not an
        // accident of there being no conversation.
        var ours = await Adapter().PanelForEntityAsync(CommunicationTestHost.Context(), Customer(5001));
        Assert.NotNull(ours);
        Assert.NotNull(ours!.ThreadId);
    }

    // The published error vocabulary is what two other tabs bind to. A code returned by the service and
    // absent from the list would be an unhandled case on somebody else's screen.
    [Fact]
    public async Task Every_refusal_uses_a_published_error_code()
    {
        var context = CommunicationTestHost.Context();

        var refusals = new[]
        {
            (await Adapter().CreateFromMessageAsync(
                CommunicationTestHost.Context(companyId: 0), Request(1))).Error,
            (await Adapter().CreateFromMessageAsync(context, Request(999999))).Error,
            (await Adapter(links: new UnprovisionedCommTaskLinkStore())
                .LinkExistingAsync(context, new CommTaskLinkRequest { ThreadId = 1, TaskId = 1 })).Error,
            (await Adapter(tasksAvailable: false).CreateFromMessageAsync(context, Request(1))).Error,
            (await Adapter().UnlinkAsync(context, 987654)).Error,
        };

        Assert.All(refusals, code =>
        {
            Assert.NotNull(code);
            Assert.Contains(code!, CommTaskErrors.All);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // §A — THE HUB'S BUSINESS-CONVERSATION LIST (participation-anchored).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task My_business_conversations_list_the_records_I_take_part_in()
    {
        var context = CommunicationTestHost.Context();

        // Commenting makes the author a participant, which is how a conversation becomes "mine".
        await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5001), "about this customer"));

        var mine = await Adapter().MyBusinessConversationsAsync(context);

        var row = Assert.Single(mine);
        Assert.Equal(EntityRegistry.Customer, row.EntityType);
        Assert.Equal(5001, row.EntityId);
        Assert.True(row.ThreadId > 0);
        Assert.True(row.CommentCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(row.Role));
    }

    // A conversation somebody else holds is not mine, however real it is. Asserted with a control so
    // an empty result cannot pass by accident.
    [Fact]
    public async Task A_conversation_I_do_not_participate_in_is_not_listed()
    {
        var author = CommunicationTestHost.Context();
        await _host.Comments().AddAsync(author, _host.CommentOn(Customer(5001), "their discussion"));

        // Colleague never joined it.
        var outsider = CommunicationTestHost.Context(employeeId: CommunicationTestHost.Colleague);
        Assert.Empty(await Adapter().MyBusinessConversationsAsync(outsider));

        // Control: the author does see it, so the emptiness is participation and not an empty store.
        Assert.NotEmpty(await Adapter().MyBusinessConversationsAsync(author));
    }

    [Fact]
    public async Task Another_companys_participant_row_cannot_surface_this_companys_conversation()
    {
        var ours = CommunicationTestHost.Context();
        await _host.Comments().AddAsync(ours, _host.CommentOn(Customer(5001), "ours"));

        var theirs = CommunicationTestHost.Context(
            employeeId: CommunicationTestHost.OtherCompanyEmployee,
            companyId: CommunicationTestHost.OtherCompanyId);

        Assert.Empty(await Adapter().MyBusinessConversationsAsync(theirs));
        Assert.NotEmpty(await Adapter().MyBusinessConversationsAsync(ours));
    }

    // A PARTICIPANT ROW THAT GENUINELY SPANS COMPANIES — the case the test above cannot reach.
    //
    // There, the other company's employee was not a participant of our thread at all, so the
    // PARTICIPATION filter did all the work and the company predicates were never exercised: a
    // multi-part mutant removing BOTH of them changed no result. The hazard they actually guard is a
    // participant row for THIS employee pointing at ANOTHER company's thread — exactly the shape that
    // forced the same predicate into the chat search. The row is seeded directly because the service
    // would refuse to create it, which is the point: it is the state that must not be trusted.
    [Fact]
    public async Task A_participant_row_pointing_at_another_companys_thread_is_not_followed()
    {
        // Their conversation, in their company.
        var theirs = CommunicationTestHost.Context(
            employeeId: CommunicationTestHost.OtherCompanyEmployee,
            companyId: CommunicationTestHost.OtherCompanyId);
        await _host.Comments().AddAsync(theirs, _host.CommentOn(Customer(7777), "their business"));

        using (var seed = _host.NewContext())
        {
            var foreign = await seed.Set<CommThread>().AsNoTracking()
                .SingleAsync(t => t.CompanyID == CommunicationTestHost.OtherCompanyId);

            // OUR employee, made a participant of THEIR thread. The row even carries their company,
            // so only a predicate comparing it against the CALLER's company can refuse it.
            seed.Set<CommParticipant>().Add(new CommParticipant
            {
                CompanyID = CommunicationTestHost.OtherCompanyId,
                ThreadId = foreign.Id,
                EntityType = foreign.EntityType,
                EntityId = foreign.EntityId,
                EmployeeId = CommunicationTestHost.Author,
                Role = "Participant",
                Source = "Test",
            });
            await seed.SaveChangesAsync();
        }

        var ours = CommunicationTestHost.Context();
        Assert.Empty(await Adapter().MyBusinessConversationsAsync(ours));

        // Control: a conversation genuinely in our company IS listed, so the emptiness above is the
        // company boundary and not the read being broken.
        await _host.Comments().AddAsync(ours, _host.CommentOn(Customer(5001), "ours"));
        var mine = await Adapter().MyBusinessConversationsAsync(ours);
        Assert.Single(mine);
        Assert.Equal(5001, mine[0].EntityId);
    }

    [Fact]
    public async Task An_unresolved_company_or_identity_lists_nothing()
    {
        var context = CommunicationTestHost.Context();
        await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5001), "x"));

        Assert.Empty(await Adapter().MyBusinessConversationsAsync(
            CommunicationTestHost.Context(companyId: 0)));
        Assert.Empty(await Adapter().MyBusinessConversationsAsync(
            CommunicationTestHost.Context(employeeId: 0)));
    }

    // PARTICIPATION IS NOT AUTHORITY. A participant row survives a permission being revoked, so the
    // list must re-check readability rather than trusting the row that put the person there.
    [Fact]
    public async Task A_participant_who_may_no_longer_read_the_record_does_not_see_it_listed()
    {
        var context = CommunicationTestHost.Context();
        await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5001), "sensitive"));

        // Same participant row, caller now holds no permission on the record.
        var revoked = Adapter(permissions: new StubPermissionProvider());
        Assert.Empty(await revoked.MyBusinessConversationsAsync(context));

        // Control: with permission the same row is listed.
        Assert.NotEmpty(await Adapter().MyBusinessConversationsAsync(context));
    }

    // Every row carries a real business anchor. Ordinary direct/group chat has none and must never
    // appear here — the list reads CommThreads, and no anchor is invented for a Conversation.
    [Fact]
    public async Task Every_listed_conversation_carries_a_real_business_anchor()
    {
        var context = CommunicationTestHost.Context();
        await _host.Comments().AddAsync(context, _host.CommentOn(Customer(5001), "a"));
        await _host.Comments().AddAsync(context, _host.CommentOn(CommunicationTestHost.Invoice(), "b"));

        var mine = await Adapter().MyBusinessConversationsAsync(context);

        Assert.Equal(2, mine.Count);
        Assert.All(mine, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.EntityType));
            Assert.True(c.EntityId > 0);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // §3 — CONVERSATION-SCOPED APPROVALS (read-only projection of TAB-6's inbox).
    // ---------------------------------------------------------------------------------------------

    // THIS TEST USED TO ENCODE THE DEFECT.
    //
    // It stubbed the inbox to return an Inventory approval for a CUSTOMER and asserted the projection
    // worked. The real system can never produce that row: only LeaveRequest, EmployeeRequest,
    // InventoryApproval and ProjectBilling are canonical approval subjects (ApprovalEntityMap), so a
    // Customer — like SalesInvoice, PurchaseInvoice and Quotation — always came back empty, and an
    // empty list is indistinguishable from "nothing is pending". The panel reported a working-but-idle
    // integration for a record that can never have an approval.
    //
    // The honest assertion is that the adapter says NOT APPLICABLE and never asks the module at all.
    [Fact]
    public async Task A_record_type_with_no_approval_workflow_is_reported_not_applicable()
    {
        var context = CommunicationTestHost.Context();
        var inbox = new StubApprovalInbox();
        inbox.Rows.Add(ApprovalRow(silo: "Inventory", entityId: 5001, status: "Pending"));

        var approvals = await AdapterWithApprovals(inbox)
            .ApprovalsForEntityAsync(context, Customer(5001));

        Assert.False(approvals.Supported);
        Assert.Empty(approvals.Items);
        Assert.NotNull(approvals.NotApplicableReason);
        Assert.Contains("no approval workflow", approvals.NotApplicableReason!);

        // And the module was never consulted — the map is asked BEFORE the query, which is the repair.
        Assert.Null(inbox.LastEntityType);
    }

    // The same for the three host types TAB-5 reported as structurally empty. A Theory, so a future
    // entity added to the conversation surface cannot quietly regain a misleading empty panel.
    [Theory]
    [InlineData(EntityRegistry.SalesInvoice)]
    [InlineData(EntityRegistry.PurchaseInvoice)]
    [InlineData(EntityRegistry.Quotation)]
    public async Task The_conversation_host_types_report_not_applicable_rather_than_empty(string code)
    {
        var context = CommunicationTestHost.Context();
        var inbox = new StubApprovalInbox();

        var approvals = await AdapterWithApprovals(inbox)
            .ApprovalsForEntityAsync(context, new CommEntityRef(code, 1001));

        Assert.False(approvals.Supported);
        Assert.Empty(approvals.Items);
        Assert.Null(inbox.LastEntityType);
    }

    // The reason names the real canonical set, so an operator reading it learns what DOES carry
    // approvals rather than only that this record does not.
    [Fact]
    public async Task The_not_applicable_reason_names_the_canonical_approval_subjects()
    {
        var approvals = await AdapterWithApprovals(new StubApprovalInbox())
            .ApprovalsForEntityAsync(CommunicationTestHost.Context(), Customer(5001));

        foreach (var canonical in CrossBuy.BL.Approvals.ApprovalEntityMap.SupportedEntityTypes)
            Assert.Contains(canonical, approvals.NotApplicableReason!);
    }

    // THE APPROVALS QUERY IS CURRENTLY UNREACHABLE, AND THAT IS THE FINDING.
    //
    // Two facts combine. Only LeaveRequest, EmployeeRequest, InventoryApproval and ProjectBilling are
    // canonical approval subjects; and all four carry SupportsComments = false in IEntityRegistry, so
    // no conversation can exist on any of them. Conversation-bearing records and approval subjects are
    // therefore DISJOINT SETS, and no anchor reaches GetPendingForEntityAsync today.
    //
    // This test pins that as a deliberate state rather than leaving it as an accident: every entity
    // the conversation surface actually enables must report not-applicable and must not consult the
    // module. If a canonical subject ever becomes conversation-enabled, this turns red and the
    // viewer-identity assertions below it have to be restored.
    [Theory]
    [InlineData(EntityRegistry.SalesInvoice)]
    [InlineData(EntityRegistry.PurchaseInvoice)]
    [InlineData(EntityRegistry.Quotation)]
    [InlineData(EntityRegistry.Customer)]
    [InlineData(EntityRegistry.Task)]
    public async Task No_conversation_enabled_record_can_reach_the_approvals_module_today(string code)
    {
        var inbox = new StubApprovalInbox();
        inbox.Rows.Add(ApprovalRow("Inventory", 1, "Pending"));

        var approvals = await AdapterWithApprovals(inbox)
            .ApprovalsForEntityAsync(CommunicationTestHost.Context(), new CommEntityRef(code, 1001));

        Assert.False(approvals.Supported);
        Assert.Null(inbox.LastEntityType);
    }

    // The canonical subjects are not conversation-enabled — the other half of the finding, asserted on
    // the registry itself so it cannot drift without this failing.
    [Fact]
    public void No_canonical_approval_subject_is_conversation_enabled()
    {
        var registry = new EntityRegistry(_host.Db);

        foreach (var code in CrossBuy.BL.Approvals.ApprovalEntityMap.SupportedEntityTypes)
        {
            Assert.True(registry.TryGetDefinition(code, out var definition), code + " is not registered");
            Assert.False(definition!.SupportsComments,
                code + " now supports comments — conversation-scoped approvals became reachable, so the "
                     + "viewer-identity and projection tests must be restored.");
        }
    }

    // THE SIDE DOOR THIS MUST NOT BE. The anchor arrives from a URL. The access check runs BEFORE the
    // canonical-type check, so a record the caller cannot open is refused for THAT reason rather than
    // being told its type carries no approvals — which would still confirm the record exists.
    [Fact]
    public async Task A_record_the_caller_cannot_open_is_refused_before_its_type_is_considered()
    {
        var context = CommunicationTestHost.Context();
        var inbox = new StubApprovalInbox();
        inbox.Rows.Add(ApprovalRow("Inventory", 1, "Pending"));

        var refused = await AdapterWithApprovals(inbox, permissions: new StubPermissionProvider())
            .ApprovalsForEntityAsync(context, Customer(5001));

        Assert.False(refused.Supported);
        Assert.Contains("not visible", refused.NotApplicableReason!);
        Assert.Null(inbox.LastEntityType);

        // The control: the SAME record, a caller who may see it. Still not-applicable — but for the
        // type, not for visibility. Two different refusals prove the ordering.
        var visible = await AdapterWithApprovals(inbox).ApprovalsForEntityAsync(context, Customer(5001));
        Assert.False(visible.Supported);
        Assert.Contains("no approval workflow", visible.NotApplicableReason!);
        Assert.DoesNotContain("not visible", visible.NotApplicableReason!);
    }

    [Fact]
    public async Task An_unresolved_company_or_identity_asks_the_approvals_module_nothing()
    {
        var inbox = new StubApprovalInbox();
        inbox.Rows.Add(ApprovalRow("Inventory", 1, "Pending"));

        Assert.Empty(await AdapterWithApprovals(inbox).ApprovalsForEntityAsync(
            CommunicationTestHost.Context(companyId: 0), Customer(5001)));
        Assert.Empty(await AdapterWithApprovals(inbox).ApprovalsForEntityAsync(
            CommunicationTestHost.Context(employeeId: 0), Customer(5001)));

        Assert.Null(inbox.LastEntityType);
    }

    // A failing approvals module must not take the conversation down with it — the same isolation
    // Phase 2 established for the kernel bridge.
    [Fact]
    public async Task A_failing_approvals_module_leaves_the_conversation_readable()
    {
        var context = CommunicationTestHost.Context();
        var inbox = new StubApprovalInbox { Throw = true };

        Assert.Empty(await AdapterWithApprovals(inbox).ApprovalsForEntityAsync(context, Customer(5001)));

        // And the absence of the module altogether is a deployment state, not an error.
        Assert.Empty(await Adapter().ApprovalsForEntityAsync(context, Customer(5001)));
    }

    private ICommTaskIntegrationService AdapterWithApprovals(
        CrossBuy.BL.Approvals.IApprovalInboxService inbox,
        IPlatformPermissionProvider? permissions = null)
    {
        var services = new StubServices();
        services.Add<IEntityRegistry>(new EntityRegistry(_host.Db));
        services.Add<ITaskService>(RealTasks());
        services.Add<ITasksAccessService>(_access);
        services.Add<CrossBuy.BL.Approvals.IApprovalInboxService>(inbox);

        return new CommTaskIntegrationService(
            _host.Db, _host.Threads(), _host.Comments(), _host.Audit(),
            _host.Access(permissions: permissions), _links, services,
            NullLogger<CommTaskIntegrationService>.Instance);
    }

    private static CrossBuy.BL.Approvals.PendingApprovalRow ApprovalRow(
        string silo, int entityId, string status) => new()
        {
            Silo = silo,
            EntityId = entityId,
            Status = status,
            TitleEn = "Awaiting approval",
            TitleAr = "بانتظار الاعتماد",
            Navigation = new CrossBuy.BL.Approvals.ApprovalNavigationTarget(silo, "Index"),
        };

    private sealed class StubApprovalInbox : CrossBuy.BL.Approvals.IApprovalInboxService
    {
        public List<CrossBuy.BL.Approvals.PendingApprovalRow> Rows { get; } = new();
        public bool Throw { get; set; }

        public BusinessContext? LastContext { get; private set; }
        public int LastEmployeeId { get; private set; }
        public string? LastEntityType { get; private set; }
        public int LastEntityId { get; private set; }

        public Task<CrossBuy.BL.Approvals.ApprovalInboxPage> GetPendingForCurrentApproverAsync(
            BusinessContext context, int employeeId, int take = 0, CancellationToken cancellationToken = default)
            => Task.FromResult(CrossBuy.BL.Approvals.ApprovalInboxPage.Empty);

        public Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> GetPendingForEntityAsync(
            BusinessContext context, int employeeId, string entityType, int entityId,
            CancellationToken cancellationToken = default)
        {
            LastContext = context; LastEmployeeId = employeeId;
            LastEntityType = entityType; LastEntityId = entityId;
            if (Throw) throw new InvalidOperationException("approvals module unavailable");
            return Task.FromResult<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>>(Rows);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Hands back one link, in the caller's company, pointing at a task that is NOT. Exists to exercise the
    // predicate that re-checks the task's own company when following a link.
    private sealed class RogueLinkStore : ICommTaskLinkStore
    {
        private readonly long _threadId;
        private readonly int _foreignTaskId;

        public RogueLinkStore(long threadId, int foreignTaskId)
        { _threadId = threadId; _foreignTaskId = foreignTaskId; }

        public bool IsProvisioned => true;

        private CommTaskLinkRow Row => new()
        {
            Id = 1, TaskId = _foreignTaskId, ThreadId = _threadId, CommentId = null,
            Origin = CommTaskLinkOrigin.LinkedExisting, CreatedAt = DateTime.UtcNow, CreatedBy = null,
        };

        public Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(int companyId, long threadId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(new[] { Row });

        public Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(int companyId, int taskId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(new[] { Row });

        public Task<CommTaskLinkRow?> GetAsync(int companyId, long linkId,
            CancellationToken cancellationToken = default) => Task.FromResult<CommTaskLinkRow?>(Row);

        public Task<CommTaskLinkRow?> AddAsync(int companyId, int taskId, long threadId, long? commentId,
            string origin, int? createdBy, CancellationToken cancellationToken = default)
            => Task.FromResult<CommTaskLinkRow?>(Row);

        public Task<bool> RemoveAsync(int companyId, long linkId, int? removedBy,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private IServiceProvider ServicesWith(ITasksAccessService access)
    {
        var services = new StubServices();
        services.Add<IEntityRegistry>(new EntityRegistry(_host.Db));
        services.Add<ITaskService>(RealTasks());
        services.Add<ITasksAccessService>(access);
        return services;
    }

    private sealed class StubServices : IServiceProvider
    {
        private readonly Dictionary<Type, object?> _map = new();
        public void Add<T>(T impl) => _map[typeof(T)] = impl;
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }

    private sealed class StubTasksAccess : ITasksAccessService
    {
        private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
        public void Allow(string action) => _allowed.Add(action);

        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default)
        {
            // Company is still checked here even though the rest is doubled: an access double that
            // ignored the company would make every isolation test in this file pass vacuously.
            if (context.CompanyId != CommunicationTestHost.CompanyId) return Task.FromResult(false);
            return Task.FromResult(_allowed.Contains(action));
        }

        public Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AccessScope { Breadth = AccessBreadth.None, CompanyId = context.CompanyId });
    }
}
