using static CrossBuy.Tests.B6TestWiring;
﻿using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch A / A6 — the first REAL background permission consumer.
    //
    // This file exists to prove a specific claim, and to make the previous claim's failure visible. Before
    // Stage 1, NotificationProjectionConsumer built a per-recipient BusinessContext and handed it to
    // IPlatformPermissionProvider — and the accounting/inventory adapters threw it away, reading the employee
    // from Session["Employee"] (absent in a dispatcher) and the company from `const int CompanyId = 1`. The
    // consumer documented this honestly in a 14-line comment. The loop ran; it decided nothing.
    //
    // The two tests the brief singles out are:
    //   * two employees in the SAME role receive different results when record access differs;
    //   * cross-company recipients are excluded.
    // Both are impossible to pass without the Batch A changes, which is the point of choosing them.
    public class Stage1NotificationAuthorizationTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        private static Employee Emp(int id, int companyId, bool active = true) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = active,
            Address = "-", PhoneNumber = "-", Email = $"e{id}@example.com", ProfileImage = "-",
            Gender = "M", MaritalStatus = "S", UserId = "user-" + id,
        };

        private static IHttpContextAccessor NoHttp() => new HttpContextAccessor();

        // The REAL provider over the REAL access services, with no HttpContext anywhere. Anything that still
        // depended on a session would deny everyone and these tests would fail loudly.
        private static IPlatformPermissionProvider RealProvider(PlatformTestHost host)
        {
            var accessor = new BusinessContextAccessor(host.Contexts(http: NoHttp()));
            var modules = new List<IModuleAccessService>
            {
                new AccountingAccessService(host.Db, NoHttp(), accessor, Policies(host.Db), Log<AccountingAccessService>()),
                new InventoryAccessService(host.Db, NoHttp(), accessor, Policies(host.Db), Log<InventoryAccessService>()),
                new CrmAccessService(host.Db, NoHttp(), accessor,
                    new OrgHierarchy(host.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrgHierarchy>.Instance),
                B6TestWiring.Policies(host.Db), NullLogger<CrmAccessService>.Instance),
                new PosAccessService(host.Db),
            };
            var adapters = new List<IModulePermissionAdapter>
            {
                new AccountingPermissionAdapter(modules),
                new InventoryPermissionAdapter(modules),
                new ManufacturingPermissionAdapter(modules),
                new CrmPermissionAdapter(modules),
                new PosPermissionAdapter(modules),
                new DefaultPermissionAdapter(),
            };
            return new PlatformPermissionProvider(host.Registry(), adapters, NullLogger<PlatformPermissionProvider>.Instance);
        }

        private static (NotificationProjectionConsumer consumer, RecordingSink sink) Consumer(PlatformTestHost host)
        {
            var sink = new RecordingSink(host.Db);
            var consumer = new NotificationProjectionConsumer(
                host.Db,
                new BusinessEventNotificationMapper(),
                sink,
                host.Registry(),
                RealProvider(host),
                host.Contexts(http: NoHttp()),
                host.HostBypass(), host.Holder,
                NullLogger<NotificationProjectionConsumer>.Instance);
            return (consumer, sink);
        }

        // A purchase invoice event is used because its mapper targets the accounting role scope, which is the
        // module whose session dependency this batch removed.
        private static async Task<BusinessEventEnvelope> PurchaseInvoiceEventAsync(
            PlatformTestHost host, int companyId, int actorId, int entityId = 900)
        {
            var invoice = new PurchaseInvoice
            {
                ID = entityId, CompanyID = companyId, InvoiceNo = "PV-" + entityId,
                InvoiceDate = new DateTime(2026, 1, 1), Status = "Posted", VendorId = 1,
            };
            host.Seed.PurchaseInvoices.Add(invoice);

            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = EntityRegistry.PurchaseInvoice, EntityId = entityId,
                EventType = PurchaseInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = PurchaseInvoiceEventPayload.Version, ActorEmployeeId = actorId,
                CreatedAt = DateTime.UtcNow,
                Payload = System.Text.Json.JsonSerializer.Serialize(
                    new PurchaseInvoiceEventPayload { InvoiceNumber = "PV-" + entityId, TotalAfter = 100m }),
            };
            // B4: the event and its invoice may belong to another company (the isolation tests depend on it),
            // so they are arranged through the authorized cross-company context.
            host.Seed.BusinessEvents.Add(ev);
            await host.Seed.SaveChangesAsync();

            return host.Events().BuildEnvelope(ev);
        }

        // =====================================================================================
        // A8/16 + A8/17 — the two headline proofs
        // =====================================================================================

        // TWO EMPLOYEES, SAME ROLE, DIFFERENT ANSWERS.
        //
        // Both are ChiefAccountant, so both are in the audience. One belongs to company 1 (the event's
        // company) and one to company 2. Before Stage 1 the check could not tell them apart: the adapter read
        // the session (empty) and company 1 (constant), so both got the same answer and both were notified.
        [Fact]
        public async Task Two_employees_in_the_same_role_get_different_results_when_record_access_differs()
        {
            using var host = new PlatformTestHost();
            const int chiefInCompanyOne = 101;
            const int chiefInCompanyTwo = 102;
            const int actor = 103;

            host.Db.Employee.AddRange(
                Emp(chiefInCompanyOne, CompanyOne), Emp(chiefInCompanyTwo, CompanyTwo), Emp(actor, CompanyOne));
            // BOTH hold a ChiefAccountant row against company 1's role table, so the audience query returns
            // both. Their DIFFERENCE is which company they belong to.
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chiefInCompanyOne, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chiefInCompanyTwo, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var (consumer, sink) = Consumer(host);

            await consumer.HandleAsync(envelope);

            Assert.Contains(chiefInCompanyOne, sink.Recipients);
            Assert.DoesNotContain(chiefInCompanyTwo, sink.Recipients);
            Assert.DoesNotContain(actor, sink.Recipients);   // actor exclusion still applies
        }

        // A8/17 — the authorized audience is preserved. A batch that "secures" a consumer by delivering
        // nothing has broken it, so this is asserted as explicitly as the exclusion.
        [Fact]
        public async Task Authorized_recipients_still_receive_notifications()
        {
            using var host = new PlatformTestHost();
            const int chiefA = 111, chiefB = 112, actor = 113;

            host.Db.Employee.AddRange(Emp(chiefA, CompanyOne), Emp(chiefB, CompanyOne), Emp(actor, CompanyOne));
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chiefA, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chiefB, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var (consumer, sink) = Consumer(host);

            await consumer.HandleAsync(envelope);

            Assert.Equal(new[] { chiefA, chiefB }, sink.Recipients.OrderBy(x => x));
            // Entity addressing is preserved so the click-through still resolves through the registry.
            Assert.All(sink.Notifications, n =>
            {
                Assert.Equal(EntityRegistry.PurchaseInvoice, n.EntityType);
                Assert.Equal(900, n.EntityId);
            });
        }

        // A8/16b — cross-company recipients are excluded even when the event's OWN company is the other one.
        [Fact]
        public async Task A_recipient_from_another_company_is_excluded()
        {
            using var host = new PlatformTestHost();
            const int chiefCompanyOne = 121, chiefCompanyTwo = 122, actor = 123;

            host.Db.Employee.AddRange(
                Emp(chiefCompanyOne, CompanyOne), Emp(chiefCompanyTwo, CompanyTwo), Emp(actor, CompanyTwo));
            // The event belongs to company 2; both employees hold a role row in company 2's table.
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyTwo, EmployeeId = chiefCompanyOne, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyTwo, EmployeeId = chiefCompanyTwo, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyTwo, actor);
            var (consumer, sink) = Consumer(host);

            await consumer.HandleAsync(envelope);

            Assert.Equal(new[] { chiefCompanyTwo }, sink.Recipients);
        }

        // An inactive recipient is excluded. A notification to a leaver is a dead link and a data leak, and
        // this is the behaviour that required IsActive = true to be added to the slice-2 test fixtures.
        [Fact]
        public async Task An_inactive_recipient_is_excluded()
        {
            using var host = new PlatformTestHost();
            const int activeChief = 131, leaver = 132, actor = 133;

            host.Db.Employee.AddRange(
                Emp(activeChief, CompanyOne), Emp(leaver, CompanyOne, active: false), Emp(actor, CompanyOne));
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = activeChief, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = leaver, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var (consumer, sink) = Consumer(host);

            await consumer.HandleAsync(envelope);

            Assert.Equal(new[] { activeChief }, sink.Recipients);
        }

        // A recipient whose Employee row is missing entirely is excluded, not defaulted into the company.
        [Fact]
        public async Task A_recipient_with_no_employee_row_is_excluded()
        {
            using var host = new PlatformTestHost();
            const int realChief = 141, ghost = 999, actor = 143;

            host.Db.Employee.AddRange(Emp(realChief, CompanyOne), Emp(actor, CompanyOne));
            // A role row pointing at an employee that does not exist — real data goes stale this way.
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = realChief, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ghost, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var (consumer, sink) = Consumer(host);

            await consumer.HandleAsync(envelope);

            Assert.Equal(new[] { realChief }, sink.Recipients);
        }

        // THE DIRECT A6 PROOF: the provider is asked once PER RECIPIENT, and each question carries THAT
        // recipient's own resolved context.
        //
        // This is the assertion the old implementation could not have passed. It built a context per candidate
        // too, but the adapters discarded it — so the only way to demonstrate the fix is to capture what the
        // provider is actually asked, not just what gets delivered.
        [Fact]
        public async Task The_provider_is_asked_once_per_recipient_with_that_recipients_own_context()
        {
            using var host = new PlatformTestHost();
            const int chiefA = 151, chiefB = 152, actor = 153;

            host.Db.Employee.AddRange(Emp(chiefA, CompanyOne), Emp(chiefB, CompanyOne), Emp(actor, CompanyOne));
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chiefA, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chiefB, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var recorder = new RecordingProvider(RealProvider(host));
            var sink = new RecordingSink(host.Db);
            var consumer = new NotificationProjectionConsumer(
                host.Db, new BusinessEventNotificationMapper(), sink, host.Registry(),
                recorder, host.Contexts(http: NoHttp()), host.HostBypass(), host.Holder,
                NullLogger<NotificationProjectionConsumer>.Instance);

            await consumer.HandleAsync(envelope);

            // Two candidates ⇒ two questions, each naming a DIFFERENT employee.
            Assert.Equal(2, recorder.Asked.Count);
            Assert.Equal(new[] { chiefA, chiefB }, recorder.Asked.Select(a => a.EmployeeId!.Value).OrderBy(x => x));

            // Each context carries the recipient's real identity, resolved from the Employee row with no HTTP
            // request in sight — and is NOT a system context, which would have bypassed the module entirely.
            Assert.All(recorder.Asked, context =>
            {
                Assert.Equal(CompanyOne, context.CompanyId);
                Assert.False(context.IsSystem);
                Assert.Equal(BusinessContextSource.Integration, context.Source);
                Assert.False(string.IsNullOrEmpty(context.UserId));
                Assert.True(context.IsAuthenticated);
            });
            // The actor is never asked about — excluded before the permission gate.
            Assert.DoesNotContain(actor, recorder.Asked.Select(a => a.EmployeeId!.Value));

            Assert.Equal(new[] { chiefA, chiefB }, sink.Recipients.OrderBy(x => x));
        }

        // A denial from the provider stops delivery. Paired with the test above, this closes the loop: the
        // question is asked per recipient, and the answer is honoured.
        [Fact]
        public async Task A_denied_recipient_receives_nothing()
        {
            using var host = new PlatformTestHost();
            const int chief = 154, actor = 155;

            host.Db.Employee.AddRange(Emp(chief, CompanyOne), Emp(actor, CompanyOne));
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chief, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var sink = new RecordingSink(host.Db);
            var consumer = new NotificationProjectionConsumer(
                host.Db, new BusinessEventNotificationMapper(), sink, host.Registry(),
                new DenyingProvider(), host.Contexts(http: NoHttp()), host.HostBypass(), host.Holder,
                NullLogger<NotificationProjectionConsumer>.Instance);

            await consumer.HandleAsync(envelope);

            Assert.Empty(sink.Recipients);
        }

        // Idempotency is unchanged by the new gate: redelivery after a stale claim must not double-send.
        [Fact]
        public async Task Redelivery_still_does_not_duplicate_an_authorized_notification()
        {
            using var host = new PlatformTestHost();
            const int chief = 161, actor = 162;

            host.Db.Employee.AddRange(Emp(chief, CompanyOne), Emp(actor, CompanyOne));
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = chief, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var envelope = await PurchaseInvoiceEventAsync(host, CompanyOne, actor);
            var (consumer, sink) = Consumer(host);

            await consumer.HandleAsync(envelope);
            await consumer.HandleAsync(envelope);   // stale-claim redelivery

            Assert.Single(sink.Recipients);
        }

        // ---- source-level guard: the withdrawn disclaimer must not silently return ----
        [Fact]
        public void The_consumer_no_longer_documents_a_non_per_recipient_limitation()
        {
            var root = FindRepoRoot();
            var code = File.ReadAllText(Path.Combine(root, "CrossBuy", "BL", "Platform", "NotificationProjectionConsumer.cs"));

            // The withdrawn disclaimer was headed "HONEST LIMITATION, do not over-read this loop". If that
            // heading returns, the per-recipient check has been undone.
            //
            // The guard deliberately does NOT search for the phrase "not specific to": the current comment uses
            // it to explain what the code no longer does, and a guard that forbids describing history would push
            // the next author to delete the explanation rather than keep the behaviour.
            Assert.DoesNotContain("HONEST LIMITATION", code);
            // The consumer must resolve each recipient through the factory rather than hand-building a context…
            Assert.Contains("ForEmployeeAsync", code);
            // …and must NOT go back to constructing one inline, which is how the discarded-context bug looked.
            Assert.DoesNotContain("BuildRecipientContextAsync", code);
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy", "BL", "Platform", "NotificationProjectionConsumer.cs")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
        }

        // Captures every context the consumer asks about, then delegates to the real provider.
        private sealed class RecordingProvider : IPlatformPermissionProvider
        {
            private readonly IPlatformPermissionProvider _inner;
            public RecordingProvider(IPlatformPermissionProvider inner) { _inner = inner; }

            public List<BusinessContext> Asked { get; } = new();

            public Task<PermissionDecision> CanAsync(
                BusinessContext context, string entityType, int entityId, string action, CancellationToken cancellationToken = default)
            {
                Asked.Add(context);
                return _inner.CanAsync(context, entityType, entityId, action, cancellationToken);
            }
        }

        private sealed class DenyingProvider : IPlatformPermissionProvider
        {
            public Task<PermissionDecision> CanAsync(
                BusinessContext context, string entityType, int entityId, string action, CancellationToken cancellationToken = default)
                => Task.FromResult(PermissionDecision.Deny("test: denied"));
        }

        // Records what was delivered AND persists it, so the consumer's own dedup lookup works.
        private sealed class RecordingSink : INotificationService
        {
            private readonly CrossBuy.Models.Context.CrossDbContext _db;
            public RecordingSink(CrossBuy.Models.Context.CrossDbContext db) { _db = db; }

            public List<int> Recipients { get; } = new();
            public List<CrossBuy.Models.Context.Admin.Notification> Notifications { get; } = new();

            public async Task NotifyAsync(
                int recipientEmployeeId, string? titleAr, string? titleEn,
                string? bodyAr, string? bodyEn, string type, int? refId = null,
                string? url = null, int? companyId = null, int? actorEmployeeId = null,
                string? priority = null, string? category = null, string? dedupKey = null,
                DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null)
            {
                var row = new CrossBuy.Models.Context.Admin.Notification
                {
                    RecipientEmployeeID = recipientEmployeeId, TitleAr = titleAr, TitleEn = titleEn,
                    BodyAr = bodyAr, BodyEn = bodyEn, Type = type, RefId = refId, Url = url,
                    CompanyID = companyId, ActorEmployeeID = actorEmployeeId,
                    Priority = priority, Category = category, DedupKey = dedupKey, ExpiresAt = expiresAt,
                    Icon = icon, EntityType = entityType, EntityId = entityId, IsRead = false,
                };
                _db.Notifications.Add(row);
                await _db.SaveChangesAsync();
                Recipients.Add(recipientEmployeeId);
                Notifications.Add(row);
            }

            public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
                string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
                string type, int? refId = null, int? exceptEmployeeId = null)
                => throw new InvalidOperationException(
                    "NotificationProjectionConsumer must resolve recipients itself so each one gets its own " +
                    "dedup key and permission check; calling NotifyRoleAsync would bypass both.");
        }
    }
}
