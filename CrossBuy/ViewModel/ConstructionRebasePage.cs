using System.ComponentModel.DataAnnotations;
using CrossBuy.BL.Construction;

namespace CrossBuy.ViewModel;

public enum ConstructionRebaseOperation { Request, Approve, Reject, Activate, PurchaseRequest }
public sealed class ConstructionRebaseInput
{
    public Guid RequestId { get; set; }
    public int ProposalId { get; set; }
    public int TargetId { get; set; }
    [Required, StringLength(64, MinimumLength = 64)] public string Version { get; set; } = "";
    [Required, StringLength(1000)] public string Reason { get; set; } = "";
}
public sealed record ConstructionRebaseOrder(int OrderId, string? Number, string Status);
public sealed record ConstructionRebaseHistory(int Id, string Reason, int RequestedBy, DateTime RequestedAt, string Outcome);
public sealed record ConstructionRebaseSelection(int OrderId, string? Number, string Status, int ReservationId,
    int? TargetId, int? ProposalId, string? ProposalVersion, bool CanDecide, string Outcome, bool Activated,
    ConstructionRebaseRequestResult? ProposalPreview, ConstructionRebaseRequestResult? SourcePreview,
    ConstructionRebasedAdmissionReview? PurchasePreview, int? ApprovalId, string? ApprovalStatus, bool CanReviewPurchase,
    IReadOnlyList<ConstructionRebaseHistory> History, int OriginalVersionNo, int? TargetVersionNo, decimal NetAmount, string? CurrencyCode);
public sealed record ConstructionRebasePage(int ProjectId, string ProjectCode, string ProjectName, string? ProjectNameEn,
    IReadOnlyList<ConstructionRebaseOrder> Orders, bool Truncated, ConstructionRebaseSelection? Selected,
    string? Error = null, ConstructionRebaseOperation? FailedOperation = null, ConstructionRebaseInput? RejectedInput = null);
