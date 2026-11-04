using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication ↔ Task Management — THE INTEGRATION ADAPTER (Phase 3).
    //
    // DIRECTION OF DEPENDENCY, AND WHY IT IS THIS WAY ROUND. This type lives on the Communication
    // side and reaches into Tasks. Core Tasks gains nothing: no Communication type appears in
    // TaskService, TaskItem or the Tasks DI graph, which is what TasksWithoutCommunicationTests
    // exists to keep true.
    //
    // AND IT DOES NOT HARD-DEPEND ON TASKS EITHER. The Tasks services are resolved through
    // IServiceProvider at call time, not injected, so adding this adapter does not make ITaskService
    // a required dependency of the Communication graph. That is the exact mirror of
    // TasksController.TryCommunication(), and it means a deployment running either module alone still
    // builds a valid container. An absent Tasks module is reported, never assumed away.
    //
    // WHAT IT REUSES RATHER THAN REBUILDS. Creation goes through ITaskService.SaveAsync, so every
    // Tasks validation, event and notification fires exactly as it does from the Tasks screens.
    // Authorization asks ITasksAccessService with the same actions and the same PermissionTarget the
    // Tasks controller uses. Reading the source message goes through ICommCommentService, so the
    // caller must already be able to SEE the message they are turning into a task.
    // =============================================================================================

    // The TWO relationships a task can have with a conversation. They are kept apart deliberately: one is
    // inferred from shared context and the other was created by a person, and showing them as one list
    // without saying which is which would claim somebody linked a task that nobody did.
    public static class CommTaskRelationship
    {
        // Same registered business record. The task and the conversation are about the same invoice. Nobody
        // linked them; they simply refer to the same thing. Available today, needs no storage.
        public const string SharedBusinessContext = "SharedBusinessContext";

        // A persisted CommTaskLink row: somebody deliberately attached this task to this conversation.
        public const string ExplicitLink = "ExplicitLink";
    }

    public sealed class CommTaskRef
    {
        // Which of the two relationships above produced this row. Never defaulted: a caller rendering a
        // panel has to be able to label it.
        public required string Relationship { get; init; }

        // The CommTaskLink row, for an explicit link only. Null for shared business context — there is no
        // link row to remove, which is precisely the difference.
        public long? LinkId { get; init; }

        // The message the link came from, when it came from one.
        public long? CommentId { get; init; }

        public required int TaskId { get; init; }
        public required string Title { get; init; }
        public string? TitleEn { get; init; }
        public required string Status { get; init; }
        public required string Priority { get; init; }
        public DateTime? DueDate { get; init; }
        public required int AssigneeEmployeeId { get; init; }
        public required int ProgressPct { get; init; }

        // The route the UI navigates to. Taken from the Tasks module's own screen rather than
        // reconstructed here, so a route change does not silently strand this link.
        public string Route => $"/Tasks/Detail/{TaskId}";
    }

    public sealed class CommTaskCreateRequest
    {
        public required long CommentId { get; init; }
        public required string Title { get; init; }
        public string? TitleEn { get; init; }
        public string? Description { get; init; }
        public required int AssigneeEmployeeId { get; init; }
        public string Priority { get; init; } = "Normal";
        public DateTime? DueDate { get; init; }

        // Idempotency for a double-submitted dialog. When supplied, a second create with the same key
        // in the same company returns the FIRST task instead of making a second one.
        public string? DedupKey { get; init; }
    }

    public sealed class CommTaskResult
    {
        public required bool Ok { get; init; }

        // A machine code, never a sentence: the UI maps it to its own bilingual string. Mirrors
        // CommValidationException.Codes rather than inventing a second error vocabulary.
        public string? Error { get; init; }

        public int TaskId { get; init; }

        // Set when the task was created but the conversation's business anchor could NOT be carried
        // across — see TaskService's hardcoded link allow-list. The task still exists and the anchor is
        // recorded in the audit row and the system comment; it simply is not a queryable task link.
        public string? AnchorWarning { get; init; }

        // The CommTaskLink row, for the link operations. Null for create-from-message, which does not
        // produce one while the link table is unprovisioned.
        public long? LinkId { get; init; }

        // A MESSAGE-LEVEL LINK THAT WAS SATISFIED BY AN EXISTING ONE WITH DIFFERENT PROVENANCE.
        //
        // The live relationship is (CompanyID, TaskId, ThreadId) — CommentId sits outside it — so
        // linking a task from a SECOND message returns the FIRST link rather than creating another.
        // That is correct under the current schema, but it is not what the caller asked for, and a
        // silent success would let a screen claim "linked to this message" about a link that points
        // at a different one. Reported so the caller can say so.
        public string? LinkWarning { get; init; }

        public static CommTaskResult Fail(string code) => new() { Ok = false, Error = code };
    }

    // THE ERROR VOCABULARY, FROZEN AND PUBLISHED.
    //
    // Two other tabs map these to their own bilingual strings. Published as constants rather than left as
    // literals in this file so a caller binds to a symbol the compiler checks, and so renaming one breaks
    // the build instead of silently turning a refusal into an unhandled case on someone else's screen.
    public static class CommTaskErrors
    {
        public const string NoCompanyScope = "no_company_scope";
        public const string NoEmployeeIdentity = "no_employee_identity";
        public const string TasksUnavailable = "tasks_unavailable";

        // Deliberately the SAME code for absent, other-company and not-visible. A distinct "forbidden"
        // would confirm the record exists to somebody who may not see it.
        public const string MessageNotFound = "message_not_found";
        public const string ConversationNotFound = "conversation_not_found";
        public const string TaskNotFound = "task_not_found";

        public const string MessageNotInConversation = "message_not_in_conversation";
        public const string NotAuthorizedToCreateTask = "not_authorized_to_create_task";
        public const string NotAuthorizedToAssign = "not_authorized_to_assign";
        public const string NotAuthorizedToLinkTask = "not_authorized_to_link_task";
        public const string LinkPersistenceNotProvisioned = "link_persistence_not_provisioned";
        public const string LinkNotFound = "link_not_found";
        public const string LinkRejected = "link_rejected";

        // Prefix only — the offending entity code is appended, e.g. "anchor_not_linkable:PurchaseOrder".
        public const string AnchorNotLinkablePrefix = "anchor_not_linkable:";

        // Prefix only — the Tasks module's own refusal is appended verbatim after it.
        public const string TaskRejectedPrefix = "task_rejected:";

        // Prefix only — a message-level link request satisfied by an EXISTING link whose provenance is
        // a different message, or the conversation as a whole. The existing CommentId (or the literal
        // "conversation") is appended. Not an error: the link exists, it simply is not the one asked
        // for, and the schema cannot hold both today.
        public const string ExistingLinkDiffersByMessagePrefix = "existing_link_differs_by_message:";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            NoCompanyScope, NoEmployeeIdentity, TasksUnavailable, MessageNotFound, ConversationNotFound,
            TaskNotFound, MessageNotInConversation, NotAuthorizedToCreateTask, NotAuthorizedToAssign,
            NotAuthorizedToLinkTask, LinkPersistenceNotProvisioned, LinkNotFound, LinkRejected,
        };
    }

    // ONE AUTHORIZED CALL A SCREEN CAN BIND TO.
    //
    // A panel on an invoice page knows (entityType, entityId) from its own URL, not a thread id. Without
    // this, each host screen would compose the lookup itself — and the obvious composition is
    // GetOrCreateAsync, which CREATES a thread row every time somebody merely opens the record. A read
    // must not write. This resolves the EXISTING conversation, or reports that there is none.
    public sealed class CommConversationTasksPanel
    {
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }

        // Null when no conversation has been started on this record yet — which is NOT the same as the
        // caller being unable to see it. That case returns no panel at all.
        public long? ThreadId { get; init; }

        public required bool LinkingAvailable { get; init; }
        public required IReadOnlyList<CommTaskRef> SharedContext { get; init; }
        public required IReadOnlyList<CommTaskRef> ExplicitLinks { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // CONVERSATION-SCOPED APPROVALS (§3).
    //
    // A projection of TAB-6's PendingApprovalRow, not a replacement for it. Communication owns no
    // approval state and raises none: this is a read, and the rows it returns are the rows the viewer's
    // OWN inbox already returns, narrowed to one record.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommConversationApproval
    {
        public required string Silo { get; init; }
        public required int EntityId { get; init; }
        public required string Status { get; init; }
        public string? TitleAr { get; init; }
        public string? TitleEn { get; init; }
        public string? RequesterNameAr { get; init; }
        public string? RequesterNameEn { get; init; }
        public DateTime? SubmittedAt { get; init; }
        public decimal? Amount { get; init; }

        // The owning module's own navigation, carried through unchanged. Communication does not invent
        // a route to somebody else's screen.
        public required string Controller { get; init; }
        public required string Action { get; init; }
        public IReadOnlyDictionary<string, string?>? RouteValues { get; init; }

        // "Silo:EntityId" or "Silo:EntityId:StepId" — TAB-6's own identity for the row, so a UI can key
        // on it without this platform minting a competing one.
        public required string Reference { get; init; }
    }

    // THE APPROVALS ANSWER, WITH ITS THIRD STATE.
    //
    // "No approvals are pending on this invoice" and "invoices have no approval workflow at all" are
    // different facts, and a bare empty list says the first while meaning the second. Only four entity
    // types are canonical approval subjects — LeaveRequest, EmployeeRequest, InventoryApproval and
    // ProjectBilling (ApprovalEntityMap) — so a SalesInvoice, PurchaseInvoice or Quotation conversation
    // ALWAYS came back empty, and a panel rendering that emptiness would have told the user there was
    // nothing awaiting them on a record that can never have anything awaiting them.
    //
    // Supported = false means HIDE THE SECTION. It is not a failure and not a refusal.
    //
    // IT IS ALSO ENUMERABLE, deliberately. TasksController already consumes this call as a sequence,
    // and that file belongs to TAB-5. Turning the return type into a plain object would have reddened
    // their build for a change they did not ask for and cannot be expected to fix on my schedule — so
    // the type carries its items as well as its state. Their existing projection keeps compiling and
    // keeps behaving exactly as before; adopting Supported to hide the section is then a one-line
    // change they make when it suits them, not a repair they are forced into.
    public sealed class CommConversationApprovals : IEnumerable<CommConversationApproval>
    {
        public required bool Supported { get; init; }
        public required IReadOnlyList<CommConversationApproval> Items { get; init; }

        public IEnumerator<CommConversationApproval> GetEnumerator() => Items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// Why the section does not apply, when it does not. Null when Supported is true.
        public string? NotApplicableReason { get; init; }

        public static CommConversationApprovals NotApplicable(string reason) => new()
        {
            Supported = false,
            Items = Array.Empty<CommConversationApproval>(),
            NotApplicableReason = reason,
        };

        public static CommConversationApprovals From(IReadOnlyList<CommConversationApproval> items) => new()
        {
            Supported = true,
            Items = items,
        };
    }

    // A business conversation this caller actually takes part in — the entity-anchored counterpart to
    // an ordinary chat conversation. Ordinary Chat has no business anchor and never appears here: an
    // anchor is read from the thread, never inferred for a conversation that has none.
    public sealed class CommBusinessConversation
    {
        public required long ThreadId { get; init; }
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
        public required string Kind { get; init; }

        public string? SubjectAr { get; init; }
        public string? SubjectEn { get; init; }

        public required int CommentCount { get; init; }
        public DateTime? LastActivityAt { get; init; }

        /// This caller's participation role, so a screen can distinguish a follower from a watcher
        /// without a second read.
        public required string Role { get; init; }

        /// Muted by this caller. Still listed — muting silences notifications, it does not hide the
        /// conversation — but a screen may render it differently.
        public bool Muted { get; init; }
    }

    public sealed class CommTaskLinkRequest
    {
        public required long ThreadId { get; init; }
        public required int TaskId { get; init; }

        // Optional message-level provenance: link the task to a specific message in the conversation
        // rather than to the conversation as a whole.
        public long? CommentId { get; init; }

        // HOW THE LINK CAME TO EXIST — CommTaskLinkOrigin. Defaults to LinkedExisting, which is what
        // attaching an already-existing task is.
        //
        // It is a parameter because only the CALLER knows. Creating a task from a message is two acts
        // owned by two services: the task is created here, and the link is written by the controller
        // immediately afterwards through this same method. That second write is indistinguishable, from
        // inside this service, from somebody attaching an existing task — so the service cannot infer
        // CreatedFromMessage, and guessing it from "a task that happens to be new" would stamp the wrong
        // provenance on any link made moments after a creation.
        //
        // Declaring it costs the caller one line and keeps the record true. The alternative considered
        // and rejected was a second link write inside CreateFromMessageAsync, which would have produced
        // two writes for one user action.
        public string Origin { get; init; } = CommTaskLinkOrigin.LinkedExisting;
    }

    public sealed class CommTaskConversation
    {
        public required int TaskId { get; init; }
        public required long ThreadId { get; init; }
        public required int CommentCount { get; init; }
        public DateTime? LastActivityAt { get; init; }

        // The record the task itself is about, when it has one — so a task opened from a conversation
        // can offer the way back to that record's discussion as well as its own.
        public string? AnchorEntityType { get; init; }
        public int? AnchorEntityId { get; init; }
    }

    public interface ICommTaskIntegrationService
    {
        Task<CommTaskResult> CreateFromMessageAsync(
            BusinessContext context, CommTaskCreateRequest request, CancellationToken cancellationToken = default);

        // SHARED BUSINESS CONTEXT. Tasks about the same record as this conversation. Inferred, not stored.
        Task<IReadOnlyList<CommTaskRef>> RelatedTasksAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);

        // EXPLICIT LINKS. Only rows somebody actually created. Empty when the store is unprovisioned —
        // which is not the same answer as "nobody linked anything", so callers check LinkingAvailable.
        Task<IReadOnlyList<CommTaskRef>> LinkedTasksAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default);

        // False until the CommTaskLink table is provisioned. A screen asks this BEFORE offering the action,
        // so nobody is shown a button that is going to refuse.
        bool LinkingAvailable { get; }

        Task<CommTaskResult> LinkExistingAsync(
            BusinessContext context, CommTaskLinkRequest request, CancellationToken cancellationToken = default);

        Task<CommTaskResult> UnlinkAsync(
            BusinessContext context, long linkId, CancellationToken cancellationToken = default);

        Task<CommTaskConversation?> ConversationForTaskAsync(
            BusinessContext context, int taskId, CancellationToken cancellationToken = default);

        // Resolves a record's EXISTING conversation and both task relationships in one authorized call.
        // Null means the caller may not see the record. Never creates a thread.
        Task<CommConversationTasksPanel?> PanelForEntityAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default);

        // Pending approvals on the record this conversation is about, as the VIEWER sees them. Empty when
        // the approvals module is absent, the record has no approval workflow, or the viewer is not an
        // approver for it — three states that are correctly indistinguishable to a reader.
        Task<CommConversationApprovals> ApprovalsForEntityAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default);

        /// The entity-anchored conversations this caller participates in, newest activity first. The
        /// Hub's business-conversation list. Empty for an unresolved scope or identity; never includes
        /// a conversation the caller cannot read, and never includes an ordinary chat conversation —
        /// those carry no business anchor and none is invented for them.
        Task<IReadOnlyList<CommBusinessConversation>> MyBusinessConversationsAsync(
            BusinessContext context, int take = 30, CancellationToken cancellationToken = default);
    }

    public sealed class CommTaskIntegrationService : ICommTaskIntegrationService
    {
        // ELIGIBILITY IS ASKED, NEVER RESTATED.
        //
        // This adapter used to keep its own copy of TaskService's link allow-list. TAB-5 then made that
        // list registry-driven and opted the four Communication anchors in — and the copy here silently
        // became wrong in the dangerous direction: it would have reported PurchaseInvoice as unstorable
        // and dropped an anchor the Tasks module had just started accepting. The adapter's own test went
        // on passing, because it asserted this file's belief rather than the Tasks module's behaviour.
        //
        // So there is no list here any more. TaskEntityLinkPolicy is published by its owner for exactly
        // this, and one source of truth cannot drift from itself.
        private bool AnchorIsStorable(string entityType, int entityId)
            => TaskService.TaskEntityLinkPolicy.IsEligible(
                _services.GetService<CrossBuy.BL.Platform.IEntityRegistry>(), entityType, entityId);

        private readonly CommDb _db;
        private readonly ICommThreadService _threads;
        private readonly ICommCommentService _comments;
        private readonly ICommAuditWriter _audit;
        private readonly ICommAccessPolicy _access;
        private readonly ICommTaskLinkStore _links;
        private readonly IServiceProvider _services;
        private readonly ILogger<CommTaskIntegrationService> _log;

        public CommTaskIntegrationService(
            CrossDbContext db,
            ICommThreadService threads,
            ICommCommentService comments,
            ICommAuditWriter audit,
            ICommAccessPolicy access,
            ICommTaskLinkStore links,
            IServiceProvider services,
            ILogger<CommTaskIntegrationService> log)
        {
            _db = new CommDb(db);
            _threads = threads;
            _comments = comments;
            _audit = audit;
            _access = access;
            _links = links;
            _services = services;
            _log = log;
        }

        public bool LinkingAvailable => _links.IsProvisioned;

        // The mirror of TasksController.TryCommunication(): resolved, never injected, so neither module
        // becomes a build-time dependency of the other.
        private (ITaskService Tasks, ITasksAccessService Access)? TryTasks()
        {
            var tasks = _services.GetService<ITaskService>();
            var access = _services.GetService<ITasksAccessService>();
            return tasks is null || access is null ? null : (tasks, access);
        }

        // ---------------------------------------------------------------------------------------------
        // A. CREATE TASK FROM MESSAGE
        // ---------------------------------------------------------------------------------------------
        public async Task<CommTaskResult> CreateFromMessageAsync(
            BusinessContext context, CommTaskCreateRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            // A caller with no resolved company must not reach a company-filtered write. Checked here as
            // well as downstream because "no company" and "wrong company" have to fail the same way.
            if (context.CompanyId <= 0) return CommTaskResult.Fail(CommTaskErrors.NoCompanyScope);
            if (context.EmployeeId is not > 0) return CommTaskResult.Fail(CommTaskErrors.NoEmployeeIdentity);

            var tasks = TryTasks();
            if (tasks == null) return CommTaskResult.Fail(CommTaskErrors.TasksUnavailable);

            // ---- 1. the SOURCE MESSAGE, read through the normal authorized path.
            //
            // This is the security hinge of the whole feature. A caller who cannot read the message
            // cannot turn it into a task, and absent / other-company / deleted all surface as the same
            // CommNotFoundException — so a task cannot be used to probe for messages.
            CommCommentDto message;
            try
            {
                message = await _comments.GetAsync(context, request.CommentId, cancellationToken);
            }
            catch (CommNotFoundException) { return CommTaskResult.Fail(CommTaskErrors.MessageNotFound); }
            catch (CommAccessDeniedException) { return CommTaskResult.Fail(CommTaskErrors.MessageNotFound); }

            var thread = await _threads.RequireAsync(context, message.ThreadId, cancellationToken);

            // ---- 2. IDEMPOTENCY, before anything is written.
            if (!string.IsNullOrWhiteSpace(request.DedupKey))
            {
                var existing = await FindByDedupKeyAsync(context.CompanyId, request.DedupKey!, cancellationToken);
                if (existing != null)
                    return new CommTaskResult { Ok = true, TaskId = existing.Value };
            }

            // ---- 3. TASKS AUTHORIZATION, asked of the Tasks module, not re-implemented here.
            if (!await tasks.Value.Access.CanAsync(context, TasksActions.Create, null, cancellationToken))
                return CommTaskResult.Fail(CommTaskErrors.NotAuthorizedToCreateTask);

            // Assigning work to somebody else is a different authority from creating your own.
            if (request.AssigneeEmployeeId != context.EmployeeId.Value
                && !await tasks.Value.Access.CanAsync(context, TasksActions.Assign, null, cancellationToken))
                return CommTaskResult.Fail(CommTaskErrors.NotAuthorizedToAssign);

            // ---- 4. THE BUSINESS ANCHOR.
            //
            // The task is about the RECORD the conversation hangs on — the invoice, the customer — not
            // about the comment row. TaskService silently nulls an EntityType outside its own allow-list,
            // so an anchor it cannot store is reported rather than quietly lost.
            bool anchorSupported = AnchorIsStorable(thread.EntityType, thread.EntityId);
            string? warning = anchorSupported
                ? null
                : CommTaskErrors.AnchorNotLinkablePrefix + thread.EntityType;

            var input = new TaskSaveInput
            {
                Id = 0,
                Title = request.Title?.Trim() ?? "",
                TitleEn = string.IsNullOrWhiteSpace(request.TitleEn) ? null : request.TitleEn!.Trim(),
                Description = request.Description,
                AssigneeEmployeeId = request.AssigneeEmployeeId,
                Priority = request.Priority,
                DueDate = request.DueDate,
                EntityType = anchorSupported ? thread.EntityType : null,
                EntityId = anchorSupported ? thread.EntityId : null,
            };

            // ---- 5. CREATION, through the Tasks module's own service.
            var (ok, error, taskId) = await tasks.Value.Tasks.SaveAsync(
                context.CompanyId, input, context.EmployeeId.Value);

            if (!ok || taskId <= 0)
            {
                _log.LogInformation(
                    "Create-task-from-message refused by TaskService for comment {CommentId}: {Error}",
                    request.CommentId, error);
                return CommTaskResult.Fail(CommTaskErrors.TaskRejectedPrefix + (error ?? "unknown"));
            }

            // ---- 6. THE AUDIT TRAIL.
            //
            // This row is what makes the link to the MESSAGE survive at all. There is no task↔comment
            // table (see the Phase 3 persistence decision), so the audit log is the only durable record
            // that this task came from this comment. It is an audit trail, not a query index: the
            // related-tasks panel reads the shared business anchor, never this row.
            _audit.Append(new CommAuditRequest
            {
                Action = CommAuditActions.TaskCreatedFromMessage,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ThreadId = thread.Id,
                CommentId = message.CommentId,
                ActorEmployeeId = context.EmployeeId,
                SubjectEmployeeId = request.AssigneeEmployeeId,
                Visibility = message.Visibility,
                Detail = new
                {
                    taskId,
                    anchorEntityType = thread.EntityType,
                    anchorEntityId = thread.EntityId,
                    anchorStored = anchorSupported,
                    dedupKey = request.DedupKey,
                },
                CorrelationId = context.CorrelationId,
                DedupKey = string.IsNullOrWhiteSpace(request.DedupKey)
                    ? null
                    : "task-from-message:" + request.DedupKey!.Trim(),
            });

            await _db.Context.SaveChangesAsync(cancellationToken);

            return new CommTaskResult { Ok = true, TaskId = taskId, AnchorWarning = warning };
        }

        // The idempotency lookup. Reads the audit row written by a previous create rather than a
        // separate dedup table — the row is already durable, already company-scoped, and already
        // carries the task id in its detail.
        private async Task<int?> FindByDedupKeyAsync(
            int companyId, string dedupKey, CancellationToken cancellationToken)
        {
            string key = "task-from-message:" + dedupKey.Trim();

            var row = await _db.Audit.AsNoTracking()
                .Where(a => a.CompanyID == companyId
                            && a.Action == CommAuditActions.TaskCreatedFromMessage
                            && a.DedupKey == key)
                .OrderBy(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (row?.DetailJson == null) return null;

            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(row.DetailJson);
                if (parsed.RootElement.TryGetProperty("taskId", out var id) && id.TryGetInt32(out int value) && value > 0)
                    return value;
            }
            catch (System.Text.Json.JsonException)
            {
                // A detail we cannot parse is not a reason to refuse the operation; it is a reason not to
                // claim a previous task exists. Falling through creates a new one, which is the safe side
                // of this particular mistake.
                _log.LogWarning("Unparseable task-from-message audit detail for dedup key {Key}", dedupKey);
            }

            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // C. RELATED TASKS FOR A CONVERSATION
        //
        // Relatedness is defined as SHARING THE BUSINESS ANCHOR: the tasks about this invoice, shown in
        // the discussion about this invoice. That is a real relationship already modelled by both sides
        // (TaskItem.EntityType/EntityId and CommThread.EntityType/EntityId), which is why it needs no new
        // storage. It is deliberately NOT "tasks created from messages in this thread" — that would be a
        // narrower set and would depend on the audit log as an index, which it is not.
        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<CommTaskRef>> RelatedTasksAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0) return Array.Empty<CommTaskRef>();

            var tasks = TryTasks();
            if (tasks == null) return Array.Empty<CommTaskRef>();

            // RequireAsync authorizes the thread and refuses another company's id, so the anchor used
            // below can only ever be one this caller may already see.
            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);

            if (!await tasks.Value.Access.CanAsync(context, TasksActions.Read, null, cancellationToken))
                return Array.Empty<CommTaskRef>();

            if (!AnchorIsStorable(thread.EntityType, thread.EntityId)) return Array.Empty<CommTaskRef>();

            // CompanyId is compared on the ROW. TaskItems has no global query filter for this read path,
            // so this predicate is the only thing keeping another tenant's tasks out of the panel.
            var rows = await _db.Context.Set<TaskItem>().AsNoTracking()
                .Where(t => t.CompanyId == context.CompanyId
                            && t.EntityType == thread.EntityType
                            && t.EntityId == thread.EntityId)
                .OrderByDescending(t => t.ID)
                .Take(50)
                .ToListAsync(cancellationToken);

            // An explicit lambda, not the Map method group: Map now carries optional parameters, which makes
            // the method group ambiguous with Select's (item, index) overload.
            return rows.Select(t => Map(t)).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        // D. TASK → CONVERSATION
        //
        // A task is itself a commentable entity in IEntityRegistry (SupportsComments, SupportsTimeline),
        // so its conversation is the thread anchored to ("Task", taskId). Nothing new is stored for this
        // direction; it is the same thread TasksController already opens on the task detail screen.
        // ---------------------------------------------------------------------------------------------
        public async Task<CommTaskConversation?> ConversationForTaskAsync(
            BusinessContext context, int taskId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0) return null;
            if (taskId <= 0) return null;

            var tasks = TryTasks();
            if (tasks == null) return null;

            // Record-level, not merely module-level: ForTask makes TasksAccessService verify this
            // caller's actual relationship to THIS task, the same question ConfirmMatch asks.
            if (!await tasks.Value.Access.CanAsync(
                    context, TasksActions.Read, PermissionTarget.ForTask(taskId, context.CompanyId), cancellationToken))
                return null;

            var task = await tasks.Value.Tasks.GetAsync(context.CompanyId, taskId);
            if (task == null) return null;

            var thread = await _threads.GetOrCreateAsync(context,
                new CommThreadRequest { Entity = new CommEntityRef(EntityRegistry.Task, taskId) },
                cancellationToken);

            return new CommTaskConversation
            {
                TaskId = taskId,
                ThreadId = thread.Id,
                CommentCount = thread.CommentCount,
                LastActivityAt = thread.LastActivityAt,
                AnchorEntityType = task.EntityType,
                AnchorEntityId = task.EntityId,
            };
        }

        // ---------------------------------------------------------------------------------------------
        // B. LINK AN EXISTING TASK.
        //
        // THE TASK'S OWN ANCHOR IS NEVER TOUCHED. TaskItem.EntityType/EntityId says what the task is
        // ABOUT; a link says which conversation it is attached to. Writing the thread's record over the
        // task's anchor would destroy information the task already carried, and could only ever hold one
        // link — which is why this is a row in CommTaskLinks and not a column on TaskItem.
        // ---------------------------------------------------------------------------------------------
        public async Task<CommTaskResult> LinkExistingAsync(
            BusinessContext context, CommTaskLinkRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);

            if (context.CompanyId <= 0) return CommTaskResult.Fail(CommTaskErrors.NoCompanyScope);
            if (context.EmployeeId is not > 0) return CommTaskResult.Fail(CommTaskErrors.NoEmployeeIdentity);

            // Checked BEFORE any authorization work, so an unprovisioned deployment gives one honest
            // answer rather than a refusal whose reason depends on who asked.
            if (!_links.IsProvisioned) return CommTaskResult.Fail(CommTaskErrors.LinkPersistenceNotProvisioned);

            var tasks = TryTasks();
            if (tasks == null) return CommTaskResult.Fail(CommTaskErrors.TasksUnavailable);

            // The CONVERSATION side. RequireAsync refuses another company's thread and a deleted one
            // identically, so a link cannot be used to discover that a thread exists.
            CommThread thread;
            try { thread = await _threads.RequireAsync(context, request.ThreadId, cancellationToken); }
            catch (CommNotFoundException) { return CommTaskResult.Fail(CommTaskErrors.ConversationNotFound); }
            catch (CommAccessDeniedException) { return CommTaskResult.Fail(CommTaskErrors.ConversationNotFound); }

            // Message-level provenance, when asked for. The message must be readable AND must belong to
            // this thread — otherwise a caller could staple a link to a message from another conversation.
            if (request.CommentId is > 0)
            {
                CommCommentDto message;
                try { message = await _comments.GetAsync(context, request.CommentId.Value, cancellationToken); }
                catch (CommNotFoundException) { return CommTaskResult.Fail(CommTaskErrors.MessageNotFound); }
                catch (CommAccessDeniedException) { return CommTaskResult.Fail(CommTaskErrors.MessageNotFound); }

                if (message.ThreadId != thread.Id) return CommTaskResult.Fail(CommTaskErrors.MessageNotInConversation);
            }

            // The TASK side. Read first — a caller who may not see the task must not learn it exists by
            // being told they cannot link it, so absent and forbidden give the same answer.
            if (!await tasks.Value.Access.CanAsync(
                    context, TasksActions.Read, PermissionTarget.ForTask(request.TaskId, context.CompanyId),
                    cancellationToken))
                return CommTaskResult.Fail(CommTaskErrors.TaskNotFound);

            var task = await tasks.Value.Tasks.GetAsync(context.CompanyId, request.TaskId);
            if (task == null) return CommTaskResult.Fail(CommTaskErrors.TaskNotFound);

            // Attaching a task to a conversation changes what that task is associated with, which is an
            // edit of the task — the same authority ConfirmMatch requires for linking a movement.
            if (!await tasks.Value.Access.CanAsync(
                    context, TasksActions.Edit, PermissionTarget.ForTask(request.TaskId, context.CompanyId),
                    cancellationToken))
                return CommTaskResult.Fail(CommTaskErrors.NotAuthorizedToLinkTask);

            // Idempotent by contract: a repeated request returns the existing row.
            // An unknown origin is refused rather than normalised: the column carries a CHECK constraint
            // mirroring this vocabulary, so a bad value would otherwise surface as a database error at
            // the end of somebody's action instead of as a refusal at its start.
            if (!CommTaskLinkOrigin.IsValid(request.Origin))
                return CommTaskResult.Fail(CommTaskErrors.LinkRejected);

            var row = await _links.AddAsync(
                context.CompanyId, request.TaskId, thread.Id, request.CommentId,
                request.Origin, context.EmployeeId, cancellationToken);

            if (row == null) return CommTaskResult.Fail(CommTaskErrors.LinkRejected);

            // The store is idempotent on (company, task, thread). If it handed back a row whose
            // provenance is not the one asked for, the message-level intent was NOT recorded — say so
            // rather than returning a bare success the caller would read as "linked to this message".
            string? linkWarning = row.CommentId == request.CommentId
                ? null
                : CommTaskErrors.ExistingLinkDiffersByMessagePrefix
                  + (row.CommentId?.ToString() ?? "conversation");

            _audit.Append(new CommAuditRequest
            {
                Action = CommAuditActions.TaskLinkedToConversation,
                Entity = new CommEntityRef(thread.EntityType, thread.EntityId),
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ThreadId = thread.Id,
                CommentId = request.CommentId,
                ActorEmployeeId = context.EmployeeId,
                Detail = new { taskId = request.TaskId, linkId = row.Id, origin = row.Origin },
                CorrelationId = context.CorrelationId,
            });

            await _db.Context.SaveChangesAsync(cancellationToken);

            return new CommTaskResult { Ok = true, TaskId = request.TaskId, LinkId = row.Id, LinkWarning = linkWarning };
        }

        public async Task<CommTaskResult> UnlinkAsync(
            BusinessContext context, long linkId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0) return CommTaskResult.Fail(CommTaskErrors.NoCompanyScope);
            if (!_links.IsProvisioned) return CommTaskResult.Fail(CommTaskErrors.LinkPersistenceNotProvisioned);

            var tasks = TryTasks();
            if (tasks == null) return CommTaskResult.Fail(CommTaskErrors.TasksUnavailable);

            // The link is resolved through the store's OWN company filter rather than from anything the
            // caller supplied, so a link id from another tenant simply does not resolve.
            var existing = await _links.GetAsync(context.CompanyId, linkId, cancellationToken);
            if (existing == null) return CommTaskResult.Fail(CommTaskErrors.LinkNotFound);

            // Removing a link is the same authority as creating one, asked of the task the link names —
            // and a caller who may not edit that task gets the same answer as for a link that is not there.
            if (!await tasks.Value.Access.CanAsync(
                    context, TasksActions.Edit, PermissionTarget.ForTask(existing.TaskId, context.CompanyId),
                    cancellationToken))
                return CommTaskResult.Fail(CommTaskErrors.LinkNotFound);

            bool removed = await _links.RemoveAsync(
                context.CompanyId, linkId, context.EmployeeId, cancellationToken);

            if (!removed) return CommTaskResult.Fail(CommTaskErrors.LinkNotFound);

            _audit.Append(new CommAuditRequest
            {
                Action = CommAuditActions.TaskUnlinkedFromConversation,
                Entity = new CommEntityRef(EntityRegistry.Task, existing.TaskId),
                CompanyId = context.CompanyId,
                BranchId = context.BranchId,
                ThreadId = existing.ThreadId,
                ActorEmployeeId = context.EmployeeId,
                Detail = new { taskId = existing.TaskId, linkId },
                CorrelationId = context.CorrelationId,
            });

            await _db.Context.SaveChangesAsync(cancellationToken);

            return new CommTaskResult { Ok = true, TaskId = existing.TaskId, LinkId = linkId };
        }

        // EXPLICIT LINKS ONLY. Never merged with RelatedTasksAsync: one was created by a person and the
        // other is inferred, and a panel that showed them as one list would claim somebody linked a task
        // that nobody did.
        public async Task<IReadOnlyList<CommTaskRef>> LinkedTasksAsync(
            BusinessContext context, long threadId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0) return Array.Empty<CommTaskRef>();
            if (!_links.IsProvisioned) return Array.Empty<CommTaskRef>();

            var tasks = TryTasks();
            if (tasks == null) return Array.Empty<CommTaskRef>();

            var thread = await _threads.RequireAsync(context, threadId, cancellationToken);

            if (!await tasks.Value.Access.CanAsync(context, TasksActions.Read, null, cancellationToken))
                return Array.Empty<CommTaskRef>();

            var links = await _links.ForThreadAsync(context.CompanyId, thread.Id, cancellationToken);
            if (links.Count == 0) return Array.Empty<CommTaskRef>();

            var ids = links.Select(l => l.TaskId).Distinct().ToList();

            // The company predicate is repeated on the ROW here as well as in the store: a link row is only
            // a pointer, and following it without re-checking the task's own company would let a bad link
            // row reach across tenants.
            var rows = await _db.Context.Set<TaskItem>().AsNoTracking()
                .Where(t => t.CompanyId == context.CompanyId && ids.Contains(t.ID))
                .ToListAsync(cancellationToken);

            var byId = rows.ToDictionary(t => t.ID);

            return links
                .Where(l => byId.ContainsKey(l.TaskId))
                .Select(l => Map(byId[l.TaskId], CommTaskRelationship.ExplicitLink, l.Id, l.CommentId))
                .ToList();
        }

        // ---------------------------------------------------------------------------------------------
        // THE SECURE ENTITY LOOKUP (§B).
        //
        // The anchor arrives from a URL, so it is attacker-controlled. It is never trusted: the entity is
        // authorized first, through the same CanAccessEntityAsync the thread service itself uses, and an
        // unauthorized record is indistinguishable from one with no conversation — the caller gets null
        // either way rather than a panel that confirms the record exists.
        //
        // And it READS. ListForEntityAsync returns what is there; it does not create a thread, so opening
        // a record does not write a row.
        // ---------------------------------------------------------------------------------------------
        public async Task<CommConversationTasksPanel?> PanelForEntityAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CompanyId <= 0) return null;
            if (entity.EntityId <= 0 || string.IsNullOrWhiteSpace(entity.EntityCode)) return null;

            var decision = await _access.CanAccessEntityAsync(
                context, entity, CommCapabilities.Comments, cancellationToken);
            if (!decision.Allowed) return null;

            // THE CANONICAL THREAD, SELECTED BY IDENTITY — not "the first one back".
            //
            // ListForEntityAsync returns EVERY thread on the record ordered by LastActivityAt, across all
            // kinds. GetOrCreateAsync's default is Kind = Discussion with an empty ThreadKey, and
            // UX_CommThreads_Anchor makes (CompanyID, EntityType, EntityId, Kind, ThreadKey) the identity.
            // So taking threads[0] would hand back a Notes or Review thread whenever one of those had more
            // recent activity than the discussion — a different conversation under the same name.
            var threads = await _threads.ListForEntityAsync(context, entity, cancellationToken);
            long? threadId = threads
                .Where(t => string.Equals(t.Kind, CommThreadKind.Discussion, StringComparison.Ordinal)
                            && t.ThreadKey.Length == 0)
                .Select(t => (long?)t.ThreadId)
                .FirstOrDefault();

            var shared = threadId == null
                ? Array.Empty<CommTaskRef>()
                : await RelatedTasksAsync(context, threadId.Value, cancellationToken);

            var explicitLinks = threadId == null
                ? Array.Empty<CommTaskRef>()
                : await LinkedTasksAsync(context, threadId.Value, cancellationToken);

            return new CommConversationTasksPanel
            {
                EntityType = entity.EntityCode,
                EntityId = entity.EntityId,
                ThreadId = threadId,
                LinkingAvailable = LinkingAvailable,
                SharedContext = shared,
                ExplicitLinks = explicitLinks,
            };
        }

        // ---------------------------------------------------------------------------------------------
        // THE HUB'S BUSINESS-CONVERSATION LIST.
        //
        // PARTICIPATION IS THE CANDIDATE SET, NOT THE AUTHORITY. CommParticipant says who was pulled
        // into a conversation; it does not say they may still read it. A participant row survives a
        // permission being revoked, so every candidate is re-checked against ResolveThreadAccessAsync
        // and dropped if CanRead is false — filtered, never merely hidden.
        //
        // THE COMPANY COMES FROM THE CONTEXT AND IS COMPARED ON BOTH ROWS. CommParticipant carries its
        // own CompanyID and so does CommThread; both are predicated, because a participant row that
        // pointed at another company's thread would otherwise surface it — the same hazard the chat
        // search has and the same answer.
        //
        // ORDINARY CHAT NEVER APPEARS. This reads CommThreads, which are entity-anchored by
        // definition. A Conversation has no EntityType/EntityId and none is invented for it.
        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<CommBusinessConversation>> MyBusinessConversationsAsync(
            BusinessContext context, int take = 30, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.CompanyId <= 0) return Array.Empty<CommBusinessConversation>();
            if (context.EmployeeId is not > 0) return Array.Empty<CommBusinessConversation>();

            int companyId = context.CompanyId;
            int meId = context.EmployeeId.Value;

            var mine = await _db.Participants.AsNoTracking()
                .Where(p => p.CompanyID == companyId && p.EmployeeId == meId)
                .Select(p => new { p.ThreadId, p.Role, p.MutedAt })
                .ToListAsync(cancellationToken);
            if (mine.Count == 0) return Array.Empty<CommBusinessConversation>();

            var threadIds = mine.Select(p => p.ThreadId).Distinct().ToList();

            // A generous read before the access filter, because filtering removes rows: taking exactly
            // `take` here would return fewer than asked whenever one was unreadable.
            var threads = await _db.Threads.AsNoTracking()
                .Where(t => threadIds.Contains(t.Id)
                            && t.CompanyID == companyId
                            && t.DeletedAt == null)
                .OrderByDescending(t => t.LastActivityAt)
                .ThenByDescending(t => t.Id)
                .Take(Math.Clamp(take, 1, 100) * 2)
                .ToListAsync(cancellationToken);

            var byThread = mine
                .GroupBy(p => p.ThreadId)
                .ToDictionary(g => g.Key, g => g.First());

            var results = new List<CommBusinessConversation>(threads.Count);
            foreach (var thread in threads)
            {
                var access = await _access.ResolveThreadAccessAsync(context, thread, cancellationToken);
                if (!access.CanRead) continue;

                var participation = byThread[thread.Id];
                results.Add(new CommBusinessConversation
                {
                    ThreadId = thread.Id,
                    EntityType = thread.EntityType,
                    EntityId = thread.EntityId,
                    Kind = thread.Kind,
                    SubjectAr = thread.SubjectAr,
                    SubjectEn = thread.SubjectEn,
                    CommentCount = thread.CommentCount,
                    LastActivityAt = thread.LastActivityAt,
                    Role = participation.Role,
                    Muted = participation.MutedAt != null,
                });

                if (results.Count >= Math.Clamp(take, 1, 100)) break;
            }

            return results;
        }

        // ---------------------------------------------------------------------------------------------
        // CONVERSATION-SCOPED APPROVALS (§3) — READ ONLY.
        //
        // IT IS THE VIEWER'S OWN IDENTITY, NEVER AN ELEVATED ONE. context and employeeId both come from
        // the caller's resolved BusinessContext. TAB-6's method is a FILTER over that person's own
        // inbox — it opens no module query and asks for no new permission — so the worst this adapter
        // can do is show somebody a narrower view of what they could already see. Substituting a system
        // identity here would turn that property inside out, which is why the employee id is read from
        // the context rather than accepted as a parameter.
        //
        // AND IT RAISES NOTHING. There is no second approval engine here and no mutation: Communication
        // owns no approval state, and an approval is submitted by the module that owns the document.
        // ---------------------------------------------------------------------------------------------
        public async Task<CommConversationApprovals> ApprovalsForEntityAsync(
            BusinessContext context, CommEntityRef entity, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.CompanyId <= 0)
                return CommConversationApprovals.NotApplicable("no company scope is resolved");
            if (context.EmployeeId is not > 0)
                return CommConversationApprovals.NotApplicable("this session has no employee identity");
            if (entity.EntityId <= 0 || string.IsNullOrWhiteSpace(entity.EntityCode))
                return CommConversationApprovals.NotApplicable("no record was named");

            // Optional, like the Tasks module: a deployment without approvals must still render a
            // conversation rather than fail the screen around it.
            var inbox = _services.GetService<CrossBuy.BL.Approvals.IApprovalInboxService>();
            if (inbox == null)
                return CommConversationApprovals.NotApplicable("the approvals module is not installed");

            // THE CONVERSATION MUST BE VISIBLE FIRST. Without this, the anchor — which arrives from a
            // URL — would let someone enumerate approvals on records they cannot otherwise open, using
            // this adapter as a side door into their own inbox filtered by somebody else's document.
            //
            // It is checked BEFORE the canonical test on purpose: "that record type has no approvals"
            // is a fact about the TYPE and is safe to say, but saying it for a record the caller cannot
            // see would still confirm the caller named something real.
            var decision = await _access.CanAccessEntityAsync(
                context, entity, CommCapabilities.Comments, cancellationToken);
            if (!decision.Allowed)
                return CommConversationApprovals.NotApplicable("this record is not visible to you");

            // RESOLVED AND VALIDATED BEFORE QUERYING — the defect this replaces.
            //
            // Only four entity types are canonical approval subjects. Passing anything else to
            // GetPendingForEntityAsync is not an error — it answers EMPTY by contract — but an empty
            // list is indistinguishable from "nothing is pending", so a SalesInvoice, PurchaseInvoice
            // or Quotation panel reported a working-but-idle integration for a record that can never
            // have an approval. Asking the map first turns that silent emptiness into an explicit
            // not-applicable the UI can hide.
            if (!CrossBuy.BL.Approvals.ApprovalEntityMap.SupportedEntityTypes.Contains(
                    entity.EntityCode, StringComparer.Ordinal))
                return CommConversationApprovals.NotApplicable(
                    $"'{entity.EntityCode}' has no approval workflow; the canonical approval subjects are "
                    + string.Join(", ", CrossBuy.BL.Approvals.ApprovalEntityMap.SupportedEntityTypes));

            IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow> rows;
            try
            {
                rows = await inbox.GetPendingForEntityAsync(
                    context, context.EmployeeId.Value, entity.EntityCode, entity.EntityId, cancellationToken);
            }
            catch (Exception ex)
            {
                // A failing approvals module must not take the conversation panel down with it — the same
                // isolation Phase 2 established for the kernel bridge. Reported as not-applicable rather
                // than as an empty supported list, so a transient outage never reads as "nothing pending".
                _log.LogError(ex,
                    "Approvals lookup failed for {Code}#{Id}; the conversation still renders.",
                    entity.EntityCode, entity.EntityId);
                return CommConversationApprovals.NotApplicable("the approvals module could not be read");
            }

            return CommConversationApprovals.From(rows.Select(r => new CommConversationApproval
            {
                Silo = r.Silo,
                EntityId = r.EntityId,
                Status = r.Status,
                TitleAr = r.TitleAr,
                TitleEn = r.TitleEn,
                RequesterNameAr = r.RequesterNameAr,
                RequesterNameEn = r.RequesterNameEn,
                SubmittedAt = r.SubmittedAt,
                Amount = r.Amount,
                Controller = r.Navigation.Controller,
                Action = r.Navigation.Action,
                RouteValues = r.Navigation.RouteValues,
                Reference = r.Reference,
            }).ToList());
        }

        private static CommTaskRef Map(
            TaskItem t,
            string relationship = CommTaskRelationship.SharedBusinessContext,
            long? linkId = null,
            long? commentId = null) => new()
        {
            Relationship = relationship,
            LinkId = linkId,
            CommentId = commentId,
            TaskId = t.ID,
            Title = t.Title,
            TitleEn = t.TitleEn,
            Status = t.Status,
            Priority = t.Priority,
            DueDate = t.DueDate,
            AssigneeEmployeeId = t.AssigneeEmployeeId,
            ProgressPct = t.ProgressPct,
        };
    }
}
