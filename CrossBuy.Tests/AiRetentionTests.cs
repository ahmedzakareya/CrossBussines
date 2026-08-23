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
    // AI Foundation Increment 3 — retention / expiry (R1–R12).
    //
    // The property under test: AI-derived data has a bounded lifetime, that lifetime is enforced AT READ
    // TIME (not by a sweep), and expiry is a distinct concept from revocation.
    public class AiRetentionTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

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

        private static BusinessContext User(int company) => new()
        {
            CompanyId = company, EmployeeId = 41, UserId = "u41", Source = BusinessContextSource.Http,
        };

        private static AiProjectionReader Reader(
            PlatformTestHost host, CrossBuy.Models.Context.CrossDbContext db, int company)
            => new(db, new FixedContext(User(company)), host.Registry(db),
                new PlatformPermissionProvider(host.Registry(db),
                    new IModulePermissionAdapter[] { new AllowAllAdapter() },
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

        // Seeds a projection with explicit retention metadata so each test states exactly the condition
        // it is about.
        private static async Task<long> SeedProjectionAsync(
            CrossBuy.Models.Context.CrossDbContext db, int company, int entityId,
            DateTime? expiresAt, string? retentionClass = nameof(AiRetentionClass.BusinessRecordBound),
            string projectionType = AiConsumerGrants.TaskLifecycleProjection, int version = 1)
        {
            var occurred = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = company, EntityType = EntityRegistry.Task,
                EntityId = entityId, EventType = TaskEvents.StatusChanged, Payload = "{}",
                PayloadVersion = 1, Visibility = BusinessEventVisibility.Internal, CreatedAt = occurred,
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
                OccurredAt = occurred, ProjectedAt = DateTime.UtcNow,
                RetentionClass = retentionClass, ExpiresAtUtc = expiresAt,
            };
            db.Set<AiProjection>().Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        }

        private static AiRetrievalRequest Req(string type = AiConsumerGrants.TaskLifecycleProjection)
            => new() { ProjectionType = type };

        // ==========================================================================================
        // R1 / R2 — active vs expired
        // ==========================================================================================
        [Fact]
        public async Task R1_an_active_unexpired_projection_is_readable()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddDays(30));

            var result = await Reader(host, host.Db, CompanyA).RetrieveAsync(Req());
            Assert.Single(result.Items);
        }

        [Fact]
        public async Task R2_an_expired_projection_is_not_readable()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddSeconds(-1));

            var result = await Reader(host, host.Db, CompanyA).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.Expired]);
        }

        // Read-time enforcement is the point: no cleanup has run, the row is still in the table, and it
        // is already unreadable. If retrievability depended on a sweep, expired data would stay readable
        // for exactly as long as the sweep was late.
        [Fact]
        public async Task Expiry_is_enforced_at_read_time_with_no_cleanup_having_run()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddSeconds(-1));

            using var verify = host.AllCompanies();
            Assert.Equal(1, await verify.Set<AiProjection>().CountAsync());   // still physically present
            Assert.Empty((await Reader(host, host.Db, CompanyA).RetrieveAsync(Req())).Items);
        }

        // ==========================================================================================
        // R3 — revocation still works, and is DISTINCT from expiry
        // ==========================================================================================
        [Fact]
        public async Task R3_a_revoked_projection_is_not_readable_even_when_unexpired()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddYears(1));

            var revoker = new AiProjectionRevocationService(
                host.Db, new FixedContext(User(CompanyA)), NullLogger<AiProjectionRevocationService>.Instance);
            await revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "erased");

            Assert.Empty((await Reader(host, host.Db, CompanyA).RetrieveAsync(Req())).Items);
        }

        // The two concepts must not collapse into each other: an expired row is counted as Expired, not
        // Revoked, so an operator can tell "withdrawn" from "aged out".
        [Fact]
        public async Task Expiry_and_revocation_are_reported_as_different_reasons()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddSeconds(-1));

            var result = await Reader(host, host.Db, CompanyA).RetrieveAsync(Req());
            Assert.True(result.Denied.ContainsKey(AiRetrievalDenialReason.Expired));
            Assert.False(result.Denied.ContainsKey(AiRetrievalDenialReason.Revoked));
        }

        // ==========================================================================================
        // R4 / R5 — malformed or unknown retention FAILS CLOSED (never "keeps forever")
        // ==========================================================================================
        [Fact]
        public async Task R4_a_projection_with_no_expiry_is_not_readable()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: null);

            var result = await Reader(host, host.Db, CompanyA).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.RetentionUnknown]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Forever")]
        [InlineData("Unknown")]
        public async Task R5_an_unknown_or_malformed_retention_class_fails_closed(string? retentionClass)
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91,
                expiresAt: DateTime.UtcNow.AddYears(1), retentionClass: retentionClass);

            var result = await Reader(host, host.Db, CompanyA).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.RetentionUnknown]);
        }

        // ==========================================================================================
        // R6 — unknown projection version still fails closed (Increment 2 property, re-proven)
        // ==========================================================================================
        [Fact]
        public async Task R6_an_unknown_projection_version_fails_closed()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddYears(1), version: 999);

            var result = await Reader(host, host.Db, CompanyA).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.UnsupportedVersion]);
        }

        // ==========================================================================================
        // R7 / R8 — company safety, including overlapping entity ids
        // ==========================================================================================
        [Fact]
        public async Task R7_and_R8_expiry_in_one_company_does_not_affect_another_with_the_same_entity_id()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyA);
            // Same EntityId 91 in both companies — the realistic shape, since entity ids are per-table.
            await SeedProjectionAsync(seed, CompanyA, 91, expiresAt: DateTime.UtcNow.AddSeconds(-1));   // expired
            await SeedProjectionAsync(seed, CompanyB, 91, expiresAt: DateTime.UtcNow.AddYears(1));      // live

            // Company A sees nothing (its row expired)...
            Assert.Empty((await Reader(host, seed, CompanyA).RetrieveAsync(Req())).Items);

            // ...and company B's row is untouched and still physically unexpired.
            using var verify = host.AllCompanies();
            var b = await verify.Set<AiProjection>().AsNoTracking().SingleAsync(p => p.CompanyID == CompanyB);
            Assert.NotNull(b.ExpiresAtUtc);
            Assert.True(b.ExpiresAtUtc!.Value > DateTime.UtcNow);
            Assert.Null(b.RevokedAt);
        }

        // ==========================================================================================
        // R9 / R10 — idempotency and destroyed payloads
        // ==========================================================================================
        [Fact]
        public async Task R9_repeated_revocation_is_idempotent_and_R10_the_payload_never_returns()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddYears(1));

            var revoker = new AiProjectionRevocationService(
                host.Db, new FixedContext(User(CompanyA)), NullLogger<AiProjectionRevocationService>.Instance);

            Assert.Equal(1, await revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "first"));
            Assert.Equal(0, await revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "second"));

            using var verify = host.AllCompanies();
            var row = await verify.Set<AiProjection>().AsNoTracking().SingleAsync();
            Assert.Equal("{}", row.PayloadJson);            // destroyed, and stays destroyed
            Assert.Equal("first", row.RevocationReason);    // the original stamp survives
            Assert.Empty((await Reader(host, host.Db, CompanyA).RetrieveAsync(Req())).Items);
        }

        // A tombstone keeps its audit evidence AND its retention metadata: the row's lifetime is not
        // erased by revocation, so a later retention sweep can still reason about it.
        [Fact]
        public async Task A_tombstone_retains_its_audit_and_retention_metadata()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, expiresAt: DateTime.UtcNow.AddYears(1));

            var revoker = new AiProjectionRevocationService(
                host.Db, new FixedContext(User(CompanyA)), NullLogger<AiProjectionRevocationService>.Instance);
            await revoker.RevokeEntityAsync(CompanyA, EntityRegistry.Task, 91, "audit check");

            using var verify = host.AllCompanies();
            var row = await verify.Set<AiProjection>().AsNoTracking().SingleAsync();

            Assert.NotNull(row.RevokedAt);
            Assert.NotNull(row.ExpiresAtUtc);                          // retention survives revocation
            Assert.Equal(nameof(AiRetentionClass.BusinessRecordBound), row.RetentionClass);
            Assert.True(row.BusinessEventId > 0);                       // provenance survives
        }

        // ==========================================================================================
        // R11 / R12 — every retrievable shape resolves to a bounded policy
        // ==========================================================================================
        [Fact]
        public void R11_task_lifecycle_v1_has_a_bounded_retention_policy()
        {
            var p = new AiRetentionPolicyRegistry()
                .Find(AiConsumerGrants.TaskLifecycleProjection, AiConsumerGrants.TaskLifecycleVersion);

            Assert.NotNull(p);
            Assert.Equal(AiRetentionClass.BusinessRecordBound, p!.Class);
            Assert.True(p.RetainDays is > 0);
        }

        [Fact]
        public void R12_calendar_scheduling_v1_has_a_bounded_retention_policy()
        {
            var p = new AiRetentionPolicyRegistry()
                .Find(AiConsumerGrants.CalendarSchedulingProjection, AiConsumerGrants.CalendarSchedulingVersion);

            Assert.NotNull(p);
            Assert.Equal(AiRetentionClass.ShortLived, p!.Class);
            Assert.True(p.RetainDays is > 0);
        }

        // THE governance guarantee: every shape that can be RETRIEVED must have a retention policy.
        // Adding a projection without deciding its lifetime fails here rather than creating an immortal
        // copy by omission.
        [Fact]
        public void Every_retrievable_shape_has_a_declared_retention_policy()
        {
            var shapes = new AiProjectionShapeRegistry();
            var retention = new AiRetentionPolicyRegistry();

            foreach (var s in shapes.All)
                Assert.True(retention.Find(s.ProjectionType, s.Version) != null,
                    $"Shape '{s.ProjectionType}' v{s.Version} is retrievable but has NO retention policy.");
        }

        // ...and no policy may be unbounded unless it is explicitly RevocationBound.
        [Fact]
        public void No_retention_policy_is_accidentally_unbounded()
        {
            foreach (var p in new AiRetentionPolicyRegistry().All)
            {
                Assert.True(AiRetentionPolicyRegistry.IsWellFormed(p),
                    $"Retention policy for '{p.ProjectionType}' v{p.ProjectionVersion} is malformed.");
                Assert.NotEqual(AiRetentionClass.RevocationBound, p.Class);   // nothing immortal today
            }
        }

        [Fact]
        public void An_unknown_shape_resolves_to_no_retention_policy_and_therefore_no_expiry()
        {
            var r = new AiRetentionPolicyRegistry();
            Assert.Null(r.Find("TotallyUnknownShape", 1));
            Assert.Null(r.ExpiresAt("TotallyUnknownShape", 1, DateTime.UtcNow));
            Assert.Null(r.ExpiresAt(AiConsumerGrants.TaskLifecycleProjection, 999, DateTime.UtcNow));
        }

        // Expiry is computed from when the FACT happened, not when it was projected — a rebuild must not
        // silently extend a lifetime.
        [Fact]
        public void Expiry_is_measured_from_the_business_fact_not_the_projection_time()
        {
            var occurred = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var expires = new AiRetentionPolicyRegistry()
                .ExpiresAt(AiConsumerGrants.TaskLifecycleProjection, AiConsumerGrants.TaskLifecycleVersion, occurred);

            Assert.NotNull(expires);
            Assert.Equal(occurred.AddDays(730), expires!.Value);
        }

        // ==========================================================================================
        // §21 — the FUTURE RAG lineage contract is documented and consistent with what exists
        // ==========================================================================================
        [Fact]
        public void The_future_derived_copy_contract_names_every_field_revocation_needs()
        {
            var required = AiDerivedCopyLineageContract.RequiredLineageFields;

            foreach (var field in new[] { "CompanyId", "EntityType", "EntityId",
                                          "ProjectionType", "ProjectionVersion", "ExpiresAtUtc" })
                Assert.Contains(field, required, StringComparer.Ordinal);

            // It must remain reachable by the revocation operations that actually exist — if one is
            // renamed, nameof() breaks the build rather than leaving a stale contract.
            Assert.Equal(3, AiDerivedCopyLineageContract.MustBeReachableBy.Count);
        }
    }
}
