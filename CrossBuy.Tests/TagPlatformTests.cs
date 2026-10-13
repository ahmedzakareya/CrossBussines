using CrossBuy.BL;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // TASK MANAGEMENT WAVE 2 — the PLATFORM TAG foundation.
    //
    // These tests deliberately exercise TagService as a PLATFORM service, not a Task feature: the
    // only thing that makes a tag "a task tag" here is the EntityType on its assignment. That is the
    // property CRM depends on, so it is the property under test.
    //
    // COMPANIES ARE 41 AND 77, never 1 — the value every historical default-company bug produced,
    // and the only way to tell "used the caller's company" from "fell back to the default".
    //
    // The host is PlatformTestHost, whose SQLite model carries the same Tags/TagAssignments mapping,
    // the same global company filters and the same CompanyWriteGuardInterceptor as production.
    // =============================================================================================
    public class TagPlatformTests
    {
        private const int CoA = 41;
        private const int CoB = 77;

        private static TagService Service(PlatformTestHost.RequestScope req) => new(req.Db);

        private static async Task<int> SeedTaskAsync(PlatformTestHost host, int companyId, string title = "مهمة")
        {
            var t = new TaskItem
            {
                CompanyId = companyId, Title = title, AssigneeEmployeeId = 5,
                CreatedByEmployeeId = 5, Status = "New", Priority = "Normal", CreatedAt = DateTime.UtcNow,
            };
            host.Seed.TaskItems.Add(t);
            await host.Seed.SaveChangesAsync();
            return t.ID;
        }

        // ---- T1 / T2 / T3: definition, normalization, per-company uniqueness ----------------------

        [Fact]
        public async Task T1_a_tag_is_created_in_the_callers_own_company()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);

            var (ok, err, tag) = await Service(req).CreateTagAsync(CoA, "Urgent", actorEmployeeId: 5);

            Assert.True(ok, err);
            Assert.Equal(CoA, tag!.CompanyID);
            Assert.Equal("Urgent", tag.Name);            // what the user typed is what the user sees
            Assert.Equal("URGENT", tag.NormalizedName);  // the comparison key
            Assert.True(tag.IsActive);
        }

        [Theory]
        [InlineData(" urgent ")]
        [InlineData("URGENT")]
        [InlineData("Urgent")]
        [InlineData("ur    gent")]   // collapsed inner whitespace, so this is NOT a collision
        public async Task T2_a_name_that_normalizes_the_same_is_refused_inside_one_company(string second)
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            await svc.CreateTagAsync(CoA, "Urgent", 5);

            var (ok, err, _) = await svc.CreateTagAsync(CoA, second, 5);

            bool collides = TagService.Normalize(second) == "URGENT";
            if (collides)
            {
                Assert.False(ok);
                Assert.False(string.IsNullOrWhiteSpace(err));
                Assert.Single(await host.AllCompanies().Tags.AsNoTracking().ToListAsync());
            }
            else
            {
                // "ur    gent" collapses to "UR GENT", a genuinely different name.
                Assert.True(ok, err);
                Assert.Equal(2, (await host.AllCompanies().Tags.AsNoTracking().ToListAsync()).Count);
            }
        }

        [Fact]
        public async Task T3_the_same_name_is_allowed_in_a_different_company()
        {
            using var hostA = new PlatformTestHost(CoA);
            Assert.True((await Service(hostA.Request(CoA)).CreateTagAsync(CoA, "Urgent", 5)).ok);

            using var hostB = new PlatformTestHost(CoB);
            var (ok, err, tag) = await Service(hostB.Request(CoB)).CreateTagAsync(CoB, "Urgent", 5);

            // Uniqueness is PER COMPANY. Two tenants each having "Urgent" is normal, not a clash.
            Assert.True(ok, err);
            Assert.Equal(CoB, tag!.CompanyID);
        }

        [Fact]
        public async Task T4_a_company_cannot_see_another_companys_tags()
        {
            using var host = new PlatformTestHost(CoA);
            await Service(host.Request(CoA)).CreateTagAsync(CoA, "أ-عاجل", 5);
            await Service(host.Request(CoB)).CreateTagAsync(CoB, "ب-عاجل", 5);

            var seenByA = await Service(host.Request(CoA)).GetTagsAsync(CoA);
            var seenByB = await Service(host.Request(CoB)).GetTagsAsync(CoB);

            Assert.Equal("أ-عاجل", Assert.Single(seenByA).Name);
            Assert.Equal("ب-عاجل", Assert.Single(seenByB).Name);
        }

        [Fact]
        public async Task Renaming_to_a_colliding_name_is_refused_but_a_case_only_fix_is_allowed()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var a = (await svc.CreateTagAsync(CoA, "Urgent", 5)).tag!;
            var b = (await svc.CreateTagAsync(CoA, "Later", 5)).tag!;

            Assert.False((await svc.RenameTagAsync(CoA, b.ID, " URGENT ", 5)).ok);

            // Fixing the DISPLAY form keeps the same normalized key and must be allowed.
            Assert.True((await svc.RenameTagAsync(CoA, a.ID, "URGENT", 5)).ok);
            var after = await host.AllCompanies().Tags.AsNoTracking().FirstAsync(t => t.ID == a.ID);
            Assert.Equal("URGENT", after.Name);
            Assert.Equal("URGENT", after.NormalizedName);
        }

        // ---- T5 / T6 / T7 / T8: assignment --------------------------------------------------------

        [Fact]
        public async Task T5_a_tag_is_assigned_to_a_task_and_reads_back()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var tag = (await svc.CreateTagAsync(CoA, "عاجل", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);

            var (ok, err) = await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, taskId, 5);

            Assert.True(ok, err);
            var read = Assert.Single(await svc.GetEntityTagsAsync(CoA, EntityRegistry.Task, taskId));
            Assert.Equal(tag.ID, read.ID);
        }

        [Fact]
        public async Task T6_assigning_the_same_tag_twice_is_idempotent_not_a_duplicate()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var tag = (await svc.CreateTagAsync(CoA, "عاجل", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);

            Assert.True((await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, taskId, 5)).ok);
            Assert.True((await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, taskId, 5)).ok);

            // The desired state already held, so the second call SUCCEEDS and writes nothing. A user
            // clicking twice has not made an error.
            Assert.Single(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task T7_removing_an_assignment_leaves_the_definition_alone()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var tag = (await svc.CreateTagAsync(CoA, "عاجل", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);
            await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, taskId, 5);

            Assert.True((await svc.RemoveAsync(CoA, tag.ID, EntityRegistry.Task, taskId)).ok);
            Assert.True((await svc.RemoveAsync(CoA, tag.ID, EntityRegistry.Task, taskId)).ok);   // idempotent

            Assert.Empty(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());
            Assert.Single(await host.AllCompanies().Tags.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task T8_a_tag_cannot_be_attached_to_another_companys_task()
        {
            using var host = new PlatformTestHost(CoA);
            var tagA = (await Service(host.Request(CoA)).CreateTagAsync(CoA, "أ", 5)).tag!;
            int taskB = await SeedTaskAsync(host, CoB, "مهمة الشركة ب");

            // THE THREE-WAY CHECK. The assignment row would claim company 41 quite happily while
            // pointing at company 77's task, so neither the global filter nor the write guard can
            // catch this - only reading the TARGET's company can.
            var (ok, err) = await Service(host.Request(CoA)).AssignAsync(CoA, tagA.ID, EntityRegistry.Task, taskB, 5);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(err));
            Assert.Empty(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task A_foreign_companys_tag_cannot_be_attached_to_my_own_task()
        {
            using var host = new PlatformTestHost(CoA);
            var tagB = (await Service(host.Request(CoB)).CreateTagAsync(CoB, "ب", 5)).tag!;
            int taskA = await SeedTaskAsync(host, CoA);

            // The other direction of the same boundary.
            var (ok, _) = await Service(host.Request(CoA)).AssignAsync(CoA, tagB.ID, EntityRegistry.Task, taskA, 5);

            Assert.False(ok);
            Assert.Empty(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task An_unsupported_entity_type_is_refused_rather_than_accepted_as_free_text()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var tag = (await svc.CreateTagAsync(CoA, "عاجل", 5)).tag!;

            // An entity kind with no company resolver is one whose tenancy nothing can verify, so it
            // is refused. CRM's codes are added beside a resolver each; they are not accepted blind.
            var (ok, err) = await svc.AssignAsync(CoA, tag.ID, "SalesInvoice", 999, 5);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(err));
            Assert.Empty(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task An_archived_tag_cannot_be_newly_assigned()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var tag = (await svc.CreateTagAsync(CoA, "قديم", 5)).tag!;
            int used = await SeedTaskAsync(host, CoA);
            await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, used, 5);
            await svc.RetireTagAsync(CoA, tag.ID, 5);                       // in use -> archived

            int other = await SeedTaskAsync(host, CoA, "مهمة أخرى");
            Assert.False((await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, other, 5)).ok);
        }

        // ---- T13: retire semantics ----------------------------------------------------------------

        [Fact]
        public async Task T13_an_unused_tag_is_deleted_and_a_used_one_is_archived()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);

            var unused = (await svc.CreateTagAsync(CoA, "مطبعي", 5)).tag!;
            var used = (await svc.CreateTagAsync(CoA, "مستخدم", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);
            await svc.AssignAsync(CoA, used.ID, EntityRegistry.Task, taskId, 5);

            // A typo may go outright.
            var (ok1, _, archived1) = await svc.RetireTagAsync(CoA, unused.ID, 5);
            Assert.True(ok1);
            Assert.False(archived1);
            Assert.Null(await host.AllCompanies().Tags.AsNoTracking().FirstOrDefaultAsync(t => t.ID == unused.ID));

            // A USED tag is history: archived, and its assignment survives so "why was this tagged?"
            // stays answerable.
            var (ok2, _, archived2) = await svc.RetireTagAsync(CoA, used.ID, 5);
            Assert.True(ok2);
            Assert.True(archived2);
            var still = await host.AllCompanies().Tags.AsNoTracking().FirstAsync(t => t.ID == used.ID);
            Assert.False(still.IsActive);
            Assert.Single(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());

            // And it is gone from the picker without being gone from history.
            Assert.DoesNotContain(await svc.GetTagsAsync(CoA), t => t.ID == used.ID);
            Assert.Contains(await svc.GetTagsAsync(CoA, includeArchived: true), t => t.ID == used.ID);
        }

        [Fact]
        public async Task Recreating_an_archived_name_revives_it_instead_of_dead_ending_the_user()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var tag = (await svc.CreateTagAsync(CoA, "عاجل", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);
            await svc.AssignAsync(CoA, tag.ID, EntityRegistry.Task, taskId, 5);
            await svc.RetireTagAsync(CoA, tag.ID, 5);

            // Without this the user is told "already exists" about something they cannot see, and the
            // unique index makes a twin impossible anyway.
            var (ok, err, revived) = await svc.CreateTagAsync(CoA, "عاجل", 5);

            Assert.True(ok, err);
            Assert.Equal(tag.ID, revived!.ID);
            Assert.True(revived.IsActive);
            Assert.Single(await host.AllCompanies().Tags.AsNoTracking().ToListAsync());
        }

        // ---- T14: the whole-set replace a Create/Edit form posts ----------------------------------

        [Fact]
        public async Task T14_setting_an_entitys_tag_set_adds_removes_and_leaves_the_rest()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var a = (await svc.CreateTagAsync(CoA, "أ", 5)).tag!;
            var b = (await svc.CreateTagAsync(CoA, "ب", 5)).tag!;
            var c = (await svc.CreateTagAsync(CoA, "ج", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);

            Assert.True((await svc.SetEntityTagsAsync(CoA, EntityRegistry.Task, taskId, new[] { a.ID, b.ID }, 5)).ok);
            Assert.Equal(2, (await svc.GetEntityTagsAsync(CoA, EntityRegistry.Task, taskId)).Count);

            // Replace the set: b stays, a goes, c arrives.
            Assert.True((await svc.SetEntityTagsAsync(CoA, EntityRegistry.Task, taskId, new[] { b.ID, c.ID }, 5)).ok);
            var now = (await svc.GetEntityTagsAsync(CoA, EntityRegistry.Task, taskId)).Select(t => t.ID).ToHashSet();
            Assert.Equal(new[] { b.ID, c.ID }.ToHashSet(), now);

            // An EMPTY set legitimately means "remove them all".
            Assert.True((await svc.SetEntityTagsAsync(CoA, EntityRegistry.Task, taskId, Array.Empty<int>(), 5)).ok);
            Assert.Empty(await svc.GetEntityTagsAsync(CoA, EntityRegistry.Task, taskId));
        }

        [Fact]
        public async Task Setting_a_set_containing_a_foreign_tag_writes_nothing_at_all()
        {
            using var host = new PlatformTestHost(CoA);
            var mine = (await Service(host.Request(CoA)).CreateTagAsync(CoA, "أ", 5)).tag!;
            var theirs = (await Service(host.Request(CoB)).CreateTagAsync(CoB, "ب", 5)).tag!;
            int taskId = await SeedTaskAsync(host, CoA);

            var (ok, err) = await Service(host.Request(CoA))
                .SetEntityTagsAsync(CoA, EntityRegistry.Task, taskId, new[] { mine.ID, theirs.ID }, 5);

            // ALL OR NOTHING. A partially-applied set would leave the task tagged with whichever ids
            // happened to be legitimate, which is worse than refusing outright.
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(err));
            Assert.Empty(await host.AllCompanies().TagAssignments.AsNoTracking().ToListAsync());
        }

        // ---- T12: the batch read the list screens use ---------------------------------------------

        [Fact]
        public async Task T12_many_entities_tags_come_back_in_one_company_safe_query()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var svc = Service(req);
            var a = (await svc.CreateTagAsync(CoA, "أ", 5)).tag!;
            var b = (await svc.CreateTagAsync(CoA, "ب", 5)).tag!;
            int t1 = await SeedTaskAsync(host, CoA, "١");
            int t2 = await SeedTaskAsync(host, CoA, "٢");
            int t3 = await SeedTaskAsync(host, CoA, "٣");
            await svc.SetEntityTagsAsync(CoA, EntityRegistry.Task, t1, new[] { a.ID, b.ID }, 5);
            await svc.SetEntityTagsAsync(CoA, EntityRegistry.Task, t2, new[] { a.ID }, 5);

            var map = await svc.GetTagsForEntitiesAsync(CoA, EntityRegistry.Task, new[] { t1, t2, t3 });

            Assert.Equal(2, map[t1].Count);
            Assert.Single(map[t2]);
            Assert.False(map.ContainsKey(t3));   // untagged entities are simply absent

            // A foreign company asking for the SAME ids gets nothing.
            var foreign = await Service(host.Request(CoB)).GetTagsForEntitiesAsync(CoB, EntityRegistry.Task, new[] { t1, t2 });
            Assert.Empty(foreign);
        }

        [Fact]
        public async Task An_empty_id_list_costs_no_query_and_returns_empty()
        {
            using var host = new PlatformTestHost(CoA);
            var svc = Service(host.Request(CoA));
            Assert.Empty(await svc.GetTagsForEntitiesAsync(CoA, EntityRegistry.Task, Array.Empty<int>()));
        }

        // ---- validation ---------------------------------------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_blank_tag_name_is_refused(string name)
        {
            using var host = new PlatformTestHost(CoA);
            var (ok, err, _) = await Service(host.Request(CoA)).CreateTagAsync(CoA, name, 5);
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        [Fact]
        public async Task A_name_longer_than_the_column_is_refused_rather_than_truncated()
        {
            using var host = new PlatformTestHost(CoA);
            var (ok, _, _) = await Service(host.Request(CoA))
                .CreateTagAsync(CoA, new string('x', TagService.MaxNameLength + 1), 5);
            Assert.False(ok);
        }

        [Fact]
        public void Normalization_is_culture_invariant_and_collapses_whitespace()
        {
            // ToUpperInvariant, never ToUpper: a culture-sensitive fold makes collision depend on the
            // server's locale, and the Turkish dotted-i is the classic way that becomes a bug nobody
            // can reproduce.
            Assert.Equal("URGENT", TagService.Normalize(" urgent "));
            Assert.Equal("URGENT", TagService.Normalize("URGENT"));
            Assert.Equal("VERY URGENT", TagService.Normalize("very    urgent"));
            Assert.Equal("", TagService.Normalize(null));
            // Arabic is unaffected by case folding, and still gets its whitespace fixed.
            Assert.Equal("عاجل", TagService.Normalize("  عاجل  "));
        }
    }

    // =============================================================================================
    // TASK WAVE 2 — the tag FILTER on the authoritative task query.
    //
    // The invariant these tests exist for: TAGS DO NOT GRANT ACCESS. The filter is applied as a
    // narrowing predicate on the already-scoped query, so a user filtering by "Urgent" sees only
    // their own urgent work — never somebody else's, and never another company's.
    // =============================================================================================
}
