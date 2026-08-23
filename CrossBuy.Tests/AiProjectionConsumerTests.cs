using System.Text.Json;
using System.Text.Json.Nodes;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation — the consumer end to end, against a real DbContext with real transactions.
    //
    // Every test here answers one of the two questions this increment exists to answer: "what may enter
    // the AI subsystem?" and "can it be attributed to the wrong tenant?".
    public class AiProjectionConsumerTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;

        // ------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------
        private static AiProjectionConsumer Consumer(
            PlatformTestHost host,
            CrossBuy.Models.Context.CrossDbContext db,
            IPlatformPermissionProvider? permissions = null,
            IAiConsumerGrants? grants = null,
            IEnumerable<IAiProjectionBuilder>? builders = null)
            => new(
                grants ?? new AiConsumerGrants(),
                builders ?? new IAiProjectionBuilder[] { new TaskLifecycleProjectionBuilder() },
                new AiProjectionStore(db, NullLogger<AiProjectionStore>.Instance),
                // The REAL provider by default: it resolves the entity through EntityRegistry, which reads
                // TaskItems WHERE CompanyId = context.CompanyId. That is what makes the isolation tests
                // below meaningful rather than a stub agreeing with itself.
                permissions ?? new PlatformPermissionProvider(
                    host.Registry(db),
                    new IModulePermissionAdapter[] { new AlwaysAllowTasksAdapter() },
                    NullLogger<PlatformPermissionProvider>.Instance),
                // Increment 3: the consumer now refuses to persist a shape with no declared retention
                // policy, so the real registry is supplied. That is the governance rule under test —
                // a stub returning "some policy" would defeat it.
                new AiRetentionPolicyRegistry(),
                db,
                NullLogger<AiProjectionConsumer>.Instance);

        // Seeds a real BusinessEvent row (the projection carries an FK to it) and returns its envelope.
        private static async Task<BusinessEventEnvelope> SeedEventAsync(
            CrossBuy.Models.Context.CrossDbContext db,
            string eventType, int companyId, int taskId,
            object payload, int? branchId = null, int? actorId = 7,
            string visibility = BusinessEventVisibility.Internal,
            string entityType = EntityRegistry.Task)
        {
            var row = new CrossBuy.Models.Context.Platform.BusinessEvent
            {
                EventUid = Guid.NewGuid(),
                CompanyID = companyId,
                BranchID = branchId,
                EntityType = entityType,
                EntityId = taskId,
                EventType = eventType,
                ActorEmployeeId = actorId,
                Payload = JsonSerializer.Serialize(payload),
                PayloadVersion = 1,
                Visibility = visibility,
                CreatedAt = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc),
            };
            db.BusinessEvents.Add(row);
            await db.SaveChangesAsync();

            return new BusinessEventEnvelope
            {
                EventUid = row.EventUid,
                EventType = eventType,
                Entity = new BusinessEventEntity { Code = entityType, Id = taskId },
                Actor = new BusinessEventActor { EmployeeId = actorId },
                Context = new BusinessEventContext { CompanyId = companyId, BranchId = branchId },
                PayloadVersion = 1,
                Visibility = visibility,
                OccurredAt = row.CreatedAt,
                Payload = JsonNode.Parse(row.Payload!),
            };
        }

        private static async Task SeedTaskAsync(
            CrossBuy.Models.Context.CrossDbContext db, int taskId, int companyId)
        {
            db.TaskItems.Add(new TaskItem
            {
                ID = taskId, CompanyId = companyId,
                Title = "Chase overdue invoice for ACME Trading LLC",   // free text — must NOT reach AI
                Description = "Customer contact: 0555-1234, complaint reference 88",
                AssigneeEmployeeId = 42, CreatedByEmployeeId = 7,
                Priority = "High", Status = "InProgress",
            });
            await db.SaveChangesAsync();
        }

        private static object StatusChangedPayload() => new
        {
            previousStatus = "New", newStatus = "InProgress", sourceModule = "Tasks",
        };

        // ------------------------------------------------------------------------------------------
        // 1. Allowed event creates a projection, with correct tenancy and provenance
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task An_allowed_event_creates_a_projection_linked_to_its_business_event()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, taskId: 91, companyId: CompanyA);
            var envelope = await SeedEventAsync(
                host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload(), branchId: 5);

            await Consumer(host, host.Db).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            var p = await verify.AiProjections.SingleAsync();

            Assert.Equal(CompanyA, p.CompanyID);
            Assert.Equal(5, p.BranchID);
            Assert.Equal(EntityRegistry.Task, p.EntityType);
            Assert.Equal(91, p.EntityId);
            Assert.Equal(TaskEvents.StatusChanged, p.EventType);
            Assert.Equal(BusinessEventConsumers.AiProjection, p.Consumer);
            Assert.Equal(AiConsumerGrants.TaskLifecycleProjection, p.ProjectionType);
            Assert.Equal(7, p.ActorEmployeeId);

            // Provenance: the projection points at a REAL BusinessEvents row, and at the right one.
            var source = await verify.BusinessEvents.SingleAsync();
            Assert.Equal(source.EventId, p.BusinessEventId);
            Assert.Equal(source.EventUid, p.EventUid);
            Assert.Equal(source.CreatedAt, p.OccurredAt);
        }

        // ------------------------------------------------------------------------------------------
        // 2. DATA MINIMIZATION — the fields that must be present, and the ones that must not
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task The_payload_contains_the_approved_fields_and_none_of_the_free_text()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());

            await Consumer(host, host.Db).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            var json = (await verify.AiProjections.SingleAsync()).PayloadJson;
            var payload = JsonNode.Parse(json)!.AsObject();

            // approved, structural fields
            Assert.Equal(91, (int)payload["taskId"]!);
            Assert.Equal("StatusChanged", (string)payload["action"]!);
            Assert.Equal("New", (string)payload["previousStatus"]!);
            Assert.Equal("InProgress", (string)payload["newStatus"]!);

            // EXCLUDED. The task's title and description contain a customer name and a phone number; the
            // whole point of the builder is that neither can reach a corpus. Asserted on the RAW JSON so a
            // future field name change cannot make the assertion vacuous.
            Assert.DoesNotContain("ACME", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0555", json, StringComparison.Ordinal);
            Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("complaint", json, StringComparison.OrdinalIgnoreCase);
        }

        // A producer that adds a field must NOT widen the AI corpus. This is the regression that the
        // builder's named-field list exists to prevent, so it is tested directly.
        [Fact]
        public async Task A_new_producer_field_does_not_reach_the_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, new
            {
                previousStatus = "New", newStatus = "InProgress", sourceModule = "Tasks",
                customerTaxNumber = "TAX-999-SECRET",       // a field nobody approved
                internalNote = "do not disclose",
            });

            await Consumer(host, host.Db).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            var json = (await verify.AiProjections.SingleAsync()).PayloadJson;
            Assert.DoesNotContain("TAX-999-SECRET", json, StringComparison.Ordinal);
            Assert.DoesNotContain("customerTaxNumber", json, StringComparison.Ordinal);
            Assert.DoesNotContain("do not disclose", json, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------------------
        // 3. DEFAULT DENY at the consumer, not merely in the grant table
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task An_ungranted_event_creates_no_projection_and_does_not_fail_the_dispatch()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(
                host.Db, TaskEvents.Assigned, CompanyA, 91, new { newAssigneeId = 42, sourceModule = "Tasks" });

            // Must NOT throw: a denial is a correct outcome, and throwing would mark the row Failed and
            // retry a decision that can never change.
            await Consumer(host, host.Db).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            Assert.Equal(0, await verify.AiProjections.CountAsync());
        }

        [Fact]
        public async Task A_confidential_event_is_refused_even_when_its_type_is_granted()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(
                host.Db, TaskEvents.Completed, CompanyA, 91,
                new { previousStatus = "InProgress", newStatus = "Done", sourceModule = "Tasks" },
                visibility: BusinessEventVisibility.Confidential);

            await Consumer(host, host.Db).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            Assert.Equal(0, await verify.AiProjections.CountAsync());
        }

        // ------------------------------------------------------------------------------------------
        // 4. COMPANY ISOLATION — the headline security property
        // ------------------------------------------------------------------------------------------
        //
        // The task belongs to company B; the event claims company A. The permission gate resolves the
        // entity inside the CLAIMED company, finds nothing, and refuses. No projection is attributed to
        // either company.
        [Fact]
        public async Task An_event_naming_the_wrong_company_produces_no_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, taskId: 91, companyId: CompanyB);      // really company B
            var envelope = await SeedEventAsync(seed, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());

            await Consumer(host, seed).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            Assert.Equal(0, await verify.AiProjections.CountAsync());
        }

        [Fact]
        public async Task A_company_a_event_never_produces_a_company_b_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyA);
            await SeedTaskAsync(seed, 92, CompanyB);

            var a = await SeedEventAsync(seed, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());
            var b = await SeedEventAsync(seed, TaskEvents.StatusChanged, CompanyB, 92, StatusChangedPayload());

            await Consumer(host, seed).HandleAsync(a);
            await Consumer(host, seed).HandleAsync(b);

            using var verify = host.AllCompanies();
            var rows = await verify.AiProjections.OrderBy(p => p.EntityId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(CompanyA, rows[0].CompanyID);
            Assert.Equal(91, rows[0].EntityId);
            Assert.Equal(CompanyB, rows[1].CompanyID);
            Assert.Equal(92, rows[1].EntityId);
            // Neither row carries the other's company — stated explicitly because that is the leak.
            Assert.DoesNotContain(rows, r => r.CompanyID == CompanyA && r.EntityId == 92);
            Assert.DoesNotContain(rows, r => r.CompanyID == CompanyB && r.EntityId == 91);
        }

        // No company ⇒ fail closed. Never company 1.
        [Fact]
        public async Task An_event_with_no_company_fails_closed_and_never_becomes_company_one()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());

            var noCompany = new BusinessEventEnvelope
            {
                EventUid = envelope.EventUid, EventType = envelope.EventType, Entity = envelope.Entity,
                Actor = envelope.Actor, PayloadVersion = 1, Visibility = envelope.Visibility,
                OccurredAt = envelope.OccurredAt, Payload = envelope.Payload,
                Context = new BusinessEventContext { CompanyId = 0 },
            };

            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(
                () => Consumer(host, host.Db).HandleAsync(noCompany));

            using var verify = host.AllCompanies();
            Assert.Equal(0, await verify.AiProjections.CountAsync());
        }

        // ------------------------------------------------------------------------------------------
        // 5. BRANCH — carried through, and never invented
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task The_branch_is_copied_from_the_event_and_stays_null_when_the_event_has_none()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedTaskAsync(host.Db, 92, CompanyA);

            var withBranch = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload(), branchId: 65);
            var noBranch = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 92, StatusChangedPayload(), branchId: null);

            var consumer = Consumer(host, host.Db);
            await consumer.HandleAsync(withBranch);
            await consumer.HandleAsync(noBranch);

            using var verify = host.AllCompanies();
            Assert.Equal(65, (await verify.AiProjections.SingleAsync(p => p.EntityId == 91)).BranchID);
            Assert.Null((await verify.AiProjections.SingleAsync(p => p.EntityId == 92)).BranchID);
        }

        // ------------------------------------------------------------------------------------------
        // 6. IDEMPOTENCY — a retry must not duplicate
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Redelivering_the_same_event_does_not_create_a_second_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());

            var consumer = Consumer(host, host.Db);
            await consumer.HandleAsync(envelope);     // attempt 1
            await consumer.HandleAsync(envelope);     // retry
            await consumer.HandleAsync(envelope);     // stale-claim redelivery

            using var verify = host.AllCompanies();
            Assert.Equal(1, await verify.AiProjections.CountAsync());
        }

        // The database — not the application pre-check — is the guarantee. Inserting the duplicate
        // directly proves the unique index is really there and really enforced.
        [Fact]
        public async Task The_database_refuses_a_duplicate_projection_for_the_same_event_and_shape()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());
            await Consumer(host, host.Db).HandleAsync(envelope);

            using var verify = host.AllCompanies();
            var existing = await verify.AiProjections.AsNoTracking().SingleAsync();

            verify.AiProjections.Add(new CrossBuy.Models.Context.Platform.AiProjection
            {
                BusinessEventId = existing.BusinessEventId, EventUid = existing.EventUid,
                Consumer = existing.Consumer, CompanyID = existing.CompanyID,
                EntityType = existing.EntityType, EntityId = existing.EntityId,
                EventType = existing.EventType, ProjectionType = existing.ProjectionType,
                ProjectionVersion = existing.ProjectionVersion, PayloadJson = "{}",
                OccurredAt = existing.OccurredAt, ProjectedAt = DateTime.UtcNow,
            });

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => verify.SaveChangesAsync());
        }

        // ------------------------------------------------------------------------------------------
        // 7. FAIL CLOSED on a malformed payload
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task An_event_with_no_payload_produces_no_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());

            var malformed = new BusinessEventEnvelope
            {
                EventUid = envelope.EventUid, EventType = envelope.EventType, Entity = envelope.Entity,
                Actor = envelope.Actor, Context = envelope.Context, PayloadVersion = 1,
                Visibility = envelope.Visibility, OccurredAt = envelope.OccurredAt,
                Payload = null,
            };

            await Consumer(host, host.Db).HandleAsync(malformed);

            using var verify = host.AllCompanies();
            Assert.Equal(0, await verify.AiProjections.CountAsync());
        }

        // ------------------------------------------------------------------------------------------
        // 8. A grant with no builder is a CONFIGURATION defect and must be loud
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_granted_shape_with_no_registered_builder_throws_rather_than_silently_ingesting_nothing()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var envelope = await SeedEventAsync(host.Db, TaskEvents.StatusChanged, CompanyA, 91, StatusChangedPayload());

            var consumer = Consumer(host, host.Db, builders: Array.Empty<IAiProjectionBuilder>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.HandleAsync(envelope));
        }

        // ------------------------------------------------------------------------------------------
        // 9. The consumer never reaches for a session
        // ------------------------------------------------------------------------------------------
        //
        // Structural, not behavioural: a background consumer that COULD reach an HTTP session would
        // resolve whichever request happened to be in flight. Proven by inspecting the constructor rather
        // than by hoping no code path calls it.
        [Fact]
        public void The_ai_consumer_takes_no_http_or_session_dependency()
        {
            var ctor = Assert.Single(typeof(AiProjectionConsumer).GetConstructors());
            foreach (var p in ctor.GetParameters())
            {
                Assert.DoesNotContain("HttpContext", p.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Session", p.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Adapter that grants the Tasks scope, so the tests exercise the REAL provider's registry
        // resolution and company check without depending on seeded role rows.
        private sealed class AlwaysAllowTasksAdapter : IModulePermissionAdapter
        {
            public string Scope => EntityRegistry.ScopeTasks;
            public Task<PermissionDecision> CanAsync(PermissionCheckRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(PermissionDecision.Allow("test adapter"));
        }
    }
}
