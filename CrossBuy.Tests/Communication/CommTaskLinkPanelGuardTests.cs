using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =================================================================================================
    // TASK LINKING — THE PANEL'S VISIBILITY CONTRACT, HELD AGAINST SILENT REMOVAL.
    //
    // WHY THIS FILE EXISTS
    //
    // On 2026-10-27 the task-link action was missing from every eligible record screen. Nothing had
    // been deleted and nothing had been denied: TaskLinksProvisioned was unset, LinkingAvailable came
    // back false, and the controls kept the `d-none` they are rendered with. The panel, the messages
    // and the related-task list all drew normally, so the only symptom was an absence — and no test
    // failed, because the gate was behaving exactly as written.
    //
    // CommunicationDiWiringTests now holds the CONFIGURATION half (the https profile must declare the
    // flag; profiles on an unprovisioned database must not). This file holds the UI half: the controls
    // must still exist, they must stay gated on the service's answer, and — the part nothing covered —
    // the per-message control must answer to the SAME gate as the heading button.
    //
    // IT READS, IT DOES NOT EDIT. _EntityConversation.cshtml is TAB-1-owned. These are assertions
    // about its contract, so a TAB-1 change that breaks the contract fails here instead of silently
    // removing a feature from nine screens.
    //
    // THE ASSERTIONS RUN AGAINST THE EXECUTABLE RAZOR, comments stripped. The partial documents this
    // gate at length in prose, and a naive text search would be satisfied by the commentary forever —
    // which is precisely how a grep-shaped guard fails open.
    // =================================================================================================
    public class CommTaskLinkPanelGuardTests
    {
        private const string Partial = "CrossBuy/Views/Shared/_EntityConversation.cshtml";

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)
                && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
            {
                return fromEnvironment;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // Razor carries two comment forms and the partial uses both: @* *@ around markup, and // inside
        // the script block. Strip both, so what remains is only what actually runs.
        private static string Executable()
        {
            var source = File.ReadAllText(
                Path.Combine(RepoRoot(), Partial.Replace('/', Path.DirectorySeparatorChar)));
            var withoutRazor = Regex.Replace(source, @"@\*.*?\*@", "", RegexOptions.Singleline);
            return string.Join("\n", withoutRazor.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        // -------------------------------------------------------------------------------------------
        // 1. BOTH CONTROLS STILL EXIST.
        //
        // The cheapest regression to ship is deletion. Each of the two link affordances is named here
        // by the identifier the rest of the panel addresses it by, so removing either one fails loudly
        // rather than reducing the feature by half.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void The_panel_retains_a_conversation_level_and_a_message_level_link_control()
        {
            var code = Executable();

            Assert.Contains("id=\"cbConvLinkTask\"", code, StringComparison.Ordinal);
            Assert.Contains("cb-msg-link", code, StringComparison.Ordinal);

            // And the picker both of them open, plus the quoted target that is the ONLY thing
            // distinguishing a message-level link from a conversation-level one on screen.
            Assert.Contains("id=\"cbConvLinkPicker\"", code, StringComparison.Ordinal);
            Assert.Contains("id=\"cbConvLinkTarget\"", code, StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------------------------------
        // 2. THE GATE ITSELF — all four conditions, spelled out.
        //
        // canLink() is the single decision both controls obey. Pinned as one expression because each
        // clause removes a different wrong state: no urls means the host did not opt in, no service
        // answer means the store is unprovisioned, no thread means there is nothing to attach to.
        //
        // `=== true` matters on its own. An older endpoint that omits the field sends undefined, and
        // a truthiness test would read that as "available" and offer a button certain to be refused.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void Linking_is_offered_only_when_the_urls_the_service_and_a_thread_all_agree()
        {
            var code = Executable();

            Assert.Matches(new Regex(
                @"function canLink\(\)\s*\{\s*return !!linkUrl && !!linkSearchUrl && " +
                @"state\.linkingAvailable === true && state\.threadId > 0;"), code);

            // The service's answer is taken verbatim, and absence means false.
            Assert.Contains("state.linkingAvailable = d.linkingAvailable === true;", code,
                StringComparison.Ordinal);

            // A failed load must CLEAR it rather than leave the last good answer standing, or a
            // refused reload would keep offering a control the server has stopped backing.
            Assert.Contains("state.linkingAvailable = false;", code, StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------------------------------
        // 3. THE GAP THIS FILE WAS WRITTEN FOR: the per-message control shares the gate.
        //
        // The heading button and the per-message buttons live in different parts of the DOM and are
        // painted by different code. Nothing asserted that they agree. Had syncLinkButton stopped
        // calling syncMessageLinkButtons — an easy casualty of an early return added above it — the
        // per-message controls would have frozen in whatever state they were last left: permanently
        // hidden on a provisioned deployment, or permanently VISIBLE on an unprovisioned one, where
        // every press returns 503.
        //
        // The call is asserted to come BEFORE the heading button's own early return, because a panel
        // whose heading button is absent from the DOM must still sync the message-level controls.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void The_per_message_control_answers_to_the_same_gate_as_the_heading_button()
        {
            var code = Executable();

            // syncLinkButton delegates FIRST — before `var btn = ...; if (!btn) { return; }`.
            Assert.Matches(new Regex(
                @"function syncLinkButton\(\)\s*\{\s*syncMessageLinkButtons\(\);"), code);

            // And the delegate decides with canLink(), not with a second opinion of its own.
            Assert.Matches(new Regex(
                @"function syncMessageLinkButtons\(\)[\s\S]{0,400}?var on = canLink\(\);"), code);
            Assert.Matches(new Regex(
                @"function syncMessageLinkButtons\(\)[\s\S]{0,400}?querySelectorAll\('\.cb-msg-link'\)"),
                code);
            Assert.Matches(new Regex(
                @"function syncMessageLinkButtons\(\)[\s\S]{0,400}?classList\.toggle\('d-none', !on\);"),
                code);
        }

        // -------------------------------------------------------------------------------------------
        // 4. NEITHER CONTROL MAY BE FORCED VISIBLE.
        //
        // The tempting fix for an invisible button is to stop hiding it. That would turn a silent
        // absence into a control that is certain to be refused — worse, because the refusal arrives
        // after the user has chosen a task. Both toggles are pinned to their gate expression, so
        // relaxing either to a constant fails here.
        //
        // The markup's own starting state is pinned too: the button ships hidden and is revealed only
        // by the gate, so a panel whose script never runs offers nothing rather than everything.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void Neither_link_control_is_revealed_except_through_the_gate()
        {
            var code = Executable();

            Assert.Contains("btn.classList.toggle('d-none', !canLink());", code, StringComparison.Ordinal);
            Assert.Contains("els[i].classList.toggle('d-none', !on);", code, StringComparison.Ordinal);

            // Rendered hidden. The class sits on the button element itself, not on an ancestor.
            Assert.Matches(new Regex(
                @"<button[^>]*class=""[^""]*\bd-none\b[^""]*""[^>]*id=""cbConvLinkTask"""), code);

            // Nothing unhides the button directly. `remove('d-none')` is legitimate elsewhere in this
            // panel, so the check is scoped to the VARIABLE the button was read into, inside the block
            // that read it. A first draft matched any removal within 300 characters of the lookup and
            // fired on `panel.classList.remove('d-none')` — which reveals the tasks heading and is
            // correct. Matching the element rather than the neighbourhood is the difference between a
            // guard and a tripwire.
            var lookups = Regex.Matches(code, @"var (\w+) = document\.getElementById\('cbConvLinkTask'\);");
            Assert.NotEmpty(lookups);
            foreach (Match lookup in lookups)
            {
                var name = lookup.Groups[1].Value;
                var block = code.Substring(lookup.Index, Math.Min(600, code.Length - lookup.Index));
                Assert.DoesNotMatch(
                    new Regex(Regex.Escape(name) + @"\.classList\.remove\('d-none'\)"), block);
            }
        }

        // -------------------------------------------------------------------------------------------
        // 5. THE REFUSAL VOCABULARY SURVIVES.
        //
        // 503 is the provisioning answer and it must never be translated into a generic failure the
        // user could retry away. Pinned beside 403, because "you may not" and "this deployment cannot"
        // are different answers and collapsing them is how an outage gets reported as a permission
        // problem for a week.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void The_unavailable_answer_keeps_its_own_message_distinct_from_denial()
        {
            var code = Executable();

            Assert.Matches(new Regex(@"if \(status === 503\) \{ return T\.linkUnavailable; \}"), code);
            Assert.Matches(new Regex(@"if \(status === 403\) \{ return T\.linkDenied; \}"), code);
        }
    }
}
