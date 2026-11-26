using CrossBuy.BL.Construction;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PartialInvoiceValueTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void Split_invoices_conserve_original_discount_net_and_tax(int decimals)
    {
        var source = new ConstructionInvoiceCommercialSource(10, 100, 10, 20, 990, 198);
        var history = new List<ConstructionInvoiceCommercialHistory>();
        foreach (var quantity in new[] { 2m, 3m, 5m })
        {
            var result = ConstructionPartialInvoiceValueRules.Validate(source, history, quantity, decimals);
            Assert.True(result.Ok, result.Code);
            history.Add(new(quantity, result.Discount, result.Net, result.Tax));
        }
        Assert.Equal(source.Discount, history.Sum(x => x.Discount));
        Assert.Equal(source.Net, history.Sum(x => x.Net));
        Assert.Equal(source.Tax, history.Sum(x => x.Tax));
    }

    [Fact]
    public void Tax_rounding_that_existing_writer_cannot_reproduce_is_refused()
    {
        var source = new ConstructionInvoiceCommercialSource(2, .03m, 0, 20, .06m, .01m);
        var first = ConstructionPartialInvoiceValueRules.Validate(source, [], 1, 2);
        Assert.True(first.Ok); Assert.Equal(.01m, first.Tax);
        var next = ConstructionPartialInvoiceValueRules.Validate(source,
            [new(1, first.Discount, first.Net, first.Tax)], 1, 2);
        Assert.False(next.Ok); Assert.Equal("InvoiceCommercialRoundingNotRepresentable", next.Code);
    }

    [Fact]
    public void Corrupt_history_cannot_be_hidden_by_later_offsetting_values()
    {
        var source = new ConstructionInvoiceCommercialSource(10, 100, 10, 20, 990, 198);
        var result = ConstructionPartialInvoiceValueRules.Validate(source,
            [new(2, 1, 199, 39.8m), new(3, 4, 296, 59.2m)], 5, 2);
        Assert.False(result.Ok); Assert.Equal("InvoiceCommercialHistoryUnproven", result.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void Unknown_currency_precision_is_refused(int decimals)
    {
        var result = ConstructionPartialInvoiceValueRules.Validate(new(1, 10, 0, 20, 10, 2), [], 1, decimals);
        Assert.False(result.Ok); Assert.Equal("InvoiceCurrencyPrecisionUnproven", result.Code);
    }

    [Fact]
    public void Changed_source_totals_are_not_trusted()
    {
        var result = ConstructionPartialInvoiceValueRules.Validate(new(1, 10, 0, 20, 9, 2), [], 1, 2);
        Assert.False(result.Ok); Assert.Equal("InvoiceCommercialSourceUnproven", result.Code);
    }

    [Fact]
    public void Overbilling_and_decimal_overflow_are_refused()
    {
        Assert.Equal("InvoiceCommercialQuantityExceeded", ConstructionPartialInvoiceValueRules.Validate(
            new(1, 10, 0, 20, 10, 2), [], 2, 2).Code);
        Assert.Equal("InvoiceCommercialArithmeticOverflow", ConstructionPartialInvoiceValueRules.Validate(
            new(decimal.MaxValue, 2, 0, 0, 0, 0), [], 1, 2).Code);
    }
}
