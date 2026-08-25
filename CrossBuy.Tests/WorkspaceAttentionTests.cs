using CrossBuy.BL.Approvals;
using CrossBuy.BL.Workspace;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WORKSPACE — "WHAT REQUIRES MY ATTENTION?"  (TAB 6)
    //
    // The panel is a COMPOSITION over panels that have already loaded — My Work, Approvals, Mentions —
    // and it adds exactly one thing they cannot say separately: the order. So these tests are about the
    // ORDER and about what the order refuses to do, not about where the rows came from.
    //
    // THE RULE UNDER TEST, in one line:
    //
    //     overdue task  >  approval waiting  >  due today  >  urgent task  >  mention
    //
    // Reason is ALWAYS the first key. Age is the second and can never overtake a reason. Identity is the
    // third, so the same data always produces the same order — which is what lets a screenshot baseline
    // over this card mean anything at all.
    //
    // NOT TESTED HERE, deliberately: who may SEE a row. Company scope, employee scope and permission all
    // happen upstream in the panels this composes from, and are covered by their own tests. A second copy
    // of those assertions here would be a copy that drifts.
    // ============================================================================================
    public class WorkspaceAttentionTests
    {
        private static readonly DateTime Now = new(2026, 5, 20, 9, 0, 0);
        private static DateTime Today => Now.Date;

        // ----------------------------------------------------------------------------------------
        // 1 · REASON IS THE FIRST KEY
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void An_overdue_task_outranks_a_task_that_is_merely_due_today()
        {
            var panel = Compose(
                work: Work(
                    Task_(1, "due today", due: Today),
                    Task_(2, "three days late", due: Today.AddDays(-3))));

            Assert.Equal("three days late", panel.Items[0].Title);
            Assert.Equal(WorkspaceAttentionReason.OverdueTask, panel.Items[0].Reason);
            Assert.Equal(1, panel.Items[0].Rank);
            Assert.Equal(WorkspaceAttentionReason.DueToday, panel.Items[1].Reason);
        }

        // An open task that is on time and not urgent is WORK, not ATTENTION. Letting it in would turn
        // this card into a second My Work list and make the ranking meaningless.
        [Fact]
        public void An_ordinary_open_task_is_not_attention_at_all()
        {
            var panel = Compose(work: Work(Task_(1, "next week", due: Today.AddDays(7))));

            Assert.Equal(WorkspacePanelState.Empty, panel.State);
            Assert.Empty(panel.Items);
        }

        [Fact]
        public void An_urgent_task_is_attention_even_before_it_is_due()
        {
            var panel = Compose(work: Work(Task_(1, "urgent, not yet due", due: Today.AddDays(5), priority: "Urgent")));

            var row = Assert.Single(panel.Items);
            Assert.Equal(WorkspaceAttentionReason.UrgentTask, row.Reason);
        }

        [Fact]
        public void An_approval_waiting_on_me_is_included_and_outranks_due_today()
        {
            var panel = Compose(
                work: Work(Task_(1, "due today", due: Today)),
                approvals: Approvals(Approval("A-1", "Leave request", ageDays: 1)));

            Assert.Equal(WorkspaceAttentionReason.ApprovalWaiting, panel.Items[0].Reason);
            Assert.Equal("Leave request", panel.Items[0].Title);
            Assert.Equal(WorkspaceAttentionReason.DueToday, panel.Items[1].Reason);
        }

        [Fact]
        public void A_mention_is_included_and_ranks_last()
        {
            var panel = Compose(
                work: Work(Task_(1, "urgent", due: Today.AddDays(9), priority: "Urgent")),
                mentions: Mentions(Mention(7, "please review", at: Today)));

            Assert.Equal(WorkspaceAttentionReason.UrgentTask, panel.Items[0].Reason);
            Assert.Equal(WorkspaceAttentionReason.Mention, panel.Items[1].Reason);
            Assert.Equal("please review", panel.Items[1].Title);
        }

        // The whole ladder at once. Each test above would also pass on a panel that could only rank its
        // own pair; this one pins the ladder itself.
        [Fact]
        public void The_full_ladder_ranks_overdue_then_approval_then_today_then_urgent_then_mention()
        {
            var panel = Compose(
                work: Work(
                    Task_(1, "urgent", due: Today.AddDays(6), priority: "Urgent"),
                    Task_(2, "today", due: Today),
                    Task_(3, "late", due: Today.AddDays(-1))),
                approvals: Approvals(Approval("A-1", "approval", ageDays: 0)),
                mentions: Mentions(Mention(9, "mention", at: Today)));

            Assert.Equal(new[]
            {
                WorkspaceAttentionReason.OverdueTask,
                WorkspaceAttentionReason.ApprovalWaiting,
                WorkspaceAttentionReason.DueToday,
                WorkspaceAttentionReason.UrgentTask,
                WorkspaceAttentionReason.Mention,
            }, panel.Items.Select(i => i.Reason).ToArray());

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, panel.Items.Select(i => i.Rank).ToArray());
        }

        // AGE MUST NOT OVERTAKE A REASON. A month-old approval is still less urgent than a commitment
        // missed yesterday — that is the business rule, and it is the one a weighted score would break.
        [Fact]
        public void A_long_waiting_approval_never_overtakes_a_freshly_overdue_task()
        {
            var panel = Compose(
                work: Work(Task_(1, "one day late", due: Today.AddDays(-1))),
                approvals: Approvals(Approval("A-1", "waiting 40 days", ageDays: 40)));

            Assert.Equal(WorkspaceAttentionReason.OverdueTask, panel.Items[0].Reason);
            Assert.Equal(WorkspaceAttentionReason.ApprovalWaiting, panel.Items[1].Reason);
        }

        // ----------------------------------------------------------------------------------------
        // 2 · AGE IS THE SECOND KEY, IDENTITY THE THIRD
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void Within_a_reason_the_older_item_ranks_first()
        {
            var panel = Compose(work: Work(
                Task_(1, "two days late", due: Today.AddDays(-2)),
                Task_(2, "nine days late", due: Today.AddDays(-9)),
                Task_(3, "one day late", due: Today.AddDays(-1))));

            Assert.Equal(new[] { "nine days late", "two days late", "one day late" },
                panel.Items.Select(i => i.Title).ToArray());
            Assert.Equal(new int?[] { 9, 2, 1 }, panel.Items.Select(i => i.AgeDays).ToArray());
        }

        // Same reason, same age: without a third key the order would be whatever the source happened to
        // yield, and two renders of identical data could disagree.
        [Fact]
        public void Ties_are_broken_deterministically_by_identity()
        {
            var a = Compose(work: Work(
                Task_(3, "c", due: Today.AddDays(-2)),
                Task_(1, "a", due: Today.AddDays(-2)),
                Task_(2, "b", due: Today.AddDays(-2))));

            var b = Compose(work: Work(
                Task_(2, "b", due: Today.AddDays(-2)),
                Task_(3, "c", due: Today.AddDays(-2)),
                Task_(1, "a", due: Today.AddDays(-2))));

            Assert.Equal(a.Items.Select(i => i.Title), b.Items.Select(i => i.Title));
            Assert.Equal(new[] { "a", "b", "c" }, a.Items.Select(i => i.Title).ToArray());
        }

        // ----------------------------------------------------------------------------------------
        // 3 · ONE ROW PER THING
        // ----------------------------------------------------------------------------------------

        // A task can be overdue AND flagged Urgent. It is one commitment, so it gets one row — under the
        // stronger reason. Two rows would spend two of the eight slots telling the reader the same thing.
        [Fact]
        public void A_task_that_is_both_overdue_and_urgent_appears_once_under_the_stronger_reason()
        {
            var panel = Compose(work: Work(
                Task_(1, "late and urgent", due: Today.AddDays(-4), priority: "Urgent")));

            var row = Assert.Single(panel.Items);
            Assert.Equal(WorkspaceAttentionReason.OverdueTask, row.Reason);
            Assert.Equal("Urgent", row.Priority);
        }

        [Fact]
        public void The_panel_is_capped_but_reports_the_true_total()
        {
            var many = Enumerable.Range(1, 20)
                .Select(i => Task_(i, $"late {i}", due: Today.AddDays(-i)))
                .ToArray();

            var panel = Compose(work: Work(many));

            Assert.Equal(8, panel.Items.Count);
            Assert.Equal(20, panel.Total);
            Assert.Equal(Enumerable.Range(1, 8), panel.Items.Select(i => i.Rank));
        }

        // ----------------------------------------------------------------------------------------
        // 4 · NAVIGATION COMES FROM THE PANEL THE ROW CAME FROM
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void Each_row_keeps_the_navigation_target_its_source_panel_supplied()
        {
            var panel = Compose(
                work: Work(Task_(1, "late", due: Today.AddDays(-1), url: "/Tasks/Index?taskId=1")),
                approvals: Approvals(Approval("A-1", "approval", ageDays: 0, url: "/People/Leaves")),
                mentions: Mentions(Mention(5, "mention", at: Today, url: "/Comm/Thread/5")));

            Assert.Equal("/Tasks/Index?taskId=1", panel.Items[0].Url);
            Assert.Equal("/People/Leaves", panel.Items[1].Url);
            Assert.Equal("/Comm/Thread/5", panel.Items[2].Url);
        }

        // ----------------------------------------------------------------------------------------
        // 5 · STATES — a failure must never read as calm
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void Nothing_pending_anywhere_is_an_empty_panel()
        {
            var panel = Compose();

            Assert.Equal(WorkspacePanelState.Empty, panel.State);
        }

        // THE RULE THIS PINS. If Tasks could not be read, "nothing needs your attention" is a lie — and
        // it is the most dangerous lie this particular card can tell.
        [Fact]
        public void A_failing_source_is_a_temporary_failure_not_a_quiet_morning()
        {
            var panel = WorkspaceService.ComposeAttention(
                WorkspacePanel<WorkspaceWorkItem>.TemporaryFailure("Tasks unreachable"),
                WorkspacePanel<WorkspaceApprovalItem>.Empty(),
                WorkspacePanel<WorkspaceMention>.Empty(),
                Now);

            Assert.Equal(WorkspacePanelState.TemporaryFailure, panel.State);
            Assert.False(string.IsNullOrWhiteSpace(panel.Reason));
        }

        [Fact]
        public void Every_source_being_unavailable_is_unavailable_not_empty()
        {
            var panel = WorkspaceService.ComposeAttention(
                WorkspacePanel<WorkspaceWorkItem>.Unavailable("no tasks module"),
                WorkspacePanel<WorkspaceApprovalItem>.Unavailable("no approvals"),
                WorkspacePanel<WorkspaceMention>.Unavailable("communication dormant"),
                Now);

            Assert.Equal(WorkspacePanelState.Unavailable, panel.State);
        }

        // A dormant Communication platform must not drag the card down: the other sources answered, so the
        // card answers. Mentions states its own dormancy on its own panel.
        [Fact]
        public void A_dormant_mention_source_does_not_block_the_card()
        {
            var panel = WorkspaceService.ComposeAttention(
                Work(Task_(1, "late", due: Today.AddDays(-1))),
                WorkspacePanel<WorkspaceApprovalItem>.Empty(),
                WorkspacePanel<WorkspaceMention>.Unavailable("communication dormant"),
                Now);

            Assert.Equal(WorkspacePanelState.Ready, panel.State);
            Assert.Single(panel.Items);
        }

        // Rows still present WITH a failing source: the reader gets the real rows, and the card does not
        // pretend the list is complete by staying silent — it is still Ready with what it has.
        [Fact]
        public void Rows_survive_when_only_one_of_several_sources_failed()
        {
            var panel = WorkspaceService.ComposeAttention(
                Work(Task_(1, "late", due: Today.AddDays(-2))),
                WorkspacePanel<WorkspaceApprovalItem>.TemporaryFailure("approvals unreachable"),
                WorkspacePanel<WorkspaceMention>.Empty(),
                Now);

            Assert.Equal(WorkspacePanelState.Ready, panel.State);
            Assert.Single(panel.Items);
        }

        // ----------------------------------------------------------------------------------------
        // 6 · BILINGUAL, AND NO MUTATION PATH
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void Every_row_carries_both_an_arabic_and_an_english_reason_and_source()
        {
            var panel = Compose(
                work: Work(Task_(1, "late", due: Today.AddDays(-1))),
                approvals: Approvals(Approval("A-1", "approval", ageDays: 0)),
                mentions: Mentions(Mention(2, "mention", at: Today)));

            Assert.All(panel.Items, row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(row.ReasonLabel(arabic: true)));
                Assert.False(string.IsNullOrWhiteSpace(row.ReasonLabel(arabic: false)));
                Assert.False(string.IsNullOrWhiteSpace(row.Source(arabic: true)));
                Assert.False(string.IsNullOrWhiteSpace(row.Source(arabic: false)));
                Assert.NotEqual(row.ReasonLabel(true), row.ReasonLabel(false));
            });
        }

        // The composition is a pure function over already-loaded panels: no service, no context, no
        // connection. That is the structural guarantee that it added no data silo and no write path.
        [Fact]
        public void The_composition_is_a_pure_function_over_loaded_panels()
        {
            var method = typeof(WorkspaceService).GetMethod("ComposeAttention",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            Assert.NotNull(method);
            Assert.True(method!.IsStatic);

            var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.Equal(new[]
            {
                typeof(WorkspacePanel<WorkspaceWorkItem>),
                typeof(WorkspacePanel<WorkspaceApprovalItem>),
                typeof(WorkspacePanel<WorkspaceMention>),
                typeof(DateTime),
            }, parameterTypes);
        }

        // ========================================================================================
        // harness — plain panels in, panel out. No container, no context, no clock.
        // ========================================================================================

        private static WorkspacePanel<WorkspaceAttentionItem> Compose(
            WorkspacePanel<WorkspaceWorkItem>? work = null,
            WorkspacePanel<WorkspaceApprovalItem>? approvals = null,
            WorkspacePanel<WorkspaceMention>? mentions = null) =>
            WorkspaceService.ComposeAttention(
                work ?? WorkspacePanel<WorkspaceWorkItem>.Empty(),
                approvals ?? WorkspacePanel<WorkspaceApprovalItem>.Empty(),
                mentions ?? WorkspacePanel<WorkspaceMention>.Empty(),
                Now);

        private static WorkspacePanel<WorkspaceWorkItem> Work(params WorkspaceWorkItem[] items) =>
            WorkspacePanel<WorkspaceWorkItem>.From(items);

        private static WorkspacePanel<WorkspaceApprovalItem> Approvals(params WorkspaceApprovalItem[] items) =>
            WorkspacePanel<WorkspaceApprovalItem>.From(items);

        private static WorkspacePanel<WorkspaceMention> Mentions(params WorkspaceMention[] items) =>
            WorkspacePanel<WorkspaceMention>.From(items);

        private static WorkspaceWorkItem Task_(int id, string title, DateTime? due = null,
            string priority = "Normal", string? url = null) =>
            new()
            {
                Id = id,
                Title = title,
                Status = "New",
                Priority = priority,
                Due = due,
                Url = url ?? $"/Tasks/Index?taskId={id}",
            };

        private static WorkspaceApprovalItem Approval(string reference, string title, int ageDays,
            string? url = null) =>
            new()
            {
                Reference = reference,
                Silo = "Leave",
                ApprovalType = "Leave",
                Title = title,
                Requester = "someone",
                Submitted = Today.AddDays(-ageDays),
                AgeDays = ageDays,
                Url = url ?? "/People/Leaves",
            };

        private static WorkspaceMention Mention(long id, string excerpt, DateTime at, string? url = null) =>
            new()
            {
                MentionId = id,
                EntityLabel = "Thread",
                Excerpt = excerpt,
                MentionedBy = "someone",
                At = at,
                Url = url,
            };
    }
}
