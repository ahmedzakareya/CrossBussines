using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebaseWorkspaceSqlTests(UatSqlProbeFixture sql)
{
    [SkippableFact]
    public async Task Real_workspace_sequences_proposal_independent_review_activation_and_purchase_request()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBUI");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            await ConstructionR7RebasedReceiptSqlTests.Apply(db);
            var s = await ConstructionR7RebasedReceiptSqlTests.Prepare(db, stopBeforeRebase: true);
            var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(7) };
            var access = new ConstructionR7ExposureRebaseTests.Access();
            var purchase = ConstructionR7PurchaseApprovalTests.Service(db, actor);
            var workspace = new ConstructionRebaseWorkspaceService(db, actor, access, new(db, access, new ConstructionAuditService(db)), purchase);
            var page = await workspace.ReadAsync(s.Seed.Project, s.Order, null); Assert.NotNull(page);
            var selected = page.Selected!; Assert.True(selected.ProposalPreview!.Ok, selected.ProposalPreview.Code);
            var input = new ConstructionRebaseInput { RequestId = Guid.NewGuid(), Version = selected.ProposalPreview.Version!,
                TargetId = selected.TargetId!.Value, Reason = "Workspace rebase proposal" };
            var requested = await workspace.SubmitAsync(s.Seed.Project, s.Order, ConstructionRebaseOperation.Request, input); Assert.True(requested.Ok, requested.Code);
            input = new() { RequestId = Guid.NewGuid(), ProposalId = requested.Id!.Value, Version = input.Version, Reason = "Independent review" };
            var self = await workspace.SubmitAsync(s.Seed.Project, s.Order, ConstructionRebaseOperation.Approve, input);
            Assert.False(self.Ok); Assert.Equal("SeparateApproverRequired", self.Code);
            actor.Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(9);
            page = await workspace.ReadAsync(s.Seed.Project, s.Order, null); Assert.True(page!.Selected!.CanDecide);
            var approved = await workspace.SubmitAsync(s.Seed.Project, s.Order, ConstructionRebaseOperation.Approve, input); Assert.True(approved.Ok, approved.Code);
            page = await workspace.ReadAsync(s.Seed.Project, s.Order, null); var source = page!.Selected!.SourcePreview!; Assert.True(source.Ok, source.Code);
            var activated = await workspace.SubmitAsync(s.Seed.Project, s.Order, ConstructionRebaseOperation.Activate,
                new() { RequestId = Guid.NewGuid(), ProposalId = requested.Id.Value, Version = source.Version!, Reason = "Activate mapping" });
            Assert.True(activated.Ok, activated.Code);
            using (var verify = sql.ContextFor(probe, 1)) { Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToArrayAsync()); }
            page = await workspace.ReadAsync(s.Seed.Project, s.Order, null); var quote = page!.Selected!.PurchasePreview!; Assert.True(quote.Ok, quote.Code);
            var pending = await workspace.SubmitAsync(s.Seed.Project, s.Order, ConstructionRebaseOperation.PurchaseRequest,
                new() { RequestId = Guid.NewGuid(), Version = quote.Version!, Reason = "Request actual purchasing" }); Assert.True(pending.Ok, pending.Code);
            actor.Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(8);
            page = await workspace.ReadAsync(s.Seed.Project, s.Order, null); Assert.True(page!.Selected!.CanReviewPurchase);
            var admission = await purchase.ApproveAsync(pending.Id!.Value, Guid.NewGuid(), "Independent purchase decision"); Assert.True(admission.Ok, admission.Code);
            page = await workspace.ReadAsync(s.Seed.Project, s.Order, null); Assert.Equal("Approved", page!.Selected!.ApprovalStatus); Assert.Null(page.Selected.PurchasePreview);
            using var final = sql.ContextFor(probe, 1);
            Assert.Single(await final.Set<ConstructionCommitmentReservation>().AsNoTracking().ToArrayAsync());
            Assert.Single(await final.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToArrayAsync());
            Assert.Empty(await final.JournalEntries.AsNoTracking().ToArrayAsync()); Assert.Empty(await final.StockMovements.AsNoTracking().ToArrayAsync());
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
