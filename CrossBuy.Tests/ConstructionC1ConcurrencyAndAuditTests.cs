using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // Construction C1 — CONCURRENCY AND LINE-LEVEL AUDIT
    //
    // Before C1 there were ZERO concurrency tokens and ZERO CHECK constraints across the whole
    // construction schema (deploy/sql/boq.sql, deploy/sql/projects_p*.sql), and the only history was
    // CreatedBy/CreatedAt/PostedBy/PostedAt. Two quantity surveyors editing the same commercial value
    // meant the second save won silently.
    //
    // The token under test is an application-rotated varbinary(16) configured with IsConcurrencyToken(),
    // NOT SQL Server `rowversion` — see the note in ConstructionCommercial.cs. That choice is what makes
    // these real tests on the SQLite provider the suite runs on, rather than tests skipped unless a
    // SQL Server instance happens to be configured.
    // ==========================================================================================
    public class ConstructionC1ConcurrencyAndAuditTests
    {
        private const int Company = 1;

        // ------------------------------------------------------------------------------------------
        // 1. A stale token on a BOQ line save is REJECTED — the first writer's work is not overwritten.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_stale_boq_line_token_is_rejected_and_the_first_write_survives()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            // Both users read the same state.
            byte[] tokenUserA = await f.LineTokenAsync(ids["1"]);
            byte[] tokenUserB = await f.LineTokenAsync(ids["1"]);
            Assert.Equal(tokenUserA, tokenUserB);

            // User A saves first.
            var a = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["1"], Code = "1", Description = "Excavation to rock", Quantity = 100m, UnitPrice = 5m,
                        ExpectedToken = tokenUserA },
            }, 7, "user A description change");
            Assert.True(a.ok, a.error);

            // User B saves with the token they read BEFORE A's write.
            var b = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["1"], Code = "1", Description = "Excavation in soil", Quantity = 100m, UnitPrice = 5m,
                        ExpectedToken = tokenUserB },
            }, 8, "user B description change");

            Assert.False(b.ok);
            Assert.Contains("another user", b.error!, StringComparison.OrdinalIgnoreCase);

            // A's write survived; B's was refused, not merged and not silently dropped.
            await using var verify = f.NewContext();
            var line = await verify.BoqItems.AsNoTracking().SingleAsync(x => x.ID == ids["1"]);
            Assert.Equal("Excavation to rock", line.Description);
        }

        // ------------------------------------------------------------------------------------------
        // 2. The token ROTATES on every write, so a token can never be replayed.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task The_token_rotates_on_every_write()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            byte[] t1 = await f.LineTokenAsync(ids["1"]);
            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["1"], Code = "1", Description = "Excavation A", Quantity = 100m, UnitPrice = 5m, ExpectedToken = t1 },
            }, 7, "first change");
            byte[] t2 = await f.LineTokenAsync(ids["1"]);

            Assert.NotEqual(t1, t2);
            Assert.NotEmpty(t2);

            // Replaying t1 fails.
            var replay = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Id = ids["1"], Code = "1", Description = "Replayed", Quantity = 100m, UnitPrice = 5m, ExpectedToken = t1 },
            }, 7, "replay attempt");
            Assert.False(replay.ok);
        }

        // ------------------------------------------------------------------------------------------
        // 3. A stale token on a subcontract SCOPE save is rejected — the cap cannot be moved by a
        //    writer working from a stale read.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_stale_scope_token_is_rejected()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var created = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            byte[] stale = await f.ScopeTokenAsync(created.scopeId);

            var first = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork revised",
                "m2", 1000m, 55m, 9, "rate agreed", scopeId: created.scopeId, expectedToken: stale);
            Assert.True(first.ok, first.error);

            var second = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork other",
                "m2", 1000m, 60m, 9, "competing edit", scopeId: created.scopeId, expectedToken: stale);
            Assert.False(second.ok);
            Assert.Contains("another user", second.error!, StringComparison.OrdinalIgnoreCase);

            await using var verify = f.NewContext();
            var scope = await verify.SubcontractScopes.AsNoTracking().SingleAsync(s => s.ID == created.scopeId);
            Assert.Equal(55m, scope.SubRate);
        }

        // ------------------------------------------------------------------------------------------
        // 4. A stale token on a revision APPROVAL is rejected — two approvers cannot both apply it.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_stale_revision_token_is_rejected_on_approval()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var contract = await f.SeedContractAsync(project.ID);
            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            var open = await f.Revisions.OpenAsync(Company, project.ID, contract.ID,
                CommercialRevisionSource.Correction, null, DateTime.Today, 3);
            byte[] stale = await f.RevisionTokenAsync(open.revisionId);

            // A concurrent staging changes the revision, rotating its token.
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 120m, 6m, "staged by someone else");

            var approve = await f.Revisions.ApproveAsync(Company, open.revisionId, 3,
                "approving from a stale read", expectedToken: stale);
            Assert.False(approve.ok);
            Assert.Contains("another user", approve.error!, StringComparison.OrdinalIgnoreCase);

            await using var verify = f.NewContext();
            var rev = await verify.CommercialRevisions.AsNoTracking().SingleAsync(r => r.ID == open.revisionId);
            Assert.Equal(CommercialRevisionStatus.Draft, rev.Status);   // not approved
            var line = await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == ids["1"]);
            Assert.Equal(5m, line.UnitPrice);                            // nothing applied
        }

        // ------------------------------------------------------------------------------------------
        // 5. Two contexts, one row: the loser is told, and the conflict is reported as a conflict
        //    rather than as a generic failure.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Two_separate_contexts_writing_the_same_scope_produce_a_reported_conflict()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var created = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");
            byte[] shared = await f.ScopeTokenAsync(created.scopeId);

            var (dbA, _, _, scopesA) = f.SecondUser();
            var (dbB, _, _, scopesB) = f.SecondUser();
            await using (dbA)
            await using (dbB)
            {
                var a = await scopesA.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "By A",
                    "m2", 1000m, 51m, 9, "A wins the race", scopeId: created.scopeId, expectedToken: shared);
                var b = await scopesB.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "By B",
                    "m2", 1000m, 52m, 9, "B is late", scopeId: created.scopeId, expectedToken: shared);

                Assert.True(a.ok, a.error);
                Assert.False(b.ok);
                Assert.Contains("another user", b.error!, StringComparison.OrdinalIgnoreCase);
            }

            await using var verify = f.NewContext();
            Assert.Equal(51m, (await verify.SubcontractScopes.AsNoTracking().SingleAsync(s => s.ID == created.scopeId)).SubRate);
        }

        // ------------------------------------------------------------------------------------------
        // 6. AUDIT — a commercial value change with no reason is refused. Reason is not decoration.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_commercial_change_without_a_reason_is_refused()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();

            foreach (var badReason in new[] { null, "", "  " })
            {
                var result = await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
                {
                    new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                }, actorEmployeeId: 7, reason: badReason!);

                Assert.False(result.ok);
                Assert.Contains("reason", result.error!, StringComparison.OrdinalIgnoreCase);
            }

            await using var verify = f.NewContext();
            Assert.Equal(0, await verify.BoqItems.CountAsync());
            Assert.Equal(0, await verify.ConstructionAuditEntries.CountAsync());
        }

        // ------------------------------------------------------------------------------------------
        // 7. AUDIT — the audit service is APPEND-ONLY by construction: it exposes no update and no
        //    delete, and a recorded entry cannot be modified through it.
        //
        //    Asserted structurally (reflection over the interface) because "append-only" is a property
        //    of the API surface, not of any one call: a future update method would make every other
        //    audit assertion in this suite worthless, and this test is what would catch it.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void The_audit_service_exposes_no_way_to_change_or_delete_history()
        {
            var methods = typeof(IConstructionAuditService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .ToList();

            Assert.NotEmpty(methods);
            Assert.DoesNotContain(methods, n =>
                n.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Purge", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Clear", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Amend", StringComparison.OrdinalIgnoreCase));

            // And the entity carries no mutability affordance of its own.
            var entityProps = typeof(ConstructionAuditEntry).GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("UpdatedAt", entityProps);
            Assert.DoesNotContain("UpdatedBy", entityProps);
            Assert.DoesNotContain("DeletedAt", entityProps);
        }

        // ------------------------------------------------------------------------------------------
        // 8. AUDIT — old and new values are recorded in both text and numeric form, with source and
        //    correlation, so one user action is recognisable as one action across many rows.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Audit_records_old_and_new_values_source_and_correlation()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var contract = await f.SeedContractAsync(project.ID);

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
                new() { Code = "2", Description = "Concrete",   Quantity = 50m,  UnitPrice = 60m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            var open = await f.Revisions.OpenAsync(Company, project.ID, contract.ID,
                CommercialRevisionSource.Correction, null, DateTime.Today, 3);
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 120m, 6m, null);
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["2"], 50m, 65m, null);
            await f.Revisions.ApproveAsync(Company, open.revisionId, 3, "agreed re-measure and re-rate");

            await using var verify = f.NewContext();
            var rows = await verify.ConstructionAuditEntries.AsNoTracking()
                .Where(a => a.ChangeKind == ConstructionChangeKind.Approved
                         && a.EntityType == ConstructionAuditEntityTypes.BoqItem)
                .ToListAsync();

            Assert.Equal(4, rows.Count);                                  // two lines × (quantity, rate)
            Assert.Single(rows.Select(r => r.CorrelationId).Distinct());   // ONE action
            Assert.All(rows, r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.OldValue));
                Assert.False(string.IsNullOrWhiteSpace(r.NewValue));
                Assert.NotNull(r.OldNumeric);
                Assert.NotNull(r.NewNumeric);
                Assert.False(string.IsNullOrWhiteSpace(r.SourceContext));
                Assert.Equal(open.revisionId, r.RevisionId);
                Assert.Equal(project.ID, r.ProjectId);
            });

            // The unchanged quantity on line 2 is recorded as unchanged rather than omitted, so the
            // audit shows what was CONSIDERED, not only what moved.
            var line2Qty = rows.Single(r => r.LineId == ids["2"] && r.FieldName == "Quantity");
            Assert.Equal(50m, line2Qty.OldNumeric);
            Assert.Equal(50m, line2Qty.NewNumeric);
        }

        // ------------------------------------------------------------------------------------------
        // 9. AUDIT — every construction audit row is company-scoped, and a read for one company never
        //    returns another company's history.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Audit_history_is_company_scoped()
        {
            using var f = new ConstructionTestFixture(Company);
            var mine = await f.SeedProjectAsync(companyId: 1, code: "P-1");
            var theirs = await f.SeedProjectAsync(companyId: 2, code: "P-2");

            await f.Boq.SaveLinesAsync(1, mine.ID, new List<BoqLineInput>
            { new() { Code = "1", Description = "Mine", Quantity = 1m, UnitPrice = 10m } }, 7, "mine");
            await f.Boq.SaveLinesAsync(2, theirs.ID, new List<BoqLineInput>
            { new() { Code = "1", Description = "Theirs", Quantity = 1m, UnitPrice = 20m } }, 7, "theirs");

            var mineHistory = await f.Audit.ForCompanyAsync(1, ConstructionAuditEntityTypes.BoqItem);
            var theirHistory = await f.Audit.ForCompanyAsync(2, ConstructionAuditEntityTypes.BoqItem);

            Assert.NotEmpty(mineHistory);
            Assert.NotEmpty(theirHistory);
            Assert.All(mineHistory, a => Assert.Equal(1, a.CompanyID));
            Assert.All(theirHistory, a => Assert.Equal(2, a.CompanyID));
            Assert.Empty(mineHistory.Select(a => a.ID).Intersect(theirHistory.Select(a => a.ID)));
        }
    }
}
