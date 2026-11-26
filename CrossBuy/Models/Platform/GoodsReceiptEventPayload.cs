namespace CrossBuy.Models.Platform;

// Summary only. Functional-currency valuation is explicitly distinct from the supplier's document currency.
public sealed class GoodsReceiptEventPayload
{
    public string? ReceiptNumber { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal TotalCostBase { get; init; }
    public int FunctionalCurrencyId { get; init; }
}
