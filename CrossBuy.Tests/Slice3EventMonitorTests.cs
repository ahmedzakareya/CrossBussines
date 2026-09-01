using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 Batch B — the Business Event Monitor.
    //
    // The monitor is the first screen that reads the event stream ACROSS modules, so the tests here are mostly
    // negative: what an operator must NOT be able to see, and what a retry must NOT be able to do. Every assertion
    // goes through IBusinessEventMonitorService / IEventDispatchStore — none of them reimplements a filter or a
    // status transition, because the rules under test are exactly those implementations.
    public class Slice3EventMonitorTests
    {
        private const string Timeline = BusinessEventConsumers.TimelineProjection;
        private const string Notifications = BusinessEventConsumers.NotificationProjection;

        private static async Task<BusinessEvent> AddEventAsync(
            CrossBuy.Models.Context.CrossDbContext db,
            int companyId = 1, int entityId = 5, int? branchId = null,
            string visibility = BusinessEventVisibility.Internal,
            string? payload = null, DateTime? createdAt = null,
            string entityType = EntityRegistry.SalesInvoice, string? eventType = null,
            Guid? correlationId = null)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId, BranchID = branchId,
                EntityType = entityType, EntityId = entityId,
                EventType = eventType ?? (entityType + ".Created"),
                Visibility = visibility, Payload = payload, PayloadVersion = 1,
                CorrelationId = correlationId, CreatedAt = createdAt ?? DateTime.UtcNow,
            };
            db.BusinessEvents.Add(ev);
            await db.SaveChangesAsync();
            return ev;
        }

        private static async Task<BusinessEventDispatch> AddDispatchAsync(
            CrossBuy.Models.Context.CrossDbContext db, long eventId, string consumer,
            string status = BusinessEventDispatchStatus.Pending, int attempts = 0,
            string? error = null, DateTime? updatedAt = null)
        {
            var row = new BusinessEventDispatch
            {
                EventId = eventId, Consumer = consumer, Status = status, Attempts = attempts,
                Error = error, UpdatedAt = updatedAt ?? DateTime.UtcNow,
            };
            db.BusinessEventDispatches.Add(row);
            await db.SaveChangesAsync();
            return row;
        }

        private static BusinessEventMonitorFilter All => new() { PageSize = 100 };

        // =========================================================================================
        // Company isolation
        // =========================================================================================

        // ---- Batch B item 38: a non-elevated operator sees only their own company ----
        [Fact]
        public async Task An_operator_without_the_cross_company_right_sees_only_their_own_company()
        {
            using var host = new PlatformTestHost();
            var mine = await AddEventAsync(host.Db, companyId: 1, entityId: 11);
            var theirs = await AddEventAsync(host.Seed, companyId: 2, entityId: 22);
            await AddDispatchAsync(host.Db, mine.EventId, Timeline);
            await AddDispatchAsync(host.Db, theirs.EventId, Timeline);

            var page = await host.Monitor().SearchAsync(All, PlatformTestHost.DefaultContext(companyId: 1), crossCompany: false);

            Assert.Equal(1, page.Total);
            Assert.Equal(mine.EventId, Assert.Single(page.Rows).EventId);
            // The summary is computed over the SAME predicate, so it must not leak the other company's row either.
            Assert.Equal(1, page.Summary.Pending);
            Assert.Equal(1, page.Summary.TotalEvents);
        }

        // ---- A hand-edited CompanyId in the query string cannot widen the scope ----
        [Fact]
        public async Task Passing_another_CompanyId_does_not_widen_a_company_scoped_search()
        {
            using var host = new PlatformTestHost();
            var mine = await AddEventAsync(host.Db, companyId: 1, entityId: 11);
            var theirs = await AddEventAsync(host.Seed, companyId: 2, entityId: 22);
            await AddDispatchAsync(host.Db, mine.EventId, Timeline);
            await AddDispatchAsync(host.Db, theirs.EventId, Timeline);

            // The URL says "company 2". The caller holds no cross-company right.
            var filter = new BusinessEventMonitorFilter { CompanyId = 2, PageSize = 100 };
            var page = await host.Monitor().SearchAsync(filter, PlatformTestHost.DefaultContext(companyId: 1), crossCompany: false);

            Assert.Equal(mine.EventId, Assert.Single(page.Rows).EventId);   // pinned to company 1, not switched to 2
        }

        // ---- The elevated operator can genuinely cross companies, and can still narrow to one ----
        [Fact]
        public async Task An_elevated_operator_sees_every_company_and_can_narrow_to_one()
        {
            using var host = new PlatformTestHost();
            var mine = await AddEventAsync(host.Db, companyId: 1, entityId: 11);
            var theirs = await AddEventAsync(host.Seed, companyId: 2, entityId: 22);
            await AddDispatchAsync(host.Db, mine.EventId, Timeline);
            await AddDispatchAsync(host.Db, theirs.EventId, Timeline);
            // Stage 1 Batch B / B3: the elevated view now takes an audited PlatformMonitoring bypass, so the
            // context must actually hold the platform-admin right rather than merely asking for crossCompany.
            var ctx = PlatformTestHost.AdminContext(companyId: 1);

            var both = await host.Monitor().SearchAsync(All, ctx, crossCompany: true);
            Assert.Equal(2, both.Total);

            var narrowed = await host.Monitor().SearchAsync(
                new BusinessEventMonitorFilter { CompanyId = 2, PageSize = 100 }, ctx, crossCompany: true);
            Assert.Equal(theirs.EventId, Assert.Single(narrowed.Rows).EventId);
        }

        // ---- Details is company-isolated too, and answers exactly as it would for a missing row ----
        [Fact]
        public async Task Details_for_another_companys_event_is_indistinguishable_from_not_found()
        {
            using var host = new PlatformTestHost();
            var theirs = await AddEventAsync(host.Seed, companyId: 2, entityId: 22);
            await AddDispatchAsync(host.Db, theirs.EventId, Timeline);
            var ctx = PlatformTestHost.DefaultContext(companyId: 1);

            Assert.Null(await host.Monitor().GetDetailsAsync(theirs.EventId, ctx, crossCompany: false, maySeeRestricted: false));
            // ...and a genuinely absent id gives the same answer, so nothing can be inferred from the difference.
            Assert.Null(await host.Monitor().GetDetailsAsync(999_999, ctx, crossCompany: false, maySeeRestricted: false));

            // Elevated, the same id resolves — proving the null above was the isolation rule, not a broken query.
            // B3: elevation is a right that must be held, so the elevated call presents an admin context.
            Assert.NotNull(await host.Monitor().GetDetailsAsync(
                theirs.EventId, PlatformTestHost.AdminContext(companyId: 1), crossCompany: true, maySeeRestricted: false));
        }

        // =========================================================================================
        // Payload safety
        // =========================================================================================

        // ---- Batch B item 39: Restricted/System payloads are masked, not merely un-linked ----
        [Theory]
        [InlineData(BusinessEventVisibility.Restricted)]
        [InlineData(BusinessEventVisibility.System)]
        public async Task A_restricted_or_system_payload_is_masked_for_a_non_elevated_operator(string visibility)
        {
            using var host = new PlatformTestHost();
            const string secret = "{\"salary\":123456}";
            var ev = await AddEventAsync(host.Db, visibility: visibility, payload: secret);
            await AddDispatchAsync(host.Db, ev.EventId, Timeline);
            var ctx = PlatformTestHost.DefaultContext();

            var masked = await host.Monitor().GetDetailsAsync(ev.EventId, ctx, crossCompany: false, maySeeRestricted: false);
            Assert.NotNull(masked);
            Assert.True(masked!.PayloadMasked);
            Assert.Null(masked.Payload);
            Assert.DoesNotContain("123456", masked.PayloadMaskReason ?? "");
            Assert.Contains(visibility, masked.PayloadMaskReason!);
            // The metadata still shows, so the row stays diagnosable without the payload.
            Assert.Equal(ev.EventType, masked.Header.EventType);
            Assert.Single(masked.Consumers);

            var elevated = await host.Monitor().GetDetailsAsync(ev.EventId, ctx, crossCompany: false, maySeeRestricted: true);
            Assert.False(elevated!.PayloadMasked);
            Assert.Contains("123456", elevated.Payload!);
        }

        // ---- Internal/Confidential payloads are visible to any operator who may open the screen ----
        [Theory]
        [InlineData(BusinessEventVisibility.Internal)]
        [InlineData(BusinessEventVisibility.Confidential)]
        public async Task An_internal_or_confidential_payload_is_shown_without_elevation(string visibility)
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db, visibility: visibility, payload: "{\"invoiceNumber\":\"SI-1\"}");
            await AddDispatchAsync(host.Db, ev.EventId, Timeline);

            var vm = await host.Monitor().GetDetailsAsync(
                ev.EventId, PlatformTestHost.DefaultContext(), crossCompany: false, maySeeRestricted: false);

            Assert.False(vm!.PayloadMasked);
            Assert.Contains("SI-1", vm.Payload!);
            Assert.False(vm.PayloadTruncated);
        }

        // ---- An oversized payload is truncated with an explicit marker, never silently cut ----
        [Fact]
        public async Task An_oversized_payload_is_truncated_and_says_so()
        {
            using var host = new PlatformTestHost();
            var big = "{\"blob\":\"" + new string('x', BusinessEventMonitorService.PayloadPreviewBytes + 2_000) + "\"}";
            var ev = await AddEventAsync(host.Db, payload: big);
            await AddDispatchAsync(host.Db, ev.EventId, Timeline);

            var vm = await host.Monitor().GetDetailsAsync(
                ev.EventId, PlatformTestHost.DefaultContext(), crossCompany: false, maySeeRestricted: false);

            Assert.True(vm!.PayloadTruncated);
            Assert.True(vm.Payload!.Length < big.Length);
            Assert.Contains("truncated", vm.Payload);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(big), vm.PayloadBytes);   // the REAL size is still reported
        }

        // ---- A corrupt payload is shown as stored rather than throwing the screen away ----
        [Fact]
        public async Task A_payload_that_is_not_valid_json_is_shown_as_stored()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db, payload: "{ this is not json");
            await AddDispatchAsync(host.Db, ev.EventId, Timeline);

            var vm = await host.Monitor().GetDetailsAsync(
                ev.EventId, PlatformTestHost.DefaultContext(), crossCompany: false, maySeeRestricted: false);

            Assert.Equal("{ this is not json", vm!.Payload);
            Assert.False(vm.PayloadMasked);
        }

        // =========================================================================================
        // Filtering and paging
        // =========================================================================================

        // ---- Batch B item 40: paging is server-side, capped, and stable ----
        [Fact]
        public async Task Paging_is_server_side_capped_and_never_repeats_or_skips_a_row()
        {
            using var host = new PlatformTestHost();
            // All 40 events share ONE timestamp, which is precisely what breaks a naive ORDER BY CreatedAt.
            var stamp = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
            for (int i = 1; i <= 40; i++)
            {
                var ev = await AddEventAsync(host.Db, entityId: i, createdAt: stamp);
                await AddDispatchAsync(host.Db, ev.EventId, Timeline);
            }
            var ctx = PlatformTestHost.DefaultContext();
            var monitor = host.Monitor();

            var seen = new List<long>();
            for (int p = 1; p <= 4; p++)
            {
                var page = await monitor.SearchAsync(new BusinessEventMonitorFilter { Page = p, PageSize = 10 }, ctx, false);
                Assert.Equal(40, page.Total);
                Assert.Equal(4, page.Pages);
                Assert.Equal(10, page.Rows.Count);
                seen.AddRange(page.Rows.Select(r => r.DispatchId!.Value));
            }
            Assert.Equal(40, seen.Distinct().Count());   // every row exactly once across the four pages

            // PageSize is capped server-side: a caller asking for 5000 gets MaxPageSize, not the table.
            var huge = await monitor.SearchAsync(new BusinessEventMonitorFilter { PageSize = 5000 }, ctx, false);
            Assert.Equal(BusinessEventMonitorFilter.MaxPageSize, huge.PageSize);
            Assert.True(huge.Rows.Count <= BusinessEventMonitorFilter.MaxPageSize);

            // And nonsense paging is normalised rather than throwing or returning a negative skip.
            var silly = await monitor.SearchAsync(new BusinessEventMonitorFilter { Page = -3, PageSize = 0 }, ctx, false);
            Assert.Equal(1, silly.Page);
            Assert.Equal(25, silly.PageSize);
        }

        // ---- An unregistered filter value returns nothing instead of probing the table ----
        [Fact]
        public async Task Unregistered_filter_values_return_nothing_rather_than_ignoring_the_filter()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, ev.EventId, Timeline);
            var ctx = PlatformTestHost.DefaultContext();
            var monitor = host.Monitor();

            // The dangerous failure mode is "unknown value => filter dropped => everything returned".
            Assert.Empty((await monitor.SearchAsync(new BusinessEventMonitorFilter { EntityType = "NotAnEntity" }, ctx, false)).Rows);
            Assert.Empty((await monitor.SearchAsync(new BusinessEventMonitorFilter { Consumer = "NotAConsumer" }, ctx, false)).Rows);
            Assert.Empty((await monitor.SearchAsync(new BusinessEventMonitorFilter { DispatchStatus = "Working" }, ctx, false)).Rows);

            // Registered values still work, so the guard is not simply breaking the filters.
            Assert.Single((await monitor.SearchAsync(new BusinessEventMonitorFilter { EntityType = EntityRegistry.SalesInvoice }, ctx, false)).Rows);
            Assert.Single((await monitor.SearchAsync(new BusinessEventMonitorFilter { Consumer = Timeline }, ctx, false)).Rows);
            Assert.Single((await monitor.SearchAsync(new BusinessEventMonitorFilter { DispatchStatus = BusinessEventDispatchStatus.Pending }, ctx, false)).Rows);
        }

        // ---- Every remaining filter narrows, and the summary follows the same predicate ----
        [Fact]
        public async Task The_summary_counts_the_filtered_set_not_the_whole_table()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };

            var ok = await AddEventAsync(host.Db, entityId: 1);
            await AddDispatchAsync(host.Db, ok.EventId, Timeline, BusinessEventDispatchStatus.Done);

            var broken = await AddEventAsync(host.Db, entityId: 2);
            await AddDispatchAsync(host.Db, broken.EventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 5, error: "SMTP timeout\nstack…");
            await AddDispatchAsync(host.Db, broken.EventId, Notifications, BusinessEventDispatchStatus.Pending);

            var ctx = PlatformTestHost.DefaultContext();
            var monitor = host.Monitor(options: options);

            var all = await monitor.SearchAsync(All, ctx, false);
            Assert.Equal(3, all.Total);
            Assert.Equal(1, all.Summary.Done);
            Assert.Equal(1, all.Summary.Failed);
            Assert.Equal(1, all.Summary.Pending);
            Assert.Equal(1, all.Summary.Exhausted);        // Failed AND Attempts >= MaxAttempts
            Assert.Equal(2, all.Summary.TotalEvents);      // three dispatch rows over two events

            // Narrow to errors only: the cards must move with the grid.
            var errors = await monitor.SearchAsync(new BusinessEventMonitorFilter { HasError = true, PageSize = 100 }, ctx, false);
            Assert.Equal(1, errors.Total);
            Assert.Equal(0, errors.Summary.Done);
            Assert.Equal(1, errors.Summary.Failed);
            Assert.Equal(1, errors.Summary.TotalEvents);
            // The grid shows a one-line summary, not the whole stack.
            Assert.Equal("SMTP timeout", Assert.Single(errors.Rows).ErrorSummary);

            var noErrors = await monitor.SearchAsync(new BusinessEventMonitorFilter { HasError = false, PageSize = 100 }, ctx, false);
            Assert.Equal(2, noErrors.Total);

            var attempts = await monitor.SearchAsync(new BusinessEventMonitorFilter { MinAttempts = 5, PageSize = 100 }, ctx, false);
            Assert.Equal(1, attempts.Total);
        }

        // ---- Identity, date and correlation filters ----
        [Fact]
        public async Task Uid_correlation_date_and_entity_filters_each_narrow_the_result()
        {
            using var host = new PlatformTestHost();
            var correlation = Guid.NewGuid();
            var old = await AddEventAsync(host.Db, entityId: 1, createdAt: new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc));
            var recent = await AddEventAsync(host.Db, entityId: 2, createdAt: new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc),
                correlationId: correlation, branchId: 3);
            await AddDispatchAsync(host.Db, old.EventId, Timeline);
            await AddDispatchAsync(host.Db, recent.EventId, Timeline);
            var ctx = PlatformTestHost.DefaultContext();
            var monitor = host.Monitor();

            async Task<long> Only(BusinessEventMonitorFilter f)
                => Assert.Single((await monitor.SearchAsync(f, ctx, false)).Rows).EventId;

            Assert.Equal(recent.EventId, await Only(new BusinessEventMonitorFilter { EventUid = recent.EventUid }));
            Assert.Equal(recent.EventId, await Only(new BusinessEventMonitorFilter { CorrelationId = correlation }));
            Assert.Equal(recent.EventId, await Only(new BusinessEventMonitorFilter { BranchId = 3 }));
            Assert.Equal(recent.EventId, await Only(new BusinessEventMonitorFilter { EntityId = 2 }));
            Assert.Equal(old.EventId, await Only(new BusinessEventMonitorFilter { DateTo = new DateTime(2026, 1, 5) }));
            Assert.Equal(recent.EventId, await Only(new BusinessEventMonitorFilter { DateFrom = new DateTime(2026, 2, 1) }));
            // DateTo is inclusive of the whole day, so an event later on the boundary date is still found.
            Assert.Equal(old.EventId, await Only(new BusinessEventMonitorFilter
            {
                DateFrom = new DateTime(2026, 1, 5), DateTo = new DateTime(2026, 1, 5),
            }));
            Assert.Equal(recent.EventId, await Only(new BusinessEventMonitorFilter { EventType = "SalesInvoice.Created", EntityId = 2 }));
        }

        // ---- The grid resolves the entity through the registry, and survives an unregistered stored value ----
        [Fact]
        public async Task An_unregistered_stored_EntityType_still_renders_without_a_link()
        {
            using var host = new PlatformTestHost();
            // A legacy/foreign row: the code is not in the registry. The screen must degrade, not throw.
            var ev = await AddEventAsync(host.Db, entityType: "LegacyThing", eventType: "LegacyThing.Created");
            await AddDispatchAsync(host.Db, ev.EventId, Timeline);

            var row = Assert.Single((await host.Monitor().SearchAsync(All, PlatformTestHost.DefaultContext(), false)).Rows);
            Assert.Equal("LegacyThing", row.EntityLabel);
            Assert.Null(row.EntityUrl);

            // A registered type does get a link, so the null above is the fallback and not a broken registry.
            var known = await AddEventAsync(host.Db, entityId: 77);
            await AddDispatchAsync(host.Db, known.EventId, Timeline);
            var page = await host.Monitor().SearchAsync(new BusinessEventMonitorFilter { EntityId = 77 }, PlatformTestHost.DefaultContext(), false);
            Assert.False(string.IsNullOrEmpty(Assert.Single(page.Rows).EntityUrl));
        }

        // =========================================================================================
        // Retry safety
        // =========================================================================================

        // ---- Batch B item 41: a Done consumer can never be replayed ----
        [Fact]
        public async Task A_completed_consumer_is_never_re_queued()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            var done = await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Done);

            var result = await host.Monitor().RetryAsync(done.ID, PlatformTestHost.DefaultContext(), "operator asked", false);

            Assert.False(result.Success);
            Assert.Equal(DispatchRetryOutcome.AlreadyDone, result.Outcome);

            using var verify = host.NewContext();
            Assert.Equal(BusinessEventDispatchStatus.Done, (await verify.BusinessEventDispatches.SingleAsync()).Status);
        }

        // ---- An already-queued row is refused rather than reported as an action that did something ----
        [Fact]
        public async Task An_already_pending_row_is_refused_instead_of_silently_doing_nothing()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            var pending = await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Pending);

            var result = await host.Monitor().RetryAsync(pending.ID, PlatformTestHost.DefaultContext(), "operator asked", false);

            Assert.False(result.Success);
            Assert.Equal(DispatchRetryOutcome.AlreadyPending, result.Outcome);
        }

        // ---- A row a live worker is holding is not stolen; the same row IS retryable once the claim goes stale ----
        [Fact]
        public async Task A_fresh_claim_is_not_stolen_but_a_stale_one_is_retryable()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { StaleClaimMinutes = 10 };
            var ev = await AddEventAsync(host.Db);
            var row = await AddDispatchAsync(host.Db, ev.EventId, Timeline,
                BusinessEventDispatchStatus.Claimed, attempts: 1, updatedAt: DateTime.UtcNow);
            var ctx = PlatformTestHost.DefaultContext();

            var held = await host.Monitor(options: options).RetryAsync(row.ID, ctx, "seems stuck", false);
            Assert.Equal(DispatchRetryOutcome.HeldByWorker, held.Outcome);

            // The worker dies. Age its claim past the window.
            using (var db = host.NewContext())
            {
                var stale = await db.BusinessEventDispatches.SingleAsync(d => d.ID == row.ID);
                stale.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
                await db.SaveChangesAsync();
            }

            var requeued = await host.Monitor(options: options).RetryAsync(row.ID, ctx, "worker died", false);
            Assert.True(requeued.Success);
            Assert.Equal(DispatchRetryOutcome.Requeued, requeued.Outcome);

            using var verify = host.NewContext();
            Assert.Equal(BusinessEventDispatchStatus.Pending, (await verify.BusinessEventDispatches.SingleAsync()).Status);
        }

        // ---- Batch B item 42: a retry never resets Attempts, and never clears the recorded error ----
        [Fact]
        public async Task A_retry_preserves_the_attempt_history_and_the_previous_error()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };
            var ev = await AddEventAsync(host.Db);
            var row = await AddDispatchAsync(host.Db, ev.EventId, Timeline,
                BusinessEventDispatchStatus.Failed, attempts: 3, error: "SMTP host unreachable");

            var result = await host.Monitor(options: options).RetryAsync(row.ID, PlatformTestHost.DefaultContext(), "smtp fixed", false);

            Assert.True(result.Success);
            Assert.Equal(3, result.Attempts);   // NOT reset to zero — the failure history is audit information

            using var verify = host.NewContext();
            var after = await verify.BusinessEventDispatches.SingleAsync();
            Assert.Equal(BusinessEventDispatchStatus.Pending, after.Status);
            Assert.Equal(3, after.Attempts);
            // Kept until the outcome is known, so a retry failing the same way is visibly not a one-off.
            Assert.Equal("SMTP host unreachable", after.Error);
        }

        // ---- Exhausted attempts need an elevated override, and the override moves the ceiling by ONE ----
        [Fact]
        public async Task An_exhausted_row_needs_an_override_which_grants_exactly_one_more_attempt()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };
            var ev = await AddEventAsync(host.Db);
            var row = await AddDispatchAsync(host.Db, ev.EventId, Timeline,
                BusinessEventDispatchStatus.Failed, attempts: 5, error: "gave up");
            var ctx = PlatformTestHost.DefaultContext();

            var refused = await host.Monitor(options: options).RetryAsync(row.ID, ctx, "please retry", false);
            Assert.False(refused.Success);
            Assert.Equal(DispatchRetryOutcome.AttemptsExhausted, refused.Outcome);
            Assert.Contains("5", refused.Message);

            var overridden = await host.Monitor(options: options).RetryAsync(row.ID, ctx, "root cause fixed", true);
            Assert.True(overridden.Success);
            // One more attempt, granted by moving the ceiling — not by rewriting the history to zero.
            Assert.Equal(4, overridden.Attempts);

            using var verify = host.NewContext();
            var after = await verify.BusinessEventDispatches.SingleAsync();
            Assert.Equal(BusinessEventDispatchStatus.Pending, after.Status);
            Assert.Equal(4, after.Attempts);

            // The dispatcher will genuinely pick it up now — the override is not cosmetic.
            using var db = host.NewContext();
            Assert.Single(await host.DispatchStore(db, options).GetPendingAsync(Timeline, 10));
        }

        // ---- The override does NOT reset a row that was retryable anyway ----
        [Fact]
        public async Task An_override_on_a_row_that_did_not_need_one_leaves_Attempts_untouched()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 5 };
            var ev = await AddEventAsync(host.Db);
            var row = await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 2);

            var result = await host.Monitor(options: options).RetryAsync(row.ID, PlatformTestHost.DefaultContext(), "retry", true);

            Assert.True(result.Success);
            Assert.Equal(2, result.Attempts);   // an override must not be a back door to clearing attempts
        }

        // ---- A reason is mandatory: the audit trail is the point of the action ----
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_retry_without_a_reason_is_refused(string reason)
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            var row = await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 1);

            var result = await host.Monitor().RetryAsync(row.ID, PlatformTestHost.DefaultContext(), reason, false);

            Assert.False(result.Success);
            Assert.Equal(DispatchRetryOutcome.ReasonRequired, result.Outcome);

            using var verify = host.NewContext();
            Assert.Equal(BusinessEventDispatchStatus.Failed, (await verify.BusinessEventDispatches.SingleAsync()).Status);
        }

        // ---- Batch B item 43: retry is company-isolated, and a probe learns nothing ----
        [Fact]
        public async Task Retrying_another_companys_dispatch_row_is_refused_as_not_found()
        {
            using var host = new PlatformTestHost();
            var theirs = await AddEventAsync(host.Seed, companyId: 2);
            var row = await AddDispatchAsync(host.Db, theirs.EventId, Timeline, BusinessEventDispatchStatus.Failed, attempts: 1);

            var crossCompany = await host.Monitor().RetryAsync(row.ID, PlatformTestHost.DefaultContext(companyId: 1), "fix it", false);
            var absent = await host.Monitor().RetryAsync(999_999, PlatformTestHost.DefaultContext(companyId: 1), "fix it", false);

            Assert.Equal(DispatchRetryOutcome.NotFound, crossCompany.Outcome);
            // Byte-identical message: a probe must not be able to tell "exists elsewhere" from "does not exist".
            Assert.Equal(absent.Message, crossCompany.Message);

            using var verify = host.NewContext();
            Assert.Equal(BusinessEventDispatchStatus.Failed, (await verify.BusinessEventDispatches.SingleAsync()).Status);
        }

        // ---- Only the named consumer's row moves. Siblings — especially Done ones — are untouched ----
        [Fact]
        public async Task A_retry_moves_only_the_named_consumers_row()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            var doneRow = await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Done);
            var failedRow = await AddDispatchAsync(host.Db, ev.EventId, Notifications, BusinessEventDispatchStatus.Failed, attempts: 2, error: "boom");

            var result = await host.Monitor().RetryAsync(failedRow.ID, PlatformTestHost.DefaultContext(), "notification service back up", false);
            Assert.True(result.Success);
            Assert.Equal(Notifications, result.Consumer);

            using var verify = host.NewContext();
            var rows = await verify.BusinessEventDispatches.ToDictionaryAsync(d => d.Consumer);
            Assert.Equal(BusinessEventDispatchStatus.Done, rows[Timeline].Status);       // NOT reset
            Assert.Equal(doneRow.Attempts, rows[Timeline].Attempts);
            Assert.Equal(BusinessEventDispatchStatus.Pending, rows[Notifications].Status);
        }

        // ---- Retry eligibility shown in the grid is the SAME rule the store enforces ----
        [Fact]
        public async Task What_the_grid_offers_is_exactly_what_the_store_accepts()
        {
            using var host = new PlatformTestHost();
            var options = new BusinessEventDispatchOptions { MaxAttempts = 3, StaleClaimMinutes = 10 };
            var ev = await AddEventAsync(host.Db);

            // One row per interesting state, all on the same event.
            await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Done);
            await AddDispatchAsync(host.Db, ev.EventId, Notifications, BusinessEventDispatchStatus.Failed, attempts: 1);
            await AddDispatchAsync(host.Db, ev.EventId, "SearchIndex", BusinessEventDispatchStatus.Failed, attempts: 3);
            await AddDispatchAsync(host.Db, ev.EventId, "Webhook", BusinessEventDispatchStatus.Claimed, attempts: 1, updatedAt: DateTime.UtcNow);
            await AddDispatchAsync(host.Db, ev.EventId, "Archive", BusinessEventDispatchStatus.Pending);

            var ctx = PlatformTestHost.DefaultContext();
            var vm = await host.Monitor(options: options).GetDetailsAsync(ev.EventId, ctx, false, false);
            var byConsumer = vm!.Consumers.ToDictionary(c => c.Consumer!);

            Assert.False(byConsumer[Timeline].CanRetry);
            Assert.False(byConsumer["Archive"].CanRetry);
            Assert.False(byConsumer["Webhook"].CanRetry);
            Assert.True(byConsumer[Notifications].CanRetry);
            Assert.False(byConsumer[Notifications].RetryNeedsOverride);
            Assert.True(byConsumer["SearchIndex"].CanRetry);
            Assert.True(byConsumer["SearchIndex"].RetryNeedsOverride);   // attempts used up

            // Now prove the store agrees with every one of those flags, rather than the screen guessing.
            foreach (var row in vm.Consumers)
            {
                var result = await host.Monitor(options: options)
                    .RetryAsync(row.DispatchId!.Value, ctx, "cross-check", elevatedOverride: false);
                bool expectedToSucceed = row.CanRetry && !row.RetryNeedsOverride;
                Assert.Equal(expectedToSucceed, result.Success);
            }
        }

        // ---- Every refused row keeps a reason the UI can show, so nothing is silently un-actionable ----
        [Fact]
        public async Task Every_non_retryable_row_carries_a_reason()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Done);
            await AddDispatchAsync(host.Db, ev.EventId, Notifications, BusinessEventDispatchStatus.Pending);

            var page = await host.Monitor().SearchAsync(All, PlatformTestHost.DefaultContext(), false);
            Assert.Equal(2, page.Rows.Count);
            Assert.All(page.Rows, r =>
            {
                Assert.False(r.CanRetry);
                Assert.False(string.IsNullOrWhiteSpace(r.RetryBlockedReason));
            });
        }

        // ---- The event's CompletedAt roll-up is reported separately from the row's own status ----
        [Fact]
        public async Task Event_completion_is_reported_separately_from_a_consumers_status()
        {
            using var host = new PlatformTestHost();
            var ev = await AddEventAsync(host.Db);
            await AddDispatchAsync(host.Db, ev.EventId, Timeline, BusinessEventDispatchStatus.Done);
            await AddDispatchAsync(host.Db, ev.EventId, Notifications, BusinessEventDispatchStatus.Failed, attempts: 1);

            var before = await host.Monitor().SearchAsync(All, PlatformTestHost.DefaultContext(), false);
            // One consumer is Done, but the EVENT is not complete — the two must not be conflated.
            Assert.All(before.Rows, r => Assert.Null(r.CompletedAt));
            Assert.Contains(before.Rows, r => r.DispatchStatus == BusinessEventDispatchStatus.Done);

            using (var db = host.NewContext())
            {
                var failed = await db.BusinessEventDispatches.SingleAsync(d => d.Consumer == Notifications);
                failed.Status = BusinessEventDispatchStatus.Done;
                await db.SaveChangesAsync();
                await host.DispatchStore(db).TryCompleteEventAsync(ev.EventId);
            }

            var after = await host.Monitor().SearchAsync(All, PlatformTestHost.DefaultContext(), false);
            Assert.All(after.Rows, r => Assert.NotNull(r.CompletedAt));
        }

        // ---- The vocabularies the screen offers are the frozen ones, not a hand-typed copy ----
        [Fact]
        public async Task The_filter_vocabularies_come_from_the_registries()
        {
            using var host = new PlatformTestHost();
            var monitor = host.Monitor();

            Assert.Equal(host.Registry().GetDefinitions().Select(d => d.Code).OrderBy(c => c),
                         monitor.KnownEntityTypes().OrderBy(c => c));
            Assert.Equal(BusinessEventConsumers.Registered.OrderBy(c => c), monitor.KnownConsumers().OrderBy(c => c));

            // Claimed is offered (an operator must be able to find stuck rows) but nothing outside the vocabulary is.
            Assert.Contains(BusinessEventDispatchStatus.Claimed, monitor.KnownStatuses());
            Assert.All(monitor.KnownStatuses(), s => Assert.Contains(s, new[]
            {
                BusinessEventDispatchStatus.Pending, BusinessEventDispatchStatus.Claimed,
                BusinessEventDispatchStatus.Done, BusinessEventDispatchStatus.Failed,
            }));
            await Task.CompletedTask;
        }

        // ---- An empty result is an empty page, not a crash and not page 0 ----
        [Fact]
        public async Task An_empty_result_still_returns_a_well_formed_page()
        {
            using var host = new PlatformTestHost();

            var page = await host.Monitor().SearchAsync(All, PlatformTestHost.DefaultContext(), false);

            Assert.Empty(page.Rows);
            Assert.Equal(0, page.Total);
            Assert.Equal(1, page.Page);
            Assert.Equal(1, page.Pages);   // the pager must not render "page 1 of 0"
            Assert.Equal(0, page.Summary.TotalEvents);
        }
    }
}
