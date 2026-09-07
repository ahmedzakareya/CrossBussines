using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // Construction C1 — CR-01: BOQ STABLE IDENTITY
    //
    // The defect these tests exist to prevent, in the words of the code that caused it:
    //   BL/BoqService.cs ReplaceAllAsync removed EVERY BoqItem of the project and re-inserted the
    //   posted rows with NEW identities, while BL/ProgressBillingService.cs keys previously-billed
    //   value by BoqItemId. One BOQ re-save therefore made work that was already billed look
    //   unbilled, and the next certificate billed it a second time.
    //
    // Every test here runs against the REAL CrossDbContext model on SQLite (PlatformTestHost), and
    // the re-billing test proves the money outcome, not just the row shape.
    // ==========================================================================================
    public class ConstructionC1BoqIdentityTests
    {
        private const int Company = 1;

        private static ConstructionTestFixture NewFixture() => new(Company);

        // ------------------------------------------------------------------------------------------
        // 1. A replace operation preserves the identity of every unchanged line.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Replace_preserves_unchanged_line_ids()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            var first = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1",   Description = "Excavation", Unit = "m3", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2",   Description = "Concrete",   Unit = "m3", Quantity = 50m,  UnitPrice = 60m },
                new() { Code = "3",   Description = "Steel",      Unit = "ton", Quantity = 10m, UnitPrice = 900m },
            }, actorEmployeeId: 7, reason: "original BOQ");
            Assert.True(first.ok, first.error);

            var idsBefore = await f.ActiveLineIdsByCodeAsync(project.ID);
            Assert.Equal(3, idsBefore.Count);

            // Re-save the SAME three lines, one of them with a changed description only.
            var second = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = idsBefore["1"], Code = "1", Description = "Excavation", Unit = "m3", Quantity = 100m, UnitPrice = 5m },
                new() { Id = idsBefore["2"], Code = "2", Description = "Concrete C30", Unit = "m3", Quantity = 50m, UnitPrice = 60m },
                new() { Id = idsBefore["3"], Code = "3", Description = "Steel", Unit = "ton", Quantity = 10m, UnitPrice = 900m },
            }, actorEmployeeId: 7, reason: "description correction");
            Assert.True(second.ok, second.error);

            // Proven from a NEW context: identity is what the database holds, not what the writer remembers.
            var idsAfter = await f.ActiveLineIdsByCodeAsync(project.ID);
            Assert.Equal(idsBefore["1"], idsAfter["1"]);
            Assert.Equal(idsBefore["2"], idsAfter["2"]);
            Assert.Equal(idsBefore["3"], idsAfter["3"]);
        }

        // ------------------------------------------------------------------------------------------
        // 2. Adding a line creates exactly ONE new identity and disturbs no existing one.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Adding_a_line_creates_exactly_one_new_id()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2", Description = "Concrete",   Quantity = 50m,  UnitPrice = 60m },
            }, 7, "original BOQ");

            var before = await f.ActiveLineIdsByCodeAsync(project.ID);

            var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = before["1"], Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Id = before["2"], Code = "2", Description = "Concrete",   Quantity = 50m,  UnitPrice = 60m },
                new() {                    Code = "3", Description = "Steel",      Quantity = 10m,  UnitPrice = 900m },
            }, 7, "added steel");
            Assert.True(result.ok, result.error);

            var after = await f.ActiveLineIdsByCodeAsync(project.ID);
            Assert.Equal(3, after.Count);
            Assert.Equal(before["1"], after["1"]);
            Assert.Equal(before["2"], after["2"]);
            Assert.DoesNotContain(after["3"], new[] { before["1"], before["2"] });
            Assert.Equal(1, result.added);
            Assert.Equal(0, result.retired);
        }

        // ------------------------------------------------------------------------------------------
        // 3. Removing an UNREFERENCED draft line is safe — and is still a retirement, not an erasure.
        //
        //    The brief allows a draft line to be removed "safely". This implementation retires rather
        //    than deletes even then, deliberately: a line that was typed, priced and then removed is
        //    commercial history, and "safely" is satisfied by it leaving the working BOQ.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Removing_an_unreferenced_line_retires_it_and_keeps_the_row()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2", Description = "Typo line",  Quantity = 1m,   UnitPrice = 1m },
            }, 7, "original BOQ");

            var before = await f.ActiveLineIdsByCodeAsync(project.ID);
            int removedId = before["2"];

            var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = before["1"], Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "removed a mistyped line");
            Assert.True(result.ok, result.error);
            Assert.Equal(1, result.retired);

            await using var verify = f.NewContext();
            // The row is STILL THERE.
            Assert.True(await verify.BoqItems.AnyAsync(x => x.ID == removedId));
            // …and it is out of the working BOQ.
            var state = await verify.BoqLineStates.AsNoTracking().SingleAsync(x => x.BoqItemId == removedId);
            Assert.Equal(BoqLineStatus.Retired, state.Status);
            Assert.NotNull(state.RetiredAt);
            Assert.Equal("removed a mistyped line", state.RetiredReason);
            // The active BOQ no longer contains it.
            var active = await f.ActiveLineIdsByCodeAsync(project.ID);
            Assert.False(active.ContainsKey("2"));
        }

        // ------------------------------------------------------------------------------------------
        // 4. Removing a CERTIFIED line is REFUSED. Nothing is retired, nothing is deleted.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Removing_a_certified_line_is_refused()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2", Description = "Concrete",   Quantity = 50m,  UnitPrice = 60m },
            }, 7, "original BOQ");

            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);
            await f.SeedPostedClientCertificateAsync(project.ID, ids["2"], periodValue: 1200m);

            var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["1"], Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "trying to drop a certified line");

            Assert.False(result.ok);
            Assert.Contains("2", result.error!);   // the refusal names the offending line

            await using var verify = f.NewContext();
            var state = await verify.BoqLineStates.AsNoTracking().SingleOrDefaultAsync(x => x.BoqItemId == ids["2"]);
            Assert.Equal(BoqLineStatus.Active, state!.Status);      // not retired
            Assert.True(await verify.BoqItems.AnyAsync(x => x.ID == ids["2"]));   // not deleted
        }

        // ------------------------------------------------------------------------------------------
        // 5. THE MONEY TEST — previously certified quantity is still recognised after a BOQ edit, so
        //    work already billed can never become billable again.
        //
        //    This is the test the delete-and-reinsert behaviour fails (mutation C-01).
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Previously_billed_work_cannot_become_billable_again_after_a_boq_edit()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2", Description = "Concrete",   Quantity = 50m,  UnitPrice = 60m },
            }, 7, "original BOQ");

            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            // 1200 of line "2" has been certified and POSTED.
            await f.SeedPostedClientCertificateAsync(project.ID, ids["2"], periodValue: 1200m);
            decimal billedBefore = await f.PreviouslyBilledForAsync(project.ID, ids["2"]);
            Assert.Equal(1200m, billedBefore);

            // The BOQ is edited: a line is added, one description changed, order shuffled.
            var edit = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["2"], Code = "2", Description = "Concrete C30", Quantity = 50m, UnitPrice = 60m },
                new() { Id = ids["1"], Code = "1", Description = "Excavation",   Quantity = 100m, UnitPrice = 5m },
                new() {                Code = "4", Description = "Backfill",     Quantity = 20m,  UnitPrice = 8m },
            }, 7, "BOQ edit after certification");
            Assert.True(edit.ok, edit.error);

            // The line that IS the BOQ today must still be the line the certificate billed. This is the
            // assertion that matters: the next certificate looks up previously-billed value by the id of
            // the line it finds in the CURRENT BOQ. If that id has moved, the money is invisible to it.
            var idsAfter = await f.ActiveLineIdsByCodeAsync(project.ID);
            Assert.Equal(ids["2"], idsAfter["2"]);

            decimal billedForTheCurrentLine = await f.PreviouslyBilledForAsync(project.ID, idsAfter["2"]);
            Assert.Equal(1200m, billedForTheCurrentLine);
            Assert.Equal(billedBefore, billedForTheCurrentLine);

            // …and the certificate line still resolves to a BOQ row that exists.
            await using var verify = f.NewContext();
            var certLine = await verify.ProgressBillingLines.AsNoTracking()
                .SingleAsync(l => l.BoqItemId == ids["2"]);
            Assert.Equal(1200m, certLine.PeriodValue);
            Assert.True(await verify.BoqItems.AnyAsync(b => b.ID == certLine.BoqItemId));
        }

        // ------------------------------------------------------------------------------------------
        // 6. Reordering lines changes sort order and NOTHING else.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Reorder_does_not_change_identity()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "A", Quantity = 1m, UnitPrice = 10m },
                new() { Code = "2", Description = "B", Quantity = 1m, UnitPrice = 20m },
                new() { Code = "3", Description = "C", Quantity = 1m, UnitPrice = 30m },
            }, 7, "original BOQ");

            var before = await f.ActiveLineIdsByCodeAsync(project.ID);

            var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = before["3"], Code = "3", Description = "C", Quantity = 1m, UnitPrice = 30m },
                new() { Id = before["1"], Code = "1", Description = "A", Quantity = 1m, UnitPrice = 10m },
                new() { Id = before["2"], Code = "2", Description = "B", Quantity = 1m, UnitPrice = 20m },
            }, 7, "reordered");
            Assert.True(result.ok, result.error);
            Assert.Equal(0, result.added);
            Assert.Equal(0, result.retired);

            var after = await f.ActiveLineIdsByCodeAsync(project.ID);
            Assert.Equal(before["1"], after["1"]);
            Assert.Equal(before["2"], after["2"]);
            Assert.Equal(before["3"], after["3"]);

            await using var verify = f.NewContext();
            var sorted = await verify.BoqItems.AsNoTracking()
                .Where(b => b.ProjectId == project.ID)
                .OrderBy(b => b.SortOrder).Select(b => b.Code).ToListAsync();
            Assert.Equal(new[] { "3", "1", "2" }, sorted);
        }

        // ------------------------------------------------------------------------------------------
        // 7. Duplicate line codes are rejected within the same project/contract.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Duplicate_line_codes_are_rejected()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "1", Description = "Duplicate",  Quantity = 1m,   UnitPrice = 1m },
            }, 7, "duplicate codes");

            Assert.False(result.ok);
            Assert.Contains("1", result.error!);

            await using var verify = f.NewContext();
            Assert.Equal(0, await verify.BoqItems.CountAsync(b => b.ProjectId == project.ID));
        }

        // ------------------------------------------------------------------------------------------
        // 8. Company / project / contract isolation.
        //    A line id belonging to another company's project may not be updated through this project,
        //    and the refusal does not distinguish "not yours" from "does not exist".
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_line_from_another_company_cannot_be_updated_through_this_project()
        {
            using var f = NewFixture();
            var mine = await f.SeedProjectAsync(companyId: 1, code: "P-1");
            var theirs = await f.SeedProjectAsync(companyId: 2, code: "P-2");

            await f.Boq.SaveLinesAsync(2, theirs.ID, new List<BoqLineInput>
            {
                new() { Code = "X", Description = "Their line", Quantity = 1m, UnitPrice = 100m },
            }, 7, "their BOQ");

            await using var read = f.NewContext();
            int foreignLineId = await read.BoqItems.Where(b => b.ProjectId == theirs.ID).Select(b => b.ID).SingleAsync();

            var result = await f.Boq.SaveLinesAsync(1, mine.ID, new List<BoqLineInput>
            {
                new() { Id = foreignLineId, Code = "X", Description = "Hijacked", Quantity = 999m, UnitPrice = 999m },
            }, 7, "attempted cross-company update");

            Assert.False(result.ok);

            await using var verify = f.NewContext();
            var untouched = await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == foreignLineId);
            Assert.Equal(1m, untouched.Quantity);
            Assert.Equal(100m, untouched.UnitPrice);
            Assert.Equal(theirs.ID, untouched.ProjectId);
        }

        // ------------------------------------------------------------------------------------------
        // 9. A contractual quantity or rate change is refused on the plain save path — it requires a
        //    revision or an approved variation (CR-03's rule, enforced at CR-01's entry point).
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Changing_a_contractual_quantity_on_a_certified_line_requires_a_revision()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");

            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);
            await f.SeedPostedClientCertificateAsync(project.ID, ids["1"], periodValue: 250m);

            var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["1"], Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 9m },
            }, 7, "sneaky rate change");

            Assert.False(result.ok);
            Assert.Contains("revision", result.error!, StringComparison.OrdinalIgnoreCase);

            await using var verify = f.NewContext();
            var line = await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == ids["1"]);
            Assert.Equal(5m, line.UnitPrice);   // unchanged
        }

        // ------------------------------------------------------------------------------------------
        // 10. Every line change writes line-level audit with an actor and a reason.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Line_changes_are_audited_with_actor_reason_and_one_correlation_id()
        {
            using var f = NewFixture();
            var project = await f.SeedProjectAsync();

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2", Description = "Concrete",   Quantity = 50m,  UnitPrice = 60m },
            }, actorEmployeeId: 42, reason: "original BOQ");

            await using var verify = f.NewContext();
            var audit = await verify.ConstructionAuditEntries.AsNoTracking()
                .Where(a => a.EntityType == ConstructionAuditEntityTypes.BoqItem)
                .ToListAsync();

            Assert.NotEmpty(audit);
            Assert.All(audit, a =>
            {
                Assert.Equal(Company, a.CompanyID);
                Assert.Equal(project.ID, a.ProjectId);
                Assert.Equal(42, a.ActorEmployeeId);
                Assert.False(string.IsNullOrWhiteSpace(a.Reason));
                Assert.NotEqual(Guid.Empty, a.CorrelationId);
                Assert.NotEqual(default, a.OccurredAt);
            });
            // One save = one correlation id, however many rows it produced.
            Assert.Single(audit.Select(a => a.CorrelationId).Distinct());
        }
    }
}
