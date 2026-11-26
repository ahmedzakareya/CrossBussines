using CrossBuy.BL.Construction;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RealizationLineageTests
{
    private static ConstructionRealizationLineage[] Links() => new[]
    {
        new ConstructionRealizationLineage(1, 2, 3, 11, 4, 21, 5, 31),
        new ConstructionRealizationLineage(1, 2, 3, 12, 4, 22, 5, 32)
    };
    private static ConstructionRealizationLineageCheck Check(ConstructionRealizationLineage[] links,
        int[]? journalIds = null) => ConstructionRealizationLineageRules.Validate(
            1, 2, 3, 4, 5, new[] { 11, 12 }, new[] { 21, 22 }, journalIds ?? new[] { 31, 32 }, links);

    [Fact]
    public void Complete_writer_identity_graph_is_order_independent_and_grants_no_release() =>
        Assert.Equal(new(true, "ValidIdentityGraphOnly"), Check(Links().Reverse().ToArray()));

    [Fact]
    public void Header_link_without_lineage_is_insufficient() =>
        Assert.Equal("WriterLineageRequired", Check(Array.Empty<ConstructionRealizationLineage>()).Code);

    [Fact]
    public void Partial_conversion_cannot_release_the_whole_order() =>
        Assert.Equal("IncompleteSourceLineage", Check(Links().Take(1).ToArray()).Code);

    [Theory]
    [InlineData("company")]
    [InlineData("project")]
    [InlineData("order")]
    [InlineData("invoice")]
    [InlineData("journal")]
    public void Matching_line_numbers_cannot_cross_source_scope(string changed)
    {
        var links = Links();
        links[0] = changed switch
        {
            "company" => links[0] with { CompanyId = 9 },
            "project" => links[0] with { ProjectId = 9 },
            "order" => links[0] with { OrderId = 9 },
            "invoice" => links[0] with { InvoiceId = 9 },
            _ => links[0] with { JournalEntryId = 9 }
        };
        Assert.Equal("SourceScopeMismatch", Check(links).Code);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("invoice")]
    [InlineData("journal")]
    public void Many_to_one_matches_need_a_separate_contract(string changed)
    {
        var links = Links();
        links[1] = changed switch
        {
            "order" => links[1] with { OrderLineId = 11 },
            "invoice" => links[1] with { InvoiceLineId = 21 },
            _ => links[1] with { JournalLineId = 31 }
        };
        Assert.Equal("AmbiguousSourceLineage", Check(links).Code);
    }

    [Fact]
    public void Tax_or_payable_lines_cannot_be_silently_included_in_net_lineage() =>
        Assert.Equal("IncompleteSourceLineage", Check(Links(), new[] { 31, 32, 33 }).Code);

    [Fact]
    public void Foreign_ledger_line_is_refused()
    {
        var links = Links(); links[0] = links[0] with { JournalLineId = 99 };
        Assert.Equal("IncompleteSourceLineage", Check(links).Code);
    }

    [Fact]
    public void Invalid_source_identity_is_refused() =>
        Assert.Equal("InvalidSourceIdentity", ConstructionRealizationLineageRules.Validate(
            0, 2, 3, 4, 5, new[] { 11 }, new[] { 21 }, new[] { 31 }, Links()).Code);
}
