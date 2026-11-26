using CrossBuy.BL.Construction;
namespace CrossBuy.ViewModel;
public sealed class ConstructionConsumptionInput
{
 public int ReceiptLineId { get; set; }
 public int IssueMovementId { get; set; }
 public Guid RequestId { get; set; }
 public string? Version { get; set; }
 public string Reason { get; set; } = "";
}
public sealed record ConstructionConsumptionRow(int Id,int ReceiptLineId,int IssueMovementId,decimal Quantity,decimal Cost,string? Currency,DateTime Date);
public sealed record ConstructionConsumptionSource(int Id,int DocumentId,string ItemName,string? ItemNameEn,string WarehouseName,string? WarehouseNameEn,decimal Quantity,decimal Cost,string? Currency);
public sealed record ConstructionConsumptionSources(IReadOnlyList<ConstructionConsumptionSource> Receipts,IReadOnlyList<ConstructionConsumptionSource> Issues,bool ReceiptsTruncated,bool IssuesTruncated);
public sealed record ConstructionConsumptionPage(int ProjectId,string Code,string Name,string? NameEn,
 IReadOnlyList<ConstructionConsumptionRow> History,ConstructionConsumptionInput Input,
 ConstructionConsumptionPreview? Preview=null,string? Error=null,ConstructionConsumptionSources? Sources=null);
