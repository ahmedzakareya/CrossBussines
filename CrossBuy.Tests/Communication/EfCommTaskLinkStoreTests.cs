using CrossBuy.BL.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Communication;

// =================================================================================================
// EfCommTaskLinkStore against a REAL relational database.
//
// WHY THIS EXISTS SEPARATELY. The adapter's own suite proves the CONTRACT using an in-memory store.
// That cannot prove what only a database decides: whether the filtered unique index actually refuses
// a second live link, whether a soft unlink frees the pair for relinking, and whether the company
// predicate holds once the rows are really there. Those are storage properties, so they need storage.
//
// HOW IT GETS A TABLE THE PRODUCTION MODEL DOES NOT HAVE. CommTaskLink is deliberately absent from
// CommunicationModel.Configure, because CommunicationSchemaParityTests holds the EF model and
// deploy/sql/communication_platform_slice_001.sql to each other exactly, and that script is TAB-1
// shared and manifest-hash-coupled. So this suite maps the entity in a TEST-ONLY subclass of
// CrossDbContext and builds exactly the keys and indexes the proposed DDL declares. Production is
// untouched: the parity test still sees the model it expects.
//
// THAT IS ALSO THE LIMITATION, AND IT IS STATED RATHER THAN HIDDEN. These tests prove the store works
// against a schema SHAPED LIKE the proposal, on SQLite. They do not prove the SQL Server script runs,
// and they are not a substitute for applying it. SQLite enforces a filtered unique index, which is the
// property under test; it does not share every SQL Server behaviour.
// =================================================================================================
public sealed class EfCommTaskLinkStoreTests : IDisposable
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CrossDbContext> _options;

    public EfCommTaskLinkStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CrossDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var seed = NewContext();
        seed.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // A fresh context per operation, on the SAME connection — so a read genuinely goes back to the
    // database instead of being answered from a change tracker that still remembers the write.
    private LinkTestContext NewContext() => new(_options);

    private EfCommTaskLinkStore Store(LinkTestContext db) => new(db);

    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_link_is_inserted_and_read_back_from_the_database()
    {
        using (var db = NewContext())
            await Store(db).AddAsync(CompanyA, 7, 12, commentId: 99,
                CommTaskLinkOrigin.LinkedExisting, createdBy: 11);

        using var fresh = NewContext();
        var rows = await Store(fresh).ForThreadAsync(CompanyA, 12);

        var row = Assert.Single(rows);
        Assert.Equal(7, row.TaskId);
        Assert.Equal(12, row.ThreadId);
        Assert.Equal(99, row.CommentId);
        Assert.Equal(CommTaskLinkOrigin.LinkedExisting, row.Origin);
        Assert.Equal(11, row.CreatedBy);

        // And the reverse direction resolves the same row — both indexes in the proposal exist for this.
        var byTask = await Store(fresh).ForTaskAsync(CompanyA, 7);
        Assert.Equal(row.Id, Assert.Single(byTask).Id);
    }

    // THE PROPERTY ONLY A DATABASE CAN GIVE. Two writers that both read nothing and both insert: the
    // application's read-before-write cannot settle this, the filtered unique index can.
    // THE PROPERTY ONLY A DATABASE CAN GIVE. Two writers that both read nothing and both insert: the
    // application's read-before-write cannot settle this, a filtered unique index can.
    //
    // Since comm_task_links_m2m there are TWO such rules, and this asserts each. The earlier version
    // of this test used two DIFFERENT CommentIds as its duplicate — which is now a pair of legitimate
    // message-level links, so it was asserting the superseded single-index rule.
    [Fact]
    public async Task The_unique_indexes_refuse_a_second_live_link_of_the_SAME_kind()
    {
        // MESSAGE-LEVEL: same pair, same message.
        using (var first = NewContext())
        using (var second = NewContext())
        {
            first.Set<CommTaskLink>().Add(NewRow(CompanyA, task: 7, thread: 12, comment: 100));
            second.Set<CommTaskLink>().Add(NewRow(CompanyA, task: 7, thread: 12, comment: 100));
            await first.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => second.SaveChangesAsync());
        }

        // CONVERSATION-LEVEL: same pair, both with no message.
        using (var third = NewContext())
        using (var fourth = NewContext())
        {
            third.Set<CommTaskLink>().Add(NewRow(CompanyA, task: 7, thread: 12, comment: null));
            fourth.Set<CommTaskLink>().Add(NewRow(CompanyA, task: 7, thread: 12, comment: null));
            await third.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => fourth.SaveChangesAsync());
        }

        // THE CONTROL, and the whole point of the migration: a DIFFERENT message on the same pair is
        // a different relationship and is accepted — so the refusals above are about the key and not
        // about two contexts writing at once.
        using (var fifth = NewContext())
        {
            fifth.Set<CommTaskLink>().Add(NewRow(CompanyA, task: 7, thread: 12, comment: 200));
            await fifth.SaveChangesAsync();
        }

        using var fresh = NewContext();
        var rows = await Store(fresh).ForThreadAsync(CompanyA, 12);

        // One conversation-level link and two message-level links, coexisting.
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.CommentId == null);
        Assert.Single(rows, r => r.CommentId == 100);
        Assert.Single(rows, r => r.CommentId == 200);
    }

    // CommentId IS part of the live identity since comm_task_links_m2m: a different message is a
    // different relationship. This test previously asserted the opposite, which was correct for the
    // single-index schema it was written against.
    [Fact]
    public async Task A_different_comment_creates_a_second_independent_relationship()
    {
        using (var db = NewContext())
            await Store(db).AddAsync(CompanyA, 7, 12, 100, CommTaskLinkOrigin.LinkedExisting, 11);

        CommTaskLinkRow? again;
        using (var db = NewContext())
            again = await Store(db).AddAsync(CompanyA, 7, 12, 200, CommTaskLinkOrigin.LinkedExisting, 11);

        using var fresh = NewContext();
        var rows = await Store(fresh).ForThreadAsync(CompanyA, 12);

        Assert.NotNull(again);
        Assert.Equal(2, rows.Count);                                   // two distinct relationships
        Assert.NotEqual(rows[0].Id, rows[1].Id);
        Assert.Contains(rows, r => r.CommentId == 100);
        Assert.Contains(rows, r => r.CommentId == 200);
    }

    // The unique index is FILTERED to live rows, so an unlink must free the pair again. Without the
    // filter, a task could never be re-attached to a conversation it had once left.
    [Fact]
    public async Task A_soft_unlink_frees_the_pair_so_it_can_be_linked_again()
    {
        long originalId;
        using (var db = NewContext())
            originalId = (await Store(db).AddAsync(
                CompanyA, 7, 12, null, CommTaskLinkOrigin.LinkedExisting, 11))!.Id;

        using (var db = NewContext())
            Assert.True(await Store(db).RemoveAsync(CompanyA, originalId, removedBy: 13));

        using (var db = NewContext())
            Assert.Empty(await Store(db).ForThreadAsync(CompanyA, 12));

        CommTaskLinkRow? relinked;
        using (var db = NewContext())
            relinked = await Store(db).AddAsync(
                CompanyA, 7, 12, null, CommTaskLinkOrigin.LinkedExisting, 11);

        Assert.NotNull(relinked);
        Assert.NotEqual(originalId, relinked!.Id);     // a NEW row, not a resurrection

        // The removed row survives — an unlink is a decision somebody made, and the history keeps it.
        using var fresh = NewContext();
        var all = await fresh.Set<CommTaskLink>().AsNoTracking()
            .Where(l => l.CompanyID == CompanyA && l.ThreadId == 12).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, l => l.RemovedAt != null && l.RemovedBy == 13);
    }

    // ---------------------------------------------------------------------------------------------
    // COMPANY ISOLATION, asymmetric on purpose: a count alone cannot say which tenant's row survived.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Each_company_keeps_its_own_link_for_the_same_task_and_thread_ids()
    {
        using (var db = NewContext())
        {
            await Store(db).AddAsync(CompanyA, 7, 12, 100, CommTaskLinkOrigin.LinkedExisting, 11);
            await Store(db).AddAsync(CompanyB, 7, 12, 200, CommTaskLinkOrigin.LinkedExisting, 21);
        }

        using var fresh = NewContext();

        // Same ids in both companies are DIFFERENT links — which is why CompanyID leads the index.
        var a = Assert.Single(await Store(fresh).ForThreadAsync(CompanyA, 12));
        var b = Assert.Single(await Store(fresh).ForThreadAsync(CompanyB, 12));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(100, a.CommentId);
        Assert.Equal(200, b.CommentId);
    }

    [Fact]
    public async Task Another_companys_link_is_invisible_to_every_read_and_cannot_be_removed()
    {
        long theirs;
        using (var db = NewContext())
            theirs = (await Store(db).AddAsync(
                CompanyB, 7, 12, null, CommTaskLinkOrigin.LinkedExisting, 21))!.Id;

        using var fresh = NewContext();
        var store = Store(fresh);

        Assert.Empty(await store.ForThreadAsync(CompanyA, 12));
        Assert.Empty(await store.ForTaskAsync(CompanyA, 7));
        Assert.Null(await store.GetAsync(CompanyA, theirs));

        // And a cross-tenant unlink is refused rather than silently succeeding.
        Assert.False(await store.RemoveAsync(CompanyA, theirs, removedBy: 11));

        using var check = NewContext();
        Assert.Single(await Store(check).ForThreadAsync(CompanyB, 12));
    }

    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_unresolved_company_writes_nothing_and_reads_nothing()
    {
        using var db = NewContext();
        var store = Store(db);

        Assert.Null(await store.AddAsync(0, 7, 12, null, CommTaskLinkOrigin.LinkedExisting, 11));
        Assert.Empty(await store.ForThreadAsync(0, 12));
        Assert.Empty(await store.ForTaskAsync(0, 7));
        Assert.Null(await store.GetAsync(0, 1));
        Assert.False(await store.RemoveAsync(0, 1, 11));

        Assert.Empty(await db.Set<CommTaskLink>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(7, 0)]
    public async Task A_non_positive_task_or_thread_is_refused(int taskId, long threadId)
    {
        using var db = NewContext();
        Assert.Null(await Store(db).AddAsync(
            CompanyA, taskId, threadId, null, CommTaskLinkOrigin.LinkedExisting, 11));
        Assert.Empty(await db.Set<CommTaskLink>().AsNoTracking().ToListAsync());
    }

    // An Origin outside the frozen vocabulary is refused before it reaches the CHECK constraint, so the
    // service gives a reason rather than letting the driver raise one.
    [Fact]
    public async Task An_unknown_origin_is_refused()
    {
        using var db = NewContext();
        Assert.Null(await Store(db).AddAsync(CompanyA, 7, 12, null, "Invented", 11));
        Assert.Empty(await db.Set<CommTaskLink>().AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // THE ACTIVATION CHECK must fail CLOSED.
    //
    // The danger it guards against is a deployment believing a .sql file in the repository means the
    // table exists in the database it is connected to. Those are different facts, and HM-D44 is what
    // conflating them cost. So the only interesting question about this probe is whether it can ever
    // say "provisioned" when it does not know — and here it is asked on SQLite, where it cannot read
    // SQL Server's system catalogues at all.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_schema_probe_reports_not_provisioned_when_it_cannot_read_the_catalogues()
    {
        using var db = NewContext();
        var probe = new CommTaskLinkSchemaProbe(db, NullLogger<CommTaskLinkSchemaProbe>.Instance);

        var status = await probe.CheckAsync();

        Assert.False(status.Provisioned);
        Assert.NotNull(status.Inconclusive);
        Assert.Contains("not SQL Server", status.Inconclusive);
    }

    // The table genuinely EXISTS in this SQLite database — the test context maps and creates it. The
    // probe must still refuse, because it cannot verify the indexes and constraints that make the
    // schema correct rather than merely present. "A table is there" is not "the schema is right".
    [Fact]
    public async Task A_present_table_is_not_enough_for_the_probe_to_report_provisioned()
    {
        using var db = NewContext();
        await db.Set<CommTaskLink>().AsNoTracking().AnyAsync();   // proves the table is really there

        var status = await new CommTaskLinkSchemaProbe(db, NullLogger<CommTaskLinkSchemaProbe>.Instance)
            .CheckAsync();

        Assert.False(status.Provisioned);
    }

    // The names the probe looks for are the names the DDL creates. Pinned so a rename on either side
    // turns a test red instead of turning the check into one that silently always fails.
    [Fact]
    public void The_probe_looks_for_exactly_the_objects_the_slice_creates()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", "comm_task_links.sql"));

        Assert.Contains($"CREATE TABLE dbo.{CommTaskLinkSchemaProbe.TableName}", script, StringComparison.Ordinal);
        var m2m = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", "comm_task_links_m2m.sql"));
        Assert.Contains($"CREATE UNIQUE INDEX {CommTaskLinkSchemaProbe.ConversationIndexName}", m2m, StringComparison.Ordinal);
        Assert.Contains($"CREATE UNIQUE INDEX {CommTaskLinkSchemaProbe.MessageIndexName}", m2m, StringComparison.Ordinal);
        Assert.Contains($"CREATE INDEX {CommTaskLinkSchemaProbe.ThreadIndexName}", script, StringComparison.Ordinal);
        Assert.Contains($"CREATE INDEX {CommTaskLinkSchemaProbe.TaskIndexName}", script, StringComparison.Ordinal);
        Assert.Contains(CommTaskLinkSchemaProbe.OriginCheckName, script, StringComparison.Ordinal);
        Assert.Contains(CommTaskLinkSchemaProbe.ThreadForeignKeyName, script, StringComparison.Ordinal);

        // And the unique index really is filtered to live rows in the DDL, which is what lets an
        // unlinked pair be linked again.
        int at = m2m.IndexOf($"CREATE UNIQUE INDEX {CommTaskLinkSchemaProbe.MessageIndexName}", StringComparison.Ordinal);
        Assert.Contains("CommentId IS NOT NULL", m2m[at..Math.Min(m2m.Length, at + 400)], StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static CommTaskLink NewRow(int company, int task, long thread, long? comment) => new()
    {
        CompanyID = company,
        TaskId = task,
        ThreadId = thread,
        CommentId = comment,
        Origin = CommTaskLinkOrigin.LinkedExisting,
        CreatedBy = 11,
        CreatedAt = DateTime.UtcNow,
    };

    // ---------------------------------------------------------------------------------------------
    // The test-only mapping. It reproduces the PROPOSED DDL — the same table name, the same filtered
    // two filtered unique indexes UX_CommTaskLinks_LiveConversation and UX_CommTaskLinks_LiveMessage, and the same two
    // directional indexes — so what these tests exercise is the schema being proposed, not a convenient
    // approximation of it. When the slice is authorized, this moves into CommunicationModel.Configure
    // unchanged.
    // ---------------------------------------------------------------------------------------------
    private sealed class LinkTestContext : CrossDbContext
    {
        public LinkTestContext(DbContextOptions<CrossDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<CommTaskLink>(e =>
            {
                e.ToTable("CommTaskLinks");
                e.HasKey(x => x.Id);
                e.Property(x => x.Origin).HasMaxLength(40).IsRequired();
                e.Property(x => x.CreatedAt).IsRequired();

                // The TWO filtered unique indexes comm_task_links_m2m deploys. They partition the
                // table on CommentId IS NULL / IS NOT NULL, so no row satisfies both and a
                // conversation-level link coexists with message-level ones.
                e.HasIndex(x => new { x.CompanyID, x.TaskId, x.ThreadId })
                    .IsUnique()
                    .HasFilter("[RemovedAt] IS NULL AND [CommentId] IS NULL")
                    .HasDatabaseName("UX_CommTaskLinks_LiveConversation");

                e.HasIndex(x => new { x.CompanyID, x.TaskId, x.ThreadId, x.CommentId })
                    .IsUnique()
                    .HasFilter("[RemovedAt] IS NULL AND [CommentId] IS NOT NULL")
                    .HasDatabaseName("UX_CommTaskLinks_LiveMessage");

                e.HasIndex(x => new { x.CompanyID, x.ThreadId })
                    .HasFilter("[RemovedAt] IS NULL")
                    .HasDatabaseName("IX_CommTaskLinks_Thread");

                e.HasIndex(x => new { x.CompanyID, x.TaskId })
                    .HasFilter("[RemovedAt] IS NULL")
                    .HasDatabaseName("IX_CommTaskLinks_Task");
            });
        }
    }
}
