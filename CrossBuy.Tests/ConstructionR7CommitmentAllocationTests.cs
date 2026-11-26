using CrossBuy.BL.Construction;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7CommitmentAllocationTests
{
    private static ConstructionOrderSource Source() => new(8, "PO-8", DateTime.Today, "Approved", 5, 1, "EGP", null,
        100m, 15m, 115m, "Open", 100m, null,
        new[] { new ConstructionOrderLineSource(11, 1, 1, 1, 60, 0, 15, 60, 0),
            new ConstructionOrderLineSource(12, 2, 1, 1, 40, 0, 15, 40, 0) });
    private static readonly ConstructionCommitmentDimension[] Dimensions = { new(3, 4, true, true, true), new(5, 4, true, true, true) };
    private static ConstructionCommitmentSplit[] Splits() => new[] { new ConstructionCommitmentSplit(11, 3, 4, 60), new ConstructionCommitmentSplit(12, 5, 4, 40) };

    [Fact]
    public void Exact_line_splits_exclude_tax_and_allow_multiple_dimensions()
    {
        var splits = new[] { new ConstructionCommitmentSplit(11, 3, 4, 20), new ConstructionCommitmentSplit(11, 5, 4, 40), new ConstructionCommitmentSplit(12, 3, 4, 40) };
        Assert.True(ConstructionCommitmentAllocationRules.Validate(Source(), 1, splits, Dimensions).Ok);
    }

    [Fact]
    public void Equal_document_total_cannot_conceal_wrong_line_allocations()
    {
        var splits = Splits(); splits[0] = splits[0] with { NetDocumentAmount = 50 }; splits[1] = splits[1] with { NetDocumentAmount = 50 };
        Assert.Equal("LineAllocationIncomplete", ConstructionCommitmentAllocationRules.Validate(Source(), 1, splits, Dimensions).Code);
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("DraftProposal")]
    [InlineData("Released")]
    public void Non_open_history_never_becomes_a_new_commitment(string classification) =>
        Assert.False(ConstructionCommitmentAllocationRules.Validate(Source() with { Classification = classification }, 1, Splits(), Dimensions).Ok);

    [Fact]
    public void Unknown_currency_and_different_currency_are_refused()
    {
        Assert.Equal("DocumentCurrencyNotProven", ConstructionCommitmentAllocationRules.Validate(Source() with { CurrencyId = null }, 1, Splits(), Dimensions).Code);
        Assert.Equal("BudgetCurrencyMismatch", ConstructionCommitmentAllocationRules.Validate(Source(), 2, Splits(), Dimensions).Code);
    }

    [Fact]
    public void Duplicate_and_foreign_line_identities_are_refused()
    {
        Assert.Equal("DuplicateAllocation", ConstructionCommitmentAllocationRules.Validate(Source(), 1, Splits().Concat(new[] { Splits()[0] }).ToArray(), Dimensions).Code);
        var splits = Splits(); splits[0] = splits[0] with { SourceLineId = 99 };
        Assert.Equal("ForeignSourceLine", ConstructionCommitmentAllocationRules.Validate(Source(), 1, splits, Dimensions).Code);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Inactive_non_cost_and_unbudgeted_dimensions_are_refused(bool active, bool ownsCost, bool budgeted) =>
        Assert.Equal("DimensionNotEligible", ConstructionCommitmentAllocationRules.Validate(Source(), 1, Splits(),
            new[] { new ConstructionCommitmentDimension(3, 4, active, ownsCost, budgeted), Dimensions[1] }).Code);

    [Fact]
    public void Splits_cannot_rely_on_database_rounding_to_reconcile()
    {
        var splits = new[] { new ConstructionCommitmentSplit(11, 3, 4, 0.000000001m), new ConstructionCommitmentSplit(11, 5, 4, 59.999999999m), Splits()[1] };
        Assert.Equal("AllocationPrecisionInvalid", ConstructionCommitmentAllocationRules.Validate(Source(), 1, splits, Dimensions).Code);
    }

    [Fact]
    public void Changed_source_totals_and_missing_zero_line_coverage_are_refused()
    {
        Assert.Equal("SourceTotalsChanged", ConstructionCommitmentAllocationRules.Validate(Source() with { StoredSubtotal = 101 }, 1, Splits(), Dimensions).Code);
        Assert.Equal("LineAllocationIncomplete", ConstructionCommitmentAllocationRules.Validate(Source(), 1, new[] { Splits()[0] }, Dimensions).Code);
    }
}
