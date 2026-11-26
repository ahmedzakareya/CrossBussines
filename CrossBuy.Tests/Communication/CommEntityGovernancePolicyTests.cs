using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication;

public sealed class CommEntityGovernancePolicyTests
{
    [Fact]
    public void Default_preserves_data_without_inventing_a_retention_period()
    {
        var policy = new CommunicationPlatformOptions().GovernanceFor(EntityRegistry.SalesInvoice);
        Assert.Null(policy.RetentionDays);
        Assert.Null(policy.ArchiveAfterDays);
        Assert.Equal("ManualOnly", policy.ArchiveBehavior);
        Assert.Equal("NoPhysicalDeletion", policy.DeletionBehavior);
        Assert.Equal("PreserveWithComment", policy.AttachmentRetention);
        Assert.True(policy.RequiresEditHistory && policy.RequiresDeletionHistory && policy.RequiresAttachmentAudit);
        Assert.True(policy.RequiresImmutableHistory && policy.RequiresReadReceipts);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    [InlineData(10, 11)]
    public void Invalid_retention_declarations_are_refused(int? retention, int? archive)
    {
        var policy = new CommEntityGovernancePolicy { RetentionDays = retention, ArchiveAfterDays = archive };
        var error = Assert.Throws<CommValidationException>(() => policy.Validate());
        Assert.Equal("governance_policy_invalid", error.Code);
    }

    [Fact]
    public async Task Legal_hold_prevents_edit_and_soft_delete_but_keeps_history_readable()
    {
        using var host = new CommunicationTestHost();
        var context = CommunicationTestHost.Context();
        var added = await host.Comments().AddAsync(context, host.CommentOn(CommunicationTestHost.Invoice(), "preserve this"));
        host.Options.EntityGovernancePolicies[EntityRegistry.SalesInvoice] = new() { LegalHold = true };
        await Assert.ThrowsAsync<CommAccessDeniedException>(() => host.Comments().EditAsync(context,
            new CommCommentEditRequest { CommentId = added.CommentId, Body = "changed" }));
        await Assert.ThrowsAsync<CommAccessDeniedException>(() => host.Comments().DeleteAsync(context, added.CommentId));
        using var fresh = host.NewContext();
        var stored = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == added.CommentId);
        Assert.Equal("preserve this", stored.Body);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(0, stored.RevisionCount);
        var visible = await host.Comments().GetAsync(context, added.CommentId);
        Assert.Equal("preserve this", visible.Body);
    }

    [Fact]
    public async Task Invalid_policy_is_refused_before_creating_a_thread()
    {
        using var host = new CommunicationTestHost();
        host.Options.EntityGovernancePolicies[EntityRegistry.SalesInvoice] = new() { RetentionDays = -1 };
        await Assert.ThrowsAsync<CommValidationException>(() => host.Threads().GetOrCreateAsync(
            CommunicationTestHost.Context(), new CommThreadRequest { Entity = CommunicationTestHost.Invoice() }));
        using var fresh = host.NewContext();
        Assert.Empty(await fresh.Set<CommThread>().AsNoTracking().ToListAsync());
    }

    // LAYER 2, PINNED DIRECTLY.
    //
    // Legal hold is enforced twice: CommAccessPolicy zeroes CanEdit/CanDelete/CanRestore, and the
    // comment service then calls RequireMutable. The access policy always denies first, so the
    // service-level test above cannot tell a working second layer from a deleted one — a mutation run
    // confirmed exactly that: removing this throw, and removing all three RequireMutable calls, changed
    // no test result. Depth that nothing measures is decoration, so it is measured here.
    [Fact]
    public void RequireMutable_refuses_an_entity_on_legal_hold()
    {
        var held = new CommEntityGovernancePolicy { LegalHold = true };
        var error = Assert.Throws<CommValidationException>(() => held.RequireMutable());
        Assert.Equal("entity_legal_hold", error.Code);

        // And permits one that is not held — so the assertion above is about the hold, not about
        // RequireMutable throwing unconditionally.
        new CommEntityGovernancePolicy { LegalHold = false }.RequireMutable();
    }

    // RequireMutable is Validate() AND THEN the hold check. The access policy reads only LegalHold — it
    // never validates the retention declaration. So without this, removing RequireMutable from the
    // mutation paths would silently drop retention validation on edit, delete and restore, and only
    // thread creation would still refuse an invalid policy.
    [Fact]
    public async Task An_invalid_governance_policy_is_refused_when_editing_an_existing_comment()
    {
        using var host = new CommunicationTestHost();
        var context = CommunicationTestHost.Context();
        var added = await host.Comments().AddAsync(
            context, host.CommentOn(CommunicationTestHost.Invoice(), "original"));

        // Archive-after later than retention: internally inconsistent, so Validate() refuses it.
        host.Options.EntityGovernancePolicies[EntityRegistry.SalesInvoice] =
            new() { RetentionDays = 10, ArchiveAfterDays = 11 };

        var error = await Assert.ThrowsAsync<CommValidationException>(() => host.Comments().EditAsync(
            context, new CommCommentEditRequest { CommentId = added.CommentId, Body = "changed" }));
        Assert.Equal("governance_policy_invalid", error.Code);

        using var fresh = host.NewContext();
        var stored = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == added.CommentId);
        Assert.Equal("original", stored.Body);
    }
}
