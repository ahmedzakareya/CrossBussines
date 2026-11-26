using CrossBuy.BL.Construction;

namespace CrossBuy.ViewModel;

public sealed record ConstructionPurchaseEditorInput
{
    public int BaselineId { get; init; }
    public string? BaselineVersion { get; init; }
    public int VendorId { get; init; }
    public int? WarehouseId { get; init; }
    public string? OrderDate { get; init; }
    public string? ExpectedDate { get; init; }
    public string? Notes { get; init; }
    public string RowsJson { get; init; } = "[]";
    public Guid RequestId { get; init; }
    public string Reason { get; init; } = "";
    public string? SourceVersion { get; init; }
}
public sealed record ConstructionPurchaseEditorLine(int Id, int Number, string? Description, decimal NetAmount);
public sealed record ConstructionPurchaseEditorDimension(int NodeId, int CostId, string NodeCode, string NodeName, string? NodeNameEn,
    string CostCode, string CostName, string? CostNameEn);
public sealed record ConstructionPurchaseEditorPage(int ProjectId, string ProjectCode, string ProjectName, string? ProjectNameEn,
    int? OrderId, string? OrderNumber, string CurrencyCode, int CurrencyDecimals, bool CanSave, string Code,
    ConstructionPurchaseEditorInput Input, IReadOnlyList<ConstructionReferenceOption> Vendors,
    IReadOnlyList<ConstructionReferenceOption> Warehouses, IReadOnlyList<ConstructionReferenceOption> Items,
    IReadOnlyList<ConstructionReferenceOption> Units, IReadOnlyList<ConstructionPurchaseEditorLine> Lines,
    IReadOnlyList<ConstructionPurchaseEditorDimension> Dimensions, string? RefusalCode = null);
