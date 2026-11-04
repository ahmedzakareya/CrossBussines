using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WHICH RECORDS A TASK MAY BE LINKED TO — and the two Communication endpoints that depend on it.
    //
    // WHAT CHANGED AND WHY IT NEEDED TESTS. TaskService.SaveAsync carried a bare string[] of seven
    // entity codes. It is now TaskEntityLinkPolicy: an explicit opt-in set, CHECKED against
    // IEntityRegistry. Two things therefore need proving that no test covered before — that the seven
    // survived the move exactly, and that consulting the registry did not quietly widen the gate to
    // every registered entity.
    //
    // THE WIDENING TRAP IS REAL AND IS ASSERTED BELOW. ListedInRecordPicker is true for NINETEEN
    // entities and looks like the obvious capability to key on; doing so would have silently made
    // LandedCost, StockCount and ten other documents linkable. P5 is the test that would have caught it.
    // ============================================================================================
    public sealed class TaskEntityLinkPolicyTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;

        private static IEntityRegistry Registry(PlatformTestHost host) => new EntityRegistry(host.Db);

        private static TaskService Tasks(PlatformTestHost host, IEntityRegistry? registry)
        {
            var accessor = new StubContextAccessor(new CrossBuy.Models.Platform.BusinessContext
            {
                CompanyId = CompanyA, EmployeeId = 1, UserId = "u1",
                Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
            });
            var events = new BusinessEventService(
                host.Db, new EntityRegistry(host.Db), accessor, NullLogger<BusinessEventService>.Instance);
            var publisher = new TaskCalendarEventPublisher(events);
            var notify = new TaskNotificationService(host.Db, new UatRecordingNotificationService(host.Db));

            // The 3-arg construction is the one the existing suites use; the registry overload is
            // exercised through the policy directly, so this stays the cheapest honest fixture.
            return registry is null
                ? new TaskService(host.Db, publisher, notify)
                : new TaskService(host.Db, publisher, notify);
        }

        private static TaskSaveInput Input(string? entityType, int? entityId) => new()
        {
            // An assignee is REQUIRED by SaveAsync; omitting it refuses the save before the link
            // policy is ever consulted, which is how the first version of these tests failed.
            Title = "مهمة", TitleEn = "Task", AssigneeEmployeeId = 1,
            Priority = "Normal", EntityType = entityType, EntityId = entityId,
        };

        // ---- the policy itself -------------------------------------------------------------------

        /// P1 — the seven that already worked still work. The whole point of the refactor was that it
        /// changed WHERE the decision lives, not WHAT it decides.
        [Theory]
        [InlineData("SalesInvoice")]
        [InlineData("Customer")]
        [InlineData("ManufWorkOrder")]
        [InlineData("PosOrder")]
        [InlineData("Employee")]
        [InlineData("Project")]
        [InlineData("Item")]
        public void P1_every_previously_supported_entity_type_is_still_eligible(string code)
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            Assert.True(TaskService.TaskEntityLinkPolicy.IsEligible(Registry(host), code, 5));
        }

        /// P2 — the four Communication anchors. Each is a registered entity that declares
        /// SupportsComments, which is what makes a conversation on it meaningful in the first place.
        [Theory]
        [InlineData("PurchaseInvoice")]
        [InlineData("Quotation")]
        [InlineData("JournalEntry")]
        [InlineData("Task")]
        public void P2_the_communication_anchors_are_eligible(string code)
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var registry = Registry(host);

            Assert.True(TaskService.TaskEntityLinkPolicy.IsEligible(registry, code, 5));

            // And the capability claim is measured, not assumed: if a module ever turns comments off for
            // one of these, the anchor stops being a conversation anchor and this says so.
            Assert.True(registry.TryGetDefinition(code, out var def) && def!.SupportsComments,
                code + " no longer declares SupportsComments, so it is not a conversation anchor.");
        }

        /// P3 — an entity that is not opted in is refused, however real it is.
        [Theory]
        [InlineData("Supplier")]       // registered, never opted in
        [InlineData("CrmTicket")]      // registered, never opted in
        [InlineData("NotAnEntity")]    // not registered at all
        [InlineData("")]
        [InlineData(null)]
        public void P3_an_entity_outside_the_opt_in_set_is_refused(string? code)
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            Assert.False(TaskService.TaskEntityLinkPolicy.IsEligible(Registry(host), code, 5));
        }

        /// P4 — THE REGISTRY IS THE AUTHORITY, and a missing one FAILS CLOSED. "Nobody told me what this
        /// is" must never read as "link it anyway".
        [Fact]
        public void P4_the_registry_and_the_id_are_both_required()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var registry = Registry(host);

            Assert.False(TaskService.TaskEntityLinkPolicy.IsEligible(registry, "SalesInvoice", 0));
            Assert.False(TaskService.TaskEntityLinkPolicy.IsEligible(registry, "SalesInvoice", -1));
            Assert.False(TaskService.TaskEntityLinkPolicy.IsEligible(registry, "SalesInvoice", null));

            // No registry, no link — even for a code that is unquestionably eligible with one.
            Assert.False(TaskService.TaskEntityLinkPolicy.IsEligible(null, "SalesInvoice", 5));
        }

        /// P5 — THE WIDENING TEST. ListedInRecordPicker is true for nineteen entities. Keying eligibility
        /// on it, or on "any registered entity", would make all of these linkable. They are not.
        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("GoodsReceipt")]
        [InlineData("DeliveryNote")]
        [InlineData("StockCount")]
        [InlineData("StockTransfer")]
        [InlineData("StockWriteOff")]
        [InlineData("LandedCost")]
        [InlineData("Payment")]
        [InlineData("Receipt")]
        [InlineData("SalesOrder")]
        [InlineData("SalesReturn")]
        [InlineData("PurchaseReturn")]
        public void P5_a_registered_picker_listed_entity_is_not_automatically_linkable(string code)
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var registry = Registry(host);

            // Non-vacuous: these really are registered and really are picker-listed, which is exactly why
            // keying on that flag would have been wrong.
            Assert.True(registry.TryGetDefinition(code, out var def) && def!.ListedInRecordPicker);
            Assert.False(TaskService.TaskEntityLinkPolicy.IsEligible(registry, code, 5));
        }

        /// P6 — THE REGISTRY FLAGS EXACTLY ELEVEN. TAB-1 added EligibleForTaskLink and this is the test
        /// that says eligibility did not widen when the authority moved: the capability must mark the
        /// original seven and the four Communication anchors, and nothing else in a registry of 36.
        [Fact]
        public void P6_the_registry_marks_exactly_the_seven_preserved_and_the_four_added()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var registry = Registry(host);

            var flagged = registry.GetDefinitions()
                .Where(d => d.EligibleForTaskLink)
                .Select(d => d.Code)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    "Customer", "Employee", "Item", "JournalEntry", "ManufWorkOrder", "PosOrder",
                    "Project", "PurchaseInvoice", "Quotation", "SalesInvoice", "Task",
                },
                flagged);
        }

        // ---- behaviour through SaveAsync ----------------------------------------------------------

        /// P7 — an eligible link is stored, and an ineligible one NORMALISES TO NULL rather than refusing
        /// the save. That second half is the pre-existing contract the Communication adapter depends on:
        /// it reports the dropped anchor as a warning, which it can only do if the task was still created.
        [Fact]
        public async Task P7_an_eligible_link_is_stored_and_an_ineligible_one_is_dropped_not_refused()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var tasks = Tasks(host, Registry(host));

            var (okEligible, _, eligibleId) = await tasks.SaveAsync(CompanyA, Input("SalesInvoice", 42), 1);
            Assert.True(okEligible);
            var linked = host.Db.TaskItems.Single(t => t.ID == eligibleId);
            Assert.Equal("SalesInvoice", linked.EntityType);
            Assert.Equal(42, linked.EntityId);

            var (okIneligible, _, droppedId) = await tasks.SaveAsync(CompanyA, Input("Supplier", 42), 1);
            Assert.True(okIneligible);                       // created, NOT refused
            var dropped = host.Db.TaskItems.Single(t => t.ID == droppedId);
            Assert.Null(dropped.EntityType);                 // and the anchor is gone, as before
        }

        /// P8 — a newly eligible Communication anchor reaches storage. Without this, P2 would only prove
        /// the policy's opinion and not that SaveAsync acts on it.
        [Fact]
        public async Task P8_a_communication_anchor_is_stored_through_SaveAsync()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var tasks = Tasks(host, Registry(host));

            var (ok, _, id) = await tasks.SaveAsync(CompanyA, Input("Quotation", 7), 1);
            Assert.True(ok);
            var row = host.Db.TaskItems.Single(t => t.ID == id);
            Assert.Equal("Quotation", row.EntityType);
            Assert.Equal(7, row.EntityId);
        }

        /// P9 — COMPANY ISOLATION. The task is written into the company that was passed, and a second
        /// company's reader does not find it. Asserted asymmetrically: a symmetric count would pass with
        /// the predicate inverted.
        [Fact]
        public async Task P9_a_linked_task_belongs_only_to_the_company_it_was_created_in()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var tasks = Tasks(host, Registry(host));

            await tasks.SaveAsync(CompanyA, Input("SalesInvoice", 42), 1);
            await tasks.SaveAsync(CompanyA, Input("Quotation", 7), 1);
            await tasks.SaveAsync(CompanyB, Input("SalesInvoice", 42), 1);

            Assert.Equal(2, host.Db.TaskItems.Count(t => t.CompanyId == CompanyA));
            Assert.Equal(1, host.Db.TaskItems.Count(t => t.CompanyId == CompanyB));

            // Same anchor, different companies, different rows — the link does not join them.
            var a = host.Db.TaskItems.Single(t => t.CompanyId == CompanyB);
            Assert.Equal("SalesInvoice", a.EntityType);
            Assert.DoesNotContain(host.Db.TaskItems.Where(t => t.CompanyId == CompanyA), t => t.ID == a.ID);
        }

        // ---- the two Communication endpoints ------------------------------------------------------
        //
        // Asserted on the SOURCE, which is the convention TasksWithoutCommunicationTests already
        // established for these endpoints and for the same reason: invoking them needs a company
        // resolver, a business context and a database, none of which is what is under test here.

        private static string ControllerSource()
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "CrossBuy", "Controllers", "TasksController.cs")))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.True(dir is not null, "Could not locate TasksController.cs from the test output directory.");
            return File.ReadAllText(Path.Combine(dir!, "CrossBuy", "Controllers", "TasksController.cs"));
        }

        private static string ActionBody(string source, string action)
        {
            var at = source.IndexOf(" " + action + "(", StringComparison.Ordinal);
            Assert.True(at > 0, "Action not found: " + action);
            var open = source.IndexOf('{', at);
            int depth = 0, i = open;
            for (; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) break;
            }
            return source[open..i];
        }

        /// P10 — both new endpoints degrade explicitly rather than throwing when Communication is absent.
        [Theory]
        [InlineData("CreateFromMessage")]
        [InlineData("ForConversation")]
        public void P10_the_new_endpoints_report_unavailable_instead_of_failing(string action)
        {
            var body = ActionBody(ControllerSource(), action);

            Assert.Contains("TryTaskIntegration()", body, StringComparison.Ordinal);
            Assert.Contains("CommunicationUnavailable()", body, StringComparison.Ordinal);
            Assert.Equal("communication_unavailable", TasksController.CommunicationUnavailableCode);
        }

        /// P11 — the permission gate runs BEFORE the availability check, so an unauthorized caller is
        /// refused without learning which platforms this deployment runs.
        [Theory]
        [InlineData("CreateFromMessage")]
        [InlineData("ForConversation")]
        public void P11_the_permission_gate_precedes_the_availability_check(string action)
        {
            var body = ActionBody(ControllerSource(), action);

            var gate = body.IndexOf("_tasksAccess.CanAsync(", StringComparison.Ordinal);
            var availability = body.IndexOf("TryTaskIntegration()", StringComparison.Ordinal);

            Assert.True(gate >= 0, action + " does not consult the task permission gate.");
            Assert.True(gate < availability,
                action + " checks platform availability before permission.");
        }

        /// P12 — neither endpoint is a mandatory dependency: the controller still resolves Communication
        /// through the container, so a deployment without it still constructs TasksController.
        [Fact]
        public void P12_communication_remains_an_optional_container_lookup()
        {
            var source = ControllerSource();

            Assert.Contains("_services.GetService<CrossBuy.BL.Communication.ICommTaskIntegrationService>()",
                source, StringComparison.Ordinal);

            // Nothing about Communication reached the constructor.
            var ctor = typeof(TasksController).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
            Assert.DoesNotContain(ctor.GetParameters(),
                p => p.ParameterType.FullName?.Contains("Communication", StringComparison.Ordinal) == true);
        }

        // ---- the panel's contract -----------------------------------------------------------------
        //
        // The two endpoints exist to serve _EntityConversation. Its request shape is therefore a
        // CONTRACT, and these tests pin it: the first implementation took commentId alone and a
        // threadId from the query string, and the panel sends neither of those shapes.

        /// P14 — the listing takes the RECORD, not a thread id. A threadId parameter would be a record
        /// identifier supplied by the browser, walkable by incrementing it.
        [Fact]
        public void P14_the_listing_takes_entity_and_id_and_never_a_client_thread_id()
        {
            var action = typeof(TasksController).GetMethod("ForConversation")!;
            var names = action.GetParameters().Select(p => p.Name).ToArray();

            Assert.Contains("entity", names);
            Assert.Contains("id", names);
            Assert.DoesNotContain("threadId", names);

            // And the thread is resolved through the authorization-respecting lookup, not fabricated.
            var body = ActionBody(ControllerSource(), "ForConversation");
            Assert.Contains("ListForEntityAsync", body, StringComparison.Ordinal);
        }

        /// P15 — the create takes the panel's three identifiers.
        [Fact]
        public void P15_the_create_takes_entity_id_and_commentId()
        {
            var names = typeof(TasksController).GetMethod("CreateFromMessage")!
                .GetParameters().Select(p => p.Name).ToArray();

            foreach (var expected in new[]
                     { "entity", "id", "commentId", "title", "titleEn", "description",
                       "assigneeEmployeeId", "priority", "dueDate", "dedupKey" })
                Assert.Contains(expected, names);
        }

        /// P16 — THE ANCHOR IS NOT TAKEN FROM THE BROWSER. `entity` is bound so the request shape
        /// matches, but the task's anchor is derived server-side by the adapter from commentId. If this
        /// action ever starts passing the caller's entity into the create request, that is a client-
        /// supplied anchor and this test says so.
        [Fact]
        public void P16_the_create_does_not_pass_the_client_entity_into_the_request()
        {
            var body = ActionBody(ControllerSource(), "CreateFromMessage");

            // THE INITIALIZER ONLY, not everything after it. Slicing to the end of the action made
            // this assertion read the action's prose as well, so a COMMENT saying the entity is not
            // used was enough to fail it — a test that a correct explanation can break is measuring
            // the wrong text.
            var from = body.IndexOf("new CrossBuy.BL.Communication.CommTaskCreateRequest", StringComparison.Ordinal);
            Assert.True(from > 0, "The create request is gone.");
            var request = body[from..body.IndexOf("}, ct)", from, StringComparison.Ordinal)];

            Assert.DoesNotContain("entity", request, StringComparison.Ordinal);
            Assert.Contains("CommentId = commentId", request, StringComparison.Ordinal);
        }

        /// P17 — the listing projects every field the panel renders. An earlier projection sent five of
        /// them, so rows drew with an undefined route, no due date and no assignee.
        [Fact]
        public void P17_the_listing_projects_every_field_the_panel_renders()
        {
            var body = ActionBody(ControllerSource(), "ForConversation");

            foreach (var field in new[]
                     { "taskId", "title", "status", "priority", "dueDate", "assigneeEmployeeId",
                       "progressPct", "relationship", "route", "linkId", "commentId" })
                Assert.Contains(field + " =", body, StringComparison.Ordinal);

            Assert.Contains("linkingAvailable = integration.LinkingAvailable", body, StringComparison.Ordinal);
        }

        /// P18 — the CREATE response carries every field the panel reads back. `sendError(d)` reads
        /// `d.error` first and falls back to the HTTP status, and the confirmation branches on
        /// `d.anchorWarning` to decide between a success and a warning-coloured message. A response
        /// missing either leaves the dialog saying "failed" after a task was in fact created.
        [Fact]
        public void P18_the_create_response_carries_what_the_panel_reads()
        {
            var body = ActionBody(ControllerSource(), "CreateFromMessage");
            var response = body[body.LastIndexOf("return Json(new", StringComparison.Ordinal)..];

            foreach (var field in new[] { "ok =", "error =", "taskId =", "anchorWarning =", "linkId," })
                Assert.Contains(field, response, StringComparison.Ordinal);

            // THE PARTIAL STATE IS A FIELD. Creating the task and attaching it to the message are two
            // writes with no shared transaction, so "task made, message not attached" is a reachable
            // outcome. `linked` states it; without it a caller reading `ok` alone would draw a plain
            // success over a half-finished pair.
            Assert.Contains("linked = linkId is > 0", response, StringComparison.Ordinal);
            Assert.Contains("linkError", response, StringComparison.Ordinal);

            // AnchorWarning comes from the service and is not reinterpreted on the way out: the panel
            // shows a different, warning-coloured confirmation for it, and a controller that swallowed
            // it would tell the user the anchor was linked when it was dropped.
            Assert.Contains("anchorWarning = result.AnchorWarning", response, StringComparison.Ordinal);
        }

        /// P19 — the approvals endpoint takes the RECORD and nothing else. No threadId, and no
        /// companyId or employeeId either: the company comes from the resolved scope and the viewer from
        /// the BusinessContext, so neither can be supplied by the caller.
        [Fact]
        public void P19_the_approvals_endpoint_takes_only_the_record()
        {
            var action = typeof(TasksController).GetMethod("ConversationApprovals");
            Assert.NotNull(action);

            var names = action!.GetParameters().Select(p => p.Name).ToArray();
            Assert.Contains("entity", names);
            Assert.Contains("id", names);
            foreach (var forbidden in new[] { "threadId", "companyId", "employeeId" })
                Assert.DoesNotContain(forbidden, names);

            // A read is a GET, and the session gate is not optional.
            var source = ControllerSource();
            var head = source[..source.IndexOf(" ConversationApprovals(", StringComparison.Ordinal)];
            var attrs = head[head.LastIndexOf("[SessionValidation]", StringComparison.Ordinal)..];
            Assert.Contains("[HttpGet]", attrs, StringComparison.Ordinal);
            Assert.DoesNotContain("[HttpPost]", attrs, StringComparison.Ordinal);
        }

        /// P20 — permission before availability, and the platform's own error vocabulary rather than a
        /// second one invented here.
        [Fact]
        public void P20_the_approvals_endpoint_gates_then_checks_availability_and_uses_the_shared_codes()
        {
            var body = ActionBody(ControllerSource(), "ConversationApprovals");

            var gate = body.IndexOf("_tasksAccess.CanAsync(", StringComparison.Ordinal);
            var availability = body.IndexOf("TryTaskIntegration()", StringComparison.Ordinal);
            Assert.True(gate >= 0 && gate < availability,
                "availability is checked before permission, so an unauthorized caller learns the platform state.");

            Assert.Contains("CommTaskErrors.NoCompanyScope", body, StringComparison.Ordinal);
            Assert.Contains("CommTaskErrors.ConversationNotFound", body, StringComparison.Ordinal);
            Assert.Contains("CommunicationUnavailable()", body, StringComparison.Ordinal);

            // No per-state branching on the adapter's answer: absent module, no workflow and not-an-
            // approver must all render as the same empty list.
            Assert.DoesNotContain("approvals.Count == 0", body, StringComparison.Ordinal);
            Assert.DoesNotContain("NotFound()", body, StringComparison.Ordinal);
        }

        /// P21 — THE ROUTE IS RESOLVED ON THE SERVER. CommConversationApproval carries Controller,
        /// Action and RouteValues and deliberately no url; the browser supplies none either. If this
        /// endpoint ever starts composing a path by hand, that is both a hard-coded foreign URL shape
        /// and a place an unescaped route value could be smuggled in.
        [Fact]
        public void P21_the_approval_url_is_resolved_through_the_routing_table()
        {
            var source = ControllerSource();
            var body = ActionBody(source, "ConversationApprovals");
            Assert.Contains("url = ApprovalUrl(a)", body, StringComparison.Ordinal);

            var helper = ActionBody(source, "ApprovalUrl");
            Assert.Contains("Url.Action(approval.Action, approval.Controller", helper, StringComparison.Ordinal);

            // Built from the delivered values, never from a string template.
            Assert.DoesNotContain("\"/\" +", helper, StringComparison.Ordinal);
            Assert.DoesNotContain("$\"/", helper, StringComparison.Ordinal);
        }

        // ---- link / unlink: the controller's own decisions -----------------------------------------
        //
        // SOURCE-CONTRACT, NOT BEHAVIOURAL, and the split is deliberate. The SERVICE layer is already
        // covered behaviourally by TAB-3's CommTaskIntegrationTests — successful link and unlink,
        // edit-authority, cross-company rejection, duplicate link, a comment from another conversation,
        // and the unprovisioned answer. Re-driving those through a controller would test their code
        // twice and mine not at all.
        //
        // What is NOT covered anywhere else is what this controller decides on its own: the verb and
        // the token, resolving the caller server-side, asking LinkingAvailable BEFORE the service, the
        // status map, and the rule that a success without a link id is not a success. Those are the
        // assertions below. No test in this repository constructs a controller (TasksWithoutCommunication
        // -Tests records why: it needs a company resolver, a business context and a database), and
        // these two actions additionally need an IUrlHelper, so the surface is read from source.

        /// P22 — both write endpoints are POSTs carrying the anti-forgery token. A link that could be
        /// made by a GET is a link any page on the internet can make on the caller's behalf.
        [Theory]
        [InlineData("LinkConversation")]
        [InlineData("UnlinkConversation")]
        public void P22_the_write_endpoints_are_guarded_posts(string action)
        {
            var source = ControllerSource();
            var head = source[..source.IndexOf(" " + action + "(", StringComparison.Ordinal)];
            var attrs = head[head.LastIndexOf("[SessionValidation]", StringComparison.Ordinal)..];

            Assert.Contains("[HttpPost]", attrs, StringComparison.Ordinal);
            Assert.Contains("ValidateAntiForgeryToken", attrs, StringComparison.Ordinal);
        }

        /// P23 — neither endpoint accepts a company or an employee from the caller. Both are resolved
        /// from the request's own context; a form field would be an authority the browser chose.
        [Theory]
        [InlineData("LinkConversation")]
        [InlineData("UnlinkConversation")]
        public void P23_no_company_or_employee_is_accepted_from_the_client(string action)
        {
            var names = typeof(TasksController).GetMethod(action)!
                .GetParameters().Select(p => p.Name).ToArray();

            foreach (var forbidden in new[] { "companyId", "employeeId", "CompanyId", "EmployeeId" })
                Assert.DoesNotContain(forbidden, names);

            var body = ActionBody(ControllerSource(), action);
            Assert.Contains("_company.ResolveAsync()", body, StringComparison.Ordinal);
            Assert.Contains("_businessContexts.TryGetCurrentAsync", body, StringComparison.Ordinal);
        }

        /// P24 — the link endpoint takes exactly the three documented inputs.
        [Fact]
        public void P24_the_link_endpoint_takes_threadId_taskId_and_optional_commentId()
        {
            var ps = typeof(TasksController).GetMethod("LinkConversation")!.GetParameters();
            var byName = ps.ToDictionary(p => p.Name!, p => p.ParameterType);

            Assert.Equal(typeof(long), byName["threadId"]);
            Assert.Equal(typeof(int), byName["taskId"]);
            Assert.Equal(typeof(long?), byName["commentId"]);

            Assert.Equal(typeof(long), typeof(TasksController).GetMethod("UnlinkConversation")!
                .GetParameters().Single(p => p.Name == "linkId").ParameterType);
        }

        /// P25 — AVAILABILITY IS ASKED BEFORE THE SERVICE, and the permission gate before that. A
        /// caller with no right to edit must be refused without learning whether this deployment has
        /// link persistence; a deployment without it must refuse before any write is attempted.
        [Theory]
        [InlineData("LinkConversation")]
        [InlineData("UnlinkConversation")]
        public void P25_permission_then_availability_then_the_service(string action)
        {
            var body = ActionBody(ControllerSource(), action);

            var gate = body.IndexOf("_tasksAccess.CanAsync(", StringComparison.Ordinal);
            var available = body.IndexOf("LinkUnavailable(integration)", StringComparison.Ordinal);
            var call = body.IndexOf(action == "LinkConversation" ? "LinkExistingAsync" : "UnlinkAsync",
                StringComparison.Ordinal);

            Assert.True(gate >= 0 && available > gate && call > available,
                action + " does not run permission, then availability, then the service, in that order.");
        }

        /// P26 — THE UNPROVISIONED ANSWER IS 503 AND THE SERVICE'S OWN CODE. Not 200-with-a-flag, which
        /// every proxy and log would record as a success, and not a code invented here.
        [Fact]
        public void P26_unprovisioned_link_persistence_answers_503_with_the_shared_code()
        {
            // READ AS A DECLARATION SLICE, not through ActionBody: LinkUnavailable is expression-
            // bodied, so brace-matching lands on the anonymous object rather than the ternary that
            // decides. The helper found the object, missed the condition, and failed honestly.
            var src = ControllerSource();
            var from = src.IndexOf("LinkUnavailable(", StringComparison.Ordinal);
            Assert.True(from > 0, "LinkUnavailable is gone.");
            var helper = src[from..src.IndexOf("});", from, StringComparison.Ordinal)];

            Assert.Contains("integration.LinkingAvailable", helper, StringComparison.Ordinal);
            Assert.Contains("Status503ServiceUnavailable", helper, StringComparison.Ordinal);
            Assert.Contains("CommTaskErrors.LinkPersistenceNotProvisioned", helper, StringComparison.Ordinal);
        }

        /// P27 — NEVER A SUCCESS WITHOUT A PERSISTED LINK ID. An Ok result carrying no LinkId means
        /// nothing was stored, and reporting it as success would show the user an attachment that does
        /// not exist. This is the single assertion that would catch that.
        [Fact]
        public void P27_a_link_without_an_id_is_not_reported_as_success()
        {
            var body = ActionBody(ControllerSource(), "LinkConversation");

            Assert.Contains("result.LinkId is not > 0", body, StringComparison.Ordinal);
            Assert.Contains("linkId = result.LinkId.Value", body, StringComparison.Ordinal);

            // The success payload is reached only after BOTH checks, and now after a third: a
            // message-level request answered by a different relationship is a conflict, not a
            // success. Ordering is the assertion — each guard must precede the payload it protects.
            var notOk = body.IndexOf("if (!result.Ok)", StringComparison.Ordinal);
            var noId = body.IndexOf("result.LinkId is not > 0", StringComparison.Ordinal);
            var warned = body.IndexOf("result.LinkWarning", StringComparison.Ordinal);
            var success = body.LastIndexOf("ok = true", StringComparison.Ordinal);
            Assert.True(notOk >= 0 && noId > notOk && warned > noId && success > warned,
                "LinkConversation must refuse, then require an id, then reject a downgrade, then succeed.");
        }

        /// P28 — the status map does not widen what the service disclosed. A link that is absent and a
        /// link in another company arrive as the SAME code and must leave as the same status, or the
        /// endpoint becomes a probe for which links exist elsewhere.
        [Fact]
        public void P28_absent_and_foreign_are_mapped_to_one_status()
        {
            var source = ControllerSource();
            var notFound = source[source.IndexOf("LinkNotFoundCodes", StringComparison.Ordinal)..];
            notFound = notFound[..notFound.IndexOf("};", StringComparison.Ordinal)];

            foreach (var code in new[] { "LinkNotFound", "TaskNotFound", "ConversationNotFound", "MessageNotFound" })
                Assert.Contains(code, notFound, StringComparison.Ordinal);

            var map = ActionBody(source, "LinkFailure");
            Assert.Contains("Status404NotFound", map, StringComparison.Ordinal);
            Assert.Contains("Status403Forbidden", map, StringComparison.Ordinal);
            Assert.Contains("Status400BadRequest", map, StringComparison.Ordinal);

            // No branch invents a code of its own: every one comes from CommTaskErrors.
            Assert.DoesNotContain("\"link_", map, StringComparison.Ordinal);
        }

        /// P29 — SUPPORTED=FALSE IS NOT AN EMPTY INBOX. The approvals response carries the flag and the
        /// reason, so a panel can hide a section that will never apply instead of rendering a
        /// permanently empty one.
        [Fact]
        public void P29_the_approvals_response_distinguishes_unsupported_from_empty()
        {
            var body = ActionBody(ControllerSource(), "ConversationApprovals");

            Assert.Contains("supported = approvals.Supported", body, StringComparison.Ordinal);
            Assert.Contains("notApplicableReason = approvals.NotApplicableReason", body, StringComparison.Ordinal);
            Assert.Contains("approvals.Items.Select", body, StringComparison.Ordinal);
        }

        /// P30 — the four pre-existing routes survive. A rename is a silent 404 for a panel that still
        /// points at the old name, so the names themselves are the contract.
        [Theory]
        [InlineData("CreateFromMessage")]
        [InlineData("ForConversation")]
        [InlineData("ConversationApprovals")]
        [InlineData("LinkSearch")]
        public void P30_the_existing_routes_are_still_present(string action)
        {
            Assert.NotNull(typeof(TasksController).GetMethod(action));
        }

        // ---- the task picker: the 404 this wave was called to explain --------------------------
        //
        // WHAT HAPPENED. The conversation panel's candidate search was pointed at LinkSearch, which is
        // the picker for the OTHER direction — "which business record is this task about". It searches
        // SalesInvoices, Customers and Items and returns THEIR ids, so a live attempt arrived as
        // { threadId: 10002, taskId: 12545 } and was refused with task_not_found. Measured in
        // CrossBuyDB2: no TaskItem has id 12545; one SalesInvoice does. The refusal was correct.
        //
        // These guards exist so the two pickers can never be confused again.

        /// P31 — THE TASK PICKER IS NOT THE RECORD PICKER. LinkSearch takes an entityType and answers
        /// with records of that type; LinkCandidates takes a term and answers with tasks. A host that
        /// points the conversation panel at the first one offers invoice ids as task ids.
        [Fact]
        public void P31_the_task_picker_and_the_record_picker_are_different_endpoints()
        {
            var candidates = typeof(TasksController).GetMethod("LinkCandidates");
            Assert.NotNull(candidates);

            var names = candidates!.GetParameters().Select(p => p.Name).ToArray();
            Assert.Contains("term", names);

            // The record picker is addressed by entity TYPE. The task picker must not be.
            Assert.DoesNotContain("entityType", names);
            Assert.Contains("entityType", typeof(TasksController).GetMethod("LinkSearch")!
                .GetParameters().Select(p => p.Name)!);
        }

        /// P32 — SEARCH AND LINK SHARE ONE AUTHORITY. A candidate the picker offers must be one the
        /// link endpoint accepts; otherwise the UI teaches people the system is broken. Both ask the
        /// same action of the same access service, and the picker reads through the authoritative task
        /// read path rather than the table.
        [Fact]
        public void P32_the_picker_and_the_linker_apply_the_same_authority()
        {
            var picker = ActionBody(ControllerSource(), "LinkCandidates");
            var linker = ActionBody(ControllerSource(), "LinkConversation");

            foreach (var body in new[] { picker, linker })
            {
                Assert.Contains("_tasksAccess.CanAsync(", body, StringComparison.Ordinal);
                Assert.Contains("TasksActions.Edit", body, StringComparison.Ordinal);
                Assert.Contains("_businessContexts.TryGetCurrentAsync", body, StringComparison.Ordinal);
            }

            // The candidate set is the AUTHORIZED one, never db.TaskItems filtered by company alone —
            // company is tenancy, not authority.
            Assert.Contains("ITaskReadAuthorizer", picker, StringComparison.Ordinal);
            Assert.Contains("authorizer.ApplyAsync", picker, StringComparison.Ordinal);
            Assert.Contains("ForListAsync", picker, StringComparison.Ordinal);
        }

        /// P33 — the picker finds a task by TITLE and by NUMBER, and a number is matched both ways so
        /// "1254" still finds "Reconcile 1254".
        [Fact]
        public void P33_the_picker_searches_by_title_and_by_id()
        {
            var body = ActionBody(ControllerSource(), "LinkCandidates");

            Assert.Contains("int.TryParse(term", body, StringComparison.Ordinal);
            Assert.Contains("t.ID == byId", body, StringComparison.Ordinal);
            Assert.Contains("t.Title != null && t.Title.Contains(term)", body, StringComparison.Ordinal);
            Assert.Contains("t.TitleEn != null && t.TitleEn.Contains(term)", body, StringComparison.Ordinal);
        }

        /// P34 — A NUMBER IS NOT AN ANSWER. The owner's requirement: the picker must say what each task
        /// IS. Every field the row needs is projected, and the legacy `text` key carries the TITLE so an
        /// older picker renders a name rather than an id.
        [Fact]
        public void P34_the_picker_returns_what_a_person_needs_to_choose()
        {
            var body = ActionBody(ControllerSource(), "LinkCandidates");

            foreach (var field in new[]
                     { "id =", "text = title", "title,", "summary =", "status =", "priority =",
                       "assigneeName", "dueDate =", "entityType =", "entityId =" })
                Assert.Contains(field, body, StringComparison.Ordinal);
        }

        /// P35 — NO CROSS-COMPANY EMPLOYEE READ, and no N+1. The assignee lookup is company-scoped and
        /// set-based; resolving a name per row is how a picker becomes twenty-five queries and how a
        /// foreign employee's name reaches a screen.
        [Fact]
        public void P35_the_assignee_lookup_is_company_scoped_and_set_based()
        {
            var body = ActionBody(ControllerSource(), "LinkCandidates");

            Assert.Contains("e.EmpCompanyID == scope.CompanyId", body, StringComparison.Ordinal);
            Assert.Contains("empIds.Contains(e.ID)", body, StringComparison.Ordinal);

            // One lookup for the page, outside the projection loop.
            Assert.True(body.IndexOf("db.Employee.AsNoTracking()", StringComparison.Ordinal)
                      < body.IndexOf("results = page.Select", StringComparison.Ordinal));
        }

        [Fact]
        public void P35a_the_picker_resolves_assignee_names_in_the_current_ui_language()
        {
            var body = ActionBody(ControllerSource(), "LinkCandidates");
            Assert.Contains("CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn)", body, StringComparison.Ordinal);

            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
                Assert.Equal("Ahmed Zakarya", EmployeeNames.Of("أحمد زكريا", "Ahmed Zakarya"));

                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
                Assert.Equal("أحمد زكريا", EmployeeNames.Of("أحمد زكريا", "Ahmed Zakarya"));

                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
                Assert.Equal("أحمد زكريا", EmployeeNames.Of("أحمد زكريا", null));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = previous;
            }
        }

        /// P36 — A REFUSAL STAYS A REFUSAL. An unresolved context or a caller without Edit gets an
        /// empty candidate list, never an unfiltered one — the same shape LinkSearch already used so a
        /// prober cannot tell "no company" from "nothing matched".
        [Fact]
        public void P36_an_unauthorized_picker_returns_an_empty_list_not_everything()
        {
            var body = ActionBody(ControllerSource(), "LinkCandidates");

            var empties = System.Text.RegularExpressions.Regex.Matches(
                body, @"return Json\(new \{ results = Array\.Empty<object>\(\) \}\);").Count;
            Assert.True(empties >= 3,
                "the no-context, no-permission and no-authorization paths must each fail closed.");
        }

        /// P37 — the three wired hosts point the panel at the TASK picker. This is the assertion that
        /// would have caught the original defect: the hosts named LinkSearch, and nothing said so.
        [Theory]
        [InlineData("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml")]
        [InlineData("CrossBuy/Views/Inventory/QuotationDetails.cshtml")]
        public void P37_the_hosts_point_the_panel_at_the_task_picker(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar))))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.True(dir is not null, "could not locate " + relative);

            var host = File.ReadAllText(Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Contains("ConversationLinkSearchUrl = Url.Action(\"LinkCandidates\", \"Tasks\")",
                host, StringComparison.Ordinal);
            Assert.DoesNotContain("ConversationLinkSearchUrl = Url.Action(\"LinkSearch\"",
                host, StringComparison.Ordinal);
        }

        // ---- message-level linking: one task, many messages -----------------------------------
        //
        // WHERE THE BEHAVIOUR IS ALREADY PROVEN. comm_task_links_m2m replaced one uniqueness rule with
        // two filtered ones, and the service and store sides are covered BEHAVIOURALLY by TAB-3:
        // EfCommTaskLinkStoreTests proves a second comment creates an independent relationship, that
        // the indexes refuse a duplicate of the same kind, and that a soft unlink frees the pair;
        // CommTaskIntegrationTests proves one task linked to several messages in one conversation, one
        // message linked to several tasks, a message from another conversation refused as provenance,
        // and the cross-company cases. Re-driving those through a controller would test their code
        // twice and this controller's not at all.
        //
        // WHAT ONLY THIS CONTROLLER DECIDES is how many ROWS those relationships become on the way out
        // — and that is exactly where the defect was. ForConversation collapsed everything into a
        // Dictionary<int, CommTaskRef> keyed by task id, which was right under the old one-link rule
        // and silently destroys relationships under the new one. The guards below hold the new shape.

        /// P38 — THE TASK ID IS NOT THE KEY. This is the regression guard for the collapse: explicit
        /// links are held by LinkId, so two links to the same task survive as two rows.
        [Fact]
        public void P38_explicit_links_are_keyed_by_link_id_and_never_by_task_id()
        {
            var body = ActionBody(ControllerSource(), "ForConversation");

            Assert.Contains("Dictionary<long, (long ThreadId, CrossBuy.BL.Communication.CommTaskRef Ref)>",
                body, StringComparison.Ordinal);
            Assert.Contains("explicitLinks[r.LinkId.Value]", body, StringComparison.Ordinal);

            // The shape that caused it must not come back in any form.
            Assert.DoesNotContain("byTask[r.TaskId] = r", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Dictionary<int, CrossBuy.BL.Communication.CommTaskRef>",
                body, StringComparison.Ordinal);
        }

        /// P39 — INFERRED CONTEXT IS STILL COLLAPSED, and suppressed where a real link exists. Shared
        /// business context has no LinkId and is the same inference however often it is derived, so it
        /// collapses by task; a task that is deliberately linked must not also appear as a guess.
        [Fact]
        public void P39_shared_context_collapses_by_task_and_yields_to_an_explicit_link()
        {
            var body = ActionBody(ControllerSource(), "ForConversation");

            Assert.Contains("if (!shared.ContainsKey(r.TaskId))", body, StringComparison.Ordinal);
            Assert.Contains("shared.Where(kv => !linkedTaskIds.Contains(kv.Key))", body, StringComparison.Ordinal);
        }

        /// P40 — EVERY ROW SAYS WHICH RELATIONSHIP IT IS. Without linkId the row cannot be unlinked,
        /// without commentId and scope the reader cannot tell a message link from a conversation one,
        /// and without threadId a record with two conversations cannot say which this belongs to.
        [Fact]
        public void P40_each_relationship_row_can_be_identified_and_acted_on()
        {
            var body = ActionBody(ControllerSource(), "ForConversation");
            var response = body[body.LastIndexOf("return Json(new", StringComparison.Ordinal)..];

            foreach (var field in new[] { "linkId =", "commentId =", "scope =", "threadId =", "relationship =" })
                Assert.Contains(field, response, StringComparison.Ordinal);

            // A row with no id cannot be addressed by the unlink endpoint, so it is dropped rather
            // than drawn as something the reader can act on and then cannot.
            Assert.Contains("r.LinkId is not > 0", body, StringComparison.Ordinal);
        }

        /// P40a — LINKED TASK ASSIGNEES USE THE READER'S LANGUAGE. The panel already renders the
        /// assignee name and avatar when present; this endpoint must supply those fields from employees
        /// in the resolved company, so an English reader does not see the Arabic-only fallback.
        [Fact]
        public void P40a_linked_task_assignee_is_localized_and_company_scoped()
        {
            var body = ActionBody(ControllerSource(), "ForConversation");

            Assert.Contains("e.EmpCompanyID == scope.CompanyId", body, StringComparison.Ordinal);
            Assert.Contains("assigneeIds.Contains(e.ID)", body, StringComparison.Ordinal);
            Assert.Contains("CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn)", body, StringComparison.Ordinal);
            Assert.Contains("assigneeName = assignees.TryGetValue", body, StringComparison.Ordinal);
            Assert.Contains("assigneeAvatar = assignees.TryGetValue", body, StringComparison.Ordinal);
        }

        /// P41 — A DOWNGRADE IS NOT A SUCCESS. The service reports LinkWarning when the row it returned
        /// has different provenance from the one asked for. Answering 200 would put a confirmation on
        /// screen for a message link nobody made.
        [Fact]
        public void P41_a_message_request_answered_by_another_relationship_is_a_conflict()
        {
            var body = ActionBody(ControllerSource(), "LinkConversation");

            // THE WHOLE GUARD, NOT ITS PARTS. Asserting that "result.LinkWarning" and
            // "Status409Conflict" each appear somewhere in the body passes against
            // `if (false && !string.IsNullOrEmpty(result.LinkWarning))` — a mutant that leaves every
            // asserted token exactly where it was and disables the branch anyway. It was run, and it
            // SURVIVED this test in its first form. The condition is now pinned whole, so neutralising
            // the branch has to rewrite the text being read.
            Assert.Contains("if (!string.IsNullOrEmpty(result.LinkWarning))", body, StringComparison.Ordinal);
            Assert.Contains("Status409Conflict", body, StringComparison.Ordinal);

            // ...and nothing may be ANDed in front of it to the same end.
            Assert.DoesNotContain("&& !string.IsNullOrEmpty(result.LinkWarning)", body, StringComparison.Ordinal);

            // The existing link's id travels with the refusal: the only useful next act needs it.
            var warn = body.IndexOf("Status409Conflict", StringComparison.Ordinal);
            var tail = body[warn..];
            Assert.Contains("linkId = result.LinkId.Value", tail, StringComparison.Ordinal);
        }

        /// P42 — the caller is told WHAT was written, not merely that something was.
        [Fact]
        public void P42_the_link_response_names_the_message_and_the_scope()
        {
            var body = ActionBody(ControllerSource(), "LinkConversation");
            var success = body[body.LastIndexOf("return Json(new", StringComparison.Ordinal)..];

            Assert.Contains("commentId", success, StringComparison.Ordinal);
            Assert.Contains("scope = commentId is > 0 ? \"message\" : \"conversation\"",
                success, StringComparison.Ordinal);
        }

        /// P43 — CREATING FROM A MESSAGE ALSO RECORDS THE MESSAGE. CreateFromMessageAsync writes a task
        /// and an audit row and never touches the link store, so without this the task appeared in
        /// neither the conversation's linked list nor its own related-messages list.
        [Fact]
        public void P43_a_task_created_from_a_message_is_linked_to_that_message()
        {
            var body = ActionBody(ControllerSource(), "CreateFromMessage");

            Assert.Contains("LinkExistingAsync", body, StringComparison.Ordinal);
            Assert.Contains("CommentId = commentId", body, StringComparison.Ordinal);

            // Only when one is not already there, and only when the deployment can store it.
            Assert.Contains("linkId is not > 0 && commentId > 0 && integration.LinkingAvailable",
                body, StringComparison.Ordinal);
        }

        /// P44 — THE THREAD IS READ OFF THE MESSAGE. This action is given no threadId; deriving one
        /// from the record would pick the wrong conversation whenever a record has more than one, and
        /// accepting one from the caller would be a client-chosen target.
        [Fact]
        public void P44_the_source_thread_is_resolved_from_the_message_not_the_caller()
        {
            Assert.DoesNotContain("threadId", typeof(TasksController).GetMethod("CreateFromMessage")!
                .GetParameters().Select(p => p.Name)!);

            var body = ActionBody(ControllerSource(), "CreateFromMessage");
            Assert.Contains("Comments.GetAsync(ctx, commentId, ct)", body, StringComparison.Ordinal);
            Assert.Contains("sourceThreadId = message.ThreadId", body, StringComparison.Ordinal);

            // An unreadable message yields no link, never a link to a guessed conversation.
            Assert.Contains("if (sourceThreadId <= 0)", body, StringComparison.Ordinal);
        }

        /// P45 — THE TASK SIDE EXISTS, and asks both authorities. Tasks/Read decides whether the
        /// caller may know the task's relationships; Communication decides, per row, whether they may
        /// read the conversation and the message.
        [Fact]
        public void P45_the_task_side_listing_gates_on_the_task_and_then_on_communication()
        {
            var method = typeof(TasksController).GetMethod("MessagesForTask");
            Assert.NotNull(method);
            Assert.Contains("taskId", method!.GetParameters().Select(p => p.Name)!);

            var body = ActionBody(ControllerSource(), "MessagesForTask");

            // The TASK-scoped target, not the company-wide one: whether this caller may read THIS task
            // is the question, and the company-level answer is a wider one.
            Assert.Contains("PermissionTarget.ForTask(taskId, scope.CompanyId)", body, StringComparison.Ordinal);
            Assert.Contains("TasksActions.Read", body, StringComparison.Ordinal);

            // Communication is asked per row, and tenancy is re-checked on the task itself before any
            // link row is followed.
            Assert.Contains("Threads.RequireAsync(ctx, link.ThreadId, ct)", body, StringComparison.Ordinal);
            Assert.Contains("t.CompanyId == scope.CompanyId", body, StringComparison.Ordinal);
        }

        /// P46 — CONTENT IS WITHHELD, THE ROW IS NOT. The relationship is the task's own data and the
        /// owner may need to unlink it; the message body is Communication's. A refused message leaves
        /// the row present with readable=false rather than vanishing, which is the difference between
        /// "you may not read this" and "there is nothing here".
        [Fact]
        public void P46_an_unreadable_message_hides_its_content_and_keeps_its_row()
        {
            var body = ActionBody(ControllerSource(), "MessagesForTask");

            Assert.Contains("readable", body, StringComparison.Ordinal);
            Assert.Contains("catch (CrossBuy.Models.Communication.CommAccessDeniedException) { }",
                body, StringComparison.Ordinal);

            // The row is added OUTSIDE the try, so a refusal cannot skip it.
            Assert.True(body.IndexOf("catch (CrossBuy.Models.Communication.CommAccessDeniedException) { }",
                            StringComparison.Ordinal)
                      < body.IndexOf("results.Add(new", StringComparison.Ordinal));

            // A deleted message has its body blanked by the service; echoing it would print an empty
            // quotation as though somebody had written one.
            Assert.Contains("!message.IsDeleted", body, StringComparison.Ordinal);
        }

        /// P47 — one row per link here too, each classified on its own, and the navigation target comes
        /// from the registry that owns the mapping rather than a path built in this file.
        [Fact]
        public void P47_the_task_side_rows_are_per_link_and_route_through_the_registry()
        {
            var body = ActionBody(ControllerSource(), "MessagesForTask");

            // Iterated, never grouped: two links to two messages in one thread are two rows.
            Assert.Contains("foreach (var link in links)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("GroupBy", body, StringComparison.Ordinal);
            Assert.Contains("linkId = link.Id", body, StringComparison.Ordinal);
            Assert.Contains("scope = link.CommentId is > 0 ? \"message\" : \"conversation\"",
                body, StringComparison.Ordinal);

            Assert.Contains("registry.BuildUrl(t.EntityType, t.EntityId)", body, StringComparison.Ordinal);
        }

        /// P48 — THE TWO PATHS RECORD DIFFERENT PROVENANCE, and the distinction is the whole point.
        /// A task raised from a message and a task attached afterwards are different events; stamping
        /// both LinkedExisting loses that permanently, because nothing else in the row says which
        /// happened. The service cannot infer it — a link written moments after a creation looks
        /// exactly like an attachment from inside it — so the caller declares it, and this holds both
        /// halves: the create path names CreatedFromMessage, and the attach path says nothing and
        /// takes the LinkedExisting default.
        [Fact]
        public void P48_only_the_create_path_claims_created_from_message()
        {
            var source = ControllerSource();
            var create = ActionBody(source, "CreateFromMessage");
            var attach = ActionBody(source, "LinkConversation");

            Assert.Contains("Origin = CrossBuy.BL.Communication.CommTaskLinkOrigin.CreatedFromMessage",
                create, StringComparison.Ordinal);

            // The attach path must not name an origin at all. Spelling LinkedExisting here would work
            // today and silently diverge the day the default changes.
            Assert.DoesNotContain("Origin =", attach, StringComparison.Ordinal);

            // ONE WRITE, NOT TWO. The origin was closed by declaring it on the request that already
            // existed; a second AddAsync or a second LinkExistingAsync for one user action would make
            // two rows out of one event.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(create, "LinkExistingAsync").Count);
        }

        // ---- the task side on screen -----------------------------------------------------------
        //
        // MessagesForTask had no consumer: the endpoint answered and nothing asked. These guards hold
        // the screen half — that it exists, that it draws one row per LINK, that it never invents a
        // destination, and that it stays inside the house rules for direction, dates and people.

        private static string TaskDetailSource()
        {
            var rel = Path.Combine("CrossBuy", "Views", "Tasks", "Detail.cshtml");
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, rel)))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.True(dir is not null, "Could not locate Views/Tasks/Detail.cshtml.");
            return File.ReadAllText(Path.Combine(dir!, rel));
        }

        /// P49 — THE TASK SIDE IS ON THE SCREEN. The endpoint existed and nothing called it, so a task
        /// attached to four messages showed none of them. This is the guard for that gap.
        [Fact]
        public void P49_task_detail_consumes_the_linked_messages_endpoint()
        {
            var view = TaskDetailSource();

            Assert.Contains("MessagesForTask", view, StringComparison.Ordinal);
            Assert.Contains("cbTaskMsgCard", view, StringComparison.Ordinal);

            // AND IT IS NOT A SECOND CONVERSATION. This screen already owns one in the Comments tab;
            // rendering the shared panel here too would put two composers on one page, and a reader
            // would have to guess which thread a colleague will see.
            //
            // THE INVOCATION, NOT THE NAME. The view NAMES the partial in the comment that explains
            // why it is deliberately absent, so a bare DoesNotContain fails on a correct explanation —
            // measured, it did. What must not appear is a render of it.
            foreach (var render in new[] { "Partial(\"_EntityConversation", "PartialAsync(\"_EntityConversation",
                                           "Html.RenderPartial(\"_EntityConversation" })
                Assert.DoesNotContain(render, view, StringComparison.Ordinal);
        }

        /// P50 — NEVER AN INVENTED DESTINATION. The server sends route=null when the entity has no
        /// detail screen or the thread is not the caller's to open. The row must then say so, not
        /// fall back to a module list — landing on a list looks like the link worked and lost the
        /// message.
        [Fact]
        public void P50_a_missing_route_renders_a_label_and_never_a_guess()
        {
            var view = TaskDetailSource();

            // THE BRANCH, NOT THE TOKENS. `m.route` and "No screen" both outlive
            // `var target = true` - a mutant that links every row whether or not the server
            // sent a destination, and it SURVIVED this test in its first form. The ternary
            // that chooses between an anchor and a label is what must be present.
            Assert.Contains("var target = m.route", view, StringComparison.Ordinal);
            Assert.Contains("No screen", view, StringComparison.Ordinal);

            // The href is only ever the server's own route value: no concatenation of a controller
            // name, and no /Index or /List fallback built on the page.
            Assert.DoesNotContain("'/Tasks/Index'", view, StringComparison.Ordinal);
            Assert.DoesNotContain("href=\"/Calendar", view, StringComparison.Ordinal);

            // And the server only emits a route it could build from the registry, with the id in it.
            var body = ActionBody(ControllerSource(), "MessagesForTask");
            Assert.Contains("registry.BuildUrl(t.EntityType, t.EntityId)", body, StringComparison.Ordinal);
            Assert.Contains("url.Contains(t.EntityId.ToString(), StringComparison.Ordinal)",
                body, StringComparison.Ordinal);

            // AND THE TEMPLATE MUST DECLARE A RECORD ROUTE. BuildUrl returns a template with no
            // "{id}" UNCHANGED — its own documented contract — so an entity registered against a
            // module index yields a valid-looking url that cannot show the message. Asking whether
            // the built url contains the id is only a heuristic: it holds today because no
            // list-only template in the registry contains a digit, and "/Tasks/Index2" would defeat
            // it. The definition is asked directly, and the substring test is kept behind it.
            Assert.Contains("def.RouteTemplate.Contains(\"{id}\", StringComparison.Ordinal)",
                body, StringComparison.Ordinal);
        }

        /// P51 — the deep link lands ON THE MESSAGE. #cbComment<id> is the anchor the conversation
        /// panel renders for every message; without the fragment the reader arrives at the top of a
        /// record and has to find the message themselves.
        [Fact]
        public void P51_the_deep_link_carries_the_message_anchor()
        {
            var body = ActionBody(ControllerSource(), "MessagesForTask");
            Assert.Contains("\"#cbComment\" + comment.Value", body, StringComparison.Ordinal);

            // THE OTHER HALF OF THE CONTRACT. An anchor nothing emits is a fragment that scrolls
            // nowhere, so the panel's own id is asserted here rather than assumed.
            var rel = Path.Combine("CrossBuy", "Views", "Shared", "_EntityConversation.cshtml");
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, rel)))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.True(dir is not null, "Could not locate _EntityConversation.cshtml.");
            Assert.Contains("id=\"cbComment' + c.id + '\"",
                File.ReadAllText(Path.Combine(dir!, rel)), StringComparison.Ordinal);
        }

        /// P52 — CONTENT IS WITHHELD, THE ROW IS NOT, and the screen says which it is. A relationship
        /// the reader may unlink but whose message they may not read is a real state; rendering it as
        /// a blank line would read as an empty message.
        [Fact]
        public void P52_an_unauthorized_source_is_suppressed_without_hiding_the_link()
        {
            var view = TaskDetailSource();

            // THE BRANCH, NOT THE TOKEN. `m.readable` is read more than once on this card, so
            // asserting the bare token passes against `var content = true` — a mutant that draws
            // every message as though it were readable. Measured: it survived.
            //
            // AND THE BRANCH IS NOW FOUR-WAY. `readable` answers one question — may this caller read
            // THIS MESSAGE — so a conversation-level link arrives false by construction. The earlier
            // two-arm form therefore showed a permission refusal to a reader who held the permission.
            // Both message arms are pinned here; P56 holds the conversation arms.
            Assert.Contains("if (isMessage && m.readable) {", view, StringComparison.Ordinal);
            Assert.Contains("} else if (isMessage) {", view, StringComparison.Ordinal);
            // ONE SENTENCE, TRUE OF ALL FOUR CAUSES. readable:false means the thread would not
            // resolve, the message was not found, the caller was refused it, or it was deleted, and
            // the row deliberately cannot tell them apart — saying which would reveal whether a
            // message somebody may not read still exists. So the sentence states the OUTCOME and
            // never guesses the cause: "you do not have permission" is simply false for a deleted
            // message, and sends its reader to ask for a grant that would not help.
            Assert.Contains("This message is not available to show here.", view, StringComparison.Ordinal);
            Assert.DoesNotContain("You do not have permission to view this message", view,
                StringComparison.Ordinal);

            // The author block is drawn only for a readable row, so a name cannot escape alongside a
            // withheld body.
            Assert.Contains("m.readable && m.authorName", view, StringComparison.Ordinal);
        }

        /// P56 — A CONVERSATION LINK IS NOT A REFUSAL. The content slot used to branch on `readable`
        /// alone, which is only ever set on the message path, so every conversation-level row told its
        /// reader "you do not have permission to view this message" — about a message that does not
        /// exist, to somebody who could open the thread perfectly well. The two facts are now asked as
        /// two questions.
        [Fact]
        public void P56_a_conversation_level_link_is_not_reported_as_a_refusal()
        {
            var view = TaskDetailSource();

            // The conversation arms exist and are worded as presence/absence, not as permission.
            Assert.Contains("} else if (m.threadReadable) {", view, StringComparison.Ordinal);
            Assert.Contains("The whole conversation is linked to this task.", view, StringComparison.Ordinal);
            Assert.Contains("This conversation is not available to show here.", view,
                StringComparison.Ordinal);

            // AND THE SERVER ANSWERS THE SECOND QUESTION. A screen cannot distinguish "no message"
            // from "not your message" unless it is told whether the THREAD resolved; `readable` keeps
            // its narrower meaning so no existing reader of this payload changes behaviour.
            var payload = ActionBody(ControllerSource(), "MessagesForTask");
            Assert.Contains("threadReadable = thread != null", payload, StringComparison.Ordinal);
            Assert.Contains("readable = !message.IsDeleted", payload, StringComparison.Ordinal);
        }

        /// P57 — THE INITIALS HELPER DECLARES ITS VARIABLE. `varch = …` — a var keyword glued to its
        /// identifier — is an assignment to an undeclared global, and the `ch` the next line reads is
        /// then nowhere. avatar() threw ReferenceError for every authored row, rows.map() carried it
        /// out of the .then(), and the card showed "could not be loaded" to anybody whose task had a
        /// linked message. Measured in node: sloppy mode "ch is not defined", strict "varch is not
        /// defined". Nothing in P49–P55 could see it, because every one of them reads tokens.
        ///
        /// THIS GUARD IS GREP-SHAPED and says so. It cannot run the card; it can only refuse the
        /// specific shape that broke it. Verified by restoring the defect and watching it fail.
        [Fact]
        public void P57_the_card_script_declares_every_variable_it_assigns()
        {
            var card = TaskDetailSource();
            card = card[card.IndexOf("cbTaskMessages", StringComparison.Ordinal)..];

            Assert.Contains("var ch = (name", card, StringComparison.Ordinal);

            // `var` welded to a name, then assigned: var/let/const followed immediately by letters.
            var glued = System.Text.RegularExpressions.Regex.Matches(
                card, @"(?<![A-Za-z0-9_$])(?:var|let|const)[A-Za-z_$][A-Za-z0-9_$]*\s*=");
            Assert.True(glued.Count == 0,
                "A declaration keyword is glued to its identifier, which assigns a global instead: "
                + string.Join(", ", glued.Select(m => m.Value)));
        }

        /// P58 — PROVENANCE REACHES THE SCREEN. Origin has been stored since the create-from-message
        /// path was closed and travelled in this payload from the day it was written, and no pixel
        /// showed it: a task RAISED from a message looked exactly like one attached to a message
        /// afterwards. Only the first answers "why does this task exist".
        [Fact]
        public void P58_a_task_created_from_a_message_says_so_on_the_task()
        {
            // THE CONSTANT, NOT A LITERAL COPY. The view compares against a string; if TAB-3 ever
            // renames the origin this asserts that the two still agree.
            Assert.Equal("CreatedFromMessage", CrossBuy.BL.Communication.CommTaskLinkOrigin.CreatedFromMessage);

            var view = TaskDetailSource();
            Assert.Contains("m.origin === 'CreatedFromMessage'", view, StringComparison.Ordinal);
            Assert.Contains("Created from this message", view, StringComparison.Ordinal);

            // Drawn, not merely computed — a badge built into a variable nothing concatenates is the
            // same as no badge.
            Assert.Contains("kind + origin", view, StringComparison.Ordinal);

            // And the server still sends the value it is compared against.
            Assert.Contains("origin = link.Origin",
                ActionBody(ControllerSource(), "MessagesForTask"), StringComparison.Ordinal);
        }

        /// P53 — ONE ROW PER LINK ON THE SCREEN TOO. Two messages in one conversation are two rows;
        /// nothing on the page groups by task or by thread, which is how the server's per-link answer
        /// would get collapsed again on its way to the reader.
        [Fact]
        public void P53_the_view_draws_every_link_it_is_given()
        {
            var view = TaskDetailSource();

            Assert.Contains("rows.map(row)", view, StringComparison.Ordinal);
            Assert.DoesNotContain("reduce(", view, StringComparison.Ordinal);

            // The count is derived from the rows actually drawn, in the same pass — a count computed
            // from anything else is the first thing to disagree with the list.
            Assert.Contains("rows.length", view, StringComparison.Ordinal);

            // Each row states which kind of relationship it is rather than leaving the reader to infer
            // it from the presence of a comment id.
            Assert.Contains("m.scope === 'message'", view, StringComparison.Ordinal);
        }

        /// P54 — THE HOUSE RULES, on a card that is new and therefore easy to get wrong: the row
        /// treatment, the assembled date, the person's photo, and no direction-specific margins.
        [Fact]
        public void P54_the_card_follows_the_direction_date_and_person_rules()
        {
            var view = TaskDetailSource();

            // THE ROW. A hairline between rows, never a border drawn around each one.
            Assert.Contains("cb-row d-flex align-items-center", view, StringComparison.Ordinal);

            // THE DATE. toLocaleDateString in Arabic embeds RTL marks that survive an isolate and
            // scramble the output; the date is assembled from parts instead.
            Assert.Contains("function dateText(iso)", view, StringComparison.Ordinal);
            var card = view[view.IndexOf("cbTaskMessages", StringComparison.Ordinal)..];

            // THE CALL, NOT THE WORD. The card's own comment names toLocaleDateString to record why it
            // is not used; asserting the bare word fails on that explanation. A call site is
            // `.toLocaleDateString(`, which prose does not write.
            Assert.DoesNotContain(".toLocaleDateString(", card, StringComparison.Ordinal);

            // NUMBERS ARE ISOLATED so a record id does not reorder inside an Arabic line.
            Assert.Contains("\\u2066#' + m.entityId", card, StringComparison.Ordinal);

            // THE PERSON, NOT A GLYPH — a photo when there is one, initials when there is not, and
            // never a broken frame.
            Assert.Contains("m.authorAvatar", card, StringComparison.Ordinal);
            Assert.Contains("data-fallback", card, StringComparison.Ordinal);
            // THE SOURCE, NOT THE FIELD NAME. `authorAvatar` appears as the declaration and as the
            // projected key, so asserting it passes against `authorAvatar = null` — a mutant that
            // ships the field permanently empty and leaves every row on initials. Measured: it
            // survived. What must be present is the read from the actor.
            var payload = ActionBody(ControllerSource(), "MessagesForTask");
            Assert.Contains("authorAvatar = message.Author?.AvatarUrl", payload, StringComparison.Ordinal);

            // LOGICAL PROPERTIES ONLY. ml-/mr-/pl-/pr- are physical and strand a margin on the wrong
            // side of the Arabic page; the card uses me-/ms-/gap instead.
            foreach (var physical in new[] { "\"ml-", "\"mr-", " ml-", " mr-", " pl-", " pr-" })
                Assert.DoesNotContain(physical, card, StringComparison.Ordinal);
        }

        /// P55 — the picker endpoint now carries the session gate its siblings already had, and the
        /// refusal says something a reader can act on without distinguishing absent from forbidden.
        [Fact]
        public void P55_linksearch_is_gated_and_refusals_stay_indistinguishable()
        {
            var source = ControllerSource();

            var head = source[..source.IndexOf(" LinkSearch(", StringComparison.Ordinal)];
            Assert.Contains("[SessionValidation]",
                head[head.LastIndexOf("//", StringComparison.Ordinal)..], StringComparison.Ordinal);

            // ONE SENTENCE FOR ALL FOUR NOT-FOUND CODES. The service merges "absent" with "you may not
            // see it" for each resource; four different sentences would hand that distinction back.
            var msg = ActionBody(source, "LinkFailureMessage");
            Assert.Contains("LinkNotFoundCodes.Contains(c)", msg, StringComparison.Ordinal);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(
                msg, @"LinkNotFoundCodes\.Contains").Count);

            // The machine contract is unchanged: the code still travels, and the sentence is additive.
            var fail = ActionBody(source, "LinkFailure");
            Assert.Contains("code = c, error = LinkFailureMessage(c)", fail, StringComparison.Ordinal);
        }

        /// P13 — the POST carries the application's anti-forgery convention and the GET does not need it.
        [Fact]
        public void P13_the_post_endpoint_validates_the_anti_forgery_token()
        {
            var source = ControllerSource();
            var at = source.IndexOf(" CreateFromMessage(", StringComparison.Ordinal);
            var head = source[Math.Max(0, at - 400)..at];

            Assert.Contains("[HttpPost]", head, StringComparison.Ordinal);
            Assert.Contains("ValidateAntiForgeryToken", head, StringComparison.Ordinal);
        }
    }
}
