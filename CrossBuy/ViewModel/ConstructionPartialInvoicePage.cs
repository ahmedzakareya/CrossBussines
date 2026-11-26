using System.ComponentModel.DataAnnotations;
using CrossBuy.BL.Construction;

namespace CrossBuy.ViewModel;

public sealed class ConstructionPartialInvoiceInput
{
    public Guid RequestId { get; set; }
    [StringLength(64)] public string? Version { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    [StringLength(1000)] public string Reason { get; set; } = "";
    [MaxLength(500)] public List<ConstructionPartialInvoiceInputRow> Rows { get; set; } = [];
}
public sealed class ConstructionPartialInvoiceInputRow
{
    public int ReceiptLineId { get; set; }
    public decimal Quantity { get; set; }
}
public sealed record ConstructionPartialInvoicePage(int ProjectId, int OrderId,
    ConstructionPartialInvoiceSourcePage Source, ConstructionPartialInvoiceInput Input,
    ConstructionPartialInvoiceResult? Preview = null, string? Error = null, int? IssuedInvoiceId = null);
