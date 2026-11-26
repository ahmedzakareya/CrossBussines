namespace CrossBuy.BL.Construction;

public sealed record ConstructionInvoiceCommercialSource(decimal Ordered, decimal UnitPrice,
    decimal Discount, decimal TaxRate, decimal Net, decimal Tax);
public sealed record ConstructionInvoiceCommercialHistory(decimal Quantity, decimal Discount, decimal Net, decimal Tax);
public sealed record ConstructionInvoiceCommercialCheck(bool Ok, string Code,
    decimal Discount = 0, decimal Net = 0, decimal Tax = 0);

// Pure commercial arithmetic. Currency decimals must come from ICurrencyRounding in the caller.
// History must first be proven against sealed source/posted invoice/GL evidence by the transactional writer.
// A result is usable only when the existing payable writer can reproduce both incremental net and tax.
public static class ConstructionPartialInvoiceValueRules
{
    public static ConstructionInvoiceCommercialCheck Validate(ConstructionInvoiceCommercialSource source,
        IReadOnlyCollection<ConstructionInvoiceCommercialHistory> history, decimal quantity, int currencyDecimals)
    {
        ConstructionInvoiceCommercialCheck Refuse(string code) => new(false, code);
        if (currencyDecimals is < 0 or > 8) return Refuse("InvoiceCurrencyPrecisionUnproven");
        if (source.Ordered <= 0 || source.UnitPrice < 0 || source.Discount < 0 ||
            source.TaxRate is < 0 or > 100 || quantity <= 0)
            return Refuse("InvoiceCommercialSourceInvalid");
        decimal Round(decimal value) => decimal.Round(value, currencyDecimals, MidpointRounding.AwayFromZero);
        try
        {
            var gross = checked(source.Ordered * source.UnitPrice);
            if (source.Discount > gross || source.Discount != Round(source.Discount) ||
                source.Net != Round(gross - source.Discount) || source.Tax != Round(source.Net * source.TaxRate / 100m))
                return Refuse("InvoiceCommercialSourceUnproven");
            decimal priorQuantity = 0, priorDiscount = 0, priorNet = 0, priorTax = 0;
            // Sequence is persisted invoice order, not caller-selected grouping: each prefix must agree.
            foreach (var fact in history)
            {
                if (fact.Quantity <= 0 || fact.Discount < 0 || fact.Net < 0 || fact.Tax < 0)
                    return Refuse("InvoiceCommercialHistoryUnproven");
                priorQuantity = checked(priorQuantity + fact.Quantity);
                priorDiscount = checked(priorDiscount + fact.Discount);
                priorNet = checked(priorNet + fact.Net);
                priorTax = checked(priorTax + fact.Tax);
                if (priorQuantity > source.Ordered) return Refuse("InvoiceCommercialHistoryUnproven");
                var prefixDiscount = Round(source.Discount * (priorQuantity / source.Ordered));
                var prefixNet = Round(checked(priorQuantity * source.UnitPrice) - prefixDiscount);
                if (priorDiscount != prefixDiscount || priorNet != prefixNet ||
                    priorTax != Round(prefixNet * source.TaxRate / 100m) ||
                    fact.Discount != Round(fact.Discount) || fact.Net != Round(fact.Net) || fact.Tax != Round(fact.Tax) ||
                    fact.Net != Round(checked(fact.Quantity * source.UnitPrice) - fact.Discount) ||
                    fact.Tax != Round(fact.Net * source.TaxRate / 100m))
                    return Refuse("InvoiceCommercialHistoryUnproven");
            }
            var cumulative = checked(priorQuantity + quantity);
            if (cumulative > source.Ordered) return Refuse("InvoiceCommercialQuantityExceeded");
            var discount = Round(source.Discount * (cumulative / source.Ordered)) - priorDiscount;
            var net = Round(checked(cumulative * source.UnitPrice) - (priorDiscount + discount)) - priorNet;
            var tax = Round((priorNet + net) * source.TaxRate / 100m) - priorTax;
            if (discount < 0 || net < 0 || tax < 0 ||
                net != Round(checked(quantity * source.UnitPrice) - discount) ||
                tax != Round(net * source.TaxRate / 100m))
                return Refuse("InvoiceCommercialRoundingNotRepresentable");
            return new(true, "ValidPartialInvoiceValue", discount, net, tax);
        }
        catch (OverflowException) { return Refuse("InvoiceCommercialArithmeticOverflow"); }
    }
}
