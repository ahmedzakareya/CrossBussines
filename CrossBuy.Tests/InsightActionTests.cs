using Xunit;
using Actions = CrossBuy.BL.Platform.Ai.InsightActions;

namespace CrossBuy.Tests
{
    // INSIGHT → ACTION → TASK.
    //
    // THE TWO FAILURES THESE TESTS EXIST TO PREVENT.
    //
    // 1. AN ACTION OFFERED BECAUSE IT EXISTS, NOT BECAUSE IT HELPS. Dead stock must never offer
    //    "Replenish". The item is sitting there BECAUSE nobody wants it, so suggesting a purchase is
    //    actively harmful advice wearing the clothes of a recommendation — and it is exactly what
    //    happens when a screen renders every action it has for every row.
    //
    // 2. A SUGGESTION MISTAKEN FOR AUTHORITY. The review form is composed by the server, spends a
    //    moment in a browser, and comes back. Everything in it is then untrusted: company is never read
    //    from the request at all, the source row must exist in the resolved company, the finding must be
    //    one that source offers a task for, the assignee must be an active employee of that company, and
    //    BOTH permissions are checked. The tests below pin each of those.
    //
    // The write itself is one call to ITaskService.SaveAsync — the same call TasksController makes — so
    // the result is an ordinary task with an ordinary lifecycle. There is no "insight task" type.
    public class InsightActionTests
    {
        private static string Controller() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "InsightActionsController.cs"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
        }

        private static string View(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { RepoRoot(), "CrossBuy", "Views" }.Concat(parts).ToArray()));

        // =========================================================================================
        // Actions depend on the finding
        // =========================================================================================

        // THE HEADLINE TEST. Dead stock offers a review, never a purchase.
        [Fact]
        public void Dead_stock_never_offers_replenishment()
        {
            var acts = Actions.For(Actions.Sources.InventoryItem, "slow");

            Assert.DoesNotContain(Actions.ActionKind.OpenReplenishment, acts);
            Assert.Contains(Actions.ActionKind.CreateFollowUpTask, acts);
            Assert.Contains(Actions.ActionKind.OpenItem, acts);
        }

        [Theory]
        [InlineData("stockout")]
        [InlineData("reorder")]
        public void A_real_shortage_offers_the_procurement_path(string finding)
        {
            var acts = Actions.For(Actions.Sources.InventoryItem, finding);

            Assert.Contains(Actions.ActionKind.OpenReplenishment, acts);
            Assert.Contains(Actions.ActionKind.CreateFollowUpTask, acts);
        }

        [Fact]
        public void The_shortage_task_is_more_urgent_than_the_dead_stock_review()
        {
            var stockout = Actions.Propose(Actions.Sources.InventoryItem, "stockout")!;
            var slow = Actions.Propose(Actions.Sources.InventoryItem, "slow")!;

            // An item out of stock with live demand is losing sales today; slow stock has been sitting
            // there for months. One due date for both would make the due date meaningless.
            Assert.Equal("Urgent", stockout.Priority);
            Assert.Equal("Normal", slow.Priority);
            Assert.True(stockout.DueInDays < slow.DueInDays);
        }

        [Fact]
        public void Crm_findings_offer_a_follow_up_and_their_own_navigation()
        {
            var opp = Actions.For(Actions.Sources.CrmOpportunity, "PastExpectedClose");
            Assert.Contains(Actions.ActionKind.CreateFollowUpTask, opp);
            Assert.Contains(Actions.ActionKind.OpenOpportunity, opp);
            Assert.Contains(Actions.ActionKind.OpenAccount, opp);
            Assert.DoesNotContain(Actions.ActionKind.OpenReplenishment, opp);

            var acct = Actions.For(Actions.Sources.CrmAccount, "DecliningActivity");
            Assert.Contains(Actions.ActionKind.CreateFollowUpTask, acct);
            Assert.Contains(Actions.ActionKind.OpenOpportunities, acct);
            Assert.DoesNotContain(Actions.ActionKind.OpenReplenishment, acct);
        }

        [Fact]
        public void An_unknown_source_or_finding_offers_nothing()
        {
            Assert.Empty(Actions.For("NotASource", "stockout"));
            Assert.False(Actions.AllowsTask("NotASource", "stockout"));
            Assert.Null(Actions.Propose("NotASource", "stockout"));

            // An inventory finding with no configured action offers navigation but no task.
            Assert.False(Actions.AllowsTask(Actions.Sources.InventoryItem, "somethingelse"));
            Assert.Null(Actions.Propose(Actions.Sources.InventoryItem, "somethingelse"));
        }

        // =========================================================================================
        // Duplicate identity
        // =========================================================================================

        // The identity includes the FINDING. An account that goes quiet again next quarter genuinely
        // needs a second task, and "account 42 forever" would block that permanently.
        [Fact]
        public void The_dedup_identity_is_per_finding_not_per_record()
        {
            var declining = Actions.DedupKey(Actions.Sources.CrmAccount, 42, "DecliningActivity");
            var overdue = Actions.DedupKey(Actions.Sources.CrmAccount, 42, "OverdueOpportunityExposure");
            var otherAccount = Actions.DedupKey(Actions.Sources.CrmAccount, 43, "DecliningActivity");

            Assert.NotEqual(declining, overdue);
            Assert.NotEqual(declining, otherAccount);
            Assert.Equal(declining, Actions.DedupKey(Actions.Sources.CrmAccount, 42, "DecliningActivity"));
        }

        // ...and it is bounded by the OPEN task, so a recurrence can raise a new one once the first is
        // finished. A permanent block is the failure nobody notices until a customer is lost.
        [Fact]
        public void The_duplicate_check_only_looks_at_open_tasks()
        {
            var code = StripComments(Controller());
            Assert.Contains("t.Status != \"Done\"", code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_origin_line_is_greppable_and_identifies_the_source()
        {
            var line = Actions.OriginLine(Actions.Sources.CrmOpportunity, 7, "PastExpectedClose");

            Assert.StartsWith(Actions.OriginStamp, line, StringComparison.Ordinal);
            Assert.Contains("CrmOpportunity#7", line, StringComparison.Ordinal);
            Assert.Contains("PastExpectedClose", line, StringComparison.Ordinal);
        }

        // =========================================================================================
        // Traceability — no invented registry codes
        // =========================================================================================

        // Item IS in the frozen EntityRegistry vocabulary and TaskLinkResolver routes it, so an
        // inventory task gets real back-navigation. CRM has no registry code, and writing an invented
        // one would store a link that resolves to nothing — which looks like a link and is not one.
        [Fact]
        public void Only_registry_backed_sources_get_a_structural_entity_link()
        {
            Assert.Equal("Item", Actions.RegistryEntityCode(Actions.Sources.InventoryItem));
            Assert.Null(Actions.RegistryEntityCode(Actions.Sources.CrmOpportunity));
            Assert.Null(Actions.RegistryEntityCode(Actions.Sources.CrmAccount));
        }

        [Fact]
        public void The_entity_link_written_to_the_task_matches_the_registry_decision()
        {
            var code = StripComments(Controller());

            Assert.Contains("EntityType = InsightActions.RegistryEntityCode(form.Source)", code, StringComparison.Ordinal);
            Assert.Contains("RegistryEntityCode(form.Source) == null ? null : form.EntityId", code, StringComparison.Ordinal);
        }

        // The origin stamp is written by the server, never taken from the posted form: an audit trail a
        // browser can edit is an audit trail of whatever it likes.
        [Fact]
        public void The_origin_line_is_server_composed_and_not_taken_from_the_form()
        {
            var code = StripComments(Controller());

            Assert.Contains("var origin = InsightActions.OriginLine(form.Source, form.EntityId, form.FindingCode);", code, StringComparison.Ordinal);
            Assert.DoesNotContain("form.Origin", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // The write uses the real task service
        // =========================================================================================

        [Fact]
        public void The_task_is_created_through_the_authoritative_task_service()
        {
            var code = StripComments(Controller());

            Assert.Contains("_tasks.SaveAsync(gate.CompanyId, input, creator)", code, StringComparison.Ordinal);
            Assert.Contains("new TaskSaveInput", code, StringComparison.Ordinal);
        }

        // No direct insert. A second writer would bypass the events, the notifications and the
        // validation that make a task behave like a task.
        [Fact]
        public void The_controller_never_inserts_a_task_row_directly()
        {
            var code = StripComments(Controller());

            Assert.DoesNotContain("_db.TaskItems.Add", code, StringComparison.Ordinal);
            Assert.DoesNotContain("TaskItems.AddAsync", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveChangesAsync", code, StringComparison.Ordinal);

            // The only TaskItems touch is the read-only duplicate lookup.
            Assert.Contains("_db.TaskItems.AsNoTracking()", code, StringComparison.Ordinal);
        }

        // No parallel engine of any kind was introduced.
        [Fact]
        public void No_second_notification_event_or_task_engine_was_created()
        {
            var code = StripComments(Controller());
            var rules = StripComments(File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "InsightActions.cs")));

            foreach (var forbidden in new[]
                     {
                         "INotificationService", "NotifyAsync", "IBusinessEventService", "RecordAsync",
                         "InsightNotification", "AiInsightTaskCreated", "InsightEventBus",
                     })
            {
                Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, rules, StringComparison.Ordinal);
            }
        }

        // The rules layer stays pure — it proposes, it does not reach anything.
        [Fact]
        public void The_action_rules_hold_no_data_source()
        {
            var t = typeof(Actions);
            Assert.True(t.IsAbstract && t.IsSealed);

            // CODE ONLY. The file's header comment names ITaskService to explain that the WRITE happens
            // there and not here, and a scan that cannot tell prose from code would forbid saying so.
            var rules = StripComments(File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "InsightActions.cs")));
            foreach (var forbidden in new[] { "DbContext", "HttpClient", "ITaskService", "SaveAsync", "_db" })
                Assert.DoesNotContain(forbidden, rules, StringComparison.Ordinal);
        }

        // =========================================================================================
        // Company isolation and permission
        // =========================================================================================

        [Fact]
        public void The_company_is_resolved_and_never_accepted_from_the_caller()
        {
            var code = StripComments(Controller());
            var vm = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "ViewModel", "Ai", "AiInsightsModels.cs"));

            Assert.Contains("_company.ResolveAsync()", code, StringComparison.Ordinal);
            Assert.Contains("if (!scope.Ok)", code, StringComparison.Ordinal);
            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CompanyID == 1", code, StringComparison.Ordinal);

            // The form model has a CompanyId for DISPLAY, but the controller never reads it back.
            Assert.Contains("form.EntityId", code, StringComparison.Ordinal);
            Assert.DoesNotContain("form.CompanyId", code, StringComparison.Ordinal);
            Assert.Contains("InsightTaskFormVm", vm, StringComparison.Ordinal);
        }

        // A cross-company source id must be answered exactly like a missing one. The difference between
        // "not found" and "exists elsewhere" is precisely what a probe is looking for.
        [Fact]
        public void The_source_row_must_exist_in_the_resolved_company()
        {
            var code = StripComments(Controller());

            Assert.Contains("i.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // Items
            Assert.Contains("o.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // Opportunities
            Assert.Contains("a.CompanyID == scope.CompanyId", code, StringComparison.Ordinal);   // CrmAccounts

            // one refusal message for both cases
            Assert.Contains("The record was not found", code, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_permission_sides_are_enforced_server_side()
        {
            var code = StripComments(Controller());

            // side one: the source module
            Assert.Contains("_invAccess.CanAsync(\"read\")", code, StringComparison.Ordinal);
            Assert.Contains("_crmAccess.CanAsync(\"read\")", code, StringComparison.Ordinal);

            // side two: task creation
            Assert.Contains("_tasksAccess.CanAsync(context, \"create\")", code, StringComparison.Ordinal);

            // and an unresolved business context refuses rather than proceeding
            Assert.Contains("if (context == null)", code, StringComparison.Ordinal);
        }

        // The POST re-runs the whole gate. The GET having passed proves nothing about the POST.
        [Fact]
        public void The_post_revalidates_everything_rather_than_trusting_the_form()
        {
            var code = StripComments(Controller());
            var post = code[code.IndexOf("public async Task<IActionResult> CreateTask", StringComparison.Ordinal)..];

            Assert.Contains("AuthoriseAsync(form.Source, form.EntityId, form.FindingCode)", post, StringComparison.Ordinal);
            Assert.Contains("ValidateAntiForgeryToken", Controller(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_assignee_must_be_an_active_employee_of_the_resolved_company()
        {
            var code = StripComments(Controller());

            Assert.Contains("_tasks.ActiveEmployeesAsync(gate.CompanyId)", code, StringComparison.Ordinal);
            Assert.Contains("!assignees.Any(a => a.Id == form.AssigneeEmployeeId)", code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_priority_is_constrained_to_the_task_services_own_vocabulary()
        {
            var code = StripComments(Controller());
            Assert.Contains("Priorities.Contains(form.Priority", code, StringComparison.Ordinal);
            Assert.Contains("\"Low\", \"Normal\", \"High\", \"Urgent\"", code, StringComparison.Ordinal);
        }

        // A finding that offers no task cannot be talked into producing one by posting straight here.
        [Fact]
        public void A_finding_with_no_task_action_is_refused_at_the_endpoint()
        {
            var code = StripComments(Controller());
            Assert.Contains("InsightActions.AllowsTask(source, finding)", code, StringComparison.Ordinal);
        }

        [Fact]
        public void A_return_url_is_only_echoed_when_it_is_local()
        {
            var code = StripComments(Controller());
            Assert.Contains("Url.IsLocalUrl(candidate)", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // UX and honesty
        // =========================================================================================

        [Fact]
        public void Success_is_reported_only_after_persistence_returns_an_id()
        {
            var code = StripComments(Controller());
            var js = View("Shared", "_InsightActionScripts.cshtml");

            // the server answers ok only on the path after SaveAsync succeeded
            Assert.Contains("if (!ok) return Refuse", code, StringComparison.Ordinal);
            Assert.Contains("taskId = id", code, StringComparison.Ordinal);

            // the client shows the success box only from the server's response
            Assert.Contains("res.taskId", js, StringComparison.Ordinal);
            Assert.DoesNotContain("optimistic", js, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_review_step_exists_and_nothing_is_created_by_opening_it()
        {
            var code = StripComments(Controller());
            var form = View("InsightActions", "_NewTask.cshtml");

            // GET composes a form; only the POST writes.
            Assert.Contains("PartialView(\"_NewTask\", vm)", code, StringComparison.Ordinal);
            Assert.Contains("[HttpGet]", code, StringComparison.Ordinal);

            foreach (var field in new[] { "Title", "Reason", "AssigneeEmployeeId", "DueDate", "Priority", "SourceLabel" })
                Assert.Contains(field, form, StringComparison.Ordinal);
        }

        // Compact by design: one primary action plus a menu, not a row of buttons.
        [Theory]
        [InlineData("Inventory", "RiskInsights.cshtml")]
        [InlineData("Crm", "OpportunityInsights.cshtml")]
        [InlineData("Crm", "AccountInsights.cshtml")]
        public void Each_screen_offers_a_compact_action_menu_wired_to_the_shared_flow(string folder, string file)
        {
            var view = View(folder, file);

            Assert.Contains("data-insight-action=\"task\"", view, StringComparison.Ordinal);
            Assert.Contains("dropdown-menu", view, StringComparison.Ordinal);
            Assert.Contains("_InsightActionScripts.cshtml", view, StringComparison.Ordinal);
            Assert.Contains("More actions", view, StringComparison.Ordinal);
        }

        // The inventory screen asks the rules which actions apply rather than hard-coding them, which is
        // what keeps "no replenishment for dead stock" true in the UI and not only in the engine.
        [Fact]
        public void The_inventory_screen_derives_its_actions_from_the_rules()
        {
            var view = View("Inventory", "RiskInsights.cshtml");

            Assert.Contains("InsightActions.For(", view, StringComparison.Ordinal);
            Assert.Contains("ActionKind.OpenReplenishment", view, StringComparison.Ordinal);
            Assert.Contains("canReplenish", view, StringComparison.Ordinal);
        }

        // §13: these are rules, not a model. The action wording must not claim otherwise.
        [Fact]
        public void No_action_wording_claims_an_ai_decision()
        {
            var sources = new[]
            {
                View("InsightActions", "_NewTask.cshtml"),
                View("Crm", "OpportunityInsights.cshtml"),
                View("Crm", "AccountInsights.cshtml"),
                Controller(),
            };

            foreach (var src in sources)
                foreach (var claim in new[]
                         {
                             "AI automatically", "AI prediction", "AI recommended", "AI probability",
                             "AI decided", "AI suggests",
                         })
                {
                    Assert.DoesNotContain(claim, src, StringComparison.OrdinalIgnoreCase);
                }
        }

        [Fact]
        public void The_action_path_reaches_no_external_provider()
        {
            foreach (var src in new[]
                     {
                         Controller(),
                         File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "InsightActions.cs")),
                         View("InsightActions", "_NewTask.cshtml"),
                     })
            {
                Assert.DoesNotContain("api.openai.com", src, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAi", src, StringComparison.Ordinal);
                Assert.DoesNotContain("IAiExternalProvider", src, StringComparison.Ordinal);
            }
        }

        // =========================================================================================
        // Localisation and layout
        // =========================================================================================

        [Theory]
        [InlineData("en")]
        [InlineData("ar")]
        [InlineData("fr")]
        public void The_action_modal_is_localised(string lang)
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "InsightActions", $"_NewTask.{lang}.resx");
            Assert.True(File.Exists(path), $"missing {lang} resources");

            var doc = System.Xml.Linq.XDocument.Load(path);
            var data = doc.Root!.Elements("data").ToList();
            Assert.NotEmpty(data);
            Assert.All(data, d => Assert.False(string.IsNullOrWhiteSpace(d.Element("value")?.Value)));
        }

        [Fact]
        public void The_arabic_action_resources_are_actually_arabic()
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "InsightActions");
            var ar = System.Xml.Linq.XDocument.Load(Path.Combine(dir, "_NewTask.ar.resx")).Root!
                .Elements("data").Select(d => d.Element("value")!.Value).ToList();

            var arabic = ar.Count(v => v.Any(c => c >= '؀' && c <= 'ۿ'));
            Assert.True(arabic >= ar.Count * 0.8, $"only {arabic} of {ar.Count} values contain Arabic script");
        }

        [Fact]
        public void The_modal_is_responsive_and_direction_neutral()
        {
            var form = View("InsightActions", "_NewTask.cshtml");
            var shell = View("Shared", "_InsightActionScripts.cshtml");

            Assert.Contains("col-12 col-md-4", form, StringComparison.Ordinal);
            Assert.Contains("modal-dialog-centered", shell, StringComparison.Ordinal);

            // logical spacing only — ml-*/mr-* would strand Arabic
            foreach (var physical in new[] { "ml-1", "ml-2", "ml-3", "mr-1", "mr-2", "mr-3", "text-left", "text-right" })
                Assert.DoesNotContain($"\"{physical}", form, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_localiser_key_the_modal_uses_is_defined()
        {
            var keys = System.Text.RegularExpressions.Regex
                .Matches(View("InsightActions", "_NewTask.cshtml"), "Localizer\\[\"([^\"]+)\"\\]")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            var defined = System.Xml.Linq.XDocument.Load(Path.Combine(
                    RepoRoot(), "CrossBuy", "Resources", "Views", "InsightActions", "_NewTask.en.resx"))
                .Root!.Elements("data").Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);

            var missing = keys.Except(defined).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, "undefined keys: " + string.Join(" | ", missing));
        }

        // =========================================================================================
        // The created task is an ORDINARY task
        // =========================================================================================

        // No special lifecycle: no status, no flag, no side table. Whatever the task pipeline does for
        // every other task — board, overdue, notifications, events, escalation — it does for these.
        [Fact]
        public void The_created_task_has_no_special_lifecycle()
        {
            var code = StripComments(Controller());

            foreach (var special in new[] { "IsInsightTask", "InsightStatus", "AiTask", "InsightTaskStatus", "Resolved = " })
                Assert.DoesNotContain(special, code, StringComparison.Ordinal);

            // it is built from the same input type TasksController posts
            Assert.Contains("TaskSaveInput", code, StringComparison.Ordinal);
        }

        // §14: a task created from an insight must arrive through the NORMAL pipeline, not beside it.
        // Proven at the source of that pipeline: creation raises the ordinary task events and
        // notifications, and every read path filters on company/scope/status/priority only — there is
        // no origin column to filter on, so these tasks cannot be separated out even by accident.
        [Fact]
        public void A_task_created_from_an_insight_travels_the_normal_pipeline()
        {
            var svc = StripComments(File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "TaskService.cs")));

            // creation raises the ordinary events and notifications
            Assert.Contains("_events.TaskCreatedAsync", svc, StringComparison.Ordinal);
            Assert.Contains("_events.TaskAssignedAsync", svc, StringComparison.Ordinal);
            Assert.Contains("_notify.TaskAssignedAsync", svc, StringComparison.Ordinal);

            // the list/board/overdue reads know nothing about where a task came from
            foreach (var originFilter in new[] { "[insight]", "InsightSource", "IsInsight", "OriginModule" })
                Assert.DoesNotContain(originFilter, svc, StringComparison.Ordinal);

            // overdue is DERIVED from the due date, so an insight task ages exactly like any other
            Assert.Contains("t.DueDate < now", svc, StringComparison.Ordinal);

            // and the escalation service exists and is likewise origin-blind
            var esc = StripComments(File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "TasksCalendar", "TaskEscalationService.cs")));
            foreach (var originFilter in new[] { "[insight]", "InsightSource", "IsInsight" })
                Assert.DoesNotContain(originFilter, esc, StringComparison.Ordinal);
        }

        // The insight screens must not persist their own copy of task state.
        [Fact]
        public void No_insight_screen_stores_task_status_of_its_own()
        {
            foreach (var view in new[]
                     {
                         View("Inventory", "RiskInsights.cshtml"),
                         View("Crm", "OpportunityInsights.cshtml"),
                         View("Crm", "AccountInsights.cshtml"),
                     })
            {
                Assert.DoesNotContain("Resolved", view, StringComparison.Ordinal);
                Assert.DoesNotContain("TaskStatus", view, StringComparison.Ordinal);
            }
        }
    }
}
