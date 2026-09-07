using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // Construction C1 — CR-02: SUBCONTRACTOR CERTIFICATION CAP  (business decision D-07: HARD BLOCK)
    //
    // The defect: BL/SubcontractBillingService.cs computes period work as
    // `cumulative − previouslyBilled` from a FREE-TYPED cumulative decimal, with no comparison to
    // Subcontract.ContractValue and no line detail at all — SubcontractBillings has no lines. So a
    // subcontractor could be certified without limit.
    //
    // D-07 as approved: certification beyond approved contract/BOQ scope is allowed ONLY after an
    // approved variation increases the scope. Not a warning. A hard block.
    // ==========================================================================================
    public class ConstructionC1SubcontractCapTests
    {
        private const int Company = 1;

        // ------------------------------------------------------------------------------------------
        // 1. Certification inside the cap succeeds and accumulates.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Certification_within_the_cap_succeeds_and_accumulates()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID, contractValue: 100_000m);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", assignedQuantity: 1000m, subRate: 50m, actorEmployeeId: 9, reason: "scope allocation");
            Assert.True(scope.ok, scope.error);

            var cert1 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 1);
            var line1 = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert1.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 400m, ActorEmployeeId = 9, Reason = "certificate 1"
            });
            Assert.True(line1.ok, line1.error);

            var cert2 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 2);
            var line2 = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert2.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 350m, ActorEmployeeId = 9, Reason = "certificate 2"
            });
            Assert.True(line2.ok, line2.error);

            await using var verify = f.NewContext();
            var lines = await verify.SubcontractCertificateLines.AsNoTracking()
                .Where(l => l.SubcontractScopeId == scope.scopeId).OrderBy(l => l.ID).ToListAsync();

            Assert.Equal(2, lines.Count);
            Assert.Equal(0m, lines[0].PreviousQuantity);
            Assert.Equal(400m, lines[0].CumulativeQuantity);
            Assert.Equal(20_000m, lines[0].CumulativeValue);      // 400 × 50
            Assert.Equal(400m, lines[1].PreviousQuantity);        // carried, not recomputed at read time
            Assert.Equal(750m, lines[1].CumulativeQuantity);
            Assert.Equal(37_500m, lines[1].CumulativeValue);
            Assert.All(lines, l => Assert.Equal(50m, l.RateSnapshot));   // rate snapshotted per line
        }

        // ------------------------------------------------------------------------------------------
        // 2. THE HARD BLOCK — cumulative quantity beyond the assigned quantity is refused.
        //    This is the test mutation C-02 must break.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Certification_beyond_assigned_quantity_is_hard_blocked()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID, contractValue: 100_000m);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", assignedQuantity: 1000m, subRate: 50m, actorEmployeeId: 9, reason: "scope allocation");

            var cert1 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 1);
            await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert1.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 900m, ActorEmployeeId = 9, Reason = "certificate 1"
            });

            var cert2 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 2);
            var over = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert2.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 200m, ActorEmployeeId = 9, Reason = "certificate 2 — over cap"
            });

            Assert.False(over.ok);
            Assert.Contains("1000", over.error!);      // the refusal states the cap
            Assert.Contains("1100", over.error!);      // …and what was attempted

            // Nothing was written.
            await using var verify = f.NewContext();
            Assert.Equal(1, await verify.SubcontractCertificateLines.CountAsync(l => l.SubcontractScopeId == scope.scopeId));
            // Aggregated client-side on purpose: SQLite cannot apply MAX to a decimal, and this is an
            // assertion about the stored rows, not a query the product performs.
            var stored = await verify.SubcontractCertificateLines.AsNoTracking()
                .Where(l => l.SubcontractScopeId == scope.scopeId).ToListAsync();
            Assert.Equal(900m, stored.Max(l => l.CumulativeQuantity));
        }

        // ------------------------------------------------------------------------------------------
        // 3. An APPROVED VARIATION raises the cap — and only then does the same certification pass.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task An_approved_variation_raises_the_cap_and_then_certification_passes()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID, contractValue: 100_000m);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            var cert1 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 1);
            await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert1.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 1000m, ActorEmployeeId = 9, Reason = "certificate 1 — full scope"
            });

            var cert2 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 2);
            var blocked = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert2.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 200m, ActorEmployeeId = 9, Reason = "extra work"
            });
            Assert.False(blocked.ok);

            // A variation authorises 250 more.
            var vo = await f.SeedApprovedVariationAsync(project.ID);
            var raise = await f.Scopes.RaiseCapAsync(Company, scope.scopeId, additionalQuantity: 250m,
                variationOrderId: vo.ID, actorEmployeeId: 3, reason: "VO-1 additional blockwork");
            Assert.True(raise.ok, raise.error);

            var allowed = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert2.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 200m, ApprovedVariationOrderId = vo.ID, ActorEmployeeId = 9,
                Reason = "extra work under VO-1"
            });
            Assert.True(allowed.ok, allowed.error);

            await using var verify = f.NewContext();
            var s = await verify.SubcontractScopes.AsNoTracking().SingleAsync(x => x.ID == scope.scopeId);
            Assert.Equal(1000m, s.AssignedQuantity);              // the original assignment is untouched
            Assert.Equal(250m, s.ApprovedVariationQuantity);       // the increase is separately visible
            Assert.Equal(1250m, s.CappedQuantity);
            Assert.Equal(vo.ID, s.LastCapVariationOrderId);

            // …and the cap raise is audited as such, naming the variation.
            var audit = await f.AuditForAsync(ConstructionAuditEntityTypes.SubcontractScope, scope.scopeId);
            Assert.Contains(audit, a => a.ChangeKind == ConstructionChangeKind.CapRaised
                                     && a.NewNumeric == 1250m && a.Reason!.Contains("VO-1"));
        }

        // ------------------------------------------------------------------------------------------
        // 4. A cap raise without a variation is refused — the cap may not be raised by editing it.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Raising_the_cap_without_a_variation_is_refused()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            var cert = await f.SeedSubcontractCertificateAsync(sub);
            await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 1000m, ActorEmployeeId = 9, Reason = "full scope"
            });

            // (a) a variation id that is not an approved variation of this project
            var unapproved = await f.SeedDraftVariationAsync(project.ID);
            var withDraftVo = await f.Scopes.RaiseCapAsync(Company, scope.scopeId, 100m, unapproved.ID, 3, "not approved yet");
            Assert.False(withDraftVo.ok);
            Assert.Contains("approved", withDraftVo.error!, StringComparison.OrdinalIgnoreCase);

            // (b) increasing the assigned quantity directly on the scope, once anything is certified
            var direct = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", assignedQuantity: 1500m, subRate: 50m, actorEmployeeId: 9,
                reason: "quietly enlarging the scope", scopeId: scope.scopeId);
            Assert.False(direct.ok);
            Assert.Contains("variation", direct.error!, StringComparison.OrdinalIgnoreCase);

            await using var verify = f.NewContext();
            var s = await verify.SubcontractScopes.AsNoTracking().SingleAsync(x => x.ID == scope.scopeId);
            Assert.Equal(1000m, s.CappedQuantity);
        }

        // ------------------------------------------------------------------------------------------
        // 5. The VALUE cap — cumulative value may not exceed the subcontract's approved value even
        //    when the quantity cap would allow it.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Cumulative_value_cannot_exceed_the_subcontract_value()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            // Scope allows 1000 × 50 = 50,000 of quantity-value, but the contract is only worth 30,000.
            var sub = await f.SeedSubcontractAsync(project.ID, contractValue: 30_000m);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            var cert = await f.SeedSubcontractCertificateAsync(sub);
            var ok = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 500m, ActorEmployeeId = 9, Reason = "25,000 — inside both caps"
            });
            Assert.True(ok.ok, ok.error);

            var cert2 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 2);
            var over = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert2.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 200m, ActorEmployeeId = 9, Reason = "would reach 35,000"
            });
            Assert.False(over.ok);
            Assert.Contains("30000", over.error!.Replace(",", "").Replace(".00", ""));
        }

        // ------------------------------------------------------------------------------------------
        // 6. Zero and negative quantities are refused.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-500)]
        public async Task Zero_or_negative_certified_quantity_is_refused(int quantity)
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");
            var cert = await f.SeedSubcontractCertificateAsync(sub);

            var result = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = quantity, ActorEmployeeId = 9, Reason = "invalid quantity"
            });

            Assert.False(result.ok);
            await using var verify = f.NewContext();
            Assert.Equal(0, await verify.SubcontractCertificateLines.CountAsync());
        }

        // ------------------------------------------------------------------------------------------
        // 7. Previous certificates are immutable — a line cannot be added to, or changed on, a
        //    certificate that is no longer Draft.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_certificate_that_is_not_draft_cannot_receive_or_change_lines()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            var posted = await f.SeedSubcontractCertificateAsync(sub, status: "Posted");
            var result = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = posted.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 100m, ActorEmployeeId = 9, Reason = "late addition"
            });

            Assert.False(result.ok);
            Assert.Contains("Draft", result.error!, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------------------------------
        // 8. Correction is an ADJUSTMENT line, not an edit — and the adjustment is itself capped.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task Correction_is_made_by_an_adjustment_line_that_reduces_cumulative()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            var cert1 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 1);
            var first = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert1.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 600m, ActorEmployeeId = 9, Reason = "certificate 1"
            });
            Assert.True(first.ok, first.error);

            // 100 of that was over-measured. The correction is a NEGATIVE adjustment line that names
            // the line it adjusts — the original line is never touched.
            var cert2 = await f.SeedSubcontractCertificateAsync(sub, billingNo: 2);
            var adjust = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert2.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = -100m, AdjustsLineId = first.lineId, ActorEmployeeId = 9,
                Reason = "over-measurement on certificate 1"
            });
            Assert.True(adjust.ok, adjust.error);

            await using var verify = f.NewContext();
            var original = await verify.SubcontractCertificateLines.AsNoTracking().SingleAsync(l => l.ID == first.lineId);
            Assert.Equal(600m, original.CurrentQuantity);        // untouched
            Assert.Equal(600m, original.CumulativeQuantity);

            var adjustment = await verify.SubcontractCertificateLines.AsNoTracking().SingleAsync(l => l.ID == adjust.lineId);
            Assert.Equal(-100m, adjustment.CurrentQuantity);
            Assert.Equal(500m, adjustment.CumulativeQuantity);   // cumulative comes down
            Assert.Equal(first.lineId, adjustment.AdjustsLineId);

            var cap = await f.Scopes.GetCapAsync(Company, sub.ID);
            Assert.Equal(500m, cap.Lines.Single().CertifiedQuantity);
        }

        // ------------------------------------------------------------------------------------------
        // 9. A header amount cannot bypass the line controls: the reconciliation guard reports a
        //    header whose gross work exceeds the sum of its lines.
        //
        //    Scope note, stated plainly: this guard is the CHECK. Wiring it into the existing
        //    approve/post path is C6 work and is NOT done here, because this increment must not change
        //    Accounting posting behaviour. See Stage-Construction-CR02-Subcontract-Cap-Evidence.md §6.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_header_amount_exceeding_its_lines_is_reported_as_unreconciled()
        {
            using var f = new ConstructionTestFixture(Company);
            var project = await f.SeedProjectAsync();
            var sub = await f.SeedSubcontractAsync(project.ID);
            var scope = await f.Scopes.SaveScopeAsync(Company, project.ID, sub.ID, null, "S1", "Blockwork",
                "m2", 1000m, 50m, 9, "scope allocation");

            var cert = await f.SeedSubcontractCertificateAsync(sub);
            await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = Company, SubcontractBillingId = cert.ID, SubcontractScopeId = scope.scopeId,
                CurrentQuantity = 100m, ActorEmployeeId = 9, Reason = "100 × 50 = 5,000"
            });

            // Someone types a bigger number straight onto the header.
            cert.GrossWork = 40_000m;
            f.Db.SubcontractBillings.Update(cert);
            await f.Db.SaveChangesAsync();

            var check = await f.Scopes.ValidateHeaderAgainstLinesAsync(Company, cert.ID);
            Assert.False(check.ok);
            Assert.Contains("5000", check.error!.Replace(",", "").Replace(".00", ""));
            Assert.Contains("40000", check.error!.Replace(",", "").Replace(".00", ""));
        }

        // ------------------------------------------------------------------------------------------
        // 10. Cross-company isolation: a scope belonging to another company is invisible, and the
        //     refusal is indistinguishable from "does not exist".
        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task A_scope_from_another_company_cannot_be_certified()
        {
            using var f = new ConstructionTestFixture(Company);
            var theirProject = await f.SeedProjectAsync(companyId: 2, code: "P-2");
            var theirSub = await f.SeedSubcontractAsync(theirProject.ID, companyId: 2);
            var theirScope = await f.Scopes.SaveScopeAsync(2, theirProject.ID, theirSub.ID, null, "S1",
                "Their blockwork", "m2", 1000m, 50m, 9, "their scope");
            var theirCert = await f.SeedSubcontractCertificateAsync(theirSub);

            var result = await f.Scopes.CertifyLineAsync(new CertifyLineInput
            {
                CompanyID = 1,   // company 1 reaching for company 2's scope
                SubcontractBillingId = theirCert.ID, SubcontractScopeId = theirScope.scopeId,
                CurrentQuantity = 100m, ActorEmployeeId = 9, Reason = "cross-company attempt"
            });

            Assert.False(result.ok);
            await using var verify = f.NewContext();
            Assert.Equal(0, await verify.SubcontractCertificateLines.CountAsync());
        }
    }
}
