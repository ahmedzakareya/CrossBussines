using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // INVENTORY APPROVAL -> PLATFORM WORK. One rule, four document families, three facts.
    //
    // WHY ONE RULE AND NOT FOUR. A purchase order, a transfer, a count and a write-off above the
    // approval threshold all become the same InventoryApproval row and travel the same state machine.
    // DocType selects which held payload is replayed when the decision is Approved; nothing else about
    // the lifecycle differs. Four rules would be four copies of one decision, and the day the lifecycle
    // changed, three of them would be wrong. The family reaches the title from the payload.
    //
    // THREE FACTS, TWO OUTCOMES:
    //
    //   Requested  ACTION_REQUIRED. An approval row is durably Pending, which is the domain's way of
    //              saying a person owes a decision. Nothing else tracks that they have not made it.
    //
    //   Approved   TASK_COMPLETION. The reviewer decided, and the held document now exists.
    //
    //   Rejected   TASK_COMPLETION TOO, and this is the part worth stating plainly: the BUSINESS
    //              outcomes are opposites, but the WORK is the same work and it is equally finished.
    //              The reviewer was asked to decide and they decided. Mapping it to a failure would
    //              record that the reviewer failed; leaving the task open would leave a queue entry
    //              nobody can ever action, because the state machine refuses a second decision.
    //              There is no Cancelled state in the task lifecycle and this rule does not invent one.
    //
    // THE ASSIGNEE IS NOBODY, AND THAT IS THE ANSWER RATHER THAN A GAP. Inventory approval authority is
    // a ROLE right - whoever holds InventoryManager in the company, minus the requester - so no single
    // employee can be named when the work is created. Not the first manager, not a hierarchy walk, not
    // a company admin, not employee 1. The platform learned who may act on it a different way: the
    // source visibility provider asks the Inventory service itself, per request, so the set stays live
    // as roles are granted and revoked. Naming somebody here would freeze a stale answer into a row.
    //
    // THIS RULE ASKS NO AUTHORIZATION QUESTION AT ALL. It reads a committed envelope and returns an
    // intent. It does not know what InventoryManager is, never touches InventoryUserRoles, and holds no
    // opinion about who the requester is. A second copy of that rule here is exactly how two surfaces
    // start disagreeing, and the one that disagrees by showing MORE is a disclosure.
    // =============================================================================================
    public sealed class InventoryApprovalRule : ITaskOrchestrationRule
    {
        public const string Rule = "InventoryApproval";

        public string RuleType => Rule;

        /// The domain carries no approval deadline: no SLA, no due column, no configured window. None is
        /// the honest answer, and it is stated rather than defaulted so that adding one later has to be a
        /// decision somebody takes. It costs something real - this work can never be overdue and can never
        /// escalate - and that consequence belongs in the open rather than behind an invented `now + 3`.
        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

        // -----------------------------------------------------------------------------------------
        // IDENTITY. The approval row is the work, so the approval id is the key.
        //
        // No cycle, no attempt, no event uid: the lifecycle allows exactly one Pending and exactly one
        // terminal decision per approval, so one approval is one piece of review work and always will be.
        // A key that folded in the event uid would be correct but would also imply a second round is
        // possible, and the guard `Status != "Pending"` says it is not.
        //
        // The completion prefix is the whole key rather than a shorter stem, so a decision on approval 12
        // cannot reach approval 120's task.
        // -----------------------------------------------------------------------------------------
        public static string KeyFor(int approvalId)
            => $"TaskProjection:{Rule}:{EntityRegistry.InventoryApproval}:{approvalId}";

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // EXACT ENTITY CODE, ordinal. Not a prefix and not a wildcard: an entity that one day grows
            // its own "Approved" must not be answered by this rule.
            if (!string.Equals(envelope.Entity.Code, EntityRegistry.InventoryApproval, StringComparison.Ordinal))
                return TaskProjectionDecision.Ignore;

            int approvalId = envelope.Entity.Id;
            if (approvalId <= 0) return TaskProjectionDecision.Ignore;   // no identity, no work

            switch (envelope.EventType)
            {
                // ---- both terminal decisions end the SAME review --------------------------------
                //
                // When nothing was projected - a decision replayed from before this rule existed, or an
                // event delivered out of order - the orchestration service answers "nothing was projected
                // for this source" and creates NOTHING. A task fabricated only to be marked Done would be
                // a record of work nobody did.
                case InventoryApprovalEvents.Approved:
                case InventoryApprovalEvents.Rejected:
                    return TaskProjectionDecision.Complete(KeyFor(approvalId));

                // ---- ACTION_REQUIRED ------------------------------------------------------------
                case InventoryApprovalEvents.Requested:
                    return TaskProjectionDecision.Create(new TaskIntent
                    {
                        CompanyId = envelope.Context.CompanyId,
                        RuleType = Rule,
                        RuleKey = KeyFor(approvalId),

                        TitleAr = "اعتماد " + DocTypeAr(envelope.Payload) + " رقم " + approvalId,
                        TitleEn = "Approve " + DocTypeEn(envelope.Payload) + " #" + approvalId,
                        Description = null,

                        // Nobody. See the header - the domain's answer, not a placeholder.
                        AssigneeEmployeeId = 0,

                        // No date, from a fact that carries none.
                        SourceDueDate = null,

                        // THE APPROVAL IS THE SOURCE, not the purchase order it will become. The document
                        // may not exist yet - that is what deferred intent means - and the thing a reviewer
                        // acts on, and the thing the visibility provider authorises, is the approval row.
                        EntityType = EntityRegistry.InventoryApproval,
                        EntityId = approvalId,
                    });

                default:
                    return TaskProjectionDecision.Ignore;
            }
        }

        // ---- payload readers -------------------------------------------------------------------
        //
        // DocType travels as the stable code the domain stores, so the title is assembled here rather
        // than carried as frozen text in the permanent log. Tolerant by design: an older producer that
        // omits the field must degrade the TITLE, never throw - a throwing rule fails its dispatch row
        // forever over a cosmetic absence.

        private static string DocTypeAr(System.Text.Json.Nodes.JsonNode? payload) => DocTypeOf(payload) switch
        {
            "PurchaseOrder" => "أمر شراء",
            "StockTransfer" => "تحويل مخزني",
            "StockCount" => "تسوية جرد",
            "WriteOff" => "إعدام مخزون",
            _ => "مستند مخزني",
        };

        private static string DocTypeEn(System.Text.Json.Nodes.JsonNode? payload) => DocTypeOf(payload) switch
        {
            "PurchaseOrder" => "purchase order",
            "StockTransfer" => "stock transfer",
            "StockCount" => "stock count",
            "WriteOff" => "write-off",
            _ => "inventory document",
        };

        private static string DocTypeOf(System.Text.Json.Nodes.JsonNode? payload)
        {
            var node = payload?["docType"];
            if (node == null) return "";
            try { return node.GetValue<string>() ?? ""; }
            catch (InvalidOperationException) { return ""; }
            catch (FormatException) { return ""; }
        }
    }
}
