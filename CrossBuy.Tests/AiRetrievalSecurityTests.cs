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
    // AI Foundation Increment 2 — the READ side, tested as an authorization boundary.
    //
    // The question under test is never "was this projection created correctly?" (Increment 1 proved that)
    // but "may THIS USER retrieve it NOW?" — recomputed against current state on every call.
    public class AiRetrievalSecurityTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;
        private const int UserAEmployee = 41;
        private const int UserBEmployee = 42;

        // ------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------

        // A BusinessContext accessor that returns a FIXED context — the authenticated user under test.
        // Deliberately not a mock of the whole factory: the reader's contract is "whatever the accessor
        // says the user is", and this makes the user explicit in every test.
        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _ctx;
            public FixedContext(BusinessContext? ctx) => _ctx = ctx;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default)
                => _ctx == null
                    ? throw new BusinessContextUnresolvedException("no context")
                    : Task.FromResult(_ctx);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);
        }

        private static BusinessContext User(int companyId, int employeeId, int? branchId = null) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, BranchId = branchId,
            UserId = $"user-{employeeId}", Source = BusinessContextSource.Http,
        };

        private sealed class AllowAllAdapter : IModulePermissionAdapter
        {
            private readonly string _scope;
            public AllowAllAdapter(string scope) => _scope = scope;
            public string Scope => _scope;
            public Task<PermissionDecision> CanAsync(PermissionCheckRequest r, CancellationToken ct = default)
                => Task.FromResult(PermissionDecision.Allow("test adapter"));
        }

        private sealed class DenyAllAdapter : IModulePermissionAdapter
        {
            private readonly string _scope;
            public DenyAllAdapter(string scope) => _scope = scope;
            public string Scope => _scope;
            public Task<PermissionDecision> CanAsync(PermissionCheckRequest r, CancellationToken ct = default)
                => Task.FromResult(PermissionDecision.Deny("test adapter denies"));
        }

        private static AiProjectionReader Reader(
            PlatformTestHost host, CrossBuy.Models.Context.CrossDbContext db,
            BusinessContext? user, IModulePermissionAdapter? adapter = null, IAiConsumerGrants? grants = null)
        {
            var adapters = new[] { adapter ?? new AllowAllAdapter(EntityRegistry.ScopeTasks) };
            return new AiProjectionReader(
                db,
                new FixedContext(user),
                host.Registry(db),
                new PlatformPermissionProvider(host.Registry(db), adapters, NullLogger<PlatformPermissionProvider>.Instance),
                grants ?? new AiConsumerGrants(),
                new AiProjectionShapeRegistry(),
                NullLogger<AiProjectionReader>.Instance);
        }

        private static async Task SeedTaskAsync(CrossBuy.Models.Context.CrossDbContext db, int id, int companyId)
        {
            db.TaskItems.Add(new TaskItem
            {
                ID = id, CompanyId = companyId, Title = "Chase ACME Trading LLC",
                AssigneeEmployeeId = 42, CreatedByEmployeeId = 7, Priority = "High", Status = "InProgress",
            });
            await db.SaveChangesAsync();
        }

        // Writes a projection row directly. The ingestion path is Increment 1's concern and is covered by
        // its own tests; here the row is the FIXTURE and the read decision is what is under test.
        private static async Task<long> SeedProjectionAsync(
            CrossBuy.Models.Context.CrossDbContext db, int companyId, int entityId,
            string entityType = EntityRegistry.Task,
            string eventType = TaskEvents.StatusChanged,
            string projectionType = AiConsumerGrants.TaskLifecycleProjection,
            int version = 1,
            string visibility = BusinessEventVisibility.Internal,
            string? payloadJson = null,
            DateTime? occurredAt = null,
            int? branchId = null)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId, BranchID = branchId,
                EntityType = entityType, EntityId = entityId, EventType = eventType,
                Payload = "{}", PayloadVersion = 1, Visibility = visibility,
                CreatedAt = occurredAt ?? new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc),
            };
            db.BusinessEvents.Add(ev);
            await db.SaveChangesAsync();

            var p = new AiProjection
            {
                BusinessEventId = ev.EventId, EventUid = ev.EventUid,
                Consumer = BusinessEventConsumers.AiProjection,
                CompanyID = companyId, BranchID = branchId,
                EntityType = entityType, EntityId = entityId, EventType = eventType,
                ProjectionType = projectionType, ProjectionVersion = version,
                Visibility = visibility,
                // Increment 3 made retention MANDATORY at read time: a row with no policy fails closed.
                // Production stamps these in AiProjectionConsumer; the fixture must supply what
                // production supplies, or every test here would be measuring the retention gate instead
                // of the property it is actually about. Retention itself has its own suite.
                RetentionClass = nameof(AiRetentionClass.BusinessRecordBound),
                ExpiresAtUtc = DateTime.UtcNow.AddYears(1),
                PayloadJson = payloadJson ?? JsonSerializer.Serialize(new
                {
                    taskId = entityId, action = "StatusChanged",
                    previousStatus = "New", newStatus = "InProgress", sourceModule = "Tasks",
                }),
                OccurredAt = ev.CreatedAt, ProjectedAt = DateTime.UtcNow,
            };
            db.Set<AiProjection>().Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        }

        private static AiRetrievalRequest Req(int? limit = null, string? entityType = null,
            IReadOnlyCollection<int>? ids = null, DateTime? from = null, DateTime? to = null,
            string type = AiConsumerGrants.TaskLifecycleProjection)
            => new() { ProjectionType = type, EntityType = entityType, EntityIds = ids, FromUtc = from, ToUtc = to, Limit = limit };

        // ==========================================================================================
        // §25 — USER AUTHORIZATION
        // ==========================================================================================

        [Fact]
        public async Task An_authorized_user_retrieves_the_allowed_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());

            var item = Assert.Single(result.Items);
            Assert.Equal(EntityRegistry.Task, item.EntityType);
            Assert.Equal(91, item.EntityId);
            Assert.Equal("InProgress", item.ApprovedPayload["newStatus"]);
            Assert.Equal(0, result.DeniedTotal);
        }

        // §38-H — permission lost AFTER the projection was created. The historical row must not survive
        // the change, which is the entire reason authorization is recomputed per read.
        [Fact]
        public async Task A_user_who_has_lost_permission_retrieves_nothing()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            var reader = Reader(host, host.Db, User(CompanyA, UserAEmployee),
                adapter: new DenyAllAdapter(EntityRegistry.ScopeTasks));
            var result = await reader.RetrieveAsync(Req());

            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.PermissionDenied]);
        }

        // §38-A / §38-B / §6 — cross-company. Company B's projection is invisible to a Company A user,
        // and there is NO company field on the request to spoof: the contract has none.
        [Fact]
        public async Task A_company_a_user_cannot_retrieve_a_company_b_projection()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyA);
            await SeedTaskAsync(seed, 92, CompanyB);
            await SeedProjectionAsync(seed, CompanyA, 91);
            await SeedProjectionAsync(seed, CompanyB, 92);

            var a = await Reader(host, seed, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());
            var b = await Reader(host, seed, User(CompanyB, UserBEmployee)).RetrieveAsync(Req());

            Assert.Equal(91, Assert.Single(a.Items).EntityId);
            Assert.Equal(92, Assert.Single(b.Items).EntityId);
            Assert.DoesNotContain(a.Items, i => i.EntityId == 92);
            Assert.DoesNotContain(b.Items, i => i.EntityId == 91);
        }

        // The structural half of the anti-spoofing claim: the request type has no company/branch member,
        // so "the caller cannot supply a company" is a property of the CONTRACT, not of a validation
        // routine somebody could later forget to call.
        [Fact]
        public void The_retrieval_request_contract_has_no_company_or_tenant_field()
        {
            var names = typeof(AiRetrievalRequest).GetProperties().Select(p => p.Name).ToArray();
            Assert.DoesNotContain(names, n => n.Contains("Company", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Branch", StringComparison.OrdinalIgnoreCase));
        }

        // §5 / §38 — a system context must not retrieve on a user's behalf. Ingestion may run as System;
        // retrieval may not.
        [Theory]
        [InlineData(BusinessContextSource.System)]
        [InlineData(BusinessContextSource.Worker)]
        public async Task A_system_or_worker_context_cannot_retrieve(BusinessContextSource source)
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            var machine = new BusinessContext { CompanyId = CompanyA, Source = source };
            var reader = Reader(host, host.Db, machine);

            await Assert.ThrowsAsync<AiRetrievalNotPermittedException>(() => reader.RetrieveAsync(Req()));
        }

        [Fact]
        public async Task An_unauthenticated_caller_cannot_retrieve()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);

            var reader = Reader(host, host.Db, user: null);
            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(() => reader.RetrieveAsync(Req()));
        }

        // §38-F / §20 — the source record is gone. The projection must not outlive it.
        [Fact]
        public async Task A_projection_whose_entity_no_longer_exists_is_not_returned()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91);   // no TaskItem seeded at all

            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());

            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.EntityNoLongerResolvable]);
        }

        // §38-G — the projection claims company A, but the entity now belongs to company B. The entity
        // resolution runs under the USER's company, so the stale row resolves to nothing.
        [Fact]
        public async Task A_projection_whose_entity_moved_company_is_not_returned()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            await SeedTaskAsync(seed, 91, CompanyB);           // the task really lives in company B
            await SeedProjectionAsync(seed, CompanyA, 91);     // stale row claiming company A

            var result = await Reader(host, seed, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());

            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.EntityNoLongerResolvable]);
        }

        // ==========================================================================================
        // §26 — CLASSIFICATION
        // ==========================================================================================
        //
        // Internal needs View; Confidential needs ViewConfidential; Restricted needs ViewRestricted;
        // System is refused outright. The adapter here grants only the View tier, which is what an
        // ordinary user holds — so the elevated rows must not come back.
        [Theory]
        [InlineData(BusinessEventVisibility.Internal, true)]
        [InlineData(BusinessEventVisibility.Confidential, false)]
        [InlineData(BusinessEventVisibility.Restricted, false)]
        [InlineData(BusinessEventVisibility.System, false)]
        public async Task Classification_decides_which_view_tier_is_required(string visibility, bool expectReturned)
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, visibility: visibility);

            // Grants admit only Internal, so a higher classification is refused by the grant gate first;
            // either way the row must not be returned. Both paths are legitimate denials.
            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());

            if (expectReturned) Assert.Single(result.Items);
            else { Assert.Empty(result.Items); Assert.Equal(1, result.DeniedTotal); }
        }

        // ==========================================================================================
        // §27 — PAYLOAD SAFETY
        // ==========================================================================================

        [Fact]
        public async Task A_malformed_payload_is_refused_rather_than_partially_read()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, payloadJson: "{ this is not json ");

            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.MalformedPayload]);
        }

        [Fact]
        public async Task An_oversized_payload_is_refused()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var huge = JsonSerializer.Serialize(new { taskId = 91, sourceModule = new string('x', AiRetrievalLimits.MaxPayloadBytes + 64) });
            await SeedProjectionAsync(host.Db, CompanyA, 91, payloadJson: huge);

            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.MalformedPayload]);
        }

        // §38-K — a stored payload containing fields nobody approved must not widen the result. This is
        // the read-side twin of Increment 1's write-side minimization, and it matters because the row
        // could have been written by an older build or altered in the database.
        [Fact]
        public async Task Unapproved_fields_in_a_stored_payload_do_not_reach_the_caller()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var tampered = JsonSerializer.Serialize(new
            {
                taskId = 91, newStatus = "InProgress",
                title = "Chase ACME Trading LLC",           // never approved
                customerTaxNumber = "TAX-999-SECRET",       // never approved
                employeeSalary = 12345m,                    // never approved
            });
            await SeedProjectionAsync(host.Db, CompanyA, 91, payloadJson: tampered);

            var item = Assert.Single((await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req())).Items);

            Assert.DoesNotContain("title", item.ApprovedPayload.Keys);
            Assert.DoesNotContain("customerTaxNumber", item.ApprovedPayload.Keys);
            Assert.DoesNotContain("employeeSalary", item.ApprovedPayload.Keys);
            Assert.Equal("InProgress", item.ApprovedPayload["newStatus"]);
        }

        // The public contract must never hand back raw stored JSON.
        [Fact]
        public void The_retrieval_result_exposes_no_raw_json_or_provenance()
        {
            var names = typeof(AiRetrievedProjection).GetProperties().Select(p => p.Name).ToArray();
            foreach (var banned in new[] { "PayloadJson", "CompanyID", "CompanyId", "BranchID", "BranchId",
                                           "BusinessEventId", "EventUid", "ActorEmployeeId", "Consumer" })
                Assert.DoesNotContain(banned, names, StringComparer.Ordinal);
        }

        // ==========================================================================================
        // §15 / §38-D / §38-E — VERSION AND TYPE COMPATIBILITY, no "latest" fallback
        // ==========================================================================================

        [Fact]
        public async Task An_unknown_projection_type_is_an_error_not_an_empty_page()
        {
            using var host = new PlatformTestHost(CompanyA);
            var reader = Reader(host, host.Db, User(CompanyA, UserAEmployee));
            await Assert.ThrowsAsync<AiRetrievalRequestException>(
                () => reader.RetrieveAsync(Req(type: "TotallyUnknownShape")));
        }

        [Fact]
        public async Task A_row_with_an_unsupported_version_is_refused_and_never_served_as_latest()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            await SeedProjectionAsync(host.Db, CompanyA, 91, version: 999);

            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req());
            Assert.Empty(result.Items);
            Assert.Equal(1, result.Denied[AiRetrievalDenialReason.UnsupportedVersion]);
        }

        // ==========================================================================================
        // §28 / §38-L — LIMITS
        // ==========================================================================================

        [Fact]
        public async Task The_caller_cannot_request_unlimited_results()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            for (int i = 0; i < AiRetrievalLimits.MaxLimit + 25; i++)
                await SeedProjectionAsync(host.Db, CompanyA, 91,
                    occurredAt: new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));

            var result = await Reader(host, host.Db, User(CompanyA, UserAEmployee))
                .RetrieveAsync(Req(limit: int.MaxValue));

            Assert.True(result.Items.Count <= AiRetrievalLimits.MaxLimit);
            Assert.True(result.Truncated);
        }

        [Theory]
        [InlineData(null, AiRetrievalLimits.DefaultLimit)]
        [InlineData(0, AiRetrievalLimits.DefaultLimit)]
        [InlineData(-5, AiRetrievalLimits.DefaultLimit)]
        [InlineData(10, 10)]
        [InlineData(int.MaxValue, AiRetrievalLimits.MaxLimit)]
        public void The_limit_is_clamped_not_trusted(int? requested, int expected)
            => Assert.Equal(expected, AiRetrievalLimits.Clamp(requested));

        [Fact]
        public async Task An_excessive_time_window_is_refused()
        {
            using var host = new PlatformTestHost(CompanyA);
            var reader = Reader(host, host.Db, User(CompanyA, UserAEmployee));
            await Assert.ThrowsAsync<AiRetrievalRequestException>(() => reader.RetrieveAsync(
                Req(from: new DateTime(2000, 1, 1), to: new DateTime(2030, 1, 1))));
        }

        [Fact]
        public async Task Ordering_is_deterministic_newest_first()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedTaskAsync(host.Db, 91, CompanyA);
            var t0 = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc);
            await SeedProjectionAsync(host.Db, CompanyA, 91, occurredAt: t0);
            await SeedProjectionAsync(host.Db, CompanyA, 91, occurredAt: t0.AddHours(2));
            await SeedProjectionAsync(host.Db, CompanyA, 91, occurredAt: t0.AddHours(1));

            var items = (await Reader(host, host.Db, User(CompanyA, UserAEmployee)).RetrieveAsync(Req())).Items;

            Assert.Equal(3, items.Count);
            Assert.True(items[0].OccurredAt > items[1].OccurredAt);
            Assert.True(items[1].OccurredAt > items[2].OccurredAt);
        }

        // ==========================================================================================
        // §29 — THE SECOND SHAPE goes through the SAME boundary, with no special case
        // ==========================================================================================

        [Fact]
        public async Task The_calendar_shape_retrieves_through_the_same_secure_boundary()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            seed.CalendarEvents.Add(new CrossBuy.Models.Context.Calendar.CalendarEvent
            {
                Id = 500, CompanyID = CompanyA, Title = "Board review — Project Falcon",
                StartAt = new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Utc),
                EndAt = new DateTime(2026, 3, 14, 11, 0, 0, DateTimeKind.Utc),
                OwnerEmpId = 7,
            });
            await seed.SaveChangesAsync();

            await SeedProjectionAsync(seed, CompanyA, 500,
                entityType: EntityRegistry.CalendarEvent,
                eventType: CalendarEventEvents.Rescheduled,
                projectionType: AiConsumerGrants.CalendarSchedulingProjection,
                payloadJson: JsonSerializer.Serialize(new
                {
                    calendarEventId = 500, action = "Rescheduled", organizerEmployeeId = 7,
                    startUtc = "2026-03-14T10:00:00Z", isAllDay = false, attendeeCount = 4,
                    title = "Board review — Project Falcon",     // stored but never approved
                    sourceModule = "Calendar",
                }));

            var reader = Reader(host, seed, User(CompanyA, UserAEmployee),
                adapter: new AllowAllAdapter(EntityRegistry.ScopeCalendar));
            var result = await reader.RetrieveAsync(Req(type: AiConsumerGrants.CalendarSchedulingProjection));

            var item = Assert.Single(result.Items);
            Assert.Equal(EntityRegistry.CalendarEvent, item.EntityType);
            Assert.Equal(500, item.EntityId);
            Assert.Equal(4, item.ApprovedPayload["attendeeCount"]);
            // The meeting SUBJECT never crosses, even though it sits in the stored row.
            Assert.DoesNotContain("title", item.ApprovedPayload.Keys);
            Assert.DoesNotContain(item.ApprovedPayload.Values,
                v => v is string s && s.Contains("Falcon", StringComparison.OrdinalIgnoreCase));
        }

        // A calendar user in company A must not reach a company B calendar projection either — the second
        // shape gets no weaker boundary than the first.
        [Fact]
        public async Task The_second_shape_is_company_isolated_too()
        {
            using var host = new PlatformTestHost(CompanyA);
            using var seed = host.AllCompanies();
            seed.CalendarEvents.Add(new CrossBuy.Models.Context.Calendar.CalendarEvent
            {
                Id = 501, CompanyID = CompanyB, Title = "B only",
                StartAt = DateTime.UtcNow, EndAt = DateTime.UtcNow.AddHours(1), OwnerEmpId = 9,
            });
            await seed.SaveChangesAsync();
            await SeedProjectionAsync(seed, CompanyB, 501,
                entityType: EntityRegistry.CalendarEvent,
                eventType: CalendarEventEvents.Created,
                projectionType: AiConsumerGrants.CalendarSchedulingProjection);

            var reader = Reader(host, seed, User(CompanyA, UserAEmployee),
                adapter: new AllowAllAdapter(EntityRegistry.ScopeCalendar));
            var result = await reader.RetrieveAsync(Req(type: AiConsumerGrants.CalendarSchedulingProjection));

            Assert.Empty(result.Items);
        }

        // ==========================================================================================
        // §33 — NO MODEL DEPENDENCY
        // ==========================================================================================
        [Fact]
        public void The_retrieval_layer_takes_no_model_provider_dependency()
        {
            foreach (var t in new[] { typeof(AiProjectionReader), typeof(AiProjectionRevocationService) })
            {
                var ctor = Assert.Single(t.GetConstructors());
                foreach (var p in ctor.GetParameters())
                {
                    var n = p.ParameterType.FullName ?? p.ParameterType.Name;
                    foreach (var banned in new[] { "OpenAi", "OpenAI", "Azure", "Llm", "Embedding", "Vector",
                                                   "HttpClient", "IAiService", "IAiInsightsService" })
                        Assert.DoesNotContain(banned, n, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // §4 / §11 — the reader must not hand out a queryable surface a caller could shape itself.
        [Fact]
        public void The_reader_exposes_no_queryable_or_dbset()
        {
            foreach (var m in typeof(IAiProjectionReader).GetMethods())
            {
                var n = m.ReturnType.FullName ?? "";
                Assert.DoesNotContain("IQueryable", n, StringComparison.Ordinal);
                Assert.DoesNotContain("DbSet", n, StringComparison.Ordinal);
                foreach (var p in m.GetParameters())
                {
                    var pn = p.ParameterType.FullName ?? "";
                    Assert.DoesNotContain("Expression", pn, StringComparison.Ordinal);
                    Assert.DoesNotContain("IQueryable", pn, StringComparison.Ordinal);
                }
            }
        }
    }
}
