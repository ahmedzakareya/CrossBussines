using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
    // H83 — the waiting-room display is pushed over SignalR by HospitalQueueSignalInterceptor, a SINGLETON attached to
    // CrossDbContext in Program.cs. A singleton that captured a scoped service would stop the app from starting while
    // every hand-built unit test stayed green (see Stage1DiWiringTests), so the container itself must build, with
    // ValidateOnBuild + ValidateScopes, exactly as Program.cs wires it.
    public class HospitalQueueSignalWiringTests
    {
        private sealed class HostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime, HostLifetime>();   // the web host supplies this
            services.AddSignalR();
            services.AddSingleton<HospitalQueueSignalInterceptor>();
            services.AddDbContext<CrossDbContext>((sp, options) =>
                options.UseSqlite("DataSource=:memory:").AddInterceptors(sp.GetRequiredService<HospitalQueueSignalInterceptor>()),
                ServiceLifetime.Scoped);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        [Fact]
        public void The_container_builds_and_the_context_carries_the_queue_signal()
        {
            using var sp = Build();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();
            var interceptors = db.GetService<IDbContextOptions>().Extensions
                .OfType<CoreOptionsExtension>().Single().Interceptors ?? Array.Empty<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>();
            Assert.Contains(interceptors, i => i is HospitalQueueSignalInterceptor);
            // one instance for the whole app: the same object across scopes
            using var other = sp.CreateScope();
            Assert.Same(sp.GetRequiredService<HospitalQueueSignalInterceptor>(), other.ServiceProvider.GetRequiredService<HospitalQueueSignalInterceptor>());
        }
        // ---- the signal's timing, on a real CrossDbContext (SQLite in memory) with a recording hub ----
        private sealed class Proxy : Microsoft.AspNetCore.SignalR.IHubClients, Microsoft.AspNetCore.SignalR.IClientProxy
        {
            public readonly List<string> Sent = new();
            private string _group = "";
            public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) { _group = groupName; return this; }
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { lock (Sent) Sent.Add(_group + ":" + method); return Task.CompletedTask; }
            public Microsoft.AspNetCore.SignalR.IClientProxy All => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> e) => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy Client(string c) => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> c) => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string g, IReadOnlyList<string> e) => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> g) => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy User(string u) => this;
            public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> u) => this;
        }

        private sealed class Recorder : Microsoft.AspNetCore.SignalR.IHubContext<CrossBuy.Hubs.NotificationsHub>
        {
            private readonly Proxy _p = new();
            public List<string> Sent => _p.Sent;
            public Microsoft.AspNetCore.SignalR.IHubClients Clients => _p;
            public Microsoft.AspNetCore.SignalR.IGroupManager Groups => throw new NotSupportedException();
        }

        private static (CrossDbContext Db, Recorder Hub, Microsoft.Data.Sqlite.SqliteConnection Conn) Open()
        {
            var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:;Foreign Keys=False"); conn.Open();
            var hub = new Recorder();
            var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(conn).AddInterceptors(new HospitalQueueSignalInterceptor(hub)).Options);
            db.Database.EnsureCreated();
            return (db, hub, conn);
        }

        private static CrossBuy.Models.Context.Hospital.HospitalAppointment Appt(int company) => new()
        { CompanyId = company, PatientId = 1, PractitionerId = 1, AppointmentDate = DateTime.Today, StartMinute = 600, DurationMinutes = 15, CreatedAt = DateTime.UtcNow };

        [Fact]
        public async Task A_status_change_signals_its_own_company_once_saved()
        {
            var (db, hub, conn) = Open(); using var _c = conn; await using var _d = db;
            var a = Appt(7); db.Add(a); await db.SaveChangesAsync();
            Assert.Equal(new[] { "co-7:hospitalQueueChanged" }, hub.Sent);
            hub.Sent.Clear();
            a.Status = CrossBuy.Models.Context.Hospital.HospitalAppointmentStatus.InRoom; await db.SaveChangesAsync();
            Assert.Equal(new[] { "co-7:hospitalQueueChanged" }, hub.Sent);
            hub.Sent.Clear();
            a.DurationMinutes = 20; await db.SaveChangesAsync();          // not a queue change: no signal
            Assert.Empty(hub.Sent);
        }

        [Fact]
        public async Task Inside_a_transaction_the_signal_waits_for_the_commit_and_dies_with_a_rollback()
        {
            var (db, hub, conn) = Open(); using var _c = conn; await using var _d = db;
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.Add(Appt(7)); await db.SaveChangesAsync();
                Assert.Empty(hub.Sent);                                   // saved but not committed: the screen would read the old state
                await tx.CommitAsync();
            }
            Assert.Equal(new[] { "co-7:hospitalQueueChanged" }, hub.Sent);
            hub.Sent.Clear();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.Add(Appt(8)); await db.SaveChangesAsync();
                await tx.RollbackAsync();
            }
            Assert.Empty(hub.Sent);
        }

        // ---- the ticket is one per hospital day: the model and its slice declare the same filtered unique index ----
        [Fact]
        public void One_ticket_per_day_is_unique_in_the_model_and_in_its_slice()
        {
            var model = HospitalSliceParity.Model(CrossBuy.Models.Context.Hospital.HospitalOutpatientModel.Configure);
            var ix = model.FindEntityType(typeof(CrossBuy.Models.Context.Hospital.HospitalAppointment))!.GetIndexes()
                .Single(i => i.GetDatabaseName() == "UX_HospitalAppointments_OneTicketPerDay");
            Assert.True(ix.IsUnique);
            Assert.Equal(new[] { "CompanyId", "AppointmentDate", "QueueNumber" }, ix.Properties.Select(p => p.Name));
            Assert.Equal("[QueueNumber] IS NOT NULL", ix.GetFilter());

            var sql = HospitalSliceParity.Slice("hospital_h83_queue_ticket_unique.sql");
            Assert.Contains("CREATE UNIQUE INDEX UX_HospitalAppointments_OneTicketPerDay", sql);
            Assert.Contains("ON dbo.HospitalAppointments (CompanyId, AppointmentDate, QueueNumber)", sql);
            Assert.Contains("WHERE QueueNumber IS NOT NULL", sql);
            Assert.Contains("RAISERROR", sql);   // refuses, and changes nothing, when duplicates already exist
        }
    }
}
