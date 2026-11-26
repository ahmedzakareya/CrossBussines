using CrossBuy.BL.Construction;

namespace CrossBuy.ViewModel;

public sealed record ConstructionExceptionForm
{
    public int AllocationId { get; init; }
    public string? SourceVersion { get; init; }
    public string? AdmissionVersion { get; init; }
    public int ExceptionId { get; init; }
    public Guid RequestId { get; init; }
    public string Reason { get; init; } = "";
}
public sealed record ConstructionExceptionOrder(int Id, string? Number);
public sealed record ConstructionExceptionHistory(int Id, int OrderId, string? OrderNumber, decimal Amount,
    decimal Overrun, string Currency, string Reason, DateTime CreatedAt, int RequesterId,
    string State, string AdmissionVersion, bool CanApprove, bool CanReject, string? DecisionReason);
public sealed record ConstructionBudgetExceptionPage(int ProjectId, string ProjectCode, string? ProjectName,
    string? ProjectNameEn, int? OrderId, IReadOnlyList<ConstructionExceptionOrder> Orders,
    ConstructionBudgetExceptionResult? Preview, int? AllocationId, string? SourceVersion, string? Currency,
    IReadOnlyList<ConstructionExceptionHistory> History, string? Refusal = null,
    ConstructionExceptionForm? RejectedForm = null, string? RejectedAction = null);
