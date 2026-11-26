namespace CrossBuy.ViewModel;

public sealed record ConstructionPurchaseRequestLink(int OrderId, int ProjectId);
public sealed record ConstructionPurchaseRequestLine(string? Description, decimal Quantity, decimal NetAmount);
public sealed record ConstructionPurchaseRequestSplit(string WbsCode, string WbsName, string? WbsNameEn,
    string CostCode, string CostName, string? CostNameEn, decimal Amount);
public sealed record ConstructionPurchaseExceptionOption(int Id, decimal Overrun, string Reason);
public sealed record ConstructionPurchaseRequestHistory(int ApprovalId, DateTime RequestedAt, string Status, string Reason, string? DecisionNote);
public sealed record ConstructionPurchaseRequestPage(int ProjectId, int OrderId, string OrderNumber,
    string ProjectCode, string ProjectName, string? ProjectNameEn, string Status, string CurrencyCode, int CurrencyDecimals,
    decimal NetAmount, decimal TaxAmount, decimal TotalAmount, string ReadinessCode, bool CanSubmit,
    int? AllocationId, string? SourceVersion, Guid RequestId, IReadOnlyList<ConstructionPurchaseRequestLine> Lines,
    IReadOnlyList<ConstructionPurchaseRequestSplit> Splits, IReadOnlyList<ConstructionPurchaseExceptionOption> Exceptions,
    IReadOnlyList<ConstructionPurchaseRequestHistory> History, string Reason = "", int? SelectedExceptionId = null, string? RefusalCode = null);
