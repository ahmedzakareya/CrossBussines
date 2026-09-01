using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B / B3 — the controlled, scoped, authorized and audited company-isolation bypass.
    //
    // WHAT THESE TESTS ARE FOR
    //
    // They were written BEFORE B2, when no query filter existed, by instruction ("Do not enable any of the 12
    // entity filters until all B3 tests pass"). They therefore prove the MECHANISM rather than its effect on a
    // filter: nothing here claims a row appeared or vanished because of a filter it could not yet fool. The
    // effect of the filters is proven separately, in Stage1QueryFilterTests.
    //
    // They still run — and still matter — with the filters on: this is the file that fails if the authorization
    // rules, the lifetime or the leak-freedom regress, independently of any entity mapping.
    //
    // What these tests prove, precisely:
    //   * the AUTHORIZATION rules — who may hold which kind, and that every refusal path denies rather than
    //     silently downgrading to an ordinary read;
    //   * the STATE a B2 filter will read — ICompanyScopeHolder.AllowsCrossCompany and CompanyId — is correct,
    //     ends with its scope, and cannot leak between scopes or parallel tasks;
    //   * the FLOWS B1 identified as filter-breaking still read across companies and are wrapped, so that when
    //     B2 installs the filters those paths already hold the right;
    //   * the public catalogue reads exactly one configured company and cannot be steered;
    //   * nothing anywhere falls back to company 1.
    //
    // Where an assertion depends on the filter itself, it is written against
    // NotificationCompanyPolicy.QueryFilter / IsVisible — the same predicate B2 installs — rather than against
    // the installed filter, which Stage1QueryFilterTests owns. That is stated at each such test.
    public class Stage1BypassTests
    {
        private const string Why = "unit test";

        // ---- fixtures -------------------------------------------------------------------------------------

        private static ICompanyIsolationBypass Bypass(
            ICompanyScopeHolder scope, PublicCatalogOptions? catalog = null, ICompanyBypassAudit? audit = null)
            => new CompanyIsolationBypass(
                scope,
                new CompanyBypassPolicy(),
                audit ?? new LoggingCompanyBypassAudit(NullLogger<LoggingCompanyBypassAudit>.Instance),
                Options.Create(catalog ?? new PublicCatalogOptions()));

        private static BusinessContext Staff(int companyId = 1, int? employeeId = 7, params string[] roles)
            => new()
            {
                CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
                Roles = roles, CorrelationId = Guid.NewGuid(), Source = BusinessContextSource.Http,
            };

        private static BusinessContext Admin(int companyId = 1) => Staff(companyId, 7, "PlatformOps");

        // Records grants instead of logging them, so a test can assert WHAT was audited rather than that
        // something was written somewhere.
        private sealed class RecordingAudit : ICompanyBypassAudit
        {
            public List<CompanyBypassGrant> Grants { get; } = new();
            public List<(CompanyBypassKind Kind, BusinessContext? Context, string Reason)> Refusals { get; } = new();
            public List<(CompanyBypassGrant Grant, TimeSpan Held)> Releases { get; } = new();

            public void Granted(CompanyBypassGrant grant) => Grants.Add(grant);
            public void Refused(CompanyBypassKind kind, BusinessContext? context, string reason)
                => Refusals.Add((kind, context, reason));
            public void Released(CompanyBypassGrant grant, TimeSpan held) => Releases.Add((grant, held));
        }

        // =====================================================================================
        // 1. AN UNAUTHORIZED BYPASS IS REJECTED
        // =====================================================================================

        // An ordinary logged-in employee cannot decide to read every company.
        [Fact]
        public void An_ordinary_employee_cannot_obtain_a_cross_company_bypass()
        {
            var scope = new CompanyScopeHolder();
            var audit = new RecordingAudit();
            var bypass = Bypass(scope, audit: audit);

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Staff(), Why));

            Assert.Equal(CompanyBypassKind.CrossCompanyAdministration, denied.Kind);
            Assert.Contains("platform-admin roles", denied.Message);

            // The refusal DENIES; it does not quietly leave an ordinary (still-filtered) read in place and let the
            // caller believe it succeeded. Nothing is active afterwards.
            Assert.Null(bypass.Current);
            Assert.False(scope.AllowsCrossCompany);
            Assert.Empty(audit.Grants);
            Assert.Single(audit.Refusals);
        }

        // The highest right inside one module is still not a right over other companies. This is the specific
        // line the policy draws deliberately, so it is asserted deliberately.
        [Fact]
        public void An_accounting_manager_role_is_not_a_cross_company_right()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope);

            Assert.Throws<CompanyBypassDeniedException>(() => bypass.Begin(
                CompanyBypassKind.CrossCompanyAdministration,
                Staff(1, 7, "ChiefAccountant", "AccountingManager", "InventoryManager"), Why));
        }

        // The dispatcher's right is the broadest read in the system. An interactive user — even an admin —
        // cannot hold it through the public entry point.
        [Fact]
        public void An_interactive_user_cannot_hold_the_dispatcher_right_even_as_an_admin()
        {
            var bypass = Bypass(new CompanyScopeHolder());

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.PlatformDispatch, Admin(), Why));

            Assert.Contains("reserved for System and Worker", denied.Message);
        }

        // ...and the reverse: the policy contains no test-only allowance, so a Test-source context is refused
        // exactly like an Http one. A security rule that exempts a context source tests can construct is a rule
        // the tests cannot prove.
        [Fact]
        public void The_policy_has_no_test_only_allowance()
        {
            var bypass = Bypass(new CompanyScopeHolder());
            var testContext = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 7, UserId = "user-7", Source = BusinessContextSource.Test,
            };

            Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.PlatformDispatch, testContext, Why));
            Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.PlatformMonitoring, testContext, Why));
        }

        [Fact]
        public void An_unauthenticated_context_is_refused()
        {
            var bypass = Bypass(new CompanyScopeHolder());
            var anonymous = new BusinessContext { CompanyId = 1, EmployeeId = null, UserId = "" };

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.PlatformMonitoring, anonymous, Why));
            Assert.Contains("authenticated identity", denied.Message);
        }

        // A user id without a resolved employee row is not enough: the audit line must be able to name a person.
        [Fact]
        public void An_identity_without_a_resolved_employee_is_refused()
        {
            var bypass = Bypass(new CompanyScopeHolder());
            var noEmployee = new BusinessContext { CompanyId = 1, EmployeeId = null, UserId = "user-x" };

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, noEmployee, Why));
            Assert.Contains("resolved employee", denied.Message);
        }

        // A MISSING CONTEXT DENIES — it never becomes unrestricted access. This is the mirror image of the
        // company-1 fallback Batch A deleted: absence must not resolve to permission.
        [Fact]
        public void A_missing_context_denies_rather_than_becoming_unrestricted()
        {
            var scope = new CompanyScopeHolder();
            var audit = new RecordingAudit();
            var bypass = Bypass(scope, audit: audit);

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, null!, Why));

            Assert.Contains("A missing context is denied", denied.Message);
            Assert.False(scope.AllowsCrossCompany);
            Assert.Single(audit.Refusals);
        }

        // No reason, no bypass — an audit line that cannot say WHY is not an audit line. Asserted on all three
        // entry points, because each builds its own grant.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void A_bypass_without_a_reason_is_refused_on_every_entry_point(string? reason)
        {
            var bypass = Bypass(new CompanyScopeHolder());

            Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), reason!));
            Assert.Throws<CompanyBypassDeniedException>(() => bypass.BeginPlatformDispatch(reason!));
            Assert.Throws<CompanyBypassDeniedException>(() => bypass.BeginPublicCatalogRead(reason!));
        }

        // The public catalogue's pin cannot be acquired through the authenticated administrative door, and the
        // administrative kinds cannot be acquired through the anonymous one. Collapsing the two would let the
        // weakest caller define the strongest right.
        [Fact]
        public void Public_catalogue_access_is_not_reachable_through_the_administrative_entry_point()
        {
            var bypass = Bypass(new CompanyScopeHolder());

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.Begin(CompanyBypassKind.PublicCompanyRead, Admin(), Why));

            Assert.Contains("not obtainable through Begin()", denied.Message);
        }

        // =====================================================================================
        // 2. AN AUTHORIZED BYPASS SEES THE RECORDS IT NEEDS
        // =====================================================================================

        // The state a B2 filter reads. `AllowsCrossCompany` — not "a bypass is active" — is the flag, because
        // PublicCompanyRead is a bypass that PINS rather than unrestricts.
        [Fact]
        public void An_authorized_administrative_bypass_unrestricts_the_scope()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope);
            scope.Set(1, null);

            Assert.False(scope.AllowsCrossCompany);

            using (bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), "consolidation report"))
            {
                Assert.True(scope.AllowsCrossCompany);
                Assert.True(bypass.Current!.AllowsCrossCompany);
                Assert.Equal(CompanyBypassKind.CrossCompanyAdministration, bypass.Current!.Kind);
                // The scope still knows which company it IS; the bypass widens what it may read, and does not
                // erase its own identity.
                Assert.Equal(1, scope.CompanyId);
            }

            Assert.False(scope.AllowsCrossCompany);
        }

        // The same assertion expressed through the predicate B2 will actually install: another company's row is
        // invisible while filtered, and visible under the authorized bypass. This is the closest a pre-B2 test
        // can honestly get to "the filter is bypassed", and it uses the real expression, not a paraphrase.
        [Fact]
        public void The_B2_predicate_hides_another_companys_row_and_the_bypass_reveals_it()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope);
            scope.Set(1, null);

            Assert.True(NotificationCompanyPolicy.IsVisible(1, scope));
            Assert.False(NotificationCompanyPolicy.IsVisible(2, scope));

            using (bypass.Begin(CompanyBypassKind.PlatformMonitoring, Admin(), "cross-company view"))
            {
                Assert.True(NotificationCompanyPolicy.IsVisible(2, scope));
            }

            Assert.False(NotificationCompanyPolicy.IsVisible(2, scope));
        }

        // The dispatcher gets its right WITHOUT inventing a company or an actor. The audit line says
        // "(anonymous)" and "(unresolved)" rather than implying an identity that does not exist — which is the
        // whole reason BeginPlatformDispatch takes no BusinessContext.
        [Fact]
        public void The_dispatch_bypass_names_no_company_and_no_actor_rather_than_inventing_them()
        {
            var scope = new CompanyScopeHolder();
            var audit = new RecordingAudit();
            var bypass = Bypass(scope, audit: audit);

            using (bypass.BeginPlatformDispatch("outbox pass for NotificationProjection"))
            {
                var grant = Assert.Single(audit.Grants);
                Assert.Equal(CompanyBypassKind.PlatformDispatch, grant.Kind);
                Assert.True(grant.AllowsCrossCompany);
                Assert.Null(grant.ActorEmployeeId);
                Assert.Null(grant.ScopeCompanyId);       // NOT 1. There is no default company.
                Assert.Contains("(anonymous)", grant.ToString());
                Assert.Contains("(unresolved)", grant.ToString());
            }
        }

        // =====================================================================================
        // 3. THE BYPASS ENDS WITH ITS SCOPE
        // =====================================================================================

        [Fact]
        public void The_bypass_ends_when_its_lease_is_disposed()
        {
            var scope = new CompanyScopeHolder();
            var audit = new RecordingAudit();
            var bypass = Bypass(scope, audit: audit);

            var lease = bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), Why);
            Assert.NotNull(bypass.Current);

            lease.Dispose();

            Assert.Null(bypass.Current);
            Assert.False(scope.AllowsCrossCompany);
            Assert.Single(audit.Releases);           // release is audited too, not only the grant
        }

        // A bypass must not survive an exception. If it did, a failed cross-company operation would leave the
        // rest of the request unrestricted — the worst possible moment to be unrestricted.
        [Fact]
        public void The_bypass_ends_even_when_the_body_throws()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope);

            // Cast to Action: a block that always throws is convertible to Func<Task> too, and the compiler
            // would otherwise pick xUnit's obsolete async overload.
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var _ = bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), Why);
                Assert.True(scope.AllowsCrossCompany);
                throw new InvalidOperationException("the operation failed");
            }));

            Assert.Null(bypass.Current);
            Assert.False(scope.AllowsCrossCompany);
        }

        // Double dispose is a no-op, so a `using` inside a `finally` cannot throw over the top of a real
        // exception and hide it.
        [Fact]
        public void Disposing_a_lease_twice_is_a_no_op()
        {
            var scope = new CompanyScopeHolder();
            var audit = new RecordingAudit();
            var bypass = Bypass(scope, audit: audit);

            var lease = bypass.Begin(CompanyBypassKind.PlatformMonitoring, Admin(), Why);
            lease.Dispose();
            lease.Dispose();

            Assert.Null(bypass.Current);
            Assert.Single(audit.Releases);           // released ONCE, not twice
        }

        // Nesting is refused rather than stacked: two overlapping bypasses in one scope would make "which right
        // is in force" ambiguous at the exact moment it matters. After the inner refusal the OUTER one is still
        // intact — a refused request must not damage the caller that was already authorized.
        [Fact]
        public void A_second_bypass_cannot_be_nested_inside_the_first()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope);

            using var outer = bypass.Begin(CompanyBypassKind.PlatformMonitoring, Admin(), "outer");

            var ex = Assert.Throws<InvalidOperationException>(
                () => bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), "inner"));
            Assert.Contains("cannot be nested", ex.Message);

            Assert.Equal(CompanyBypassKind.PlatformMonitoring, bypass.Current!.Kind);
            Assert.True(scope.AllowsCrossCompany);
        }

        // A bypass granted in one scope must be invisible in another, and each must end independently — the
        // reason the state lives on the scoped holder rather than in a static.
        [Fact]
        public void Two_scopes_do_not_share_bypass_state()
        {
            var scopeA = new CompanyScopeHolder();
            var scopeB = new CompanyScopeHolder();
            var bypassA = Bypass(scopeA);
            var bypassB = Bypass(scopeB);

            using (bypassA.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), "A"))
            {
                Assert.True(scopeA.AllowsCrossCompany);
                Assert.False(scopeB.AllowsCrossCompany);   // B is untouched
                Assert.Null(bypassB.Current);
            }
            Assert.False(scopeA.AllowsCrossCompany);
        }

        // =====================================================================================
        // 4. BYPASS STATE DOES NOT LEAK ACROSS PARALLEL TASKS OR REQUESTS
        // =====================================================================================

        // The structural test for "no static, no AsyncLocal". Many scopes run concurrently; half take a bypass
        // and half do not, and each asserts its own state repeatedly with yields in between so the tasks
        // genuinely interleave on the thread pool.
        //
        // A static flag fails this immediately. An AsyncLocal fails it as soon as one task's continuation is
        // observed by another — and it would also carry into unrelated fire-and-forget work, which is why the
        // implementation uses neither.
        [Fact]
        public async Task Bypass_state_does_not_leak_across_parallel_scopes()
        {
            const int scopes = 32;
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

            var tasks = Enumerable.Range(0, scopes).Select(i => Task.Run(async () =>
            {
                // One holder per task = one DI scope per request, which is how they are registered.
                var scope = new CompanyScopeHolder();
                var bypass = Bypass(scope);
                bool wantsBypass = i % 2 == 0;
                scope.Set(companyId: i + 1, branchId: null);

                IDisposable? lease = null;
                if (wantsBypass)
                    lease = bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(i + 1), "parallel " + i);

                try
                {
                    for (int round = 0; round < 20; round++)
                    {
                        await Task.Yield();
                        if (scope.AllowsCrossCompany != wantsBypass)
                            failures.Add($"scope {i}: AllowsCrossCompany={scope.AllowsCrossCompany}, expected {wantsBypass}");
                        if (scope.CompanyId != i + 1)
                            failures.Add($"scope {i}: CompanyId={scope.CompanyId}, expected {i + 1}");
                        // The identity on the grant must be this task's, never a neighbour's.
                        if (wantsBypass && bypass.Current!.ScopeCompanyId != i + 1)
                            failures.Add($"scope {i}: grant scope={bypass.Current!.ScopeCompanyId}");
                        if (!wantsBypass && bypass.Current != null)
                            failures.Add($"scope {i}: saw a bypass it never requested ({bypass.Current!.Kind})");
                    }
                }
                finally { lease?.Dispose(); }

                if (scope.AllowsCrossCompany) failures.Add($"scope {i}: still unrestricted after dispose");
            })).ToArray();

            await Task.WhenAll(tasks);
            Assert.Empty(failures);
        }

        // The specific AsyncLocal failure mode, isolated: work started INSIDE an active bypass, but running
        // against its own scope, must not inherit the bypass. With an AsyncLocal the child would inherit the
        // ambient value; with a scoped holder it cannot, because it holds a different object.
        [Fact]
        public async Task A_child_task_with_its_own_scope_does_not_inherit_an_active_bypass()
        {
            var outerScope = new CompanyScopeHolder();
            var outerBypass = Bypass(outerScope);

            using (outerBypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), "outer request"))
            {
                Assert.True(outerScope.AllowsCrossCompany);

                var (childCross, childCompany) = await Task.Run(() =>
                {
                    var childScope = new CompanyScopeHolder();
                    var childBypass = Bypass(childScope);
                    Assert.Null(childBypass.Current);
                    return (childScope.AllowsCrossCompany, childScope.CompanyId);
                });

                Assert.False(childCross);
                Assert.Null(childCompany);          // and no company was inherited either
                Assert.True(outerScope.AllowsCrossCompany);   // the parent is unaffected by the child
            }
        }

        // =====================================================================================
        // 5. THE DISPATCHER STILL LOADS EVENTS ACROSS COMPANIES
        // =====================================================================================

        // B1 marked this Critical: BusinessEventDispatch is ONE queue for every company, claimed in a single
        // atomic statement, and the worker then loads each claimed event BY ID with no company predicate. Under
        // a filter and without the bypass, an event from another company would come back null and the worker
        // would mark a healthy row Failed while burning an attempt.
        //
        // The claim + by-id load is exercised here for real, over two companies, inside the dispatch bypass.
        [Fact]
        public async Task The_dispatcher_claims_and_loads_events_from_every_company_in_one_pass()
        {
            using var host = new PlatformTestHost(companyId: 1);
            // B2: host.Db is filtered, so the bypass must be taken on the holder that filters it — exactly as the
            // dispatch worker does, where the holder, the bypass and the DbContext all belong to one DI scope.
            var scope = host.Holder;
            var bypass = host.HostBypass();

            var one = await AddEventWithDispatchAsync(host, companyId: 1, entityId: 11);
            var two = await AddEventWithDispatchAsync(host, companyId: 2, entityId: 22);
            var three = await AddEventWithDispatchAsync(host, companyId: 3, entityId: 33);

            using (bypass.BeginPlatformDispatch("outbox pass"))
            {
                Assert.True(scope.AllowsCrossCompany);

                var claimed = await host.DispatchStore().ClaimPendingAsync(
                    BusinessEventConsumers.NotificationProjection, batchSize: 50);

                // One pass, three companies, one queue.
                Assert.Equal(3, claimed.Count);
                Assert.Equal(new[] { one, two, three }.OrderBy(x => x),
                             claimed.Select(c => c.EventId).OrderBy(x => x));

                // ...and the by-id load the worker performs next resolves for every one of them.
                foreach (var work in claimed)
                {
                    var stored = await host.Db.BusinessEvents.AsNoTracking()
                        .FirstOrDefaultAsync(e => e.EventId == work.EventId);
                    Assert.NotNull(stored);
                }
            }

            // The pass ends and the right ends with it.
            Assert.False(scope.AllowsCrossCompany);
        }

        // =====================================================================================
        // 6. NOTIFICATION DEDUP REMAINS CORRECT ACROSS RECIPIENT COMPANIES
        // =====================================================================================

        // The other Critical finding: NotificationProjectionConsumer's (recipient, dedupKey) read is the ONLY
        // real idempotency guard — NotificationService's own check suppresses UNREAD duplicates only, so a
        // redelivery after the user read the notification would otherwise send a second one.
        //
        // The row it must find may carry a company other than the reading scope's. This test writes the existing
        // row with CompanyID = 2 and asks the guard's question from a company-1 scope: under the bypass it is
        // found (no duplicate); under the B2 predicate without a bypass it would NOT be found — which is exactly
        // the duplicate the bypass prevents, asserted rather than described.
        [Fact]
        public async Task The_dedup_guard_finds_an_existing_notification_that_belongs_to_another_company()
        {
            using var host = new PlatformTestHost(companyId: 1);
            var scope = host.Holder;
            var bypass = host.HostBypass();

            const int recipient = 501;
            const string dedupKey = "evt:abcdef:501";
            host.Seed.Notifications.Add(new Notification
            {
                RecipientEmployeeID = recipient, CompanyID = 2, Type = "purchase_invoice",
                DedupKey = dedupKey, IsRead = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();

            // The consumer's actual guard query, verbatim.
            async Task<bool> AlreadySentAsync() => await host.Db.Notifications.AsNoTracking()
                .AnyAsync(n => n.RecipientEmployeeID == recipient && n.DedupKey == dedupKey);

            using (bypass.BeginPlatformDispatch("notification projection dedup"))
            {
                Assert.True(scope.AllowsCrossCompany);
                Assert.True(await AlreadySentAsync());                 // found -> no duplicate is sent
                Assert.True(NotificationCompanyPolicy.IsVisible(2, scope));
            }

            // Without the bypass, B2's predicate would hide that row from a company-1 scope — and a hidden row
            // means the guard answers "not sent yet" and a SECOND notification goes out.
            Assert.False(NotificationCompanyPolicy.IsVisible(2, scope));
        }

        // The projection consumer, run end to end TWICE on the same event, produces one notification per
        // recipient. It manages its own bypass when the dispatcher has not already taken one, so this also
        // proves the re-use path does not throw on the holder's no-nesting rule.
        [Fact]
        public async Task Redelivering_the_same_event_does_not_duplicate_a_notification()
        {
            using var host = new PlatformTestHost();
            const int chief = 601, actor = 602;

            host.Db.Employee.AddRange(TestEmployee(chief, 1), TestEmployee(actor, 1));
            host.Db.AccountingUserRoles.Add(new CrossBuy.Models.Context.Accounting.AccountingUserRole
            {
                CompanyID = 1, EmployeeId = chief, Role = "ChiefAccountant",
            });
            await host.Db.SaveChangesAsync();

            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1,
                EntityType = EntityRegistry.PurchaseInvoice, EntityId = 77,
                EventType = PurchaseInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = PurchaseInvoiceEventPayload.Version, ActorEmployeeId = actor,
                CreatedAt = DateTime.UtcNow,
                Payload = "{\"invoiceNumber\":\"PV-77\",\"totalAfter\":100.0}",
            };
            host.Db.BusinessEvents.Add(ev);
            await host.Db.SaveChangesAsync();
            var envelope = host.Events().BuildEnvelope(ev);

            // A fresh scope per delivery, as the dispatcher does (one DI scope per batch).
            async Task DeliverAsync()
            {
                var scope = new CompanyScopeHolder();
                var consumer = new NotificationProjectionConsumer(
                    host.Db, new BusinessEventNotificationMapper(),
                    new CountingSink(host.Db), host.Registry(),
                    new StubPermissionProvider(PlatformActions.View),
                    host.Contexts(), Bypass(scope), scope,
                    NullLogger<NotificationProjectionConsumer>.Instance);
                await consumer.HandleAsync(envelope);
            }

            await DeliverAsync();
            await DeliverAsync();          // the stale-claim redelivery

            var rows = await host.Db.Notifications.AsNoTracking()
                .Where(n => n.RecipientEmployeeID == chief).ToListAsync();
            Assert.Single(rows);
            Assert.Equal(1, rows[0].CompanyID);
        }

        // =====================================================================================
        // 7. THE MONITOR'S ELEVATED VIEW BEHAVES HONESTLY
        // =====================================================================================

        // "Honestly" has a precise meaning here: an elevated view either shows every company or refuses. What it
        // must never do is show a grid narrowed to the operator's own company while the screen says
        // "all companies" — a silent narrowing that reads as "there is nothing wrong in the other companies".
        [Fact]
        public async Task The_elevated_monitor_view_takes_an_audited_monitoring_bypass_and_returns_other_companies()
        {
            using var host = new PlatformTestHost(companyId: 1);
            var audit = new RecordingAudit();
            var scope = host.Holder;
            var monitor = MonitorWith(host, Bypass(scope, audit: audit));

            await AddEventWithDispatchAsync(host, companyId: 1, entityId: 11);
            await AddEventWithDispatchAsync(host, companyId: 2, entityId: 22);

            var page = await monitor.SearchAsync(
                new BusinessEventMonitorFilter { PageSize = 100 }, PlatformTestHost.AdminContext(companyId: 1),
                crossCompany: true);

            Assert.Equal(2, page.Total);
            Assert.Contains(2, page.Rows.Select(r => r.CompanyId));

            var grant = Assert.Single(audit.Grants);
            Assert.Equal(CompanyBypassKind.PlatformMonitoring, grant.Kind);
            Assert.Equal(7, grant.ActorEmployeeId);
            Assert.Contains("elevated cross-company view", grant.Reason);
            Assert.NotNull(grant.CorrelationId);
            Assert.NotEqual(default, grant.GrantedAtUtc);

            // ...and it ended with the call, rather than leaving the request unrestricted.
            Assert.Single(audit.Releases);
            Assert.False(scope.AllowsCrossCompany);
        }

        [Fact]
        public async Task The_ordinary_monitor_view_takes_no_bypass_and_shows_only_the_callers_company()
        {
            using var host = new PlatformTestHost();
            var audit = new RecordingAudit();
            var monitor = MonitorWith(host, Bypass(new CompanyScopeHolder(), audit: audit));

            await AddEventWithDispatchAsync(host, companyId: 1, entityId: 11);
            await AddEventWithDispatchAsync(host, companyId: 2, entityId: 22);

            var page = await monitor.SearchAsync(
                new BusinessEventMonitorFilter { PageSize = 100 }, PlatformTestHost.DefaultContext(companyId: 1),
                crossCompany: false);

            Assert.Equal(1, page.Total);
            Assert.All(page.Rows, r => Assert.Equal(1, r.CompanyId));
            Assert.Empty(audit.Grants);          // no bypass is taken for an ordinary view
        }

        // An operator who is not elevated cannot widen the view by posting another company's id — the filter
        // value is honoured only when the cross-company right is held.
        [Fact]
        public async Task A_non_elevated_operator_cannot_widen_the_view_by_posting_another_company_id()
        {
            using var host = new PlatformTestHost();
            var monitor = MonitorWith(host, Bypass(new CompanyScopeHolder()));

            await AddEventWithDispatchAsync(host, companyId: 1, entityId: 11);
            await AddEventWithDispatchAsync(host, companyId: 2, entityId: 22);

            var page = await monitor.SearchAsync(
                new BusinessEventMonitorFilter { CompanyId = 2, PageSize = 100 },
                PlatformTestHost.DefaultContext(companyId: 1), crossCompany: false);

            Assert.All(page.Rows, r => Assert.Equal(1, r.CompanyId));
        }

        // The honesty rule stated as a test: a caller who reaches crossCompany: true WITHOUT the right is
        // refused loudly. It does not receive a quietly narrowed grid.
        [Fact]
        public async Task An_unauthorized_elevated_request_is_refused_loudly_not_narrowed_silently()
        {
            using var host = new PlatformTestHost();
            var monitor = MonitorWith(host, Bypass(new CompanyScopeHolder()));
            await AddEventWithDispatchAsync(host, companyId: 2, entityId: 22);

            await Assert.ThrowsAsync<CompanyBypassDeniedException>(() => monitor.SearchAsync(
                new BusinessEventMonitorFilter { PageSize = 100 },
                PlatformTestHost.DefaultContext(companyId: 1), crossCompany: true));

            await Assert.ThrowsAsync<CompanyBypassDeniedException>(() => monitor.GetDetailsAsync(
                1, PlatformTestHost.DefaultContext(companyId: 1), crossCompany: true, maySeeRestricted: false));
        }

        // =====================================================================================
        // 8. THE PUBLIC CATALOGUE READS ONLY THE CONFIGURED COMPANY
        // =====================================================================================

        // It PINS rather than unrestricts. That distinction is the entire reason PublicCompanyRead is a separate
        // kind: the anonymous storefront must not share a right with a consolidation report.
        [Fact]
        public void The_public_catalogue_pins_the_configured_company_and_never_crosses_companies()
        {
            var scope = new CompanyScopeHolder();
            var audit = new RecordingAudit();
            var bypass = Bypass(scope, new PublicCatalogOptions { StoreCompanyId = 7 }, audit);

            Assert.Equal(7, bypass.PublicCatalogCompanyId);

            using (bypass.BeginPublicCatalogRead("anonymous storefront"))
            {
                Assert.Equal(7, scope.CompanyId);
                Assert.True(scope.IsResolved);
                // Pinned, NOT unrestricted: the scope stays fully filtered.
                Assert.False(scope.AllowsCrossCompany);
                Assert.False(bypass.Current!.AllowsCrossCompany);
                Assert.True(bypass.Current!.Kind.IsReadOnly());

                // The predicate B2 installs confirms it: company 7 is readable, company 8 is not.
                Assert.True(NotificationCompanyPolicy.IsVisible(7, scope));
                Assert.False(NotificationCompanyPolicy.IsVisible(8, scope));

                var grant = Assert.Single(audit.Grants);
                Assert.Equal(7, grant.PinnedCompanyId);
                Assert.Null(grant.ActorEmployeeId);       // anonymous BY DESIGN, and the audit line says so
                Assert.Contains("(anonymous)", grant.ToString());
            }
        }

        // The company comes from configuration and NOWHERE else. Asserted structurally as well as behaviourally:
        // no entry point to the public scope accepts a company argument, so no route value, query string, header
        // or cookie can reach it. A behavioural test alone would pass even if such a parameter existed.
        [Fact]
        public void No_public_catalogue_entry_point_accepts_a_company_from_the_caller()
        {
            var method = typeof(ICompanyIsolationBypass).GetMethod(nameof(ICompanyIsolationBypass.BeginPublicCatalogRead))!;
            var parameter = Assert.Single(method.GetParameters());
            Assert.Equal(typeof(string), parameter.ParameterType);     // the reason, and nothing else

            // PublicCatalogCompanyId is read-only to callers: there is no setter to point it elsewhere at runtime.
            var property = typeof(ICompanyIsolationBypass).GetProperty(nameof(ICompanyIsolationBypass.PublicCatalogCompanyId))!;
            Assert.Null(property.SetMethod);

            // And a different configuration genuinely produces a different company — the value is configured,
            // not hardcoded behind the property.
            Assert.Equal(3, Bypass(new CompanyScopeHolder(), new PublicCatalogOptions { StoreCompanyId = 3 }).PublicCatalogCompanyId);
            Assert.Equal(9, Bypass(new CompanyScopeHolder(), new PublicCatalogOptions { StoreCompanyId = 9 }).PublicCatalogCompanyId);
        }

        // =====================================================================================
        // 9. THE PUBLIC SCOPE CANNOT SWITCH COMPANIES
        // =====================================================================================

        // An authenticated staff request must never be silently repointed at the public company — that would let
        // a storefront call inside a staff request read (or worse, write against) a different company's data.
        [Fact]
        public void The_public_scope_cannot_be_applied_over_a_scope_that_is_already_another_company()
        {
            var scope = new CompanyScopeHolder();
            scope.Set(2, null);                    // a staff request, operating as company 2
            var bypass = Bypass(scope, new PublicCatalogOptions { StoreCompanyId = 7 });

            var denied = Assert.Throws<CompanyBypassDeniedException>(
                () => bypass.BeginPublicCatalogRead("storefront read inside a staff request"));

            Assert.Contains("already operates as company 2", denied.Message);
            Assert.Equal(2, scope.CompanyId);       // unchanged
            Assert.Null(bypass.Current);
        }

        // ...and the pin cannot be moved once taken, in either direction: a second public read against the same
        // scope with a different configured company is refused rather than repointing the scope.
        [Fact]
        public void The_public_pin_cannot_be_moved_to_a_second_company_within_one_scope()
        {
            var scope = new CompanyScopeHolder();
            using (Bypass(scope, new PublicCatalogOptions { StoreCompanyId = 7 }).BeginPublicCatalogRead("first"))
            {
                Assert.Equal(7, scope.CompanyId);
            }

            var other = Bypass(scope, new PublicCatalogOptions { StoreCompanyId = 8 });
            Assert.Throws<CompanyBypassDeniedException>(() => other.BeginPublicCatalogRead("second"));
            Assert.Equal(7, scope.CompanyId);
        }

        // A closed storefront is CLOSED. It does not fall back to an unfiltered read, which is the failure mode
        // that would turn a configuration mistake into a cross-company data leak.
        [Fact]
        public void A_disabled_storefront_is_closed_rather_than_unfiltered()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope, new PublicCatalogOptions { StoreCompanyId = 7, Enabled = false });

            var denied = Assert.Throws<CompanyBypassDeniedException>(() => bypass.BeginPublicCatalogRead("storefront"));

            Assert.Contains("closed rather than falling back", denied.Message);
            Assert.False(scope.IsResolved);
            Assert.False(scope.AllowsCrossCompany);
        }

        // =====================================================================================
        // 10. COMPANY 1 IS NEVER AN IMPLICIT FALLBACK
        // =====================================================================================

        // Company 1 is a perfectly legitimate company, and the storefront's real configuration happens to be 1.
        // The rule is that it must never be reached IMPLICITLY. Every test below configures something other than
        // 1 and proves the code does not drift back to it.
        [Fact]
        public void The_public_catalogue_uses_the_configured_company_not_company_one()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope, new PublicCatalogOptions { StoreCompanyId = 4 });

            using (bypass.BeginPublicCatalogRead("storefront"))
            {
                Assert.Equal(4, scope.CompanyId);
                Assert.NotEqual(1, scope.CompanyId);
                Assert.Equal(4, bypass.Current!.PinnedCompanyId);
            }
        }

        // A missing or nonsensical configuration is refused. It does NOT become company 1 — the exact fallback
        // Batch A deleted, re-tested at the one place a new default could plausibly have crept back in.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void An_unconfigured_store_company_is_refused_rather_than_defaulting_to_one(int configured)
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope, new PublicCatalogOptions { StoreCompanyId = configured });

            var denied = Assert.Throws<CompanyBypassDeniedException>(() => bypass.BeginPublicCatalogRead("storefront"));

            Assert.Contains("There is no default company", denied.Message);
            Assert.False(scope.IsResolved);
            Assert.Null(scope.CompanyId);           // not 1
        }

        // Granting a bypass never RESOLVES a company that was not resolved. An unresolved scope stays
        // unresolved, so B2's filter still denies rather than silently reading company 1's rows.
        [Fact]
        public void A_bypass_over_an_unresolved_scope_leaves_it_unresolved()
        {
            var scope = new CompanyScopeHolder();
            var bypass = Bypass(scope);

            using (bypass.BeginPlatformDispatch("outbox pass"))
            {
                Assert.False(scope.IsResolved);
                Assert.Null(scope.CompanyId);
            }

            using (bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(companyId: 5), Why))
            {
                Assert.False(scope.IsResolved);
                Assert.Null(scope.CompanyId);        // the context's company is audited, not assumed as the scope
                Assert.Equal(5, bypass.Current!.ScopeCompanyId);
            }
        }

        // The B2 predicate itself refuses an unresolved scope instead of widening it. Written against the real
        // expression, because this is the case where the "obvious" formulation is wrong: with a nullable on both
        // sides EF emits a NULL-safe comparison, and an unresolved scope would match `CompanyID IS NULL` — the
        // legacy rows, handed to the request that could not prove who it was.
        [Fact]
        public void The_B2_predicate_denies_an_unresolved_scope_rather_than_widening_it()
        {
            var scope = new CompanyScopeHolder();

            Assert.False(NotificationCompanyPolicy.IsVisible(1, scope));
            Assert.False(NotificationCompanyPolicy.IsVisible(2, scope));
            Assert.False(NotificationCompanyPolicy.IsVisible(null, scope));
        }

        // =====================================================================================
        // Notification.CompanyID NULL policy (decided in B3, enforced by B2's filter)
        // =====================================================================================

        // Policy (c): a NULL-company row belongs to NO company, so it is invisible to a company-scoped request.
        // Ownership is never inferred from the reader's company — the mistake that made the company-1 fallback
        // dangerous. Live data holds zero such rows today (measured), but NotifyAsync still accepts
        // `companyId: null`, so the case is reachable and the behaviour is pinned.
        [Fact]
        public void A_null_company_notification_is_invisible_to_a_company_scoped_request()
        {
            var scope = new CompanyScopeHolder();
            scope.Set(1, null);

            Assert.False(NotificationCompanyPolicy.IsVisible(null, scope));

            var other = new CompanyScopeHolder();
            other.Set(2, null);
            Assert.False(NotificationCompanyPolicy.IsVisible(null, other));
        }

        // Only an authorized cross-company bypass sees it — and then it is visible for what it is, an
        // unattributed row, to an operator who was authorized and audited.
        [Fact]
        public void A_null_company_notification_is_visible_only_under_an_authorized_bypass()
        {
            var scope = new CompanyScopeHolder();
            scope.Set(1, null);
            var bypass = Bypass(scope);

            using (bypass.Begin(CompanyBypassKind.PlatformMonitoring, Admin(), "inspect unattributed rows"))
            {
                Assert.True(NotificationCompanyPolicy.IsVisible(null, scope));
            }

            Assert.False(NotificationCompanyPolicy.IsVisible(null, scope));
        }

        // The predicate is not only correct in memory — it must translate to SQL and exclude the NULL row there
        // too, which is where the three-valued logic actually applies. Run against the real provider.
        [Fact]
        public async Task The_null_company_row_is_excluded_by_the_real_predicate_in_the_database()
        {
            using var host = new PlatformTestHost();
            host.Seed.Notifications.AddRange(
                new Notification { RecipientEmployeeID = 1, CompanyID = 1, Type = "t", IsRead = false, CreatedAt = DateTime.UtcNow },
                new Notification { RecipientEmployeeID = 2, CompanyID = 2, Type = "t", IsRead = false, CreatedAt = DateTime.UtcNow },
                new Notification { RecipientEmployeeID = 3, CompanyID = null, Type = "t", IsRead = false, CreatedAt = DateTime.UtcNow });
            await host.Seed.SaveChangesAsync();

            var scope = host.Holder;

            var visible = await host.Db.Notifications.AsNoTracking()
                .Where(NotificationCompanyPolicy.QueryFilter(scope))
                .Select(n => n.RecipientEmployeeID).OrderBy(x => x).ToListAsync();
            Assert.Equal(new[] { 1 }, visible);       // company 2 excluded, NULL excluded

            var bypass = host.HostBypass();
            using (bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, Admin(), "audit"))
            {
                var all = await host.Db.Notifications.AsNoTracking()
                    .Where(NotificationCompanyPolicy.QueryFilter(scope))
                    .Select(n => n.RecipientEmployeeID).OrderBy(x => x).ToListAsync();
                Assert.Equal(new[] { 1, 2, 3 }, all);
            }
        }

        // The unresolved case, in SQL. This is the assertion that would FAIL against the naive
        // `n.CompanyID == scope.CompanyId` formulation: EF's null-safe comparison would return row 3.
        [Fact]
        public async Task An_unresolved_scope_reads_no_notifications_at_all_in_the_database()
        {
            using var host = new PlatformTestHost(companyId: null);   // nothing resolved
            host.Seed.Notifications.AddRange(
                new Notification { RecipientEmployeeID = 1, CompanyID = 1, Type = "t", IsRead = false, CreatedAt = DateTime.UtcNow },
                new Notification { RecipientEmployeeID = 3, CompanyID = null, Type = "t", IsRead = false, CreatedAt = DateTime.UtcNow });
            await host.Seed.SaveChangesAsync();

            // Both the installed filter and the standalone predicate must agree: nothing.
            var visible = await host.Db.Notifications.AsNoTracking()
                .Where(NotificationCompanyPolicy.QueryFilter(host.Holder))
                .ToListAsync();

            Assert.Empty(visible);
        }

        // ---- helpers --------------------------------------------------------------------------------------

        private static IBusinessEventMonitorService MonitorWith(PlatformTestHost host, ICompanyIsolationBypass bypass)
        {
            var opts = Options.Create(new BusinessEventDispatchOptions());
            return new BusinessEventMonitorService(
                host.Db, host.Registry(), new SqlEventDispatchStore(host.Db, opts), opts, bypass,
                NullLogger<BusinessEventMonitorService>.Instance);
        }

        private static async Task<long> AddEventWithDispatchAsync(PlatformTestHost host, int companyId, int entityId)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = EntityRegistry.PurchaseInvoice, EntityId = entityId,
                EventType = PurchaseInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = PurchaseInvoiceEventPayload.Version, CreatedAt = DateTime.UtcNow,
                Payload = "{\"invoiceNumber\":\"PV-" + entityId + "\",\"totalAfter\":100.0}",
            };
            // B4: seeding spans companies, so it uses the authorized arrangement context; the assertions still
            // read through the filtered host.Db.
            host.Seed.BusinessEvents.Add(ev);
            await host.Seed.SaveChangesAsync();

            host.Seed.BusinessEventDispatches.Add(new BusinessEventDispatch
            {
                EventId = ev.EventId, Consumer = BusinessEventConsumers.NotificationProjection,
                Status = BusinessEventDispatchStatus.Pending, Attempts = 0,
            });
            await host.Seed.SaveChangesAsync();
            return ev.EventId;
        }

        private static CrossBuy.Models.Context.Admin.Employee TestEmployee(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        // Persists what it is told, so the dedup assertion is about rows in the table rather than about a
        // recording list. Mirrors NotificationService's write without needing SignalR.
        private sealed class CountingSink : INotificationService
        {
            private readonly CrossBuy.Models.Context.CrossDbContext _db;
            public CountingSink(CrossBuy.Models.Context.CrossDbContext db) { _db = db; }

            public async Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
                string? bodyAr, string? bodyEn, string type, int? refId = null,
                string? url = null, int? companyId = null, int? actorEmployeeId = null,
                string? priority = null, string? category = null, string? dedupKey = null,
                DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null)
            {
                _db.Notifications.Add(new Notification
                {
                    RecipientEmployeeID = recipientEmployeeId, TitleAr = titleAr, TitleEn = titleEn,
                    BodyAr = bodyAr, BodyEn = bodyEn, Type = type, RefId = refId, IsRead = false,
                    CreatedAt = DateTime.UtcNow, CompanyID = companyId, Url = url,
                    ActorEmployeeID = actorEmployeeId, DedupKey = dedupKey,
                    EntityType = entityType, EntityId = entityId,
                });
                await _db.SaveChangesAsync();
            }

            public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
                string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
                string type, int? refId = null, int? exceptEmployeeId = null)
                => throw new InvalidOperationException("The projection resolves recipients itself.");
        }
    }
}
