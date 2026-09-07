using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // Construction C1 — CR-03: IMMUTABLE COMMERCIAL REVISIONS
    //
    // The defect: BL/VariationOrderService.cs ApproveAsync assigned the new quantity and rate straight
    // onto the live BoqItem. The only trace was OldQuantity/OldUnitPrice on the variation line, and a
    // certificate line (Models/Context/Accounting/ProgressBilling.cs) stores no revision and no rate —
    // so a POSTED certificate's commercial basis changed underneath it and could no longer be
    // reproduced from its own data.
    //
    // The fix under test: a commercial change may only happen through an approved CommercialRevision
    // that records previous and new values per line, and every certificate line carries a snapshot of
    // the revision, quantity and rate it was certified against.
    // ==========================================================================================
    public class ConstructionC1CommercialRevisionTests
    {
        private const int Company = 1;

        // ------------------------------------------------------------------------------------------
        // 1. Approving a revision applies the new values AND preserves the previous ones.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Approving_a_revision_applies_new_values_and_preserves_previous_ones()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var contract = await f.SeedContractAsync(project.ID);

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Unit = "m3", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            var vo = await f.SeedApprovedVariationAsync(project.ID);
            var open = await f.Revisions.OpenAsync(Company, project.ID, contract.ID,
                CommercialRevisionSource.Variation, vo.ID, new DateTime(2026, 3, 1), actorEmployeeId: 3);
            Assert.True(open.ok, open.error);

            var stage = await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"],
                newQuantity: 120m, newRate: 6m, note: "rate re-negotiated, quantity increased");
            Assert.True(stage.ok, stage.error);

            var approve = await f.Revisions.ApproveAsync(Company, open.revisionId, actorEmployeeId: 3,
                reason: "VO-1 approved by employer letter 2026/114");
            Assert.True(approve.ok, approve.error);

            await using var verify = f.NewContext();

            // The live BOQ carries the NEW values…
            var line = await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == ids["1"]);
            Assert.Equal(120m, line.Quantity);
            Assert.Equal(6m, line.UnitPrice);

            // …and the revision holds the PREVIOUS ones, immutably, with the impact computed.
            var rev = await verify.CommercialRevisions.AsNoTracking().Include(r => r.Lines)
                .SingleAsync(r => r.ID == open.revisionId);
            Assert.Equal(CommercialRevisionStatus.Approved, rev.Status);
            Assert.Equal(CommercialRevisionSource.Variation, rev.Source);
            Assert.Equal(vo.ID, rev.SourceVariationOrderId);
            Assert.Equal(new DateTime(2026, 3, 1), rev.EffectiveDate);
            Assert.Equal(3, rev.ApprovedBy);
            Assert.NotNull(rev.ApprovedAt);
            Assert.Contains("2026/114", rev.Reason!);

            var rl = rev.Lines.Single();
            Assert.Equal(100m, rl.PreviousQuantity);
            Assert.Equal(5m, rl.PreviousRate);
            Assert.Equal(120m, rl.NewQuantity);
            Assert.Equal(6m, rl.NewRate);
            Assert.Equal(20m, rl.QuantityImpact);
            Assert.Equal(220m, rl.ValueImpact);                 // 120×6 − 100×5
            Assert.Equal(CommercialLineChangeKind.Adjusted, rl.ChangeKind);
            Assert.Equal(500m, rev.TotalValueBefore);
            Assert.Equal(720m, rev.TotalValueAfter);
            Assert.Equal(220m, rev.ValueImpact);

            // The BOQ line now points at the revision in force.
            var state = await verify.BoqLineStates.AsNoTracking().SingleAsync(s => s.BoqItemId == ids["1"]);
            Assert.Equal(open.revisionId, state.CurrentRevisionId);
        }

        // ------------------------------------------------------------------------------------------
        // 2. An APPROVED revision is immutable — it cannot be staged onto, re-approved, or reused.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task An_approved_revision_is_immutable()
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
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 110m, 5m, null);
            await f.Revisions.ApproveAsync(Company, open.revisionId, 3, "corrected quantity per re-measure");

            var stageAgain = await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 999m, 999m, "tamper");
            Assert.False(stageAgain.ok);
            Assert.Contains("approved", stageAgain.error!, StringComparison.OrdinalIgnoreCase);

            var approveAgain = await f.Revisions.ApproveAsync(Company, open.revisionId, 3, "again");
            Assert.False(approveAgain.ok);

            await using var verify = f.NewContext();
            var rl = await verify.CommercialRevisionLines.AsNoTracking()
                .SingleAsync(l => l.CommercialRevisionId == open.revisionId);
            Assert.Equal(110m, rl.NewQuantity);      // untouched by the tamper attempt
            Assert.Equal(100m, rl.PreviousQuantity);
            var line = await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == ids["1"]);
            Assert.Equal(110m, line.Quantity);        // applied exactly once
        }

        // ------------------------------------------------------------------------------------------
        // 3. A revision cannot be approved without a reason.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_revision_cannot_be_approved_without_a_reason()
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
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 110m, 5m, null);

            foreach (var badReason in new[] { null, "", "   " })
            {
                var result = await f.Revisions.ApproveAsync(Company, open.revisionId, 3, badReason!);
                Assert.False(result.ok);
                Assert.Contains("reason", result.error!, StringComparison.OrdinalIgnoreCase);
            }

            await using var verify = f.NewContext();
            var line = await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == ids["1"]);
            Assert.Equal(100m, line.Quantity);   // nothing applied
        }

        // ------------------------------------------------------------------------------------------
        // 4. THE HISTORICAL-REPRODUCTION TEST — a posted certificate reproduces the exact commercial
        //    values used at approval time, after a later revision has changed the live BOQ.
        //
        //    This is the test mutation C-03 must break.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_posted_certificate_reproduces_the_rate_it_was_certified_at()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var contract = await f.SeedContractAsync(project.ID);

            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Unit = "m3", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            // The original commercial baseline is itself a revision — so every certificate has something
            // to point at from day one.
            var baseline = await f.Revisions.OpenBaselineAsync(Company, project.ID, contract.ID, DateTime.Today, 3,
                "as-awarded contract BOQ");
            Assert.True(baseline.ok, baseline.error);

            // 50 m3 certified at the ORIGINAL rate of 5 → 250.
            var cert = await f.SeedPostedClientCertificateAsync(project.ID, ids["1"], periodValue: 250m);
            int certLineId = await f.CertificateLineIdAsync(cert.ID);
            var snap = await f.Revisions.CaptureCertificateLineSnapshotAsync(Company, certLineId,
                certifiedQuantity: 50m, actorEmployeeId: 7);
            Assert.True(snap.ok, snap.error);

            // Later, a variation doubles the rate.
            var vo = await f.SeedApprovedVariationAsync(project.ID);
            var open = await f.Revisions.OpenAsync(Company, project.ID, contract.ID,
                CommercialRevisionSource.Variation, vo.ID, DateTime.Today.AddDays(30), 3);
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 100m, 10m, "rate doubled");
            await f.Revisions.ApproveAsync(Company, open.revisionId, 3, "VO-2 rate increase");

            await using var verify = f.NewContext();
            // The live BOQ has moved on…
            Assert.Equal(10m, (await verify.BoqItems.AsNoTracking().SingleAsync(b => b.ID == ids["1"])).UnitPrice);

            // …and the posted certificate still reproduces exactly what it certified.
            var reproduced = await f.Revisions.ReproduceCertificateLineAsync(Company, certLineId);
            Assert.NotNull(reproduced);
            Assert.Equal(5m, reproduced!.ContractedRate);
            Assert.Equal(100m, reproduced.ContractedQuantity);
            Assert.Equal(50m, reproduced.CertifiedQuantity);
            Assert.Equal(250m, reproduced.CertifiedValue);
            Assert.Equal(baseline.revisionId, reproduced.CommercialRevisionId);
            Assert.NotEqual(open.revisionId, reproduced.CommercialRevisionId);
        }

        // ------------------------------------------------------------------------------------------
        // 5. A new approved revision SUPERSEDES the previous one, and the chain is navigable.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_new_approved_revision_supersedes_the_previous_one()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var contract = await f.SeedContractAsync(project.ID);
            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            var baseline = await f.Revisions.OpenBaselineAsync(Company, project.ID, contract.ID, DateTime.Today, 3, "as-awarded");

            var r2 = await f.Revisions.OpenAsync(Company, project.ID, contract.ID, CommercialRevisionSource.Correction, null, DateTime.Today, 3);
            await f.Revisions.StageLineChangeAsync(Company, r2.revisionId, ids["1"], 100m, 6m, null);
            await f.Revisions.ApproveAsync(Company, r2.revisionId, 3, "rate correction 1");

            var r3 = await f.Revisions.OpenAsync(Company, project.ID, contract.ID, CommercialRevisionSource.Correction, null, DateTime.Today, 3);
            await f.Revisions.StageLineChangeAsync(Company, r3.revisionId, ids["1"], 100m, 7m, null);
            await f.Revisions.ApproveAsync(Company, r3.revisionId, 3, "rate correction 2");

            await using var verify = f.NewContext();
            var all = await verify.CommercialRevisions.AsNoTracking()
                .Where(r => r.ProjectId == project.ID).OrderBy(r => r.RevisionNo).ToListAsync();

            Assert.Equal(3, all.Count);
            Assert.Equal(new[] { 1, 2, 3 }, all.Select(r => r.RevisionNo).ToArray());
            Assert.Equal(CommercialRevisionStatus.Superseded, all[0].Status);
            Assert.Equal(all[1].ID, all[0].SupersededByRevisionId);
            Assert.Equal(CommercialRevisionStatus.Superseded, all[1].Status);
            Assert.Equal(all[2].ID, all[1].SupersededByRevisionId);
            Assert.Equal(CommercialRevisionStatus.Approved, all[2].Status);
            Assert.Null(all[2].SupersededByRevisionId);

            // Only ONE revision is in force at a time, and it is the one the BOQ line points at.
            var state = await verify.BoqLineStates.AsNoTracking().SingleAsync(s => s.BoqItemId == ids["1"]);
            Assert.Equal(all[2].ID, state.CurrentRevisionId);
            Assert.NotEqual(baseline.revisionId, state.CurrentRevisionId);
        }

        // ------------------------------------------------------------------------------------------
        // 6. A revision cannot cross a company or a project boundary.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_revision_cannot_stage_a_line_from_another_project()
        {
            using var f = new ConstructionTestFixture(Company);
            var a = await f.SeedProjectAsync(code: "P-A");
            var b = await f.SeedProjectAsync(code: "P-B");
            var contractA = await f.SeedContractAsync(a.ID, contractNo: "C-A");

            await f.Boq.SaveLinesAsync(Company, b.ID, new List<BoqLineInput>
            {
                new() { Code = "9", Description = "Other project line", Quantity = 1m, UnitPrice = 1m },
            }, 7, "project B BOQ");
            var bIds = await f.ActiveLineIdsByCodeAsync(b.ID);

            var open = await f.Revisions.OpenAsync(Company, a.ID, contractA.ID, CommercialRevisionSource.Correction, null, DateTime.Today, 3);
            var stage = await f.Revisions.StageLineChangeAsync(Company, open.revisionId, bIds["9"], 5m, 5m, null);

            Assert.False(stage.ok);
            await using var verify = f.NewContext();
            Assert.Equal(0, await verify.CommercialRevisionLines.CountAsync(l => l.CommercialRevisionId == open.revisionId));
        }

        // ------------------------------------------------------------------------------------------
        // 7. Approving a revision writes line-level audit carrying both values and the revision id.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Approving_a_revision_audits_the_old_and_new_commercial_values()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var contract = await f.SeedContractAsync(project.ID);
            await f.Boq.SaveLinesAsync(Company, project.ID, new List<BoqLineInput>
            {
                new() { Code = "1", Description = "Excavation", Quantity = 100m, UnitPrice = 5m },
            }, 7, "original BOQ");
            var ids = await f.ActiveLineIdsByCodeAsync(project.ID);

            var open = await f.Revisions.OpenAsync(Company, project.ID, contract.ID, CommercialRevisionSource.Correction, null, DateTime.Today, 3);
            await f.Revisions.StageLineChangeAsync(Company, open.revisionId, ids["1"], 120m, 6m, null);
            await f.Revisions.ApproveAsync(Company, open.revisionId, actorEmployeeId: 3, reason: "re-measure agreed with QS");

            var audit = await f.AuditForAsync(ConstructionAuditEntityTypes.BoqItem, ids["1"]);
            var rate = audit.Single(a => a.FieldName == "UnitPrice" && a.ChangeKind == ConstructionChangeKind.Approved);
            Assert.Equal(5m, rate.OldNumeric);
            Assert.Equal(6m, rate.NewNumeric);
            Assert.Equal(open.revisionId, rate.RevisionId);
            Assert.Equal(3, rate.ActorEmployeeId);
            Assert.Contains("QS", rate.Reason!);

            var qty = audit.Single(a => a.FieldName == "Quantity" && a.ChangeKind == ConstructionChangeKind.Approved);
            Assert.Equal(100m, qty.OldNumeric);
            Assert.Equal(120m, qty.NewNumeric);
            // One approval = one correlation id across every row it produced.
            Assert.Single(new[] { rate.CorrelationId, qty.CorrelationId }.Distinct());
        }
    }
}
