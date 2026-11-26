using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication;

public sealed class CommEntityPrivacyPolicyTests
{
    [Theory]
    [InlineData("Employee")]
    [InlineData("JournalEntry")]
    [InlineData("SalesInvoice")]
    [InlineData("PurchaseInvoice")]
    [InlineData("Task")]
    public void Missing_policy_never_allows_public_disclosure(string entity)
    {
        var options = new CommunicationPlatformOptions();
        Assert.False(options.MayAuthorVisibility(entity, CommVisibility.Public));
        Assert.True(options.MayAuthorVisibility(entity, CommVisibility.Internal));
        Assert.True(options.MayAuthorVisibility(entity, CommVisibility.Restricted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Public_configuration_without_external_policy_reference_is_refused(string? reference)
    {
        var options = new CommunicationPlatformOptions();
        options.EntityPrivacyPolicies[EntityRegistry.SalesInvoice] = new()
        { MaximumVisibility = CommVisibility.Public, ExternalPolicyReference = reference };
        Assert.False(options.MayAuthorVisibility(EntityRegistry.SalesInvoice, CommVisibility.Public));
    }

    [Theory]
    [InlineData("Employee")]
    [InlineData("JournalEntry")]
    public void Protected_internal_entities_cannot_be_opened_by_configuration(string entity)
    {
        var options = new CommunicationPlatformOptions();
        options.EntityPrivacyPolicies[entity] = new()
        { MaximumVisibility = CommVisibility.Public, ExternalPolicyReference = "synthetic-approved-policy" };
        Assert.False(options.MayAuthorVisibility(entity, CommVisibility.Public));
        Assert.True(options.MayAuthorVisibility(entity, CommVisibility.Internal));
    }

    [Fact]
    public void Configuration_cannot_invent_an_external_invoice_authority()
    {
        var options = new CommunicationPlatformOptions();
        options.EntityPrivacyPolicies[EntityRegistry.SalesInvoice] = new()
        { MaximumVisibility = CommVisibility.Public, ExternalPolicyReference = "unregistered-invoice-authority" };
        Assert.False(options.MayAuthorVisibility(EntityRegistry.SalesInvoice, CommVisibility.Public));
        Assert.True(options.MayAuthorVisibility(EntityRegistry.Quotation, CommVisibility.Public));
    }

    [Fact]
    public void Invalid_ceiling_is_refused_instead_of_widened()
    {
        var options = new CommunicationPlatformOptions();
        options.EntityPrivacyPolicies[EntityRegistry.SalesInvoice] = new() { MaximumVisibility = "Typo" };
        Assert.False(options.MayAuthorVisibility(EntityRegistry.SalesInvoice, CommVisibility.Internal));
        Assert.False(options.MayAuthorVisibility(EntityRegistry.SalesInvoice, "Typo"));
    }

    [Fact]
    public async Task Withdrawing_quotation_policy_closes_external_list_and_direct_reads()
    {
        using var host = new PortalConversationHost();
        var thread = host.SeedUsableThread();
        host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
        var options = new CommunicationPlatformOptions();
        var configured = Microsoft.Extensions.Options.Options.Create(options);
        var service = new CrossBuy.BL.Communication.CommPortalService(host.Db,
            new CrossBuy.BL.Communication.CommExternalAccessPolicy(host.Db),
            new CrossBuy.BL.Communication.CommBodyPolicy(configured), configured);
        Assert.Single(await service.ListConversationsAsync(host.Portal()));
        Assert.Single(await service.ListCommentsAsync(host.Portal(), thread.Id));
        options.EntityPrivacyPolicies.Clear();
        Assert.Empty(await service.ListConversationsAsync(host.Portal()));
        Assert.Null(await service.GetConversationAsync(host.Portal(), thread.Id));
        Assert.Empty(await service.ListCommentsAsync(host.Portal(), thread.Id));
        Assert.Null(await service.PostCommentAsync(host.Portal(), thread.Id, "refused after withdrawal"));
        Assert.False(await service.MarkReadAsync(host.Portal(), thread.Id));
    }

    [Fact]
    public async Task Invalid_policy_does_not_offer_a_compose_control()
    {
        using var host = new CommunicationTestHost();
        var context = CommunicationTestHost.Context();
        var thread = await host.Threads().GetOrCreateAsync(context,
            new CommThreadRequest { Entity = CommunicationTestHost.Invoice() });
        host.Options.EntityPrivacyPolicies[EntityRegistry.SalesInvoice] = new() { MaximumVisibility = "Invalid" };
        var access = await host.Access().ResolveThreadAccessAsync(context, thread);
        Assert.True(access.CanRead);
        Assert.False(access.CanComment);
        Assert.Empty(access.AuthorVisibilities);
    }

    [Fact]
    public async Task Public_thread_is_refused_before_any_thread_is_stored()
    {
        var options = CommunicationTestHost.DefaultOptions();
        options.EntityPrivacyPolicies.Clear();
        using var host = new CommunicationTestHost(options);
        var error = await Assert.ThrowsAsync<CommValidationException>(() => host.Threads().GetOrCreateAsync(
            CommunicationTestHost.Context(), new CommThreadRequest
            { Entity = CommunicationTestHost.Invoice(), Visibility = CommVisibility.Public }));
        Assert.Equal(CommValidationException.Codes.VisibilityInvalid, error.Code);
        using var fresh = host.NewContext();
        Assert.Empty(await fresh.Set<CommThread>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Removing_public_policy_closes_authoring_on_an_existing_public_thread()
    {
        using var host = new CommunicationTestHost();
        var entity = CommunicationTestHost.Invoice();
        var context = CommunicationTestHost.Context();
        var thread = await host.Threads().GetOrCreateAsync(context,
            new CommThreadRequest { Entity = entity, Visibility = CommVisibility.Public });
        host.Options.EntityPrivacyPolicies.Clear();
        var access = await host.Access().ResolveThreadAccessAsync(context, thread);
        Assert.DoesNotContain(CommVisibility.Public, access.AuthorVisibilities);
        Assert.Contains(CommVisibility.Internal, access.AuthorVisibilities);
        var error = await Assert.ThrowsAsync<CommValidationException>(() => host.Comments().AddAsync(context,
            new CommCommentRequest { Entity = entity, ThreadId = thread.Id, Body = "must not escape", Visibility = CommVisibility.Public }));
        Assert.Equal(CommValidationException.Codes.VisibilityInvalid, error.Code);
        using var fresh = host.NewContext();
        Assert.Empty(await fresh.Set<CommComment>().AsNoTracking().ToListAsync());
    }
}
