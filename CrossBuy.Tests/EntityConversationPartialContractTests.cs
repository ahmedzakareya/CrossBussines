using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // _EntityConversation — the shared conversation panel's CALLER CONTRACT.
    //
    // The partial used to resolve its own endpoints:
    //
    //     var convListUrl = Url.Action("InvoiceConversation", "Accounting");
    //     var convAddUrl  = Url.Action("InvoiceConversationAdd", "Accounting");
    //
    // while its own header claimed it "knows nothing about invoices". Any second module that
    // included it would have posted its messages at the Accounting controller, to be judged by a
    // permission gate written for invoices - a silent wrong-module write, not a visible error.
    //
    // The endpoints are now the including view's to name. These tests hold that line: the partial
    // must stay module-neutral, and every caller must name its own two endpoints. Nothing here
    // asserts that a string exists somewhere in the file - the checks read the EXECUTABLE Razor with
    // comments stripped, because the header legitimately shows Accounting's URLs as the worked
    // example and a naive text search would be satisfied by that comment forever.
    // ============================================================================================
    public class EntityConversationPartialContractTests
    {
        private const string Partial = "CrossBuy/Views/Shared/_EntityConversation.cshtml";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        // U+2066 LRI and U+2069 PDI are written in this file BOTH ways - as the literal characters
        // and as a \u escape - and an edit has already flipped one of them from the escape to the
        // character. Either spelling isolates the value at runtime, so an assertion that pins one of
        // them is pinning an accident. Normalising to the escape form asserts what actually matters:
        // that the value is wrapped at all.
        private static string Isolates(string source) =>
            source.Replace("\u2066", "\\u2066").Replace("\u2069", "\\u2069");

        // Razor carries two comment forms and the partial uses both: @* *@ around markup, and // inside
        // the @{ } block. Strip both, so what remains is only what actually runs.
        private static string Executable(string source)
        {
            var withoutRazor = Regex.Replace(source, @"@\*.*?\*@", "", RegexOptions.Singleline);
            return string.Join("\n", withoutRazor.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        // 1. The partial carries no Accounting dependency.
        [Fact]
        public void The_shared_partial_names_no_module_and_no_controller()
        {
            var code = Executable(Read(Partial));

            // The exact pair that was there. Both fail on the pre-change file.
            Assert.DoesNotContain("InvoiceConversation", code, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Accounting\"", code, StringComparison.Ordinal);

            // And no route resolution of ANY kind, so the next module cannot be hardcoded either -
            // including a default that would quietly restore the old behaviour.
            Assert.DoesNotMatch(new Regex(@"Url\.Action\s*\("), code);
            Assert.DoesNotContain("Inventory", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Quotation", code, StringComparison.Ordinal);

            // It takes them from the caller instead.
            Assert.Contains("ViewBag.ConversationListUrl", code, StringComparison.Ordinal);
            Assert.Contains("ViewBag.ConversationAddUrl", code, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unwired_caller_gets_a_visible_failure_rather_than_an_empty_conversation()
        {
            var code = Executable(Read(Partial));

            // Server side: the not-configured branch exists and is distinct from loading/empty/503.
            Assert.Contains("convWired", code, StringComparison.Ordinal);
            Assert.Contains("Conversations are not configured for this screen", code, StringComparison.Ordinal);

            // Client side: with no endpoints the script must return before fetching. Without this guard a
            // null list url resolves against the CURRENT page, and the panel reports a load failure -
            // which reads as "Communication is broken" rather than "this view forgot two lines".
            var script = code[code.IndexOf("<script>", StringComparison.Ordinal)..];
            int guard = script.IndexOf("data-list') || !box.getAttribute('data-add')", StringComparison.Ordinal);
            int fetch = script.IndexOf("fetch(", StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard < fetch,
                "the unwired guard must come before the first fetch");
        }

        // 2. Every caller names its own endpoints - checked by sweep, so a new caller cannot be added
        //    without them and quietly render the disabled panel.
        public static TheoryData<string> Callers()
        {
            var root = RepoRoot();
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "CrossBuy", "Views"), "*.cshtml",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "_EntityConversation.cshtml") continue;
                if (File.ReadAllText(file).Contains("PartialAsync(\"_EntityConversation\")", StringComparison.Ordinal))
                    data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(Callers))]
        public void Every_including_view_supplies_all_four_values(string caller)
        {
            var code = Executable(Read(caller));

            foreach (var required in new[]
                     {
                         "ViewBag.ConversationEntityCode",
                         "ViewBag.ConversationEntityId",
                         "ViewBag.ConversationListUrl",
                         "ViewBag.ConversationAddUrl",
                     })
                Assert.Contains(required, code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_sweep_actually_found_the_accounting_callers()
        {
            // Without this the theory above passes vacuously the day someone renames the partial.
            var found = Callers().Cast<object[]>().Select(row => (string)row[0]).ToList();

            Assert.Contains("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml", found);
            Assert.Contains("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml", found);
        }

        // 3. Accounting's behaviour is preserved: the SAME two actions it used before, still reachable.
        [Theory]
        [InlineData("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml", "SalesInvoice")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml", "PurchaseInvoice")]
        public void Accounting_still_points_at_the_endpoints_it_always_used(string view, string entityCode)
        {
            var code = Executable(Read(view));

            Assert.Contains($"EntityRegistry.{entityCode}", code, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"ConversationListUrl\s*=\s*Url\.Action\(""InvoiceConversation"",\s*""Accounting""\)"), code);
            Assert.Matches(new Regex(@"ConversationAddUrl\s*=\s*Url\.Action\(""InvoiceConversationAdd"",\s*""Accounting""\)"), code);
        }

        [Theory]
        [InlineData("InvoiceConversation")]
        [InlineData("InvoiceConversationAdd")]
        public void The_accounting_actions_those_urls_name_exist(string action)
        {
            // Url.Action of a missing action renders an empty string, and the panel would then show the
            // not-configured notice on a screen that used to work. The names must resolve to real methods.
            var controller = Read("CrossBuy/Controllers/AccountingController.cs");
            Assert.Matches(new Regex($@"IActionResult>\s+{action}\s*\("), controller);
        }

        [Fact]
        public void The_accounting_write_endpoint_keeps_its_protection()
        {
            // Parameterizing a URL must not become a way to reach an unguarded write. This asserts the
            // guard on the POST is still there, on the committed controller, after the change.
            var controller = Read("CrossBuy/Controllers/AccountingController.cs");
            var idx = controller.IndexOf("InvoiceConversationAdd(", StringComparison.Ordinal);
            Assert.True(idx > 0);

            var preamble = controller[Math.Max(0, idx - 600)..idx];
            Assert.Contains("[HttpPost]", preamble, StringComparison.Ordinal);
            Assert.Contains("[ValidateAntiForgeryToken]", preamble, StringComparison.Ordinal);
            Assert.Contains("[SessionValidation]", preamble, StringComparison.Ordinal);
        }

        // ========================================================================================
        // COMMUNICATION PHASE 3 - the task integration's own caller contract.
        //
        // Every check below reads the EXECUTABLE razor, for the reason the header already gives:
        // this panel documents itself heavily, and a naive text search would be satisfied by a
        // comment forever. The point of these is that the integration stays OPT-IN and that the
        // two relationships never collapse into one.
        // ========================================================================================

        // 8. Opt-in, exactly like the reaction and mention urls: absent host values mean an absent
        //    feature, not a feature pointed at nothing.
        [Fact]
        public void The_task_integration_is_opt_in_on_both_urls()
        {
            var razor = Executable(Read(Partial));

            Assert.Contains("ViewBag.ConversationCreateTaskUrl as string) ?? \"\"", razor, StringComparison.Ordinal);
            Assert.Contains("ViewBag.ConversationTasksUrl as string) ?? \"\"", razor, StringComparison.Ordinal);

            // The create action is gated on the flag, and the flag cannot be true without the url.
            Assert.Contains("state.canCreateTask = !!createTaskUrl", razor, StringComparison.Ordinal);
            Assert.Contains("if (state.canCreateTask &&", razor, StringComparison.Ordinal);

            // The related panel never fetches when the host named no endpoint.
            Assert.Matches(new Regex(@"function loadTasks\(\)\s*\{\s*if \(!tasksUrl\) \{ return; \}", RegexOptions.Singleline), razor);
        }

        // 9. The message is what the task comes FROM, so its identifier has to reach the endpoint.
        //    A dialog that posted only the title would silently make an unanchored task.
        [Fact]
        public void The_create_post_carries_the_message_identifier_and_a_token()
        {
            var razor = Executable(Read(Partial));

            Assert.Contains("fd.append('commentId', taskFor.id)", razor, StringComparison.Ordinal);
            Assert.Contains("fd.append('__RequestVerificationToken', token())", razor, StringComparison.Ordinal);

            // One message yields one task however many times the button is pressed: the dedup key
            // is the message, which is what CommTaskCreateRequest.DedupKey is for.
            Assert.Contains("fd.append('dedupKey', 'commtask:'", razor, StringComparison.Ordinal);
            Assert.Contains("taskFor.id", razor, StringComparison.Ordinal);
        }

        // 10. THE LOAD-BEARING ONE. CommTaskRelationship keeps SharedBusinessContext and ExplicitLink
        //     apart because one is inferred and the other was created by a person. Labelling a shared
        //     context as a link claims somebody linked a task when nobody did.
        [Fact]
        public void A_shared_business_context_is_never_labelled_an_explicit_link()
        {
            var razor = Executable(Read(Partial));

            // BOUND AS ONE UNIT, because asserting the condition and the label separately lets the
            // condition be inverted while both strings stay present - a mutant that survived this
            // test until it was written this way, and that labels every shared context "Linked".
            // THREE RELATIONSHIPS, THREE BADGES, BOUND AS ONE UNIT - because asserting the branches
            // and the labels separately lets the conditions be swapped while every string stays
            // present, which is the mutant that labels a shared context "Linked".
            Assert.Matches(new Regex(
                @"sc === 'message'\s*\?\s*'<span[^']*badge-light-info[^']*'\s*\+\s*esc\(T\.badgeFromMessage\)"
                + @"[\s\S]{0,200}?sc === 'conversation'"
                + @"[\s\S]{0,200}?badge-light-primary[^']*'\s*\+\s*esc\(T\.badgeLinked\)"
                + @"[\s\S]{0,200}?:\s*'<span[^']*'\s*\+\s*esc\(T\.badgeShared\)", RegexOptions.Singleline), razor);

            // And the three groups are rendered from mutually exclusive, exhaustive filters over the
            // SAME classifier, so a row cannot appear in two of them and cannot vanish from all three.
            foreach (var scope in new[] { "conversation", "message", "inferred" })
                Assert.Contains("return scopeOf(t) === '" + scope + "'; });", razor, StringComparison.Ordinal);
        }

        // 11. LinkingAvailable is false until CommTaskLinks is provisioned, and a button certain to be
        //     refused is worse than an absent one. Absent must read as false, not as permission.
        [Fact]
        public void Link_existing_is_offered_only_when_the_service_says_so()
        {
            var razor = Executable(Read(Partial));

            // THE RULE IS UNCHANGED; THE EXPRESSION MOVED. This used to pin the literal
            // `d.linkingAvailable !== true` inline in loadTasks. The decision now lives in
            // syncLinkButton, which asks MORE than the service answer - it also wants a thread to
            // attach to - so the invariant is pinned where it is now decided. What has not changed,
            // and what this still enforces, is that only an explicit true may reveal the button:
            // `=== true` keeps absent meaning false, so an older endpoint cannot light it up.
            Assert.Contains("state.linkingAvailable = d.linkingAvailable === true;", razor, StringComparison.Ordinal);
            Assert.Contains("state.linkingAvailable === true", razor, StringComparison.Ordinal);
            Assert.DoesNotContain("linkingAvailable !== false", razor, StringComparison.Ordinal);

            // It ships hidden; only the endpoint's explicit true may reveal it.
            Assert.Contains("id=\"cbConvLinkTask\"", razor, StringComparison.Ordinal);
            var button = razor[..razor.IndexOf("id=\"cbConvLinkTask\"", StringComparison.Ordinal)];
            var open = button.LastIndexOf("<button", StringComparison.Ordinal);
            Assert.Contains("d-none", button[open..], StringComparison.Ordinal);
        }

        // 12. A panel that could not load says so. Silence here would read as "no tasks", which is a
        //     different and far more comfortable claim than "this did not load".
        [Fact]
        public void A_failed_task_load_is_reported_rather_than_shown_as_empty()
        {
            var razor = Executable(Read(Partial));

            Assert.Contains("T.tasksFailed", razor, StringComparison.Ordinal);
            Assert.Contains("T.noTasks", razor, StringComparison.Ordinal);

            // Two DIFFERENT strings: one empty state and one failure. A single string serving both
            // is the defect this panel already fixed once for send-versus-load.
            Assert.NotEqual("T.tasksFailed", "T.noTasks");
            Assert.Contains("d.ok === false) { throw new Error('refused'); }", razor, StringComparison.Ordinal);
        }

        // 13. Nothing user-visible is written into the markup in either language: the panel is
        //     Arabic-first and English falls through to the key, so a missing Arabic row is a
        //     visible regression rather than a silent one.
        [Fact]
        public void Every_new_task_string_has_an_arabic_resource()
        {
            var resx = Read("CrossBuy/Resources/Views/Shared/_EntityConversation.ar.resx");
            string[] keys =
            {
                "Create a task", "Link an existing task", "Related tasks", "Task from this message",
                "Linked to this conversation", "About the same record", "Linked", "Same record",
                "Title", "Title (English)", "Description", "Assignee", "Priority", "Due date", "Due",
                "Cancel", "Create the task", "Employee",
                "No tasks are related to this conversation", "The related tasks could not be loaded",
                "The task was not created", "The task was created",
                "The task was created but could not be attached to this record",
                "Write a title for the task", "Choose who the task is for", "Type a name to search",
                "Loading the related tasks",
                "You do not have access to the tasks on this record",
                "You do not have access to the approvals on this record"
            };
            foreach (var k in keys)
                Assert.Contains("<data name=\"" + k + "\"", resx, StringComparison.Ordinal);
        }

        // 14. THE CONTRACT THIS PANEL HAS ALREADY DRIFTED FROM TWICE.
        //
        //     TasksController.ForConversation takes (string? entity, int id) and resolves the
        //     conversation itself through ListForEntityAsync - which is also the authorization, so a
        //     record the caller may not read yields no threads and therefore no tasks.
        //
        //     An earlier revision of this panel sent threadId instead. That binds cleanly to 0 and
        //     the endpoint answers "Invalid record", so the failure is a silently empty list rather
        //     than an error anyone would notice. The guard is therefore TWO-SIDED: the request must
        //     be addressed by the record, and the obsolete parameter must not appear anywhere that
        //     runs.
        [Fact]
        public void The_related_tasks_request_is_addressed_by_the_record_and_never_by_a_thread()
        {
            var razor = Executable(Read(Partial));

            var at = razor.IndexOf("function loadTasks()", StringComparison.Ordinal);
            Assert.True(at > 0, "loadTasks() is gone - the related-tasks panel no longer fetches.");
            var body = razor[at..Math.Min(razor.Length, at + 1200)];

            Assert.Contains("'entity=' + encodeURIComponent(code) + '&id=' + encodeURIComponent(id)",
                body, StringComparison.Ordinal);

            // Nowhere that runs, not merely nowhere in loadTasks: the obsolete contract came back
            // once already by way of a helper that cached the id somewhere else.
            //
            // WHAT WAS FORBIDDEN, AND WHAT STILL IS. This used to ban the word threadId outright,
            // because at the time nothing in the panel had any business knowing one. Linking gave it
            // a legitimate use: LinkConversation takes a threadId, and the list endpoint has always
            // returned the thread it resolved for THIS record. Banning the word would now force the
            // panel to invent an id or send the record id in its place, which is the very confusion
            // the ban existed to prevent.
            //
            // So the ban is narrowed to the shape of the defect rather than the word: a thread may
            // never ADDRESS a request, and loadTasks may not mention one at all. The single place it
            // is allowed is the link payload, and this names it so a second use cannot appear quietly.
            Assert.DoesNotContain("threadId=", razor, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("threadId", body, StringComparison.OrdinalIgnoreCase);
            // TWO NOW, AND BOTH ARE THE SAME PAYLOAD: the link write, and the recovery that
            // completes a create whose link failed. The count stays so a third use cannot appear
            // quietly, and every one of them is pinned to state.threadId - the value the list
            // endpoint GAVE this panel - rather than to the record id it already knows.
            Assert.Equal(2, Regex.Matches(razor, @"fd\.append\('threadId'").Count);
            Assert.Equal(2, Regex.Matches(razor, @"fd\.append\('threadId', state\.threadId\)").Count);
        }

        // 15. The approvals section is READ ONLY and INERT until a host names an endpoint. It must
        //     never grow a decide affordance here: the owning module screen holds that authority.
        [Fact]
        public void The_approvals_section_is_opt_in_and_read_only()
        {
            var razor = Executable(Read(Partial));

            Assert.Contains("ViewBag.ConversationApprovalsUrl as string) ?? \"\"", razor, StringComparison.Ordinal);

            var at = razor.IndexOf("function loadApprovals()", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("if (!approvalsUrl) { return; }",
                razor[at..Math.Min(razor.Length, at + 200)], StringComparison.Ordinal);

            // No write of any kind from this section.
            var section = razor[at..Math.Min(razor.Length, at + 4000)];
            Assert.DoesNotContain("method: 'POST'", section, StringComparison.Ordinal);
            Assert.DoesNotContain("__RequestVerificationToken", section, StringComparison.Ordinal);

            // Addressed by the record, like every other call this panel makes.
            Assert.Contains("'entity=' + encodeURIComponent(code) + '&id=' + encodeURIComponent(id)",
                section, StringComparison.Ordinal);
        }

        // 16. Every approvals string is localized, same rule as the task strings.
        [Fact]
        public void Every_approvals_string_has_an_arabic_resource()
        {
            var resx = Read("CrossBuy/Resources/Views/Shared/_EntityConversation.ar.resx");
            foreach (var k in new[]
                     { "Approvals on this record", "Nothing is waiting for approval here",
                       "The approvals could not be loaded", "Requested by", "Submitted" })
                Assert.Contains("<data name=\"" + k + "\"", resx, StringComparison.Ordinal);
        }

        // ========================================================================================
        // THE FOUR ANSWERS A PANEL CAN GIVE, AND WHY THEY MUST NOT LOOK ALIKE.
        //
        // Loading, empty, refused and failed are four different statements. The panel used to make
        // only two of them in the tasks section: it revealed the heading over a blank body while the
        // request was in flight - which reads as "no tasks are related to this" - and it rendered a
        // 403 with the same red "could not be loaded" notice as a dead connection.
        //
        // Those are opposite instructions to the reader. "Could not be loaded" invites a retry that
        // will never succeed when the real answer is that the server understood and said no.
        // ========================================================================================

        [Fact]
        public void The_tasks_panel_announces_that_it_is_loading_as_the_approvals_panel_does()
        {
            var razor = Read(Partial);

            Assert.Contains("esc(T.loadingTasks)", razor, StringComparison.Ordinal);
            Assert.Contains("esc(T.loadingApprovals)", razor, StringComparison.Ordinal);
        }

        /// A refusal is rendered in the neutral "denied" tone this panel already uses for a refused
        /// conversation, not in danger red. Both panels must discriminate, and each must name its own
        /// string - a shared message would tell a reader the wrong section was refused.
        [Fact]
        public void A_refused_panel_is_told_apart_from_a_failed_one()
        {
            var razor = Read(Partial);

            Assert.Equal(2, Regex.Matches(razor, @"r\.status === 403").Count);

            Assert.Matches(new Regex(
                @"panelNotice\(body,\s*wasDenied \? T\.tasksDenied : T\.tasksFailed,\s*wasDenied \? 'denied' : 'danger'\)",
                RegexOptions.CultureInvariant), razor);

            Assert.Matches(new Regex(
                @"panelNotice\(body,\s*wasDenied \? T\.approvalsDenied : T\.approvalsFailed,\s*wasDenied \? 'denied' : 'danger'\)",
                RegexOptions.CultureInvariant), razor);
        }

        /// The denied tone is the one notice() already uses, so a refusal looks the same wherever this
        /// panel shows one. Amber is deliberately not among them: a refusal is not a warning.
        [Fact]
        public void The_panel_notice_reuses_the_existing_refusal_tone()
        {
            var razor = Read(Partial);

            Assert.Matches(new Regex(@"panelTones = \{ denied: \['#EEF2F6', '#334155'\]",
                RegexOptions.CultureInvariant), razor);
            Assert.Matches(new Regex(@"tones = \{ denied: \['#EEF2F6', '#334155'\]",
                RegexOptions.CultureInvariant), razor);
        }

        // ========================================================================================
        // LINK AND UNLINK - the write half of the panel.
        //
        // The endpoints are TAB-5's; what is pinned here is the panel's side of the bargain: which
        // control appears when, what it sends, and which refusal it shows. Every one of these has a
        // way of being quietly wrong that a screenshot would not reveal.
        // ========================================================================================

        /// Three urls, three opt-ins. A host that names none of them renders what it did before.
        [Fact]
        public void Linking_is_opt_in_on_all_three_urls()
        {
            var razor = Read(Partial);

            Assert.Contains("ViewBag.ConversationLinkUrl as string) ?? \"\"", razor, StringComparison.Ordinal);
            Assert.Contains("ViewBag.ConversationUnlinkUrl as string) ?? \"\"", razor, StringComparison.Ordinal);
            Assert.Contains("ViewBag.ConversationLinkSearchUrl as string) ?? \"\"", razor, StringComparison.Ordinal);

            var js = Executable(razor);
            Assert.Contains("box.getAttribute('data-link')", js, StringComparison.Ordinal);
            Assert.Contains("box.getAttribute('data-unlink')", js, StringComparison.Ordinal);
            Assert.Contains("box.getAttribute('data-linksearch')", js, StringComparison.Ordinal);
        }

        /// THE FOUR CONDITIONS. Dropping any one of them offers a control that is certain to be
        /// refused - and the thread one is the easy miss, because it is null until the first message.
        [Fact]
        public void The_link_control_requires_the_urls_the_service_and_a_thread()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(
                @"function canLink\(\)\s*\{\s*return !!linkUrl && !!linkSearchUrl && " +
                @"state\.linkingAvailable === true && state\.threadId > 0;",
                RegexOptions.CultureInvariant), js);

            // LinkingAvailable is the service's answer and absence means false.
            Assert.Contains("state.linkingAvailable = d.linkingAvailable === true;", js, StringComparison.Ordinal);
        }

        /// The thread is the one the LIST endpoint resolved for this record. Not the record id.
        [Fact]
        public void The_thread_comes_from_the_list_response_and_never_from_the_record_id()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("state.threadId = (data.threadId > 0) ? data.threadId : 0;", js, StringComparison.Ordinal);
            Assert.DoesNotContain("fd.append('threadId', id)", js, StringComparison.Ordinal);
            Assert.DoesNotContain("fd.append('threadId', code)", js, StringComparison.Ordinal);
        }

        /// Unlink is drawn only where a link row exists to remove. A shared business context has none.
        ///
        /// THE GATE MOVED, AND NARROWING IT WAS THE POINT. It used to read `relationship ===
        /// 'ExplicitLink' && linkId > 0`, which is true for a conversation link AND a message link
        /// and says nothing about which - while the m2m migration made that distinction the whole
        /// question. The link id alone is the honest test: an inferred row has none, and both kinds
        /// of real link have their own.
        [Fact]
        public void Unlink_is_offered_only_where_a_link_row_exists_to_remove()
        {
            var js = Executable(Read(Partial));

            // The list row, and the message chip - the two places a relationship is drawn.
            Assert.Equal(2, Regex.Matches(js, @"if \(unlinkUrl && t\.linkId > 0\)").Count);

            // And never on the relationship string, which cannot tell the two kinds apart.
            Assert.DoesNotContain("t.relationship === 'ExplicitLink' && t.linkId > 0", js, StringComparison.Ordinal);
        }

        /// Both writes are POSTs carrying the page token, through the one helper that appends it.
        [Fact]
        public void Both_writes_carry_the_antiforgery_token()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("fd.append('__RequestVerificationToken', token());", js, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"fetch\(url, \{ method: 'POST', credentials: 'same-origin', body: fd \}\)",
                RegexOptions.CultureInvariant), js);
            Assert.Contains("postLink(fd, linkUrl,", js, StringComparison.Ordinal);
            Assert.Contains("postLink(fd, unlinkUrl,", js, StringComparison.Ordinal);
        }

        /// ONE WRITE AT A TIME. A double click used to mean two POSTs racing each other's refresh.
        [Fact]
        public void A_second_click_cannot_start_a_second_write()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(@"if \(busy\) \{ return; \}\s*busy = true;", RegexOptions.CultureInvariant), js);
        }

        /// FIVE STATUSES, FIVE ANSWERS. 503 in particular must not read as something to retry.
        [Theory]
        [InlineData("401", "T.expired")]
        [InlineData("403", "T.linkDenied")]
        [InlineData("404", "T.linkNotFound")]
        [InlineData("503", "T.linkUnavailable")]
        [InlineData("400", "T.linkRejected")]
        public void Each_refusal_status_has_its_own_message(string status, string key)
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(@"status === " + status + @"\) \{ return " + Regex.Escape(key) + @"; \}",
                RegexOptions.CultureInvariant), js);
        }

        /// NEVER A SUCCESS WITHOUT A PERSISTED ID. The endpoint refuses to claim one it did not write;
        /// the panel refuses to draw one it was not given.
        [Fact]
        public void A_link_is_only_shown_when_the_server_returned_a_link_id()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(@"if \(!\(d\.linkId > 0\)\)", RegexOptions.CultureInvariant), js);
        }

        /// A MUTATION NEVER RELOADS. Both writes repaint the two things that changed.
        [Fact]
        public void A_successful_write_repaints_rather_than_reloading()
        {
            var js = Executable(Read(Partial));

            // THREE WRITES REPAINT: create from a message, link, unlink. The count is pinned so a
            // fourth write cannot be added without deciding what it refreshes.
            // Five now: create, link, unlink, and the two halves of the partial-success
            // recovery - a created-but-unlinked task repaints, and so does the attach that
            // completes it. Every one of them is a repaint; none of them is a reload.
            Assert.Equal(5, Regex.Matches(js, @"loadTasks\(\);\s*load\(true\);").Count);
            Assert.DoesNotContain("location.reload", js, StringComparison.Ordinal);
        }

        /// NOT APPLICABLE IS NOT EMPTY. "Nothing is waiting for approval here" over a heading is a
        /// claim about a workflow that does not exist, and it reads as all-clear to the one person
        /// who would have acted.
        [Fact]
        public void An_unsupported_approvals_section_is_hidden_rather_than_shown_empty()
        {
            var js = Executable(Read(Partial));

            var at = js.IndexOf("function loadApprovals()", StringComparison.Ordinal);
            Assert.True(at > 0);
            var body = js[at..Math.Min(js.Length, at + 2000)];

            Assert.Matches(new Regex(@"if \(d\.supported === false\) \{[^}]*panel\.classList\.add\('d-none'\)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant), body);
        }

        /// EVERY STRING THIS PANEL CAN SHOW, not a list somebody remembered to extend.
        ///
        /// A hand-kept array of keys guards the twelve that were typed into it and nothing else, so
        /// a new Localizer call ships with no Arabic behind it and renders its English key in an
        /// Arabic panel - which is exactly what had happened to two of them by the time this was
        /// written. The sweep reads the keys out of the view instead.
        [Fact]
        public void Every_localizer_key_has_an_arabic_resource()
        {
            var razor = Read(Partial);
            var resx = Read("CrossBuy/Resources/Views/Shared/_EntityConversation.ar.resx");

            // Razor comments do not render, so a key quoted inside one is not a key in use.
            var live = Regex.Replace(razor, @"@\*.*?\*@", "", RegexOptions.Singleline);
            var keys = Regex.Matches(live, @"Localizer\[""([^""]+)""\]")
                .Select(m => m.Groups[1].Value).Distinct().ToList();

            // The sweep is worthless if it found nothing to sweep.
            Assert.True(keys.Count > 80, $"only {keys.Count} localizer keys found - the scan is broken.");

            var missing = keys.Where(k => !resx.Contains("<data name=\"" + k + "\"", StringComparison.Ordinal))
                              .ToList();
            Assert.True(missing.Count == 0,
                "these keys render their English text in an Arabic panel: " + string.Join(", ", missing));
        }

        // ========================================================================================
        // THE TASK PICKER.
        //
        // These exist because of a live failure, not a hypothetical one. The panel called
        // Tasks/LinkSearch - the picker for "which business record is this task about" - and posted
        // the SalesInvoice id it returned as a taskId. { threadId: 10002, taskId: 12545 } was
        // refused because 12545 is an invoice. The 404 was the lucky outcome: the same wiring on an
        // id that exists in both tables links a stranger's task in silence, and nothing on screen
        // would have said so. Every assertion below is aimed at a way that can come back.
        // ========================================================================================

        /// THE ENDPOINT, AND NOTHING LEFT OVER FROM THE OLD ONE. entityType was LinkSearch's
        /// parameter; LinkCandidates ignores it, and still sending it keeps alive the idea that the
        /// two are interchangeable.
        [Fact]
        public void The_picker_asks_for_tasks_and_sends_only_a_search_term()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("'term=' + encodeURIComponent(term || '')", js, StringComparison.Ordinal);
            Assert.DoesNotContain("entityType", js, StringComparison.Ordinal);
        }

        /// EVERY WIRED HOST NAMES THE WHOLE CONTRACT, not one url of it.
        ///
        /// The partial reveals the link controls only when the three LINK urls are all present, and
        /// renders the related-task list only when ConversationTasksUrl is. A screen that named four
        /// of the five would therefore ship a panel that looks wired and silently is not - which is
        /// not something a reader can see, so it is pinned per screen instead.
        ///
        /// LinkSearch is banned by name: it is the picker for the OTHER direction and returns
        /// business-record ids, which is how a live attempt once posted a SalesInvoice id as a taskId.
        [Theory]
        [InlineData("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml")]
        [InlineData("CrossBuy/Views/Inventory/QuotationDetails.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/JournalEntry.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/SalesReturnDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseReturnDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/MovementSummary.cshtml")]
        [InlineData("CrossBuy/Views/Inventory/DocumentDetails.cshtml")]
        [InlineData("CrossBuy/Views/Crm/TicketEditor.cshtml")]
        public void Every_wired_host_names_the_whole_task_link_contract(string view)
        {
            var razor = Read(view);

            foreach (var (bag, action) in new[]
            {
                ("ConversationCreateTaskUrl", "CreateFromMessage"),
                ("ConversationTasksUrl", "ForConversation"),
                ("ConversationLinkUrl", "LinkConversation"),
                ("ConversationUnlinkUrl", "UnlinkConversation"),
                ("ConversationLinkSearchUrl", "LinkCandidates"),
            })
                Assert.Contains($"{bag} = Url.Action(\"{action}\", \"Tasks\")",
                    razor, StringComparison.Ordinal);

            // Every one of them is the TASKS controller. A module that pointed these at its own
            // controller would be asking a permission gate written for its records about a task.
            Assert.DoesNotContain("Url.Action(\"LinkSearch\", \"Tasks\")", razor, StringComparison.Ordinal);

            // And the panel is actually on the page - a wired ViewBag with no render is dead config.
            Assert.Contains("_EntityConversation", razor, StringComparison.Ordinal);
        }

        /// A MULTIPLEXED SCREEN PASSES THE RECORD IT IS SHOWING, never a hardcoded code.
        ///
        /// These two views carry FOURTEEN of the entity codes between them. A literal code here would
        /// attach every stock count to whichever type was typed in, and the link endpoint would accept
        /// it: it authorizes the thread and the task, and the thread is whatever the panel asked for.
        [Theory]
        [InlineData("CrossBuy/Views/Accounting/MovementSummary.cshtml", "Model.TimelineCode", "Model.Id")]
        [InlineData("CrossBuy/Views/Inventory/DocumentDetails.cshtml", "Model.EntityCode", "Model.DocumentId")]
        public void A_shared_host_passes_the_record_it_is_showing(string view, string code, string id)
        {
            var razor = Read(view);

            Assert.Contains($"ViewBag.ConversationEntityCode = {code};", razor, StringComparison.Ordinal);
            Assert.Contains($"ViewBag.ConversationEntityId = {id};", razor, StringComparison.Ordinal);

            // No literal registry code anywhere in the wiring - that is the defect being prevented.
            Assert.DoesNotContain("ViewBag.ConversationEntityCode = CrossBuy.BL.Platform.EntityRegistry.",
                razor, StringComparison.Ordinal);
        }

        /// THE SAME RECORD, THE SAME OFFER, FROM EITHER SCREEN.
        ///
        /// Four codes are reachable from two routes: their own detail view and MovementSummary. Wiring
        /// one and not the other shows the Link button on one and hides it on the other for the SAME
        /// invoice, which a user reads as a broken screen rather than as a scope decision.
        [Theory]
        [InlineData("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/SalesReturnDetail.cshtml")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseReturnDetail.cshtml")]
        public void A_record_offers_the_same_linking_from_both_of_its_screens(string detailView)
        {
            var detail = Read(detailView);
            var movement = Read("CrossBuy/Views/Accounting/MovementSummary.cshtml");

            foreach (var bag in new[]
            {
                "ConversationCreateTaskUrl", "ConversationTasksUrl",
                "ConversationLinkUrl", "ConversationUnlinkUrl", "ConversationLinkSearchUrl",
            })
            {
                Assert.Contains(bag, detail, StringComparison.Ordinal);
                Assert.Contains(bag, movement, StringComparison.Ordinal);
            }
        }

        /// THE OPERATOR LOG IS STILL CONVERSATION-ONLY, and that is a security position rather than a
        /// backlog item. PlatformEvent carries PermissionScope "None", so the platform entity gate
        /// answers View for ANY authenticated caller in the company; the real authority is the
        /// monitor controller's [PlatformOps], and TasksController neither carries it nor should.
        /// Wiring these urls here would route an operator surface through a company-wide task gate.
        [Fact]
        public void The_event_monitor_panel_stays_conversation_only()
        {
            var razor = Read("CrossBuy/Views/BusinessEventMonitor/_Details.cshtml");

            // It still HAS a conversation - this is an exclusion, not a removal.
            Assert.Contains("ViewBag.ConversationListUrl", razor, StringComparison.Ordinal);
            Assert.Contains("ViewBag.ConversationAddUrl", razor, StringComparison.Ordinal);

            foreach (var bag in new[]
            {
                "ConversationCreateTaskUrl", "ConversationTasksUrl",
                "ConversationLinkUrl", "ConversationUnlinkUrl", "ConversationLinkSearchUrl",
            })
                Assert.DoesNotContain(bag, razor, StringComparison.Ordinal);
        }

        /// THE UI WAVE DID NOT WIDEN THE REGISTRY, which is the one thing a screen-wiring change must
        /// not quietly do. EligibleForTaskLink decides what a task may be ABOUT; it is not what gates
        /// linking, and nothing here needed it moved.
        [Fact]
        public void Wiring_a_screen_did_not_widen_task_link_eligibility()
        {
            var eligible = CrossBuy.BL.TaskService.TaskEntityLinkPolicy.EligibleEntityTypes;

            Assert.Equal(11, eligible.Count);
            foreach (var code in new[]
            {
                "Customer", "Employee", "Item", "JournalEntry", "ManufWorkOrder", "PosOrder",
                "Project", "PurchaseInvoice", "Quotation", "SalesInvoice", "Task",
            })
                Assert.Contains(code, eligible);

            // The twelve this wave wired on screen are deliberately still NOT anchor-eligible: a task
            // created from one of those messages is saved with no business anchor and the panel says
            // so, rather than the registry being widened behind the owner's decision.
            foreach (var code in new[]
            {
                "SalesReturn", "PurchaseReturn", "Receipt", "Payment", "PurchaseOrder", "GoodsReceipt",
                "SalesOrder", "DeliveryNote", "StockTransfer", "StockCount", "StockWriteOff",
                "LandedCost", "CrmTicket", "PlatformEvent",
            })
                Assert.DoesNotContain(code, eligible);
        }

        /// A ROW THAT CANNOT BE LINKED IS A ROW THAT SHOULD NOT BE OFFERED, and the id a row carries
        /// is a TASK id - never the entityId the same payload also contains.
        [Fact]
        public void Only_rows_carrying_a_real_task_id_are_rendered()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("candidates = (d.results || []).filter(function (c) { return c && c.id > 0; });",
                js, StringComparison.Ordinal);

            // The write is fed from the SELECTION, which is a row of that filtered list.
            Assert.Contains("linkTask(chosen.id)", js, StringComparison.Ordinal);
            Assert.DoesNotContain("c.entityId", js, StringComparison.Ordinal);
            Assert.DoesNotContain("c.taskId", js, StringComparison.Ordinal);
        }

        /// THE TITLE IS THE ROW AND THE NUMBER IS A FOOTNOTE. The old picker fell back to '#' + id as
        /// the row's text, so a task with no title rendered as a bare number and the owner's
        /// complaint - a number explains nothing - was the whole brief.
        [Fact]
        public void The_title_is_primary_and_the_task_number_is_secondary()
        {
            var js = Executable(Read(Partial));

            // The endpoint resolved the display name already; `text` is its older twin.
            Assert.Contains("var title = c.title || c.text || '';", js, StringComparison.Ordinal);
            Assert.Contains("flex-grow-1 text-truncate fs-8 fw-semibold text-gray-900", js, StringComparison.Ordinal);

            // The number is smaller, muted, isolated, and labelled for a screen reader.
            Assert.Matches(new Regex(@"aria-label=""' \+ esc\(T\.taskNumber\)"), js);
            Assert.Contains("\">\\u2066#' + esc(c.id) + '\\u2069</span>'", Isolates(js), StringComparison.Ordinal);
        }

        /// OPTIONAL MEANS GUARDED. Every one of these is absent on some real task, and an ungated
        /// read renders the word "undefined" into a row that is meant to help somebody choose.
        [Fact]
        public void Optional_metadata_is_rendered_only_when_the_endpoint_sent_it()
        {
            var js = Executable(Read(Partial));
            var at = js.IndexOf("function candidateRow(", StringComparison.Ordinal);
            Assert.True(at > 0, "candidateRow() is gone - the picker no longer renders rows.");
            var body = js[at..js.IndexOf("function renderCandidates(", StringComparison.Ordinal)];

            Assert.Contains("if (c.assigneeName) {", body, StringComparison.Ordinal);
            Assert.Contains("} else if (c.assigneeEmployeeId) {", body, StringComparison.Ordinal);
            Assert.Contains("esc(T.unassigned)", body, StringComparison.Ordinal);
            Assert.Contains("var d = parseUtc(c.dueDate);", body, StringComparison.Ordinal);
            Assert.Contains("var summary = c.summary", body, StringComparison.Ordinal);

            // And nothing the endpoint sends is written into the row unescaped.
            foreach (var field in new[] { "c.id", "c.summary", "c.assigneeName", "c.assigneeEmployeeId" })
                Assert.Contains("esc(" + field + ")", body, StringComparison.Ordinal);
        }

        /// THE STORED TOKEN IS NOT A LABEL. Status is persisted as "InProgress", and the badge used to
        /// print it verbatim - in Arabic too.
        [Fact]
        public void Status_and_priority_are_translated_rather_than_printed_as_stored()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("function statusLabel(s) { return (T.statuses && T.statuses[s]) || s || ''; }",
                js, StringComparison.Ordinal);
            Assert.Contains("esc(statusLabel(s))", js, StringComparison.Ordinal);
            Assert.Contains("esc(priorityLabel(p))", js, StringComparison.Ordinal);

            // The Tasks screens' own colour mapping, not a second one invented here.
            Assert.Matches(new Regex(
                @"p === 'Urgent' \? 'badge-light-danger' : p === 'High' \? 'badge-light-warning'"), js);
        }

        /// CHOOSING IS NOT WRITING. The old picker posted on the click that chose, so a mis-click was
        /// a persisted relationship on a record other people read.
        [Fact]
        public void Choosing_a_row_and_linking_it_are_two_separate_actions()
        {
            var js = Executable(Read(Partial));

            // The row click SELECTS.
            Assert.Matches(new Regex(
                @"if \(pick\) \{ ev\.preventDefault\(\); choose\(Number\(pick\.getAttribute\('data-index'\)\)\); return; \}"), js);

            // Nothing but the confirmation calls the write.
            Assert.Equal(1, Regex.Matches(js, @"(?<![A-Za-z])linkTask\(").Count
                           - Regex.Matches(js, @"function linkTask\(").Count);

            // And the confirmation refuses to act without a selection.
            Assert.Matches(new Regex(@"if \(!chosen \|\| busy\) \{ return; \}\s*linkTask\(chosen\.id\);",
                RegexOptions.Singleline), js);
        }

        /// The write posts the chosen TASK and the thread the LIST endpoint resolved - never the
        /// record id, which is the mistake that started this.
        [Fact]
        public void The_write_posts_the_chosen_task_and_the_resolved_thread()
        {
            var js = Executable(Read(Partial));
            var at = js.IndexOf("function linkTask(taskId)", StringComparison.Ordinal);
            Assert.True(at > 0);
            var body = js[at..Math.Min(js.Length, at + 2600)];

            Assert.Contains("fd.append('threadId', state.threadId);", body, StringComparison.Ordinal);
            Assert.Contains("fd.append('taskId', taskId);", body, StringComparison.Ordinal);
            Assert.Contains("if (!canLink() || !(Number(taskId) > 0)) { return; }", body, StringComparison.Ordinal);
        }

        /// ONE WRITE AT A TIME, and the control says so. busy already refused the second POST; a
        /// button that still looks pressable invites the click it then swallows.
        [Fact]
        public void A_submission_in_flight_disables_the_confirmation()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("btn.disabled = true;", js, StringComparison.Ordinal);
            Assert.Contains("function restore() { if (btn) { btn.disabled = false; btn.innerHTML = label; } }",
                js, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"if \(busy\) \{ return; \}\s*busy = true;", RegexOptions.CultureInvariant), js);
        }

        /// NOTHING REASSURING IS SAID BEFORE THE ROW EXISTS. say() is the panel's success voice, and
        /// it must sit AFTER the linkId guard - not beside it.
        [Fact]
        public void The_success_message_comes_only_after_a_persisted_link_id()
        {
            var js = Executable(Read(Partial));
            var at = js.IndexOf("function linkTask(taskId)", StringComparison.Ordinal);
            var body = js[at..Math.Min(js.Length, at + 2600)];

            var guard = body.IndexOf("if (!(d.linkId > 0))", StringComparison.Ordinal);
            var said = body.IndexOf("say(T.linkCreated);", StringComparison.Ordinal);
            Assert.True(guard > 0, "the persisted-id guard is gone");
            Assert.True(said > guard, "the panel announces success before it knows a row was written");
        }

        /// A SEARCH THAT NEVER ANSWERED IS NOT A SEARCH THAT FOUND NOTHING. The old catch rendered
        /// "no matching tasks" for both, which tells somebody looking at a task they can see to
        /// stop looking.
        [Fact]
        public void A_failed_search_is_not_shown_as_an_empty_one()
        {
            var js = Executable(Read(Partial));
            var at = js.IndexOf("function searchCandidates(term)", StringComparison.Ordinal);
            Assert.True(at > 0);
            var body = js[at..js.IndexOf("function linkTask(taskId)", StringComparison.Ordinal)];

            Assert.Contains("esc(T.searchFailed)", body, StringComparison.Ordinal);
            Assert.Contains("esc(T.searching)", body, StringComparison.Ordinal);

            // The empty answer keeps its own wording, and only the empty answer uses it.
            Assert.Contains("esc(T.noCandidates)", js, StringComparison.Ordinal);

            // A slower earlier query must not repaint over a newer answer.
            Assert.Contains("if (seq !== searchSeq) { return; }", body, StringComparison.Ordinal);
        }

        /// A CANDIDATE THAT WENT AWAY IS EXPLAINED WITHOUT SAYING WHAT HAPPENED TO IT. Closed out of
        /// reach, reassigned, deleted - which one it was is a fact about a task this caller may no
        /// longer read, and the 404 body carries only a code for exactly that reason.
        [Fact]
        public void A_stale_candidate_is_explained_without_leaking_why()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(
                @"if \(status === 404\) \{ clearChoice\(\); taskErr\(T\.linkStale\); searchCandidates\(currentTerm\(\)\); return; \}"), js);

            // The status reaches that branch at all only because postLink forwards it.
            Assert.Contains("onFail(linkError(d && d.httpStatus, d), d && d.httpStatus)", js, StringComparison.Ordinal);
        }

        /// THE KEYBOARD REACHES THE LIST. Without the combobox pairing the arrow keys move the caret
        /// inside the text box and the picker is mouse-only.
        [Fact]
        public void The_picker_is_operable_from_the_keyboard()
        {
            var razor = Read(Partial);
            var js = Executable(razor);

            Assert.Contains("role=\"combobox\"", razor, StringComparison.Ordinal);
            Assert.Contains("role=\"listbox\"", razor, StringComparison.Ordinal);
            Assert.Contains("role=\"option\"", js, StringComparison.Ordinal);
            Assert.Contains("aria-controls=\"cbConvLinkResults\"", razor, StringComparison.Ordinal);
            Assert.Contains("input.setAttribute('aria-activedescendant', 'cbConvLinkOpt' + activeIndex);",
                js, StringComparison.Ordinal);

            foreach (var key in new[] { "ArrowDown", "ArrowUp", "Home", "End", "Enter", "Escape" })
                Assert.Contains("ev.key === '" + key + "'", js, StringComparison.Ordinal);

            // Enter CHOOSES. A return key that wrote the link would be the mis-click in another form.
            Assert.Matches(new Regex(@"ev\.key === 'Enter'[^}]*choose\(activeIndex\);", RegexOptions.Singleline), js);

            // And the cursor is a different thing from the selection, so arrowing is not committing.
            Assert.Contains("var picked = !!chosen && chosen.id === c.id;", js, StringComparison.Ordinal);
            Assert.Contains("var active = i === activeIndex;", js, StringComparison.Ordinal);
        }

        /// BOTH DIRECTIONS FROM ONE RULE. Physical utilities (ml-, pl-, text-left) pin a layout to
        /// LTR and leave the Arabic panel mirrored wrongly; the logical ones flip themselves.
        [Fact]
        public void The_picker_lays_out_correctly_in_both_directions()
        {
            var razor = Read(Partial);
            var js = Executable(razor);
            var at = razor.IndexOf("id=\"cbConvLinkPicker\"", StringComparison.Ordinal);
            Assert.True(at > 0);
            var markup = razor[at..razor.IndexOf("id=\"cbConvTasksBody\"", StringComparison.Ordinal)];

            foreach (var physical in new[] { "text-left", "text-right", "ml-", "mr-", "pl-", "pr-" })
                Assert.DoesNotContain(physical, markup, StringComparison.Ordinal);

            // A Gregorian date and a Latin number are isolated, so neither reorders an Arabic row.
            Assert.Contains("'\\u2066' +", Isolates(js), StringComparison.Ordinal);

            // AND THE DATE IS ASSEMBLED, NOT LOCALE-FORMATTED. toLocaleDateString in Arabic emits
            // RIGHT-TO-LEFT MARKS BETWEEN the components, and those marks survive the isolate - due
            // dates rendered as "112026/10/", the day torn off and thrown to the front. The isolate
            // was never the missing piece; the formatter was the wrong tool. Pinning the old
            // expression would have demanded the defect back, so the ban is pinned instead.
            Assert.Contains("dateText(d)", js, StringComparison.Ordinal);
            Assert.DoesNotContain("toLocaleDateString", js, StringComparison.Ordinal);
            Assert.Matches(new Regex(
                @"function dateText\(d\).{0,400}?getFullYear\(\)", RegexOptions.Singleline), js);
        }

        /// UNLINK IS UNTOUCHED. The picker rewrite shares the delegated click handler with it, which
        /// is exactly the kind of neighbour an edit removes by accident.
        [Fact]
        public void The_existing_unlink_flow_is_preserved()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(
                @"var drop = ev\.target\.closest \? ev\.target\.closest\('\.cb-unlink'\) : null;"), js);
            Assert.Contains("unlinkTask(Number(drop.getAttribute('data-linkid')))", js, StringComparison.Ordinal);
            Assert.Contains("postLink(fd, unlinkUrl,", js, StringComparison.Ordinal);
        }

        /// CLOSING CLEARS THE SELECTION TOO. A chosen task left behind after the picker closed is one
        /// the next confirmation would write without the reader ever seeing it named.
        [Fact]
        public void Closing_the_picker_discards_every_piece_of_its_state()
        {
            var js = Executable(Read(Partial));
            var at = js.IndexOf("function closePicker()", StringComparison.Ordinal);
            Assert.True(at > 0);
            var body = js[at..Math.Min(js.Length, at + 900)];

            Assert.Contains("candidates = []; activeIndex = -1; chosen = null; searchSeq++;",
                body, StringComparison.Ordinal);
            Assert.Contains("input.removeAttribute('aria-activedescendant');", body, StringComparison.Ordinal);
        }

        // ========================================================================================
        // MESSAGE-LEVEL TASK LINKING.
        //
        // The schema under this became many-to-many: a task may be attached to the conversation AND
        // to several of its messages, and each of those is a separate row with its own LinkId that
        // is removed on its own. The UI that sat on top of it keyed everything by task id, so the
        // second link overwrote the first - one row on screen, pointing at a message the reader
        // could not identify, and the link that lost the race could not be removed at all because
        // its id was gone. Every assertion here is aimed at that collapse coming back.
        // ========================================================================================

        /// THE ANCHOR THE REST OF THE PLATFORM ALREADY POINTS AT. MessagesForTask builds every
        /// route as "<record>#cbComment<id>"; nothing in this panel carried that id, so each of
        /// those links landed on the record and stopped.
        [Fact]
        public void Every_message_carries_the_anchor_the_platform_links_to()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("id=\"cbComment' + c.id + '\"", js, StringComparison.Ordinal);

            // The other half of the contract, in the controller that builds the destination. If the
            // fragment is ever renamed there, this fails here rather than in a dead link.
            var controller = Read("CrossBuy/Controllers/TasksController.cs");
            Assert.Contains("\"#cbComment\" + comment.Value", controller, StringComparison.Ordinal);
        }

        /// FOUR ACTIONS ON AN ELIGIBLE MESSAGE, and the new one routes to the picker rather than
        /// writing anything by itself.
        [Fact]
        public void An_eligible_message_offers_react_reply_create_and_link()
        {
            var js = Executable(Read(Partial));

            foreach (var act in new[] { "picker", "reply", "task", "linktask" })
                Assert.Contains("data-act=\"" + act + "\"", js, StringComparison.Ordinal);

            Assert.Contains("if (act === 'linktask') { openPicker(cid); return; }", js, StringComparison.Ordinal);

            // A removed message is not turned into work, and not attached to any.
            Assert.Matches(new Regex(@"if \(!c\.isDeleted\) \{\s*bits\.push\('<button type=""button"" class=""cb-msg-link",
                RegexOptions.Singleline), js);
        }

        /// NEVER A MESSAGE-LEVEL REQUEST WITHOUT ITS MESSAGE. Dropping the comment id does not fail -
        /// it quietly attaches the task to the WHOLE CONVERSATION while the dialog says otherwise.
        /// That is the downgrade the endpoint answers 409 for, performed by the client instead.
        [Fact]
        public void A_message_level_link_always_carries_its_comment_id()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("if (pickerFor > 0) { fd.append('commentId', pickerFor); }",
                js, StringComparison.Ordinal);

            // And the id cannot go missing on the way in: message mode refuses to open without one.
            Assert.Contains("if (commentId !== 0 && !(wanted > 0)) { return; }", js, StringComparison.Ordinal);

            // The comment is never inferred from the record this panel is about.
            Assert.DoesNotContain("fd.append('commentId', id)", js, StringComparison.Ordinal);
            Assert.DoesNotContain("fd.append('commentId', code)", js, StringComparison.Ordinal);
        }

        /// A 409 IS A FAILURE WITH ITS OWN NAME. It is not "could not be saved" - nothing is broken -
        /// and it is certainly not a success.
        [Fact]
        public void A_downgrade_conflict_is_reported_as_its_own_refusal()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(@"status === 409\) \{ return T\.linkDowngraded; \}"), js);

            // And it reaches the failure path at all because nothing but ok === true is a success.
            Assert.Contains("if (!d || d.ok !== true) { onFail(", js, StringComparison.Ordinal);
        }

        /// ONE MESSAGE, SEVERAL TASKS. The chips accumulate per message rather than replacing each
        /// other, and nothing withdraws the link action once a message already has one.
        [Fact]
        public void One_message_may_carry_more_than_one_task()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("(byComment[t.commentId] = byComment[t.commentId] || []).push(t);",
                js, StringComparison.Ordinal);

            // The action is gated on the message existing and on canLink() - never on a count.
            var at = js.IndexOf("function actionsHtml(c)", StringComparison.Ordinal);
            var body = js[at..js.IndexOf("function markMentions(c)", StringComparison.Ordinal)];
            Assert.DoesNotContain("linked", body, StringComparison.Ordinal);
        }

        /// ONE TASK, SEVERAL PLACES. The same taskId may sit on the conversation and on two of its
        /// messages, and all three rows survive - which is only true while nothing keys by task.
        [Fact]
        public void One_task_may_appear_once_per_relationship()
        {
            var js = Executable(Read(Partial));

            foreach (var scope in new[] { "conversation", "message", "inferred" })
                Assert.Contains("scopeOf(t) === '" + scope + "'", js, StringComparison.Ordinal);

            // The server's own classification is used, with a derivation only as a fallback for an
            // older build - never instead of it.
            Assert.Contains("if (t.scope === 'conversation' || t.scope === 'message' || t.scope === 'inferred') { return t.scope; }",
                js, StringComparison.Ordinal);

            // Nothing anywhere collapses the list onto the task id.
            Assert.DoesNotContain("byTask[", js, StringComparison.Ordinal);
            Assert.DoesNotContain("[t.taskId] =", js, StringComparison.Ordinal);
        }

        /// THE LINK IS THE KEY. Both the list row and the message chip carry the id the unlink
        /// endpoint deletes by, and neither carries the task id in its place.
        [Fact]
        public void Every_relationship_is_addressed_by_its_link_id()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("'data-linkid=\"' + esc(t.linkId || '') + '\" data-taskid=\"' + esc(t.taskId) + '\">'",
                js, StringComparison.Ordinal);
            Assert.Contains("data-linkid=\"' + esc(t.linkId) + '\"", js, StringComparison.Ordinal);

            // The unlink reads that attribute and sends that id, and sends nothing else.
            Assert.Contains("unlinkTask(Number(drop.getAttribute('data-linkid')))", js, StringComparison.Ordinal);
            Assert.Contains("fd.append('linkId', linkId);", js, StringComparison.Ordinal);

            var at = js.IndexOf("function unlinkTask(linkId)", StringComparison.Ordinal);
            var body = js[at..Math.Min(js.Length, at + 700)];
            Assert.DoesNotContain("taskId", body, StringComparison.Ordinal);
        }

        /// THE TASK EXISTS AND THE LINK DOES NOT IS A REAL OUTCOME. `linked` is its own field on the
        /// response precisely so this cannot be inferred from `ok`, and a plain "created" over it
        /// leaves a message pointing at nothing with nobody any the wiser.
        [Fact]
        public void A_created_task_that_was_not_attached_does_not_report_plain_success()
        {
            var js = Executable(Read(Partial));

            Assert.Matches(new Regex(
                @"if \(d\.linked !== true\) \{\s*showRecovery\(d\);", RegexOptions.Singleline), js);

            // The success line sits after that branch, not before it.
            var guard = js.IndexOf("if (d.linked !== true)", StringComparison.Ordinal);
            var said = js.IndexOf("say(d.anchorWarning ? T.anchorWarning : T.taskCreated", StringComparison.Ordinal);
            Assert.True(guard > 0 && said > guard,
                "the dialog announces a created task before it knows whether it was attached");

            // anchorWarning is still surfaced on its own terms.
            Assert.Contains("T.anchorWarning", js, StringComparison.Ordinal);
        }

        /// THE RECOVERY ATTACHES; IT NEVER CREATES. A retry that re-posted the dialog would be safe
        /// (the create is idempotent on DedupKey) but wrong: the task is not what is missing.
        [Fact]
        public void The_recovery_attaches_the_existing_task_and_makes_no_second_one()
        {
            var js = Executable(Read(Partial));
            var at = js.IndexOf("function wireRecovery()", StringComparison.Ordinal);
            Assert.True(at > 0, "the recovery is gone - a half-done pair has no way out again.");
            var body = js[at..Math.Min(js.Length, at + 1800)];

            Assert.Contains("fd.append('taskId', createdUnlinked.taskId);", body, StringComparison.Ordinal);
            Assert.Contains("fd.append('commentId', createdUnlinked.commentId);", body, StringComparison.Ordinal);
            Assert.Contains("postLink(fd, linkUrl,", body, StringComparison.Ordinal);

            // It must not be able to reach the create endpoint at all.
            Assert.DoesNotContain("createTaskUrl", body, StringComparison.Ordinal);

            // And the same persisted-id rule as every other write in this panel.
            Assert.Contains("if (!(r.linkId > 0)) { taskErr(T.linkUnavailable); return; }", body, StringComparison.Ordinal);

            // The DedupKey the backend relies on is still the message, unchanged.
            Assert.Contains("fd.append('dedupKey', 'commtask:' + code + ':' + id + ':' + taskFor.id);",
                js, StringComparison.Ordinal);
        }

        /// The chips are a LIST to a screen reader, and the remove button says which task it removes.
        [Fact]
        public void The_message_task_chips_are_announced_as_a_labelled_list()
        {
            var js = Executable(Read(Partial));

            Assert.Contains("h.setAttribute('role', 'list');", js, StringComparison.Ordinal);
            Assert.Contains("h.setAttribute('aria-label', T.messageTasks);", js, StringComparison.Ordinal);
            Assert.Contains("role=\"listitem\"", js, StringComparison.Ordinal);

            // "Unlink" alone, repeated down a message, names nothing. The task number is in the label.
            Assert.Contains("esc(T.unlinkAction) + ' \\u2066#' + esc(t.taskId)", Isolates(js), StringComparison.Ordinal);
        }

        /// BIDI SAFETY REACHES THE NEW SURFACES TOO: every task number the chips and the recovery
        /// print is isolated, and no date anywhere goes through the locale formatter.
        [Fact]
        public void The_new_surfaces_are_bidi_safe()
        {
            var js = Isolates(Executable(Read(Partial)));

            Assert.Contains("\\u2066#' + esc(t.taskId) + '\\u2069</span>'", js, StringComparison.Ordinal);
            Assert.Contains("esc(T.taskNumber) + ' \\u2066#' + esc(d.taskId) + '\\u2069</span>'", js, StringComparison.Ordinal);
            Assert.DoesNotContain("toLocaleDateString", js, StringComparison.Ordinal);
        }

        /// THE CONVERSATION-LEVEL UI IS PRESERVED. The message work went through the same picker, and
        /// the heading button is the one thing that could have been left behind by that move.
        [Fact]
        public void The_conversation_level_linking_still_works_the_way_it_did()
        {
            var js = Executable(Read(Partial));

            // 0 is the conversation, through the same entry point - so the reset that clears a stale
            // message id cannot be skipped on one of the two paths.
            Assert.Contains("if (picker.classList.contains('d-none')) { openPicker(0); } else { closePicker(); }",
                js, StringComparison.Ordinal);
            Assert.Contains("pickerFor = 0;", js, StringComparison.Ordinal);

            // And all three groups are still drawn, with the conversation one first.
            Assert.Matches(new Regex(
                @"group\(T\.groupLinked, onConversation\);\s*html \+= group\(T\.groupOnMessage, onMessage\);" +
                @"\s*html \+= group\(T\.groupShared, inferred\);", RegexOptions.Singleline), js);
        }

        // ========================================================================================
        // THE ACTIVATION INVARIANT.
        //
        // SupportsComments is the flag that puts a conversation on a record. Flipping one is two
        // lines in the registry and it reads as free - but the panel it reveals posts at a module
        // endpoint, and that endpoint decides who may read the record. Turn the flag on before the
        // module gate knows the code and the panel does not fail: it asks a gate that answers for a
        // DIFFERENT record type, or falls through a whitelist it was never added to.
        //
        // So the registry and the two module whitelists are held to each other here. A flag flipped
        // without its gate turns these red, which is the point: the review happens before the
        // deployment, not after somebody notices a conversation on a record they cannot open.
        // ========================================================================================

        private static IReadOnlyList<CrossBuy.Models.Platform.EntityDefinition> Registry() =>
            new CrossBuy.BL.Platform.EntityRegistry(null!).GetDefinitions();

        /// EVERY CONVERSATION-CAPABLE CODE HAS A MODULE GATE THAT ANSWERS FOR IT, or is one of three
        /// named exceptions. The arithmetic closes exactly - 8 Accounting + 9 Inventory + 2 Hospital +
        /// 3 exceptions = the 22 codes that carry comments - so a new flag cannot hide in the slack.
        [Fact]
        public void Every_conversation_capable_code_has_a_module_gate_or_a_named_exception()
        {
            var accounting = Read("CrossBuy/Controllers/AccountingController.cs");
            var inventory = Read("CrossBuy/Controllers/InventoryController.cs");

            // The whitelists are read where they are declared, so a code removed from one of them
            // fails here rather than at a user's next click.
            var accountingFamilies = Section(accounting, "ConversationFamilies");
            var inventoryFamilies = Section(inventory, "InventoryConversationFamilies");

            // PlatformEvent  - BusinessEventMonitor owns its endpoints and gates them with
            //                  [PlatformOps]; its PermissionScope is None and the platform entity
            //                  gate would otherwise answer View for any authenticated caller.
            // Task           - Tasks/Detail owns its own Comments tab; a second panel would split
            //                  the thread, which that view says in its own comment.
            // CalendarEvent  - carries comments and has NO record-level screen; /Calendar/Index is
            //                  a list, so there is nowhere for a panel to live yet.
            var exceptions = new[] { "PlatformEvent", "Task", "CalendarEvent" };

            // Hospital (H1 room, H2 patient) gates its own record conversations: RoomGateAsync and
            // PatientGateAsync ask hospital `read` FOR THAT RECORD and require it in the caller's company.
            var hospital = Read("CrossBuy/Controllers/HospitalController.cs") + Read("CrossBuy/Controllers/HospitalController.Patients.cs");
            // 2026-11-24: every hospital record answers through ONE record gate (RecordGateAsync), whose families are
            // declared in HospitalConversationFamilies and each resolved in the caller's company by RecordExistsAsync.
            var records = Read("CrossBuy/Controllers/HospitalController.Conversations.cs");
            var hospitalFamilies = Section(records, "HospitalConversationFamilies");
            bool HospitalGates(string c) => hospital.Contains($"PermissionTarget.ForEntity(EntityRegistry.{c}, id)", StringComparison.Ordinal)
                || (hospitalFamilies.Contains($"EntityRegistry.{c},", StringComparison.Ordinal) && records.Contains($"EntityRegistry.{c} => await", StringComparison.Ordinal));

            var capable = Registry().Where(d => d.SupportsComments).Select(d => d.Code).ToList();
            Assert.Equal(31, capable.Count);   // 22 + 9 hospital records (2026-11-24)

            var ungated = capable
                .Where(c => !exceptions.Contains(c, StringComparer.Ordinal))
                .Where(c => !accountingFamilies.Contains($"[\"{c}\"]", StringComparison.Ordinal)
                         && !inventoryFamilies.Contains($"[\"{c}\"]", StringComparison.Ordinal)
                         && !HospitalGates(c))
                .ToList();

            Assert.True(ungated.Count == 0,
                "these codes carry a conversation that no module gate answers for: " +
                string.Join(", ", ungated) +
                ". Add the code to its module whitelist (and its authority branch) BEFORE the flag.");
        }

        /// Reads one declaration out of a controller, so a whitelist assertion cannot accidentally
        /// match the same code mentioned anywhere else in a 3,000-line file.
        private static string Section(string source, string declaration)
        {
            var at = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(at > 0, $"{declaration} is gone - the module conversation gate changed shape.");
            var close = source.IndexOf("};", at, StringComparison.Ordinal);
            Assert.True(close > at, $"{declaration} is not a closed initializer.");
            return source[at..close];
        }

        /// THE AUTHORITY IS THE ONE THAT OWNS THE RECORD, not the controller the endpoint lives on.
        /// The Accounting gate already proves it knows the difference - it routes CrmTicket to the CRM
        /// service rather than asking AccountingAccessService about a ticket. That branch is the
        /// precedent every future cross-module code must follow, so it is pinned.
        [Fact]
        public void A_cross_module_conversation_asks_the_authority_that_owns_the_record()
        {
            var accounting = Read("CrossBuy/Controllers/AccountingController.cs");

            Assert.Matches(new Regex(
                @"if \(code == CrossBuy\.BL\.Platform\.EntityRegistry\.CrmTicket\)[\s\S]{0,400}?ICrmAccessService"),
                accounting);

            // And an unresolvable authority REFUSES rather than falling through to the module default.
            Assert.Contains("if (crm == null) return new ConversationGate();", accounting, StringComparison.Ordinal);
        }

        /// THE DEFERRED CANDIDATES ARE STILL DEFERRED, each for a reason recorded here rather than in
        /// somebody's head. Flipping one of these turns this red, which is the review gate.
        [Theory]
        [InlineData("CrmAccount", "host Crm/AccountEditor has no conversation endpoints; adding it to the Accounting whitelist is outside SHF-19")]
        [InlineData("ManufWorkOrder", "PermissionScope is Manufacturing but the only available gate asks IInventoryAccessService - the wrong authority")]
        [InlineData("Item", "SHF-26 covers Inventory DOCUMENTS; an item is master data and needs its own ruling")]
        [InlineData("ProjectBilling", "TAB-6 owns Views/Project/Billing.cshtml - coordination required")]
        [InlineData("ProjectMilestone", "the Milestones screen is keyed by the PROJECT id, so the route cannot address a milestone")]
        [InlineData("Employee", "HR visibility and company-safe resolution unconfirmed; the registry route points at the employee LIST")]
        [InlineData("Customer", "no stable record anchor decision for a statement conversation")]
        [InlineData("Supplier", "same as Customer, and the registry has no RouteTemplate although VendorStatement exists")]
        [InlineData("PosOrder", "excluded by owner decision")]
        [InlineData("Lead", "registered route is a list screen - no record-level host")]
        [InlineData("Opportunity", "registered route is a list screen - no record-level host")]
        [InlineData("InventoryApproval", "approval inbox - excluded by owner decision")]
        public void A_deferred_entity_code_still_carries_no_conversation(string code, string why)
        {
            var definition = Registry().SingleOrDefault(d => d.Code == code);
            Assert.True(definition is not null, $"{code} is no longer registered.");

            Assert.False(definition!.SupportsComments,
                $"{code} was given a conversation without the review it is waiting on: {why}");
        }

        /// EligibleForTaskLink IS NOT A LINKING PREREQUISITE and must not be flipped as though it were.
        /// It decides what a task may be ABOUT; LinkExistingAsync has no entity-type gate at all. The
        /// count is pinned so a wave that wires screens cannot widen anchors on the way past.
        [Fact]
        public void The_anchor_flag_is_unchanged_and_is_not_a_linking_prerequisite()
        {
            Assert.Equal(11, Registry().Count(d => d.EligibleForTaskLink));

            // Thirteen codes carry a wired conversation WITHOUT being anchor-eligible, which is only
            // possible because the two capabilities are genuinely independent.
            var wiredButNotAnchorable = Registry()
                .Where(d => d.SupportsComments && !d.EligibleForTaskLink)
                .Select(d => d.Code).ToList();
            Assert.Contains("StockCount", wiredButNotAnchorable);
            Assert.Contains("CrmTicket", wiredButNotAnchorable);
            Assert.Contains("Receipt", wiredButNotAnchorable);
        }

        /// EVERY OPTIONAL CAPABILITY FAILS CLOSED, and it fails closed WHERE THE CONTROL IS DRAWN -
        /// not only in the handler behind it. A guard that merely returns early still leaves a button
        /// on screen, and "hiding a UI control is not a control" cuts both ways: an offered control
        /// that cannot work teaches people the system is broken.
        [Fact]
        public void Every_optional_capability_fails_closed_when_the_host_omits_its_url()
        {
            var razor = Read(Partial);
            var js = Executable(razor);

            // The whole panel, when the host named neither of the two mandatory endpoints.
            Assert.Contains("@if (convWired)", razor, StringComparison.Ordinal);
            Assert.Contains("Conversations are not configured for this screen", razor, StringComparison.Ordinal);

            // Each optional capability, gated where the control is decided.
            Assert.Contains("state.canReact = !!data.canReact && !!reactUrl;", js, StringComparison.Ordinal);
            Assert.Contains("state.canCreateTask = !!createTaskUrl && data.canCreateTask !== false;", js, StringComparison.Ordinal);
            Assert.Contains("if (unlinkUrl && t.linkId > 0) {", js, StringComparison.Ordinal);
            Assert.Contains("return !!linkUrl && !!linkSearchUrl && state.linkingAvailable === true && state.threadId > 0;",
                js, StringComparison.Ordinal);

            // And each loader refuses to run at all, so an absent url paints nothing rather than an
            // empty section that reads as "there is nothing here".
            foreach (var guard in new[]
            {
                "if (!tasksUrl) { return; }",
                "if (!approvalsUrl) { return; }",
                "if (!reactUrl) { return; }",
            })
                Assert.Contains(guard, js, StringComparison.Ordinal);
        }

        // ==========================================================================================
        // THE AVAILABILITY HINT.
        //
        // The link controls hide themselves while the store is unprovisioned, which is right, and
        // until now they did it silently - leaving a "Related tasks" heading with no way to add one
        // and no reason given. The hint says why, through the SAME element that already explains the
        // missing-thread case, and the whole difficulty is in WHEN it is allowed to speak: three of
        // the four states it can be in must produce nothing at all.
        // ==========================================================================================

        /// The hint block as it actually runs, comments stripped - from the element lookup to the
        /// end of the `if (hint)` body. Scoped rather than file-wide, because `T.linkUnavailable`
        /// legitimately appears in linkError() too and a file-wide search would be satisfied by that
        /// forever, whatever this block came to say.
        private static string HintBlock()
        {
            var code = Executable(Read(Partial));
            var start = code.IndexOf("var hint = document.getElementById('cbConvLinkHint');",
                                     StringComparison.Ordinal);
            Assert.True(start >= 0, "the hint element is no longer looked up in syncLinkButton.");

            var end = code.IndexOf("\n        }", start, StringComparison.Ordinal);
            Assert.True(end > start, "the hint block is no longer a closed `if (hint)` body.");
            return code.Substring(start, end - start);
        }

        // ------------------------------------------------------------------------------------------
        // 1. SILENCE UNTIL THE SERVICE ITSELF ANSWERS NO.
        //
        // `state.linkingAvailable` is read with `=== true` everywhere, and that reading cannot tell
        // "the service said no" from "nobody has asked yet" - both are simply not-true. A hint
        // written on `!== true` or on `!state.linkingAvailable` therefore appears during the first
        // paint of EVERY healthy conversation and then disappears, which is how a provisioned
        // deployment ends up reported as a broken one. The second term is the entire guard.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void The_unavailable_hint_waits_for_an_answer_before_it_says_there_is_none()
        {
            var block = HintBlock();

            Assert.Contains("T.linkUnavailable", block, StringComparison.Ordinal);

            // Both halves, in one expression: an answer was received AND it was no.
            Assert.Matches(new Regex(
                @"state\.linkingAnswered === true &&\s*state\.linkingAvailable === false",
                RegexOptions.Singleline), block);

            // The spellings that fire before the answer arrives.
            Assert.DoesNotContain("linkingAvailable !== true", block, StringComparison.Ordinal);
            Assert.DoesNotContain("!state.linkingAvailable", block, StringComparison.Ordinal);

            // A HOST THAT NEVER ASKED FOR LINKING IS NOT MISSING IT, so the hint stays inside the
            // same wiring gate the button answers to rather than announcing an absent feature on
            // every screen that merely renders a conversation.
            Assert.Contains("!!linkUrl && !!linkSearchUrl", block, StringComparison.Ordinal);

            // And the missing-thread case is untouched: still the service's yes, still no thread.
            Assert.Contains("state.linkingAvailable === true && !(state.threadId > 0)", block,
                            StringComparison.Ordinal);
            Assert.Contains("T.linkNeedsThread", block, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------------------
        // 2. A PANEL THAT DID NOT LOAD EXPLAINS NOTHING.
        //
        // The catch is reached by a network failure, a refused payload and a 403 alike - and the 403
        // is a decision about THIS READER, not about the deployment. It already sets linkingAvailable
        // to false to hide the controls, which is correct and is exactly why the answer has to be
        // retracted in the same breath: without that, every denial reads as an unprovisioned store.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void A_task_panel_that_failed_or_was_refused_retracts_the_answer()
        {
            var js = Executable(Read(Partial));

            // Recorded in exactly one place, and retracted in exactly one place.
            Assert.Equal(1, Regex.Matches(js, @"state\.linkingAnswered = true;").Count);
            Assert.Equal(1, Regex.Matches(js, @"state\.linkingAnswered = false;").Count);

            // SCOPED TO loadTasks. Other loaders catch the same way, and the first catch in the file
            // belongs to the approvals panel - comparing against that one would compare against a
            // different function and pass or fail for reasons that have nothing to do with this.
            var fn = js.IndexOf("function loadTasks()", StringComparison.Ordinal);
            Assert.True(fn > 0, "loadTasks is no longer where the answer is recorded.");
            var body = js.Substring(fn);

            var caught = body.IndexOf(".catch(function (e) {", StringComparison.Ordinal);
            Assert.True(caught > 0, "the task panel no longer has a catch to retract the answer in.");

            // The answer is recorded in the success path and retracted in the failure path, not the
            // other way round - which the two counts above cannot tell apart on their own.
            Assert.True(body.IndexOf("state.linkingAnswered = true;", StringComparison.Ordinal) < caught,
                        "the answer is recorded outside the success path.");
            Assert.True(body.IndexOf("state.linkingAnswered = false;", caught, StringComparison.Ordinal) > caught,
                        "a failed load leaves the last answer standing.");

            // Pinned beside the denial branch it must not be confused with, in the same block.
            Assert.Contains("wasDenied ? T.tasksDenied : T.tasksFailed", js, StringComparison.Ordinal);

            // AND IT STARTS UNANSWERED. An initial `true` would make the first paint of a panel whose
            // request is still in flight claim the deployment cannot link tasks.
            Assert.Matches(new Regex(@"var state = \{[^;]*linkingAnswered: false", RegexOptions.Singleline), js);
        }

        // ------------------------------------------------------------------------------------------
        // 3. IT EXPLAINS; IT DOES NOT OFFER.
        //
        // The hint exists because the controls are hidden. If adding it also widened the gate - or
        // touched a control at all - it would have created the very thing the service refused.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void The_hint_reveals_no_control_and_widens_no_gate()
        {
            var js = Executable(Read(Partial));
            var block = HintBlock();

            // The gate is the one TAB-3 pinned, unchanged, and it does NOT consult linkingAnswered -
            // an answered-but-negative panel must not become a visible button.
            Assert.Contains(
                "return !!linkUrl && !!linkSearchUrl && state.linkingAvailable === true && state.threadId > 0;",
                js, StringComparison.Ordinal);
            var gate = Regex.Match(js, @"function canLink\(\) \{.*?\}", RegexOptions.Singleline).Value;
            Assert.DoesNotContain("linkingAnswered", gate, StringComparison.Ordinal);

            // The hint block writes its own element and nothing else.
            Assert.DoesNotContain("cbConvLinkTask", block, StringComparison.Ordinal);
            Assert.DoesNotContain("cb-msg-link", block, StringComparison.Ordinal);
            Assert.DoesNotContain("remove('d-none')", block, StringComparison.Ordinal);

            // Both controls still hide on the gate alone.
            Assert.Contains("btn.classList.toggle('d-none', !canLink());", js, StringComparison.Ordinal);
            Assert.Contains("els[i].classList.toggle('d-none', !on);", js, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------------------
        // 4. ONE ELEMENT, AND NO ENGLISH IN IT.
        //
        // A second hint element would be a second thing to keep in step, and the one that is right
        // less often wins the argument on screen. The text is server-localized and reaches the DOM as
        // text, never markup - a sentence built in the script is a sentence the Arabic build cannot
        // translate.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void The_hint_reuses_one_localized_element()
        {
            var source = Read(Partial);
            var block = HintBlock();

            Assert.Equal(1, Regex.Matches(source, @"id=""cbConvLinkHint""").Count);
            Assert.Equal(1, Regex.Matches(source, @"getElementById\('cbConvLinkHint'\)").Count);

            // textContent, not innerHTML.
            Assert.Contains("hint.textContent", block, StringComparison.Ordinal);
            Assert.DoesNotContain("hint.innerHTML", block, StringComparison.Ordinal);

            // NOTHING IS SPELLED OUT HERE. Every assigned value is a T.* lookup or the empty string,
            // so the only literals the block may contain are '' and the element id.
            foreach (Match literal in Regex.Matches(block, @"=\s*'([^']*)'"))
                Assert.True(literal.Groups[1].Value.Length == 0,
                            $"the hint assigns a hard-coded string: '{literal.Groups[1].Value}'");

            // The key is declared from the resource table, not from a literal in the T map.
            Assert.Matches(new Regex(
                @"linkUnavailable: @Html\.Raw\(Json\.Serialize\(Localizer\[""Task linking is not available in this environment""\]"),
                source);

            // AND THE ARABIC BUILD HAS SOMETHING TO SAY. A key with no translation renders the
            // English fallback, which is the same defect as writing English here in the first place.
            var ar = Read("CrossBuy/Resources/Views/Shared/_EntityConversation.ar.resx");
            foreach (var key in new[]
            {
                "Task linking is not available in this environment",
                "Write the first message before linking a task",
            })
            {
                var entry = Regex.Match(ar,
                    @"<data name=""" + Regex.Escape(key) + @"""[^>]*>\s*<value>(.*?)</value>",
                    RegexOptions.Singleline);
                Assert.True(entry.Success, $"'{key}' has no Arabic entry.");
                Assert.Matches(new Regex(@"\p{IsArabic}"), entry.Groups[1].Value);
            }
        }
    }
}
