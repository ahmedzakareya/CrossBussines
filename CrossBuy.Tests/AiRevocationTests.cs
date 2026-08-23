using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 2 — revocation.
    //
    // The property under test throughout is TENANT SAFETY: a revocation that could reach another
    // company's rows would be a worse defect than the exposure it was meant to fix.
    public class AiRevocationTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _ctx;
            public FixedContext(BusinessContext? c) => _ctx = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default)
                => _ctx == null ? throw new BusinessContextUnresolvedException("none") : Task.FromResult(_ctx);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);
        }

        private sealed class AllowAllAdapter : IModulePermissionAdapter
        {
            public string Scope => EntityRegistry.ScopeTasks;
            public Task<PermissionDecision> CanAsync(PermissionCheckRequest r, CancellationToken ct = default)
                => Task.FromResult(PermissionDecision.Allow("test"));
        }

        private static BusinessContext User(int company, int employee = 41) => new()
        {
            CompanyId = company, EmployeeId = employee, UserId = $"user-{employee}",
            Source = BusinessContextSource.Http,
        };

        private static AiProjectionRevocationService Revoker(
            CrossBuy.Models.Context.CrossDbContext db, BusinessContext? actor)
            => new(db, new FixedContext(actor), NullLogger<AiProjectionRevocationService>.Instance);

        private static AiProjectionReader Reader(
            PlatformTestHost host, CrossBuy.Models.Context.CrossDbContext db, BusinessContext user)
            => new(db, new FixedContext(user), host.Registry(db),
                new PlatformPermissionProvider(host.Registry(db), new IModulePermissionAdapter[] { new AllowAllAdapter() },
                    NullLogger<PlatformPermissionProvider>.Instance),
                new AiConsumerGrants(), new AiProjectionShapeRegistry(),
                NullLogger<AiProjectionReader>.Instance);

        private static async Task SeedTaskAsync(CrossBuy.Models.Context.CrossDbContext db, int id, int company)
        {
            db.TaskItems.Add(new TaskItem
            {
                ID = id, CompanyId = company, Title = "t", AssigneeEmployeeId = 42,
                CreatedByEmployeeId = 7, Priority = "Normal", Status = "New",
            });
            await db.SaveChangesAsync();
        }

        private static async Task<long> SeedProjectionAsync(
            CrossBuy.Models.Context.CrossDbContext db, int company, int entityId,
            string projectionType = AiConsumerGrants.TaskLifecycleProjection, int version = 1)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = company, EntityType = EntityRegistry.Task,
                EntityId = entityId, EventType = TaskEvents.StatusChanged, Payload = "{}",
                PayloadVersion = 1, Visibility = BusinessEventVisibility.Internal,
                CreatedAt = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc),
            };
            db.BusinessEvents.Add(ev);
            await db.SaveChangesAsync();

            var p = new AiProjection
            {
                BusinessEventId = ev.EventId, EventUid = ev.EventUid,
                Consumer = BusinessEventConsumers.AiProjection, CompanyID = company,
                EntityType = EntityRegistry.Task, EntityId = entityId, EventType = TaskEvents.StatusChanged,
                ProjectionType = projectionType, ProjectionVersion = version,
                Visibility = BusinessEventVisibility.Internal,
                PayloadJson = JsonSerializer.Serialize(new { taskId = entityId, newStatus = "InProgress" }),
                OccurredAt = ev.CreatedAt, ProjectedAt = DateTime.UtcNow,
                // Increment 3: retention is mandatory at read time. Supplied here so these tests measure
                // REVOCATION rather than the retention gate — expiry has its own suite, and the two must
                // stay separable.
                RetentionClass = nameof(AiRetentionClass.BusinessRecordBound),
                ExpiresAtUtc = DateTime.UtcNow.AddYears(1),
            };
            db.Set<AiProjection>().Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        }

        // ==========================================================================================
        // §30.1 — entity revocation ends retrievability
        // ==========================================================================================
        [Fact]
        public async Task Revoking_an_entity_makes_its_projection_unretrievable()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            var req = new AiRetrievalRequest { ProjectionType = AiConsumerGrants.TaskLifecycleProjection };
            Assert.Single((await Reader(host, host.Db, User(CompanyA)).RetrieveAsync(req)).Items);

            int n = await Revoker(host.Db, User(CompanyA)).RevokeEntityAsync(
                CompanyA, EntityRegistry.Task, 91, "entity deleted by owner");
            Assert.Equal(1, n);

            Assert.Empty((await Reader(host, host.Db, User(CompanyA)).RetrieveAsync(req)).Items);
        }

        // §18 — the payload is destroyed, the AUDIT EVIDENCE is retained.
        [Fact]
        public async Task Revocation_destroys_the_payload_but_keeps_the_audit_row()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            await Revoker(host.Db, User(CompanyA, employee: 7)).RevokeEntityAsync(
                CompanyA, EntityRegistry.Task, 91, "GDPR erasure request 4471");

            using var verify = host.AllCompanies();
            var row = await verify.Set<AiProjection>().AsNoTracking().SingleAsync();

            Assert.NotNull(row.RevokedAt);
            Assert.Equal("employee:7", row.RevokedBy);
            Assert.Equal("GDPR erasure request 4471", row.RevocationReason);
            Assert.Equal("{}", row.PayloadJson);            // content gone
            Assert.Equal(91, row.EntityId);                  // evidence retained
            Assert.True(row.BusinessEventId > 0);            // provenance retained
        }

        // ==========================================================================================
        // §17 / §30.2 — TENANT SAFETY. The same EntityId exists in every company.
        // ==========================================================================================
        [Fact]
        public async Task Revoking_an_entity_in_company_a_does_not_touch_the_same_id_in_company_b()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            // No TaskItem is seeded: TaskItem.ID is the primary key, so the same id cannot exist twice,
            // and revocation does not read the source entity anyway. The point of this test is that two
            // PROJECTIONS carry the same EntityId in different companies — which is the realistic shape,
            // because entity ids are per-table, not global.
            await SeedProjectionAsync(seed, CompanyA, 91);
            await SeedProjectionAsync(seed, CompanyB, 91);

            int n = await Revoker(seed, User(CompanyA)).RevokeEntityAsync(
                CompanyA, EntityRegistry.Task, 91, "company A cleanup");

            Assert.Equal(1, n);
            using var verify = host.AllCompanies();
            Assert.NotNull((await verify.Set<AiProjection>().AsNoTracking().SingleAsync(p => p.CompanyID == CompanyA)).RevokedAt);
            Assert.Null((await verify.Set<AiProjection>().AsNoTracking().SingleAsync(p => p.CompanyID == CompanyB)).RevokedAt);
        }

        [Fact]
        public async Task Revoking_a_whole_company_does_not_touch_another_company()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyA);
            await SeedTaskAsync(seed, 92, CompanyA);
            await SeedTaskAsync(seed, 93, CompanyB);
            await SeedProjectionAsync(seed, CompanyA, 91);
            await SeedProjectionAsync(seed, CompanyA, 92);
            await SeedProjectionAsync(seed, CompanyB, 93);

            int n = await Revoker(seed, User(CompanyA)).RevokeCompanyAsync(CompanyA, "tenant offboarded");

            Assert.Equal(2, n);
            using var verify = host.AllCompanies();
            Assert.Equal(2, await verify.Set<AiProjection>().CountAsync(p => p.CompanyID == CompanyA && p.RevokedAt != null));
            Assert.Equal(0, await verify.Set<AiProjection>().CountAsync(p => p.CompanyID == CompanyB && p.RevokedAt != null));
        }

        // A company-less revocation must fail closed — it would otherwise reach every tenant at once.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task A_revocation_with_no_company_is_refused(int companyId)
        {
            using var host = new PlatformTestHost(CompanyA);
            var revoker = Revoker(host.Db, User(CompanyA));
            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(
                () => revoker.RevokeCompanyAsync(companyId, "should never run"));
            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(
                () => revoker.RevokeEntityAsync(companyId, EntityRegistry.Task, 91, "should never run"));
        }

        // ==========================================================================================
        // §30.5 — a retired projection VERSION/shape
        // ==========================================================================================
        [Fact]
        public async Task Revoking_a_projection_type_removes_only_that_shape()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyA);
            await SeedProjectionAsync(seed, CompanyA, 91, AiConsumerGrants.TaskLifecycleProjection);
            await SeedProjectionAsync(seed, CompanyA, 91, AiConsumerGrants.CalendarSchedulingProjection);

            int n = await Revoker(seed, User(CompanyA)).RevokeProjectionTypeAsync(
                CompanyA, AiConsumerGrants.TaskLifecycleProjection, projectionVersion: null, "shape retired");

            Assert.Equal(1, n);
            using var verify = host.AllCompanies();
            Assert.NotNull((await verify.Set<AiProjection>().AsNoTracking()
                .SingleAsync(p => p.ProjectionType == AiConsumerGrants.TaskLifecycleProjection)).RevokedAt);
            Assert.Null((await verify.Set<AiProjection>().AsNoTracking()
                .SingleAsync(p => p.ProjectionType == AiConsumerGrants.CalendarSchedulingProjection)).RevokedAt);
        }

        // ==========================================================================================
        // §30.7 / §38-J — IDEMPOTENT. A second run must not re-stamp or double-count.
        // ==========================================================================================
        [Fact]
        public async Task Revocation_is_idempotent_and_does_not_rewrite_the_original_stamp()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            var revoker = Revoker(host.Db, User(CompanyA));
            Assert.Equal(1, await revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "first"));

            using (var check = host.AllCompanies())
            {
                var first = await check.Set<AiProjection>().AsNoTracking().SingleAsync();
                Assert.Equal("first", first.RevocationReason);

                // Second run: affects nothing.
                Assert.Equal(0, await revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "second"));
            }

            using var verify = host.AllCompanies();
            var row = await verify.Set<AiProjection>().AsNoTracking().SingleAsync();
            // The ORIGINAL reason survives — re-stamping would destroy the record of when the data
            // actually stopped being retrievable.
            Assert.Equal("first", row.RevocationReason);
        }

        // §30.6 — a revoked row must not reappear on a subsequent read through a fresh context.
        [Fact]
        public async Task Revoked_data_does_not_reappear_from_a_fresh_context()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);
            await Revoker(host.Db, User(CompanyA)).RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "erased");

            using var fresh = host.AllCompanies();
            var result = await Reader(host, fresh, User(CompanyA))
                .RetrieveAsync(new AiRetrievalRequest { ProjectionType = AiConsumerGrants.TaskLifecycleProjection });

            Assert.Empty(result.Items);
            // Filtered out in SQL, so it is not even a counted denial — it never becomes a candidate.
            Assert.Equal(0, result.Considered);
        }

        // An unattributed (background) revocation is still recorded rather than refused.
        [Fact]
        public async Task A_revocation_with_no_signed_in_actor_is_recorded_as_system()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            await Revoker(host.Db, actor: null).RevokeEntityAsync(
                CompanyA, EntityRegistry.Task, 91, "background retention sweep");

            using var verify = host.AllCompanies();
            Assert.Equal("system", (await verify.Set<AiProjection>().AsNoTracking().SingleAsync()).RevokedBy);
        }

        // A revocation with no reason is refused: the reason IS the audit evidence this operation exists
        // to leave behind.
        [Fact]
        public async Task A_revocation_without_a_reason_is_refused()
        {
            using var host = new PlatformTestHost(CompanyA);
            var revoker = Revoker(host.Db, User(CompanyA));
            await Assert.ThrowsAsync<ArgumentException>(
                () => revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "  "));
        }

        [Fact]
        public async Task Revoked_rows_are_counted_for_observability()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyA);
            await SeedTaskAsync(seed, 92, CompanyB);
            await SeedProjectionAsync(seed, CompanyA, 91);
            await SeedProjectionAsync(seed, CompanyB, 92);

            var revoker = Revoker(seed, User(CompanyA));
            await revoker.RevokeCompanyAsync(CompanyA, "cleanup");

            Assert.Equal(1, await revoker.CountRevokedAsync(CompanyA));
            Assert.Equal(0, await revoker.CountRevokedAsync(CompanyB));
        }
    }
}
