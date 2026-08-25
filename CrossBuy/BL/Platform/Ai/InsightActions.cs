namespace CrossBuy.BL.Platform.Ai
{
    // INSIGHT → ACTION. ONE contract for turning any insight finding into a proposed Task.
    //
    // WHY ONE AND NOT THREE. Inventory Risk, Opportunity Insights and Account Health all want the same
    // thing: "let me act on this". Three copies of that flow would be three places to get company
    // isolation, permission revalidation and dedup subtly different — and the third copy is always the
    // one that forgets. Everything below is a SUGGESTION only; the write itself goes through
    // ITaskService, which is the operational system of record.
    //
    // THIS FILE PERSISTS NOTHING AND DECIDES NOTHING ABOUT AUTHORITY. It is a pure function from a
    // finding to a proposed task, so it can be tested exhaustively without a database. The controller
    // revalidates every field it produces, because a suggestion that arrives back from a browser is just
    // an untrusted string.
    public static class InsightActions
    {
        /// <summary>Which screen a finding came from. Frozen vocabulary — the server maps it to a table.</summary>
        public static class Sources
        {
            public const string InventoryItem = "InventoryItem";
            public const string CrmOpportunity = "CrmOpportunity";
            public const string CrmAccount = "CrmAccount";

            public static readonly IReadOnlyList<string> All = new[] { InventoryItem, CrmOpportunity, CrmAccount };
            public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
        }

        /// <summary>An action a finding may offer. NOT every finding offers every action — see <see cref="For"/>.</summary>
        public enum ActionKind
        {
            /// The primary operational act: raise a normal Task through the normal Task service.
            CreateFollowUpTask,

            OpenItem,
            OpenStockMovements,
            OpenOpportunity,
            OpenAccount,
            OpenOpportunities,
            OpenActivities,

            /// Navigate into the existing procurement entry point. Replenishment only.
            OpenReplenishment,
        }

        /// <summary>What a proposed task should say, before the user reviews it.</summary>
        public sealed record TaskProposal
        {
            public required string TitleKey { get; init; }
            public required string ReasonKey { get; init; }

            /// Days from today for the suggested due date. Urgency follows the finding, not a constant.
            public required int DueInDays { get; init; }

            /// One of TaskService's own priorities: Low | Normal | High | Urgent.
            public required string Priority { get; init; }
        }

        // ---------------------------------------------------------------------------------------
        // WHICH ACTIONS A FINDING OFFERS.
        //
        // THE RULE THAT MATTERS: an action is offered because the finding calls for it, never because
        // the action exists. Dead stock must not offer "Replenish" — the item is sitting there BECAUSE
        // nobody wants it, and suggesting a purchase would be actively harmful advice dressed up as a
        // recommendation. A shortage offers replenishment; slow stock offers a review.
        // ---------------------------------------------------------------------------------------
        public static IReadOnlyList<ActionKind> For(string source, string findingCode)
        {
            if (!Sources.IsKnown(source)) return Array.Empty<ActionKind>();

            return source switch
            {
                Sources.InventoryItem => findingCode switch
                {
                    // A real shortage: the buyer needs the procurement path.
                    "stockout" or "reorder" => new[]
                    {
                        ActionKind.OpenReplenishment,
                        ActionKind.CreateFollowUpTask,
                        ActionKind.OpenItem,
                        ActionKind.OpenStockMovements,
                    },

                    // Slow/dead stock. NO replenishment — see the note above.
                    "slow" => new[]
                    {
                        ActionKind.CreateFollowUpTask,
                        ActionKind.OpenItem,
                        ActionKind.OpenStockMovements,
                    },

                    _ => new[] { ActionKind.OpenItem, ActionKind.OpenStockMovements },
                },

                Sources.CrmOpportunity => new[]
                {
                    ActionKind.CreateFollowUpTask,
                    ActionKind.OpenOpportunity,
                    ActionKind.OpenAccount,
                    ActionKind.OpenActivities,
                },

                Sources.CrmAccount => new[]
                {
                    ActionKind.CreateFollowUpTask,
                    ActionKind.OpenAccount,
                    ActionKind.OpenOpportunities,
                    ActionKind.OpenActivities,
                },

                _ => Array.Empty<ActionKind>(),
            };
        }

        /// <summary>True when this finding may raise a follow-up task at all.</summary>
        public static bool AllowsTask(string source, string findingCode) =>
            For(source, findingCode).Contains(ActionKind.CreateFollowUpTask);

        // ---------------------------------------------------------------------------------------
        // WHAT THE PROPOSED TASK SAYS.
        //
        // Returns RESOURCE KEYS, not sentences. The rules layer states no prose in any language — the
        // view renders it, so Arabic is a first-class translation rather than a copy of the English.
        //
        // Urgency follows the finding. An item that is out of stock with live demand is losing sales
        // today; a slow-moving item has been sitting there for months and a week's delay costs nothing.
        // A single "due in 7 days" for everything would make the due date meaningless.
        // ---------------------------------------------------------------------------------------
        public static TaskProposal? Propose(string source, string findingCode)
        {
            if (!AllowsTask(source, findingCode)) return null;

            return (source, findingCode) switch
            {
                (Sources.InventoryItem, "stockout") => new TaskProposal
                {
                    TitleKey = "task.title.stockout",
                    ReasonKey = "task.reason.stockout",
                    DueInDays = 1,
                    Priority = "Urgent",
                },
                (Sources.InventoryItem, "reorder") => new TaskProposal
                {
                    TitleKey = "task.title.reorder",
                    ReasonKey = "task.reason.reorder",
                    DueInDays = 3,
                    Priority = "High",
                },
                (Sources.InventoryItem, "slow") => new TaskProposal
                {
                    TitleKey = "task.title.slowstock",
                    ReasonKey = "task.reason.slowstock",
                    DueInDays = 14,
                    Priority = "Normal",
                },

                (Sources.CrmOpportunity, "PastExpectedClose") => new TaskProposal
                {
                    TitleKey = "task.title.oppOverdue",
                    ReasonKey = "task.reason.oppOverdue",
                    DueInDays = 2,
                    Priority = "High",
                },
                (Sources.CrmOpportunity, "ClosingSoon") => new TaskProposal
                {
                    TitleKey = "task.title.oppClosingSoon",
                    ReasonKey = "task.reason.oppClosingSoon",
                    DueInDays = 2,
                    Priority = "High",
                },
                (Sources.CrmOpportunity, _) => new TaskProposal
                {
                    TitleKey = "task.title.oppFollowUp",
                    ReasonKey = "task.reason.oppFollowUp",
                    DueInDays = 5,
                    Priority = "Normal",
                },

                (Sources.CrmAccount, "DecliningActivity") => new TaskProposal
                {
                    TitleKey = "task.title.acctDeclining",
                    ReasonKey = "task.reason.acctDeclining",
                    DueInDays = 5,
                    Priority = "High",
                },
                (Sources.CrmAccount, "OverdueOpportunityExposure") => new TaskProposal
                {
                    TitleKey = "task.title.acctOverdue",
                    ReasonKey = "task.reason.acctOverdue",
                    DueInDays = 3,
                    Priority = "High",
                },
                (Sources.CrmAccount, _) => new TaskProposal
                {
                    TitleKey = "task.title.acctFollowUp",
                    ReasonKey = "task.reason.acctFollowUp",
                    DueInDays = 7,
                    Priority = "Normal",
                },

                _ => null,
            };
        }

        // ---------------------------------------------------------------------------------------
        // TRACEABILITY — why does this task exist?
        //
        // TaskItem ALREADY carries EntityType/EntityId, so no schema change was needed and none was
        // made. But the frozen EntityRegistry vocabulary has no code for a CRM opportunity or account,
        // and TaskLinkResolver returns null for a code it does not know — so writing an invented code
        // would store a link that never resolves.
        //
        // Therefore: link structurally where the registry supports it (Item), and always record the
        // origin as a readable stamp in the description. The stamp is greppable, survives any future
        // registry change, and is the thing a human actually reads on the task.
        public const string OriginStamp = "[insight]";

        /// <summary>The registry entity code to store, or null when the registry has none for this source.</summary>
        /// <remarks>
        /// Returns "Item" for inventory, which is registered and routed today, so those tasks get real
        /// back-navigation immediately. CRM sources return null rather than an invented code: a link
        /// that silently resolves to nothing is worse than no link, because it looks like one.
        /// </remarks>
        public static string? RegistryEntityCode(string source) => source switch
        {
            Sources.InventoryItem => "Item",
            _ => null,
        };

        /// <summary>A stable, greppable origin line appended to the task description.</summary>
        public static string OriginLine(string source, int entityId, string findingCode) =>
            $"{OriginStamp} {source}#{entityId} finding={findingCode}";

        // ---------------------------------------------------------------------------------------
        // DUPLICATE PROTECTION.
        //
        // The identity is (source, entity, FINDING) — never the entity alone. An account that goes quiet
        // again next quarter genuinely needs a second task, and an identity of "account 42 forever"
        // would block that permanently. Including the finding means a NEW KIND of problem always raises
        // a new task, while a double-click on the same problem does not.
        //
        // It is also bounded by the open task: once the follow-up is Done, the same finding may raise
        // another one, because the situation has recurred rather than persisted.
        // ---------------------------------------------------------------------------------------
        public static string DedupKey(string source, int entityId, string findingCode) =>
            OriginLine(source, entityId, findingCode);
    }
}
