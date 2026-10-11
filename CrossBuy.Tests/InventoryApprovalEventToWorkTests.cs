using System.Linq;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // INVENTORY APPROVAL — EVENT TO WORK.
    //
    // The claim: an approval request that becomes durably Pending records one canonical fact, that fact
    // becomes exactly one unassigned review Task, and either terminal decision ends that same Task —
    // for all four document families, through one event model and one rule.
    //
    // WHY THE FAMILIES ARE PROVED SEPARATELY EVEN THOUGH THEY SHARE A CODE PATH. They share it TODAY.
    // A test that only ever submits a purchase order cannot tell "the lifecycle is shared" from "the
    // discriminator happens to be ignored", and the day somebody special-cases one DocType, three of
    // these tests are how it gets noticed.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1 — company 1 is the value every historical bug defaulted to.
    // ================================================================================================
    public class InventoryApprovalEventToWorkTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int Requester = 10;
        private const int Manager = 11;
        private const int Outsider = 12;

        private static readonly string[] Families =
            { "PurchaseOrder", "StockTransfer", "StockCount", "WriteOff" };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        // ==========================================================================================
        // THE RULE'S OWN DISPATCH GUARDS.
        //
        // These call Decide() directly, and that is the point rather than a shortcut. The service path
        // only ever hands this rule a well-formed envelope it built itself, so the guards that refuse a
        // MALFORMED one are unreachable from it - and those are precisely the guards that matter. The
        // dispatcher fans every committed fact at every registered rule, so the envelope this rule must
        // refuse is somebody else's, and a rule that answers too readily writes work into a queue that
        // does not belong to it.
        // ==========================================================================================

        [Theory]
        [InlineData(EntityRegistry.SalesInvoice)]
        [InlineData(EntityRegistry.PurchaseInvoice)]
        [InlineData(EntityRegistry.ProjectBilling)]
        [InlineData(EntityRegistry.Task)]
        public void The_rule_answers_only_for_the_inventory_approval_entity(string entityCode)
        {
            var decision = new InventoryApprovalRule().Decide(
                Envelope(entityCode, 5, InventoryApprovalEvents.Requested, CompanyA, EventPayload("PurchaseOrder", 5)));

            Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
            Assert.Null(decision.Intent);
        }

        /// ProjectBilling is in that list on purpose: it is the OTHER approval rule on this platform, it
        /// carries approval-shaped event types, and it is the one whose events would look plausible here.
        [Theory]
        [InlineData("inventoryapproval")]
        [InlineData("INVENTORYAPPROVAL")]
        [InlineData("InventoryApprovals")]
        public void The_entity_code_is_matched_exactly_and_never_case_folded(string nearMiss)
        {
            var decision = new InventoryApprovalRule().Decide(
                Envelope(nearMiss, 5, InventoryApprovalEvents.Requested, CompanyA, EventPayload("PurchaseOrder", 5)));

            Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
        }

        /// An id of zero is not an approval, it is the absence of one. Work created from it would name no
        /// record, and the reviewer opening it would have nothing to decide.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void An_envelope_with_no_identity_becomes_no_work(int noIdentity)
        {
            var rule = new InventoryApprovalRule();

            Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(Envelope(
                EntityRegistry.InventoryApproval, noIdentity, InventoryApprovalEvents.Requested,
                CompanyA, EventPayload("PurchaseOrder", noIdentity))).Action);

            // and the same refusal on the way out, so a malformed decision cannot close somebody's task
            Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(Envelope(
                EntityRegistry.InventoryApproval, noIdentity, InventoryApprovalEvents.Approved,
                CompanyA, EventPayload("PurchaseOrder", noIdentity))).Action);
        }

        /// An event type this rule has never heard of must be INERT, not treated as a decision. The
        /// dangerous default is Complete: a future InventoryApproval.Cancelled or .Posted would then
        /// silently close review work that nobody actually reviewed.
        [Theory]
        [InlineData("InventoryApproval.Cancelled")]
        [InlineData("InventoryApproval.Posted")]
        [InlineData("InventoryApproval.Returned")]
        [InlineData("Something.Else")]
        public void An_event_type_this_rule_does_not_know_is_inert(string unknown)
        {
            var decision = new InventoryApprovalRule().Decide(Envelope(
                EntityRegistry.InventoryApproval, 5, unknown, CompanyA, EventPayload("PurchaseOrder", 5)));

            Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
            Assert.Null(decision.CompleteRuleKeyPrefix);
        }

        /// The key is asserted LITERALLY rather than by round-tripping KeyFor against itself, because a
        /// round-trip passes just as happily when the key stops naming the approval at all.
        [Fact]
        public void The_projection_key_names_the_rule_the_entity_and_the_approval()
        {
            Assert.Equal("TaskProjection:InventoryApproval:InventoryApproval:12", InventoryApprovalRule.KeyFor(12));
            Assert.NotEqual(InventoryApprovalRule.KeyFor(12), InventoryApprovalRule.KeyFor(120));
            Assert.Equal("InventoryApproval", new InventoryApprovalRule().RuleType);
            Assert.Equal(DueDatePolicy.None, new InventoryApprovalRule().DefaultDueDatePolicy);
        }

        // ==========================================================================================
        // THE TITLE IS ASSEMBLED, NOT STORED.
        //
        // DocType travels as the stable code the domain keeps and the family name is resolved at render
        // time, so these assert that the discriminator actually REACHES the title. Without them the rule
        // could label a write-off a purchase order and every other test would still pass.
        // ==========================================================================================

        [Theory]
        [InlineData("PurchaseOrder", "purchase order")]
        [InlineData("StockTransfer", "stock transfer")]
        [InlineData("StockCount", "stock count")]
        [InlineData("WriteOff", "write-off")]
        public void Each_family_names_its_own_document_in_the_title(string docType, string english)
        {
            var intent = RequestedIntent(docType, 12);

            Assert.Equal("Approve " + english + " #12", intent.TitleEn);
            Assert.Contains("12", intent.TitleAr);
            Assert.NotEqual(intent.TitleAr, intent.TitleEn);
        }

        /// The four families must not collapse into one another - a title that says the same thing for
        /// every document is the same defect as no title.
        [Fact]
        public void The_four_families_are_titled_distinguishably()
        {
            var titles = new[] { "PurchaseOrder", "StockTransfer", "StockCount", "WriteOff" }
                .Select(d => RequestedIntent(d, 12).TitleEn).ToList();

            Assert.Equal(4, titles.Distinct().Count());
        }

        /// A producer older than this rule, or one that grows a family this rule has not learned, must
        /// degrade the TITLE and still raise the work. Throwing here would fail the dispatch row forever
        /// over a cosmetic absence, and the decision the reviewer owes is not cosmetic.
        [Theory]
        [InlineData("SomethingNew")]
        [InlineData("")]
        public void An_unfamiliar_family_degrades_the_title_and_still_raises_the_work(string docType)
        {
            var intent = RequestedIntent(docType, 12);

            Assert.Equal("Approve inventory document #12", intent.TitleEn);
            Assert.Contains("مستند مخزني", intent.TitleAr);
            Assert.Equal(EntityRegistry.InventoryApproval, intent.EntityType);
            Assert.Equal(12, intent.EntityId);
        }

        /// Same rule, harder input: a payload whose docType is not a string at all. GetValue<string> on a
        /// number throws, and an unguarded read would take the whole projection down with it.
        [Fact]
        public void A_wrong_typed_or_absent_doctype_degrades_the_title_rather_than_throwing()
        {
            var rule = new InventoryApprovalRule();

            foreach (object payload in new object[] { new { DocType = 7 }, new { DocType = (string?)null }, new { Nothing = 1 } })
            {
                var decision = rule.Decide(Envelope(
                    EntityRegistry.InventoryApproval, 12, InventoryApprovalEvents.Requested, CompanyA, payload));

                Assert.Equal(TaskProjectionAction.Create, decision.Action);
                Assert.Equal("Approve inventory document #12", decision.Intent!.TitleEn);
            }
        }

        /// The Arabic side is asserted once concretely, so a family that silently loses its Arabic name
        /// is caught by something other than 'the two strings differ'.
        [Fact]
        public void The_arabic_title_names_the_family_too()
            => Assert.Contains("أمر شراء", RequestedIntent("PurchaseOrder", 12).TitleAr);

        // ==========================================================================================
        // THE RULE TYPE IS THE POLICY KEY.
        // ==========================================================================================

        /// Proves the rule's RuleType is what the policy resolver is asked with - not a label. A company
        /// that switches InventoryApproval off must stop getting the work, and if the rule reported any
        /// other name the switch would silently do nothing.
        [Fact]
        public async Task A_company_that_switches_this_rule_off_stops_getting_the_work()
        {
            using var rig = await ReadyAsync();
            rig.Host.Seed.TaskAutoRules.Add(new TaskAutoRule
            {
                CompanyId = CompanyA, RuleType = InventoryApprovalRule.Rule, IsActive = false,
            });
            await rig.Host.Seed.SaveChangesAsync();

            await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await rig.DispatchAllAsync();

            // the FACT is still recorded - switching off the work does not rewrite history
            Assert.Single(rig.Events_(InventoryApprovalEvents.Requested));
            Assert.Empty(rig.Tasks());
            Assert.Empty(rig.Ledger());
        }

        // ==========================================================================================
        // WHAT THE PERMANENT RECORD ACTUALLY CARRIES.
        // ==========================================================================================

        /// Every other test reads the ENVELOPE. This one reads the PAYLOAD, which is the part a consumer
        /// that is not this rule will depend on, and the part no envelope assertion can vouch for.
        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task The_recorded_fact_carries_the_approval_and_then_its_decision(string docType)
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync(docType, 500m, PayloadFor(docType), Requester);

            // The TYPED read is what a real consumer does, so the values are asserted through the same
            // deserialization rather than through hand-picked JSON nodes.
            var requested = Payload(Assert.Single(rig.Events_(InventoryApprovalEvents.Requested)));
            Assert.Equal(id, requested.ApprovalId);
            Assert.Equal(docType, requested.DocType);
            Assert.Equal("Pending", requested.Status);
            Assert.Equal(500m, requested.Amount);
            Assert.Equal(Requester, requested.RequestedByEmployeeId);
            Assert.Null(requested.DecidedByEmployeeId);        // nobody has decided yet
            Assert.Null(requested.ResultDocNo);                // and no document exists yet

            await rig.Approvals.ApproveAsync(id, Manager, null);

            var approved = Payload(Assert.Single(rig.Events_(InventoryApprovalEvents.Approved)));
            Assert.Equal(id, approved.ApprovalId);
            Assert.Equal(docType, approved.DocType);
            Assert.Equal("Approved", approved.Status);
            Assert.Equal(Manager, approved.DecidedByEmployeeId);
            Assert.Equal(Requester, approved.RequestedByEmployeeId);
            // separation of duties survives into the permanent record, not just the write path
            Assert.NotEqual(approved.RequestedByEmployeeId, approved.DecidedByEmployeeId);
            Assert.False(string.IsNullOrWhiteSpace(approved.ResultDocNo));

            // NO company field and NO deferred-intent blob. The envelope carries the company, and the
            // held document is an implementation detail of replay that a consumer resolves through its
            // own authorization rather than reading out of an event it was handed.
            var raw = RawPayload(Assert.Single(rig.Events_(InventoryApprovalEvents.Approved)));
            Assert.Null(raw["companyId"]);
            Assert.Null(raw["payloadJson"]);
            Assert.Equal(
                new[] { "amount", "approvalId", "decidedByEmployeeId", "docType",
                        "requestedByEmployeeId", "resultDocNo", "status" },
                raw.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        // ---- helpers for the direct-Decide tests -------------------------------------------------

        private static TaskIntent RequestedIntent(string docType, int approvalId)
        {
            var decision = new InventoryApprovalRule().Decide(Envelope(
                EntityRegistry.InventoryApproval, approvalId, InventoryApprovalEvents.Requested,
                CompanyA, EventPayload(docType, approvalId)));

            Assert.Equal(TaskProjectionAction.Create, decision.Action);
            return decision.Intent!;
        }

        private static object EventPayload(string docType, int approvalId) => new
        {
            ApprovalId = approvalId, DocType = docType, Status = "Pending", Amount = 500m,
            RequestedByEmployeeId = Requester,
        };

        private static System.Text.Json.Nodes.JsonNode RawPayload(BusinessEvent stored) =>
            System.Text.Json.Nodes.JsonNode.Parse(stored.Payload!)!;

        /// The same envelope the dispatch worker builds, assembled by hand so that a shape the producer
        /// would never emit can still be put in front of the rule.
        private static BusinessEventEnvelope Envelope(string entityCode, int entityId, string eventType,
            int companyId, object payload)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            return new BusinessEventEnvelope
            {
                EventUid = Guid.NewGuid(),
                EventType = eventType,
                Entity = new BusinessEventEntity { Code = entityCode, Id = entityId },
                Actor = new BusinessEventActor { EmployeeId = null },
                Context = new BusinessEventContext { CompanyId = companyId },
                OccurredAt = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
                PayloadVersion = InventoryApprovalService.InventoryApprovalEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Payload = System.Text.Json.Nodes.JsonNode.Parse(json),
            };
        }

        // ---- the harness: every piece production except the sinks -----------------------------------
        //
        // The doubles carry the SAME signatures the replay path already exercises elsewhere; only the
        // failure switch is new, and it exists so a consequence that fails can be observed rather than
        // simulated by breaking a database.
        private sealed class Sink : INotificationService
        {
            public readonly List<(int Employee, string Type, int? RefId)> Sent = new();
            public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn, string? bodyAr,
                string? bodyEn, string type, int? refId = null, string? url = null, int? companyId = null,
                int? actorEmployeeId = null, string? priority = null, string? category = null,
                string? dedupKey = null, DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null)
            { Sent.Add((recipientEmployeeId, type, refId)); return Task.CompletedTask; }

            public Task<int> NotifyRoleAsync(int companyId, string role, string[] actions, string? titleAr,
                string? titleEn, string? bodyAr, string? bodyEn, string type, int? refId = null,
                int? actorEmployeeId = null) => Task.FromResult(0);
        }

        private sealed class Rig : IDisposable
        {
            public PlatformTestHost Host { get; }
            public Sink Notifications { get; } = new();
            public IInventoryApprovalService Approvals { get; }
            public IBusinessEventService Events { get; }
            public TaskOrchestrationConsumer Consumer { get; }
            public int CompanyId { get; }

            public Rig(int companyId, int actor)
            {
                CompanyId = companyId;
                Host = new PlatformTestHost(companyId: companyId);
                var accessor = new StubContextAccessor(Ctx(actor, companyId));
                var events = new BusinessEventService(
                    Host.Db, new EntityRegistry(Host.Db), accessor,
                    NullLogger<BusinessEventService>.Instance);
                Events = events;

                Approvals = new InventoryApprovalService(
                    Host.Db, new StockDouble(), new ProcDouble(),
                    Notifications, accessor,
                    new IModuleAccessService[] { new AlwaysInventory() }, events);

                var tasks = new TaskService(
                    Host.Db, new TaskCalendarEventPublisher(events),
                    new TaskNotificationService(Host.Db, new UatRecordingNotificationService(Host.Db)));
                Consumer = new TaskOrchestrationConsumer(new TaskOrchestrationService(
                    Host.Db,
                    new ITaskOrchestrationRule[] { new InventoryApprovalRule() },
                    tasks,
                    new TaskWorkPolicyResolver(Host.Db, null)));
            }

            public async Task SeedAsync()
            {
                var db = Host.Seed;
                db.Employee.AddRange(Emp(Requester, CompanyId), Emp(Manager, CompanyId), Emp(Outsider, CompanyId));
                db.InventorySettings.Add(new InventorySettings { CompanyID = CompanyId, ApprovalThreshold = 100m });
                db.InventoryUserRoles.Add(new InventoryUserRole
                {
                    CompanyID = CompanyId, EmployeeId = Manager, Role = "InventoryManager",
                });
                await db.SaveChangesAsync();
            }

            private static Employee Emp(int id, int companyId) => new()
            {
                ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
                EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
                Email = $"e{id}-{companyId}@example.com", ProfileImage = "-", Gender = "M",
                MaritalStatus = "S", UserId = $"user-{id}-{companyId}",
            };

            public List<BusinessEvent> Events_(string? type = null) => Host.Seed.BusinessEvents
                .AsNoTracking().Where(e => e.CompanyID == CompanyId)
                .Where(e => type == null || e.EventType == type)
                .OrderBy(e => e.EventId).ToList();

            /// EXACTLY what BusinessEventDispatchWorker does with a committed row.
            public async Task DispatchAllAsync()
            {
                foreach (var stored in Events_())
                    await Consumer.HandleAsync(Events.BuildEnvelope(stored));
            }

            public async Task DispatchAsync(BusinessEvent stored) =>
                await Consumer.HandleAsync(Events.BuildEnvelope(stored));

            public List<TaskItem> Tasks() => Host.Seed.TaskItems
                .AsNoTracking().Where(t => t.CompanyId == CompanyId).OrderBy(t => t.ID).ToList();

            public List<TaskAutoLog> Ledger() => Host.Seed.TaskAutoLogs
                .AsNoTracking().Where(l => l.CompanyId == CompanyId).OrderBy(l => l.ID).ToList();

            public void Dispose() => Host.Dispose();
        }

        /// Grants inventory authority, so these tests exercise the EVENT path rather than re-proving the
        /// service authorization TAB-1 already closed.
        private sealed class AlwaysInventory : IModuleAccessService
        {
            public string Scope => EntityRegistry.ScopeInventory;
            public IReadOnlyCollection<string> Actions => new[] { "read", "doc", "purchase", "manage" };
            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(true);
            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<string>>(new[] { "InventoryManager" });
        }

        private sealed class DenyInventory : IModuleAccessService
        {
            public string Scope => EntityRegistry.ScopeInventory;
            public IReadOnlyCollection<string> Actions => new[] { "read", "doc", "purchase", "manage" };
            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(false);
            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private sealed class StockDouble : IStockService
        {
            public bool Fail { get; set; }

            public Task<(bool ok, string? error, StockTransfer? transfer)> TransferAsync(int companyId, int fromWarehouseId, int toWarehouseId, DateTime date, string? notes, List<TransferLineInput> lines, string? userId)
                => Task.FromResult<(bool, string?, StockTransfer?)>(Fail ? (false, "no", null) : (true, null, new StockTransfer { TransferNo = "TR-1" }));

            public Task<(bool ok, string? error, StockCount? count)> PostCountAsync(int companyId, int warehouseId, DateTime date, string? notes, List<CountLineInput> lines, string? userId)
                => Task.FromResult<(bool, string?, StockCount?)>(Fail ? (false, "no", null) : (true, null, new StockCount { CountNo = "CN-1" }));

            public Task<(bool ok, string? error, string? docNo, int? docId, string mode)> WriteOffAsync(int companyId, int warehouseId, DateTime date, string? reason, string? notes, List<WriteOffLineInput> lines, string? userId)
                => Task.FromResult<(bool, string?, string?, int?, string)>(Fail ? (false, "no", null, null, "") : (true, null, "WO-1", 1, "posted"));

            private static Task<T> No<T>() => throw new InvalidOperationException(
                "the approval replay path reached a stock method it has no business calling");

            public Task<(bool ok, string? error, LandedCost? landed)> PostLandedCostAsync(int companyId, int goodsReceiptId, DateTime date, string allocationMethod, List<LandedChargeInput> charges, string? notes, string? userId) => No<(bool, string?, LandedCost?)>();
            public Task<(bool ok, string? error, int? jeId, decimal total)> PostOpeningStockAsync(int companyId, DateTime cutoff, List<OpeningStockLineInput> lines, string? userId) => No<(bool, string?, int?, decimal)>();
            public Task<(bool ok, string? error, int? assetId, decimal cost)> CapitalizeFromStockAsync(int companyId, int itemId, int warehouseId, decimal qty, DateTime date, int? costCenterId, string? userId) => No<(bool, string?, int?, decimal)>();
            public Task<(bool ok, string? error, StockMovement? movement)> PostMovementAsync(int companyId, MovementRequest req, string? userId) => No<(bool, string?, StockMovement?)>();
            public Task<(decimal qty, decimal value, decimal avg)> GetBalanceAsync(int companyId, int itemId, int warehouseId) => No<(decimal, decimal, decimal)>();
            public Task<List<StockBalance>> GetBalancesAsync(int companyId, int? warehouseId = null) => No<List<StockBalance>>();
            public Task<(List<StockBalanceRow> rows, int total, decimal grandValue)> SearchBalancesAsync(int companyId, string? q, int? warehouseId, bool onlyInStock, int page, int pageSize) => No<(List<StockBalanceRow>, int, decimal)>();
            public Task<List<StockMovement>> GetMovementsAsync(int companyId, int? itemId = null, int? warehouseId = null, int take = 300) => No<List<StockMovement>>();
            public Task<(bool ok, string? error, StockMovement? produced)> AssembleAsync(int companyId, int assemblyItemId, int warehouseId, decimal qty, DateTime date, bool disassemble, string? userId) => No<(bool, string?, StockMovement?)>();
            public Task<(bool ok, string? error)> ReleaseWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error, decimal unitCost)> CompleteWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId) => No<(bool, string?, decimal)>();
            public Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int workOrderId, decimal qty, decimal stdUnitCost, bool finalize, DateTime date, string? userId) => No<(bool, string?, decimal)>();
            public Task<(bool ok, string? error)> CancelWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error, int laborId)> AddWorkOrderLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId, string? workerNameEn = null) => No<(bool, string?, int)>();
            public Task<(bool ok, string? error)> RemoveWorkOrderLaborAsync(int companyId, int laborId, DateTime date, string? userId) => No<(bool, string?)>();
            public Task<List<BinStock>> GetBinStocksAsync(int companyId, int warehouseId) => No<List<BinStock>>();
            public Task<(bool ok, string? error)> RelocateBinAsync(int companyId, int warehouseId, int itemId, int fromBinId, int toBinId, decimal qty, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error)> SetBinCountAsync(int companyId, int warehouseId, int binLocationId, int itemId, decimal countedQty, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error, int rows)> InitializeBinStockFromDefaultsAsync(int companyId, int warehouseId, string? userId) => No<(bool, string?, int)>();
        }

        private sealed class ProcDouble : IProcurementService
        {
            public bool Fail { get; set; }
            public CrossDbContext? Persistence { get; set; }

            public async Task<(bool ok, string? error, PurchaseOrder? po)> CreatePurchaseOrderAsync(int companyId, int vendorId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<PoLineInput> lines, string? userId, int? projectId = null)
            {
                if (Fail) return (false, "no", null);
                var po = new PurchaseOrder { CompanyID = companyId, OrderNo = "PO-1", OrderDate = date, VendorId = vendorId };
                if (Persistence != null)
                {
                    // A transactional delegate, like the production procurement writer. The test verifies
                    // the orchestration boundary; SQL Server qualification remains a separate gate.
                    Assert.NotNull(Persistence.Database.CurrentTransaction);
                    await using var joined = await ScopedTx.BeginOrJoinAsync(Persistence);
                    Assert.False(joined.Owns);
                    Persistence.PurchaseOrders.Add(po);
                    await Persistence.SaveChangesAsync();
                    await joined.CommitAsync();
                }
                return (true, null, po);
            }

            private static Task<T> No<T>() => throw new InvalidOperationException(
                "the approval replay path reached a procurement method it has no business calling");

            public Task<List<PurchaseOrder>> GetPurchaseOrdersAsync(int companyId) => No<List<PurchaseOrder>>();
            public Task<PurchaseOrder?> GetPurchaseOrderAsync(int companyId, int id) => No<PurchaseOrder?>();
            public Task<List<GoodsReceipt>> GetReceiptsAsync(int companyId) => No<List<GoodsReceipt>>();
            public Task<GoodsReceipt?> GetReceiptAsync(int companyId, int id) => No<GoodsReceipt?>();
            public Task<(bool ok, string? error, GoodsReceipt? gr)> CreateReceiptAsync(int companyId, int? vendorId, int warehouseId, int? poId, DateTime date, string? notes, List<ReceiptLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null) => No<(bool, string?, GoodsReceipt?)>();
            public Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int poId, string? userId) => No<(bool, string?, int?)>();
        }
        private static object PayloadFor(string docType) => docType switch
        {
            "PurchaseOrder" => new { VendorId = 1, WarehouseId = 1, OrderDate = DateTime.Today, Lines = new List<object>() },
            "StockTransfer" => new { FromWarehouseId = 1, ToWarehouseId = 2, Date = DateTime.Today, Lines = new List<object>() },
            "StockCount" => new { WarehouseId = 1, CountDate = DateTime.Today, Lines = new List<object>() },
            _ => new { WarehouseId = 1, WriteOffDate = DateTime.Today, Reason = "damage", Lines = new List<object>() },
        };

        private static async Task<Rig> ReadyAsync(int companyId = CompanyA, int actor = Requester)
        {
            var rig = new Rig(companyId, actor);
            await rig.SeedAsync();
            return rig;
        }

        private static readonly string Requested = InventoryApprovalEvents.Requested;
        private static readonly string Approved = InventoryApprovalEvents.Approved;
        private static readonly string Rejected = InventoryApprovalEvents.Rejected;

        // ============================================================================================
        // THE THREE FACTS — and all four families.
        // ============================================================================================

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task A_pending_approval_records_one_requested_fact_for_every_family(string docType)
        {
            using var rig = await ReadyAsync();

            int id = await rig.Approvals.SubmitAsync(docType, 500m, PayloadFor(docType), Requester);

            var e = Assert.Single(rig.Events_(Requested));
            Assert.Equal(EntityRegistry.InventoryApproval, e.EntityType);
            Assert.Equal(id, e.EntityId);
            Assert.Equal(CompanyA, e.CompanyID);
            Assert.Equal($"inventoryapproval:{id}:Requested", e.DedupKey);
            Assert.Equal(InventoryApprovalService.InventoryApprovalEventPayload.Version, e.PayloadVersion);

            var p = Payload(e);
            Assert.Equal(docType, p.DocType);
            Assert.Equal("Pending", p.Status);
            Assert.Equal(Requester, p.RequestedByEmployeeId);
            Assert.Null(p.DecidedByEmployeeId);
            Assert.Null(p.ResultDocNo);
        }

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task Approving_records_one_approved_fact_for_every_family(string docType)
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync(docType, 500m, PayloadFor(docType), Requester);

            Assert.True((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);

            var e = Assert.Single(rig.Events_(Approved));
            Assert.Equal(id, e.EntityId);
            Assert.Equal($"inventoryapproval:{id}:Approved", e.DedupKey);
            var p = Payload(e);
            Assert.Equal("Approved", p.Status);
            Assert.Equal(Manager, p.DecidedByEmployeeId);
            Assert.Equal(docType, p.DocType);
        }

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task Rejecting_records_one_rejected_fact_for_every_family(string docType)
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync(docType, 500m, PayloadFor(docType), Requester);

            Assert.True((await rig.Approvals.RejectAsync(id, Manager, "no")).ok);

            var e = Assert.Single(rig.Events_(Rejected));
            Assert.Equal(id, e.EntityId);
            Assert.Equal($"inventoryapproval:{id}:Rejected", e.DedupKey);
            Assert.Equal("Rejected", Payload(e).Status);
            Assert.Empty(rig.Events_(Approved));
        }

        [Fact]  // The threshold decides whether an approval exists at all - and no approval, no fact.
        public async Task A_document_below_the_threshold_never_becomes_approval_work()
        {
            using var rig = await ReadyAsync();

            Assert.False(await rig.Approvals.RequiresApprovalAsync(50m));    // threshold is 100
            Assert.True(await rig.Approvals.RequiresApprovalAsync(500m));

            // Nothing was submitted, so there is no approval row and no fact to carry.
            Assert.Empty(rig.Events_());
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        // ============================================================================================
        // TRANSACTIONAL ATOMICITY — the seam is the ambient transaction the service JOINS.
        // ============================================================================================

        [Fact]
        public async Task A_submit_that_never_commits_records_no_fact_and_creates_no_work()
        {
            using var rig = await ReadyAsync();

            await using (var outer = await ScopedTx.BeginOrJoinAsync(rig.Host.Db))
            {
                Assert.True(outer.Owns);
                await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
                // disposed WITHOUT commit
            }

            Assert.Empty(rig.Events_());
            Assert.Empty(rig.Host.Seed.InventoryApprovals.AsNoTracking().ToList());
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        [Fact]
        public async Task An_approval_that_never_commits_leaves_it_pending_with_no_fact()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await rig.DispatchAllAsync();
            var before = Assert.Single(rig.Tasks());

            await using (var outer = await ScopedTx.BeginOrJoinAsync(rig.Host.Db))
            {
                Assert.True((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);
            }

            Assert.Empty(rig.Events_(Approved));
            Assert.Equal("Pending", Row(rig, id).Status);
            await rig.DispatchAllAsync();
            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks()).Status);
            Assert.Equal(before.ID, rig.Tasks()[0].ID);
        }

        [Fact]
        public async Task Failure_after_a_delegate_writes_rolls_back_document_decision_and_event_then_retry_writes_once()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            ((ProcDouble)Proc(rig)).Persistence = rig.Host.Db;
            var approvals = (InventoryApprovalService)rig.Approvals;
            approvals.BeforeDecisionSaveHookForTests = () => throw new InvalidOperationException("decision unavailable");
            Assert.False((await approvals.ApproveAsync(id, Manager, "ok")).ok);

            using (var fresh = rig.Host.NewContext())
            {
                Assert.Empty(await fresh.PurchaseOrders.AsNoTracking().ToListAsync());
                Assert.Equal("Pending", (await fresh.InventoryApprovals.AsNoTracking().SingleAsync(a => a.ID == id)).Status);
                Assert.Empty(await fresh.BusinessEvents.AsNoTracking().Where(e => e.EventType == Approved).ToListAsync());
            }
            approvals.BeforeDecisionSaveHookForTests = null;
            Assert.True((await approvals.ApproveAsync(id, Manager, "ok")).ok);
            Assert.False((await approvals.ApproveAsync(id, Manager, "again")).ok);
            using var final = rig.Host.NewContext();
            Assert.Single(await final.PurchaseOrders.AsNoTracking().ToListAsync());
            Assert.Equal("Approved", (await final.InventoryApprovals.AsNoTracking().SingleAsync(a => a.ID == id)).Status);
            Assert.Single(await final.BusinessEvents.AsNoTracking().Where(e => e.EventType == Approved).ToListAsync());
        }

        [Fact]
        public async Task A_rejection_that_never_commits_leaves_it_pending_with_no_fact()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await rig.DispatchAllAsync();

            await using (var outer = await ScopedTx.BeginOrJoinAsync(rig.Host.Db))
            {
                Assert.True((await rig.Approvals.RejectAsync(id, Manager, "no")).ok);
            }

            Assert.Empty(rig.Events_(Rejected));
            Assert.Equal("Pending", Row(rig, id).Status);
            await rig.DispatchAllAsync();
            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks()).Status);
        }

        [Fact]  // The consequence and the fact share one fate.
        public async Task A_consequence_that_fails_records_no_approved_fact_and_writes_no_document()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);

            ((ProcDouble)Proc(rig)).Fail = true;
            Assert.False((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);

            Assert.Empty(rig.Events_(Approved));
            Assert.Equal("Pending", Row(rig, id).Status);
            Assert.Empty(rig.Host.Seed.PurchaseOrders.AsNoTracking().ToList());
        }

        // ============================================================================================
        // AUTHORIZATION AND SEGREGATION — TAB-1 closed these; the events must not reopen them.
        // ============================================================================================

        [Fact]
        public async Task An_unauthorized_decision_records_no_fact_at_all()
        {
            using var rig = new Rig(CompanyA, Requester);
            await rig.SeedAsync();
            var denied = new InventoryApprovalService(
                rig.Host.Db, new StockDouble(), new ProcDouble(),
                rig.Notifications, new StubContextAccessor(Ctx(Outsider, CompanyA)),
                new IModuleAccessService[] { new DenyInventory() }, rig.Events);

            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);

            Assert.False((await denied.ApproveAsync(id, Outsider, "x")).ok);
            Assert.False((await denied.RejectAsync(id, Outsider, "x")).ok);

            Assert.Empty(rig.Events_(Approved));
            Assert.Empty(rig.Events_(Rejected));
            Assert.Equal("Pending", Row(rig, id).Status);
        }

        [Fact]
        public async Task The_requester_cannot_decide_and_records_no_fact()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);

            Assert.False((await rig.Approvals.ApproveAsync(id, Requester, "self")).ok);
            Assert.False((await rig.Approvals.RejectAsync(id, Requester, "self")).ok);

            Assert.Empty(rig.Events_(Approved));
            Assert.Empty(rig.Events_(Rejected));
            Assert.Equal("Pending", Row(rig, id).Status);
        }

        [Fact]  // One terminal decision, in both directions.
        public async Task A_second_terminal_decision_is_refused_and_records_nothing()
        {
            using var rig = await ReadyAsync();
            int approved = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            Assert.True((await rig.Approvals.ApproveAsync(approved, Manager, "ok")).ok);
            Assert.False((await rig.Approvals.RejectAsync(approved, Manager, "no")).ok);
            Assert.Empty(rig.Events_(Rejected));

            int rejected = await rig.Approvals.SubmitAsync("StockCount", 500m, PayloadFor("StockCount"), Requester);
            Assert.True((await rig.Approvals.RejectAsync(rejected, Manager, "no")).ok);
            Assert.False((await rig.Approvals.ApproveAsync(rejected, Manager, "ok")).ok);
            Assert.Single(rig.Events_(Approved));      // still only the first one
        }

        // ============================================================================================
        // EVENT TO WORK.
        // ============================================================================================

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task Every_family_becomes_one_unassigned_task_linked_to_the_approval(string docType)
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync(docType, 500m, PayloadFor(docType), Requester);

            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(CompanyA, task.CompanyId);
            Assert.Equal(0, task.AssigneeEmployeeId);                       // nobody, on purpose
            Assert.NotEqual(Requester, task.AssigneeEmployeeId);
            Assert.Null(task.DueDate);                                      // no invented SLA
            Assert.Equal(TaskStatuses.New, task.Status);
            Assert.Equal(0, task.CreatedByEmployeeId);                      // the system
            Assert.Equal("Normal", task.Priority);                          // the policy default
            // THE APPROVAL is the source, not the document it will become.
            Assert.Equal(EntityRegistry.InventoryApproval, task.EntityType);
            Assert.Equal(id, task.EntityId);
            Assert.Equal(InventoryApprovalRule.KeyFor(id), Assert.Single(rig.Ledger()).RuleKey);
        }

        [Fact]
        public async Task Replaying_the_requested_fact_creates_no_second_task()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            var e = Assert.Single(rig.Events_(Requested));

            await rig.DispatchAsync(e);
            await rig.DispatchAsync(e);
            await rig.DispatchAsync(e);

            Assert.Single(rig.Tasks());
            Assert.Single(rig.Ledger());
        }

        [Theory]  // both terminal decisions end the same work
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_terminal_decision_completes_the_same_task_and_creates_none(bool approve)
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await rig.DispatchAllAsync();
            var opened = Assert.Single(rig.Tasks());

            if (approve) Assert.True((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);
            else Assert.True((await rig.Approvals.RejectAsync(id, Manager, "no")).ok);
            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(opened.ID, task.ID);
            Assert.Equal(TaskStatuses.Done, task.Status);   // NOT a Cancelled state - none exists
            Assert.Single(rig.Ledger());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Replaying_a_terminal_decision_leaves_one_completed_task(bool approve)
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await rig.DispatchAllAsync();
            if (approve) await rig.Approvals.ApproveAsync(id, Manager, "ok");
            else await rig.Approvals.RejectAsync(id, Manager, "no");

            await rig.DispatchAllAsync();
            await rig.DispatchAllAsync();
            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(TaskStatuses.Done, task.Status);
            Assert.Single(rig.Ledger());
        }

        [Fact]  // a decision on one approval must not reach another's work
        public async Task A_decision_completes_only_its_own_approvals_task()
        {
            using var rig = await ReadyAsync();
            int first = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            int second = await rig.Approvals.SubmitAsync("StockCount", 500m, PayloadFor("StockCount"), Requester);
            await rig.DispatchAllAsync();
            Assert.Equal(2, rig.Tasks().Count);

            Assert.True((await rig.Approvals.ApproveAsync(first, Manager, "ok")).ok);
            await rig.DispatchAllAsync();

            Assert.Equal(TaskStatuses.Done, rig.Tasks().Single(t => t.EntityId == first).Status);
            Assert.Equal(TaskStatuses.New, rig.Tasks().Single(t => t.EntityId == second).Status);
        }

        [Fact]  // an id that matches but a type that does not
        public async Task A_decision_does_not_reach_a_task_of_another_entity_type()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await rig.DispatchAllAsync();

            // A hand-linked task on the SAME id under a different type - TM-2 lets a user do this.
            rig.Host.Seed.TaskItems.Add(new TaskItem
            {
                CompanyId = CompanyA, Title = "unrelated", Status = TaskStatuses.New,
                EntityType = EntityRegistry.Item, EntityId = id, CreatedAt = DateTime.UtcNow,
            });
            await rig.Host.Seed.SaveChangesAsync();

            Assert.True((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);
            await rig.DispatchAllAsync();

            Assert.Equal(TaskStatuses.New,
                rig.Tasks().Single(t => t.EntityType == EntityRegistry.Item).Status);
        }

        [Fact]  // an Approved with nothing behind it fabricates nothing
        public async Task A_decision_with_no_projected_work_creates_no_phantom_task()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            Assert.True((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);

            // Only the decision is dispatched; the Requested fact never reaches orchestration.
            await rig.DispatchAsync(Assert.Single(rig.Events_(Approved)));

            Assert.Empty(rig.Tasks());
            Assert.Empty(rig.Ledger());
        }

        // ============================================================================================
        // COMPANY ISOLATION.
        // ============================================================================================

        [Fact]
        public async Task Two_companies_keep_their_own_approval_work()
        {
            using var a = await ReadyAsync(CompanyA);
            using var b = await ReadyAsync(CompanyB);

            int idA = await a.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            int idB = await b.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            await a.DispatchAllAsync();
            await b.DispatchAllAsync();

            Assert.Equal(CompanyA, Assert.Single(a.Tasks()).CompanyId);
            Assert.Equal(CompanyB, Assert.Single(b.Tasks()).CompanyId);

            Assert.True((await a.Approvals.ApproveAsync(idA, Manager, "ok")).ok);
            await a.DispatchAllAsync();

            Assert.Equal(TaskStatuses.Done, Assert.Single(a.Tasks()).Status);
            Assert.Equal(TaskStatuses.New, Assert.Single(b.Tasks()).Status);
        }

        // ============================================================================================
        // NOTIFICATIONS — unchanged, and not duplicated by the new facts.
        // ============================================================================================

        [Fact]
        public async Task The_existing_notifications_are_unchanged_and_not_duplicated()
        {
            using var rig = await ReadyAsync();

            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            // ONE request notification, to the manager, not to the requester.
            Assert.Equal(new[] { Manager }, rig.Notifications.Sent.Select(s => s.Employee).ToArray());

            rig.Notifications.Sent.Clear();
            Assert.True((await rig.Approvals.ApproveAsync(id, Manager, "ok")).ok);
            Assert.Equal(new[] { Requester }, rig.Notifications.Sent.Select(s => s.Employee).ToArray());

            // Dispatching the facts adds NO further notification: the events drive TASKS, not a second
            // notification authority. Inline remains the only producer until a proven migration.
            rig.Notifications.Sent.Clear();
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Notifications.Sent);
        }

        [Fact]
        public async Task A_rejection_notifies_the_requester_exactly_once()
        {
            using var rig = await ReadyAsync();
            int id = await rig.Approvals.SubmitAsync("PurchaseOrder", 500m, PayloadFor("PurchaseOrder"), Requester);
            rig.Notifications.Sent.Clear();

            Assert.True((await rig.Approvals.RejectAsync(id, Manager, "no")).ok);
            Assert.Equal(new[] { Requester }, rig.Notifications.Sent.Select(s => s.Employee).ToArray());

            rig.Notifications.Sent.Clear();
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Notifications.Sent);
        }

        // ============================================================================================
        // STRUCTURAL — claims no behavioural test can make.
        // ============================================================================================

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        private static string Code(params string[] parts) =>
            string.Join("\n", File.ReadAllText(RepoFile(parts)).Replace("\r\n", "\n").Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));

        [Fact]  // the rule decides; it never asks who may approve
        public void The_rule_restates_no_inventory_policy_and_writes_no_task()
        {
            var src = Code("CrossBuy", "BL", "Platform", "Orchestration", "InventoryApprovalRule.cs");

            Assert.DoesNotContain("InventoryUserRoles", src);
            Assert.DoesNotContain("InventoryManager", src);
            Assert.DoesNotContain("ApprovalInboxAsync", src);
            Assert.DoesNotContain("ITaskService", src);
            Assert.DoesNotContain("TaskItem", src);
            Assert.DoesNotContain("PayloadJson", src);       // the held blob never reaches a task
        }

        [Fact]  // one rule for four families, registered exactly once
        public void One_inventory_rule_is_registered_and_the_existing_rules_are_intact()
        {
            var program = Code("CrossBuy", "Program.cs");

            Assert.Equal(1, program.Split("InventoryApprovalRule").Length - 1);
            foreach (var forbidden in new[]
                     { "PurchaseOrderApprovalRule", "TransferApprovalRule", "CountApprovalRule", "WriteOffApprovalRule" })
                Assert.DoesNotContain(forbidden, program);

            // The rules that existed before this increment are all still registered, named rather than counted.
            foreach (var rule in new[]
                     {
                         "ProjectMilestoneOrchestrationRule", "DocumentVerificationRule",
                         "InventoryApprovalRule",
                     })
                Assert.Contains(
                    "CrossBuy.BL.Platform.Orchestration." + rule + ">", program);
        }

        [Fact]  // the producer is the service, and the facts are recorded before the commit
        public void The_events_are_recorded_inside_the_authoritative_transaction()
        {
            var src = Code("CrossBuy", "BL", "InventoryApprovalService.cs");

            foreach (var action in new[] { "Requested", "Approved", "Rejected" })
            {
                int emit = src.IndexOf("InventoryApprovalEvents." + action, StringComparison.Ordinal);
                Assert.True(emit > 0, action + " is not emitted");
                int commit = src.IndexOf("tx.CommitAsync()", emit, StringComparison.Ordinal);
                Assert.True(commit > emit, action + " is not recorded before its commit");
            }

            // The held payload is never serialised into the permanent log.
            Assert.DoesNotContain("PayloadJson = ap.PayloadJson", src);
        }

        [Fact]  // the compromise is bounded: the constructor DI picks carries the recorder
        public void The_constructor_dependency_injection_resolves_carries_the_event_recorder()
        {
            var greediest = typeof(InventoryApprovalService).GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length).First();

            Assert.Contains(greediest.GetParameters(), p => p.ParameterType == typeof(IBusinessEventService));
            Assert.Contains("AddScoped<IInventoryApprovalService, InventoryApprovalService>",
                Code("CrossBuy", "Program.cs"));
        }

        [Fact]  // the event vocabulary is exactly three
        public void The_inventory_approval_event_vocabulary_is_exactly_three()
        {
            var names = typeof(InventoryApprovalEvents)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[] { "InventoryApproval.Approved", "InventoryApproval.Rejected", "InventoryApproval.Requested" },
                names);
        }

        // ---- helpers -------------------------------------------------------------------------------
        private static readonly System.Text.Json.JsonSerializerOptions Web =
            new(System.Text.Json.JsonSerializerDefaults.Web);

        private static InventoryApprovalService.InventoryApprovalEventPayload Payload(BusinessEvent e) =>
            System.Text.Json.JsonSerializer.Deserialize<InventoryApprovalService.InventoryApprovalEventPayload>(e.Payload!, Web)!;

        private static InventoryApproval Row(Rig rig, int id) =>
            rig.Host.Seed.InventoryApprovals.AsNoTracking().First(a => a.ID == id);

        private static IProcurementService Proc(Rig rig) =>
            (IProcurementService)typeof(InventoryApprovalService)
                .GetField("_proc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(rig.Approvals)!;
    }
}
