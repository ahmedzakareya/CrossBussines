using CrossBuy.BL.Construction;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ReservationRebaseRulesTests
{
    private static ConstructionRebaseDimension Row(decimal amount, int node = 1, string category = "Material") => new(9, node, 2, category, amount);

    [Fact]
    public void Baseline_only_plan_preserves_dimensions_even_when_rows_are_split_and_reordered()
    {
        var result = ConstructionReservationRebaseRules.Validate(3, 3, 3, 12.345m,
            new[] { Row(10), Row(2.345m, 4) }, new[] { Row(2.345m, 4), Row(4), Row(6) });
        Assert.True(result.Ok, result.Code);
    }

    [Theory]
    [InlineData(11, 1, "Material")]
    [InlineData(10, 4, "Material")]
    [InlineData(10, 1, "Labor")]
    public void Changed_money_or_dimension_requires_an_amendment(decimal amount, int node, string category)
    {
        var result = ConstructionReservationRebaseRules.Validate(3, 3, 3, 10,
            new[] { Row(10) }, new[] { Row(amount, node, category) });
        Assert.False(result.Ok); Assert.Equal("ReservationAmendmentRequired", result.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void Precision_is_not_assumed_to_be_two(int places)
    {
        decimal unit = 1; for (var i = 0; i < places; i++) unit /= 10;
        Assert.True(ConstructionReservationRebaseRules.Validate(3, 3, places, unit, new[] { Row(unit) }, new[] { Row(unit) }).Ok);
        Assert.Equal("InvalidCurrencyPrecision", ConstructionReservationRebaseRules.Validate(3, 3, places, 1,
            new[] { Row(1) }, new[] { Row(1 + unit / 10) }).Code);
    }

    [Fact]
    public void Currency_change_and_incomplete_history_are_refused()
    {
        Assert.Equal("BudgetCurrencyMismatch", ConstructionReservationRebaseRules.Validate(3, 4, 3, 10, new[] { Row(10) }, new[] { Row(10) }).Code);
        Assert.Equal("ReservationCoverageRequired", ConstructionReservationRebaseRules.Validate(3, 3, 3, 11, new[] { Row(10) }, new[] { Row(10) }).Code);
        Assert.False(ConstructionReservationRebaseRules.Validate(3, 3, 3, 0, Array.Empty<ConstructionRebaseDimension>(), Array.Empty<ConstructionRebaseDimension>()).Ok);
    }

    [Fact]
    public void Moving_the_same_dimension_to_another_purchase_line_is_not_a_baseline_only_rebase()
    {
        Assert.Equal("ReservationAmendmentRequired", ConstructionReservationRebaseRules.Validate(3, 3, 3, 10,
            new[] { Row(10) }, new[] { Row(10) with { SourceLineId = 10 } }).Code);
    }

    [Theory]
    [InlineData(-1, 2, "Material")]
    [InlineData(1, 0, "Material")]
    [InlineData(1, 2, " ")]
    public void Invalid_dimension_evidence_is_refused(int node, int code, string category)
    {
        Assert.Equal("InvalidBudgetDimension", ConstructionReservationRebaseRules.Validate(3, 3, 3, 10,
            new[] { Row(10) }, new[] { new ConstructionRebaseDimension(9, node, code, category, 10) }).Code);
    }

    [Fact]
    public void Negative_or_unrepresentable_reservation_is_refused()
    {
        foreach (var amount in new[] { -1m, 1.0001m })
            Assert.Equal("InvalidReservationAmount", ConstructionReservationRebaseRules.Validate(3, 3, 3, amount,
                new[] { Row(amount) }, new[] { Row(amount) }).Code);
    }

    [Fact]
    public void Overflow_is_a_refusal_not_a_partial_plan()
    {
        Assert.Equal("AmountOverflow", ConstructionReservationRebaseRules.Validate(3, 3, 0, decimal.MaxValue,
            new[] { Row(decimal.MaxValue), Row(1) }, new[] { Row(decimal.MaxValue) }).Code);
    }
}

