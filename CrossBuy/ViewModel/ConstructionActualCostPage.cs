using CrossBuy.BL.Construction;
namespace CrossBuy.ViewModel;
public sealed class ConstructionActualInput
{
    public Guid RequestId { get; set; }
    public string? Version { get; set; }
    public string Reason { get; set; } = "";
    public List<ConstructionActualInputRow> Rows { get; set; } = [];
}
public sealed class ConstructionActualInputRow
{
    public int WbsNodeId { get; set; }
    public int CostCodeId { get; set; }
    public decimal? Amount { get; set; }
}
public sealed record ConstructionActualSourceRow(int LineId, int EntryId, DateTime Date, decimal Net, string? Currency, bool Allocated);
public sealed record ConstructionActualCostPage(int ProjectId, string ProjectCode, string ProjectName, string? ProjectNameEn,
    IReadOnlyList<ConstructionActualSourceRow> Sources, bool Truncated, int? LineId, bool AlreadyAllocated,
    ConstructionActualPreview? Source, IReadOnlyList<ConstructionReferenceOption> Nodes, IReadOnlyList<ConstructionReferenceOption> CostCodes,
    ConstructionActualInput Input, bool Confirmed = false, string? Error = null, string? SourceCurrency = null);
