using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Communication Platform test fixture.
    //
    // Deliberately built the same way PlatformTestHost is, for the same reasons it gives:
    //   * SQLITE, not the EF InMemory provider. SQLite honours transactions; InMemory ignores them, which
    //     would let every CommTransaction rollback test pass without proving anything.
    //   * THE REAL CrossDbContext MODEL, so the mapping under test is the production mapping — including the
    //     one-line CommunicationModel.Configure hook in OnModelCreating.
    //   * REFERENTIAL INTEGRITY OFF. The comm tables sit at the end of long FK chains (Employee ->
    //     AspNetUsers -> …, Employee -> Companies -> CompanyTypes) and seeding those would add dozens of rows
    //     irrelevant to what these tests assert. The FKs that matter within this platform are created and
    //     enforced by deploy/sql/communication_platform_slice_001.sql in a real database.
    //
    // The permission provider is a STUB, so visibility tests are deterministic instead of standing up four
    // module RBAC services and their role tables. That is the same concession StubPermissionProvider makes for
    // the kernel's timeline tests — and it is exactly the right seam, because CommAccessPolicy's contract is
    // "consume IPlatformPermissionProvider, never replace it".
    // =============================================================================================
    public sealed class CommunicationTestHost : IDisposable
    {
        public const int CompanyId = 1;
        public const int OtherCompanyId = 2;

        // Fixed employee ids used across the suite. Named so an assertion reads as a sentence.
        public const int Author = 11;
        public const int Colleague = 12;
        public const int Manager = 13;
        public const int Outsider = 14;
        public const int OtherCompanyEmployee = 21;

        // Org node ids for the department tests.
        public const int SalesDepartmentNode = 501;
        public const int SalesSubDepartmentNode = 502;

        private readonly SqliteConnection _connection;

        public CommunicationTestHost(CommunicationPlatformOptions? options = null)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            Holder = new CompanyScopeHolder();
            Holder.Set(CompanyId, null);

            Options = options ?? DefaultOptions();

            Db = NewContext();
            Db.Database.EnsureCreated();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = OFF;";
                command.ExecuteNonQuery();
            }

            SeedEmployees();
        }

        public CompanyScopeHolder Holder { get; }
        public CrossDbContext Db { get; }
        public CommunicationPlatformOptions Options { get; }

        // Every entity code these tests use is onboarded through the CONFIGURATION allow-list rather than by
        // editing EntityRegistry — which is the whole point of ADR-031 and is therefore what the tests exercise.
        // SalesInvoice/PurchaseInvoice/Quotation already carry SupportsComments in the registry; PosOrder does
        // NOT, so it is the case that proves the allow-list works.
        public static CommunicationPlatformOptions DefaultOptions() => new()
        {
            EnabledEntityCodes = new List<string>
            {
                EntityRegistry.SalesInvoice,
                EntityRegistry.PurchaseInvoice,
                EntityRegistry.Quotation,
                EntityRegistry.PosOrder,
                EntityRegistry.Customer,
            },
            EnabledChannels = new List<string> { CommChannel.InApp, CommChannel.Email },

            // The window is left at its default so the edit-window test can drive it explicitly rather than
            // depending on a fixture value.
            AuthorEditWindowMinutes = 60 * 24,
        };

        public CrossDbContext NewContext(ICompanyScopeHolder? scope = null)
        {
            var builder = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_connection)
                .EnableSensitiveDataLogging()
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance));
            return new CrossDbContext(builder.Options, scope ?? Holder);
        }

        // A second request scope over the same database, with its own company. Used by the isolation tests —
        // the arrangement PlatformTestHost.Request exists for, and the only coherent way to simulate another
        // tenant now that the pilot entities are filtered by an ambient scope.
        public (CompanyScopeHolder Holder, CrossDbContext Db) Request(int companyId)
        {
            var holder = new CompanyScopeHolder();
            holder.Set(companyId, null);
            return (holder, NewContext(holder));
        }

        // ---------------------------------------------------------------------------------------------
        // Services. Constructed by hand so each test can substitute one collaborator — but note that
        // CommunicationDiWiringTests separately builds the REAL container from AddCommunicationPlatform,
        // because CLAUDE.md is explicit that hand-constructed services do not verify a DI graph.
        // ---------------------------------------------------------------------------------------------
        public IOptions<CommunicationPlatformOptions> Opt => Microsoft.Extensions.Options.Options.Create(Options);

        public IEntityRegistry Registry(CrossDbContext? db = null) => new EntityRegistry(db ?? Db);

        public ICommEntitySurface Surface(CrossDbContext? db = null) => new CommEntitySurface(Registry(db), Opt);

        public ICommBodyPolicy BodyPolicy() => new CommBodyPolicy(Opt);

        public ICommActorDirectory Actors(CrossDbContext? db = null) => new CommActorDirectory(db ?? Db);

        public ICommAuditWriter Audit(CrossDbContext? db = null) => new CommAuditWriter(db ?? Db);

        public ICommPrincipalResolver Principals(CrossDbContext? db = null)
        {
            var ctx = db ?? Db;
            var org = new OrgHierarchy(ctx, NullLogger<OrgHierarchy>.Instance);
            var sources = new ICommPrincipalSource[]
            {
                new EmployeeCommPrincipalSource(ctx),
                new TeamCommPrincipalSource(org, ctx),
                new DepartmentCommPrincipalSource(ctx),
            };
            return new CommPrincipalResolver(sources, ctx, Opt, NullLogger<CommPrincipalResolver>.Instance);
        }

        // The permission stub, as a field so a test can swap it mid-arrangement.
        public StubPermissionProvider Permissions { get; set; } =
            new(PlatformActions.View);

        public ICommAccessPolicy Access(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommAccessPolicy(permissions ?? Permissions, Surface(ctx), Principals(ctx), ctx, Opt);
        }

        // The recording bridge: captures what WOULD have gone to the kernel without a kernel schema present.
        // That is how the bridge is testable at all — PlatformBusinessEventBridge calls RecordAsync, which
        // requires BusinessEvents to exist and an ambient transaction to be open.
        public RecordingBusinessEventBridge Bridge { get; } = new();

        public ICommEventPublisher Events(CrossDbContext? db = null, ICommBusinessEventBridge? bridge = null)
        {
            var ctx = db ?? Db;
            return new CommEventPublisher(ctx, Audit(ctx), bridge ?? Bridge, Opt, NullLogger<CommEventPublisher>.Instance);
        }

        public ICommTemplateCatalog Catalog() => new CommTemplateCatalog();

        public ICommTemplateRenderer Renderer() => new CommTemplateRenderer(new DefaultCommTemplateTextProvider());

        public ICommPreferenceResolver Preferences(CrossDbContext? db = null)
            => new CommPreferenceResolver(db ?? Db, Opt);

        public ICommNotificationService Notifications(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommNotificationService(
                ctx, Catalog(), Renderer(), Preferences(ctx), Access(ctx, permissions), Actors(ctx), Registry(ctx), Opt);
        }

        public ICommAttachmentService Attachments(CrossDbContext? db = null)
        {
            var ctx = db ?? Db;
            return new CommAttachmentService(
                ctx, new DefaultCommFilePreviewProvider(), Events(ctx), Actors(ctx), Surface(ctx), Access(ctx), Opt);
        }

        public ICommThreadService Threads(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommThreadService(
                ctx, Surface(ctx), Access(ctx, permissions), Events(ctx), Audit(ctx), Actors(ctx), Principals(ctx), Opt);
        }

        public ICommMentionService Mentions(CrossDbContext? db = null)
        {
            var ctx = db ?? Db;
            return new CommMentionService(ctx, Principals(ctx), Events(ctx), Actors(ctx), BodyPolicy(), Opt);
        }

        public ICommParticipationService Participation(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommParticipationService(
                ctx, Threads(ctx, permissions), Access(ctx, permissions), Events(ctx), Actors(ctx), Opt);
        }

        public ICommCommentService Comments(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommCommentService(
                ctx,
                Threads(ctx, permissions),
                Access(ctx, permissions),
                BodyPolicy(),
                Mentions(ctx),
                Participation(ctx, permissions),
                Notifications(ctx, permissions),
                Attachments(ctx),
                Events(ctx),
                Actors(ctx),
                Surface(ctx),
                Opt);
        }

        public ICommReactionService Reactions(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommReactionService(
                ctx, Threads(ctx, permissions), Access(ctx, permissions), Events(ctx), Actors(ctx),
                Notifications(ctx, permissions));
        }

        public ICommReadStatusService ReadStatus(CrossDbContext? db = null, IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            return new CommReadStatusService(ctx, Threads(ctx, permissions), Access(ctx, permissions));
        }

        public ICommNotificationDispatcher Dispatcher(
            CrossDbContext? db = null, IEnumerable<ICommNotificationChannel>? channels = null)
        {
            var ctx = db ?? Db;
            return new CommNotificationDispatcher(
                ctx,
                new EfCommDeliveryClaimStore(ctx, Opt),
                channels ?? new ICommNotificationChannel[] { new InAppCommNotificationChannel() },
                Audit(ctx),
                Opt,
                NullLogger<CommNotificationDispatcher>.Instance);
        }

        public ICommTimelineAggregator Timeline(
            CrossDbContext? db = null, IEnumerable<ICommTimelineSource>? sources = null,
            IPlatformPermissionProvider? permissions = null)
        {
            var ctx = db ?? Db;
            var access = Access(ctx, permissions);
            var built = sources ?? new ICommTimelineSource[]
            {
                new CommCommentTimelineSource(ctx, access, Actors(ctx), BodyPolicy()),
                new CommMentionTimelineSource(ctx, access, Actors(ctx)),
                new CommAuditTimelineSource(ctx, access, Actors(ctx)),
            };
            return new CommTimelineAggregator(built, access, Opt, NullLogger<CommTimelineAggregator>.Instance);
        }

        // ---------------------------------------------------------------------------------------------
        public static BusinessContext Context(int employeeId = Author, int companyId = CompanyId, int? branchId = null)
            => new()
            {
                CompanyId = companyId,
                BranchId = branchId,
                EmployeeId = employeeId,
                UserId = "user-" + employeeId,
                Roles = Array.Empty<string>(),
                CorrelationId = Guid.NewGuid(),
            };

        public static CommEntityRef Invoice(int id = 1001) => new(EntityRegistry.SalesInvoice, id);

        // PosOrder does NOT carry SupportsComments in the registry, so it is the case that proves the
        // configuration allow-list actually onboards an entity.
        public static CommEntityRef PosOrder(int id = 2001) => new(EntityRegistry.PosOrder, id);

        public CommCommentRequest CommentOn(
            CommEntityRef entity, string body = "hello", string? visibility = null, long? threadId = null,
            long? parentCommentId = null, string? dedupKey = null,
            IReadOnlyList<CommMentionRequest>? mentions = null,
            IReadOnlyList<CommAttachmentRequest>? attachments = null)
            => new()
            {
                Entity = entity,
                ThreadId = threadId,
                ParentCommentId = parentCommentId,
                Body = body,
                Visibility = visibility ?? CommVisibility.Internal,
                Mentions = mentions,
                Attachments = attachments,
                DedupKey = dedupKey,
            };

        // ---------------------------------------------------------------------------------------------
        // Seed. Five employees and a two-level org tree — the minimum that lets team, department and
        // company-isolation assertions all be real rather than mocked.
        private void SeedEmployees()
        {
            Db.Employee.AddRange(
                Employee(Author, "أحمد", "Ahmed", CompanyId, SalesDepartmentNode),
                Employee(Colleague, "سارة", "Sara", CompanyId, SalesDepartmentNode),
                Employee(Manager, "خالد", "Khaled", CompanyId, SalesDepartmentNode),

                // In the SUB-department, so a mention of the parent node must reach them — the case that
                // distinguishes a node walk from a flat column match.
                Employee(Outsider, "منى", "Mona", CompanyId, SalesSubDepartmentNode),

                // Another company, same department node. The org tree carries no CompanyID, so this row is what
                // proves the resolver's company intersection actually excludes them.
                Employee(OtherCompanyEmployee, "بدر", "Badr", OtherCompanyId, SalesDepartmentNode));

            Db.Hierarchicals.AddRange(
                new Hierarchical { H_ID = SalesDepartmentNode, H_Name = "المبيعات", H_NameEn = "Sales", H_Parent = null, H_Type = 1, IsActive = true },
                new Hierarchical { H_ID = SalesSubDepartmentNode, H_Name = "مبيعات التجزئة", H_NameEn = "Retail Sales", H_Parent = SalesDepartmentNode, H_Type = 1, IsActive = true },

                // Employee nodes (H_Type == 5) for the TEAM walk: Manager above Author and Colleague. The type
                // constant is OrgHierarchy's, not ours — it is what that service filters on.
                new Hierarchical { H_ID = 601, H_Type = 5, H_ObjectID = Manager, H_Parent = SalesDepartmentNode, IsActive = true },
                new Hierarchical { H_ID = 602, H_Type = 5, H_ObjectID = Author, H_Parent = 601, IsActive = true },
                new Hierarchical { H_ID = 603, H_Type = 5, H_ObjectID = Colleague, H_Parent = 601, IsActive = true },
                new Hierarchical { H_ID = 604, H_Type = 5, H_ObjectID = OtherCompanyEmployee, H_Parent = 601, IsActive = true });

            Db.SaveChanges();
        }

        // Employee carries several NON-NULLABLE string columns (FirstName, LastName, Address, PhoneNumber,
        // Email, ProfileImage, Gender, MaritalStatus, UserId) that this platform never reads. They are filled
        // with placeholders because SQLite enforces NOT NULL and the seed would otherwise fail — the alternative,
        // relaxing the model for tests, would mean testing a context the application never creates.
        private static Employee Employee(int id, string nameAr, string nameEn, int companyId, int? departmentId)
            => new()
            {
                ID = id,
                FirstName = nameEn,
                LastName = "Test",
                FullName = nameAr,
                FullNameEn = nameEn,
                Address = "-",
                PhoneNumber = "-",
                Email = $"emp{id}@test.local",
                ProfileImage = "-",
                Gender = "-",
                MaritalStatus = "-",
                UserId = "user-" + id,
                EmpCompanyID = companyId,
                DepartmentID = departmentId,
                IsActive = true,
            };

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    // Captures forwarded events instead of writing them, so the bridge's TRANSLATION can be asserted without a
    // kernel schema. The real bridge is separately covered by asserting it is NOT registered by default.
    public sealed class RecordingBusinessEventBridge : ICommBusinessEventBridge
    {
        private long _next = 1;
        public List<CommEvent> Forwarded { get; } = new();

        public Task<long?> ForwardAsync(CommEvent commEvent, CancellationToken cancellationToken = default)
        {
            if (!CommEventTypes.IsBridgeable(commEvent.EventType)) return Task.FromResult<long?>(null);
            Forwarded.Add(commEvent);
            return Task.FromResult<long?>(_next++);
        }
    }

    // A channel that records what it was asked to send and answers however the test wants. This is how the
    // dispatcher's Sent / Failed / Skipped branches are driven without a real transport.
    public sealed class RecordingCommChannel : ICommNotificationChannel
    {
        private readonly Func<CommChannelMessage, CommChannelResult> _behaviour;

        public RecordingCommChannel(string channel, bool enabled = true, Func<CommChannelMessage, CommChannelResult>? behaviour = null)
        {
            Channel = channel;
            IsEnabled = enabled;
            _behaviour = behaviour ?? (_ => CommChannelResult.Ok("recorded"));
        }

        public string Channel { get; }
        public bool IsEnabled { get; }
        public List<CommChannelMessage> Sent { get; } = new();

        public Task<CommChannelResult> SendAsync(CommChannelMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(_behaviour(message));
        }
    }

    // A channel that throws, to prove a throwing adapter becomes a retryable Failed row rather than aborting the
    // batch — the contract stated in ICommNotificationChannel.
    public sealed class ThrowingCommChannel : ICommNotificationChannel
    {
        public ThrowingCommChannel(string channel) => Channel = channel;
        public string Channel { get; }
        public bool IsEnabled => true;

        public Task<CommChannelResult> SendAsync(CommChannelMessage message, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("transport exploded");
    }
}
