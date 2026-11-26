using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Construction;

// A canonical admitted transition. Only independently reconciled receipts and sealed partial clearing may evolve.
internal static class ConstructionRebasedPurchaseWitness
{
    internal static async Task<bool> ProvenAsync(CrossDbContext db, BusinessContext c, int project, int proposalId,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("A rebased admission transaction is required");
        var map = await db.Set<ConstructionReservationExposureRebase>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.RebaseRequestId == proposalId, ct);
        if (map == null) return false;
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == map.ReservationId && x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        if (reservation == null) return false;
        var decision = await db.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == reservation.PurchaseOrderId &&
            x.ReservationId == reservation.ID && x.TargetBudgetVersionId == map.TargetBudgetVersionId, ct);
        if (decision == null || decision.SourceVersion != map.SourceVersion) return false;
        var bridge = await db.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == decision.RebaseRequestId && x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.PurchaseOrderId == reservation.PurchaseOrderId && x.ExposureRebaseId == map.ID, ct);
        if (bridge == null || decision.RequestVersion != bridge.ReviewVersion) return false;
        var request = await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == bridge.ApprovalRequestId && x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.PurchaseOrderId == reservation.PurchaseOrderId && x.AllocationId == reservation.AllocationId, ct);
        if (request == null || request.ExceptionId != null || request.SourceVersion != reservation.SourceVersion ||
            request.CreatedBy != bridge.CreatedBy || request.CreatedBy == decision.CreatedBy) return false;
        var draft = await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == request.DraftId && x.CompanyID == c.CompanyId && x.ProjectId == project &&
            x.PurchaseOrderId == reservation.PurchaseOrderId, ct);
        if (draft == null || draft.CreatedBy == decision.CreatedBy || request.ContentHash != draft.InitialSourceVersion) return false;
        var admission = await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyID == c.CompanyId && x.ProjectId == project && x.PurchaseOrderId == reservation.PurchaseOrderId &&
            x.ApprovalRequestId == request.ID && x.ReservationId == reservation.ID, ct);
        if (admission == null || admission.RequestId != decision.RequestId || admission.RequestHash != decision.RequestHash ||
            admission.CreatedBy != decision.CreatedBy || admission.Reason != decision.Reason ||
            admission.BeforeVersion != request.SourceVersion || admission.ContentHash != request.ContentHash ||
            admission.ApprovedVersion != ConstructionPurchaseApprovalService.ApprovedVersion(request.ContentHash)) return false;
        if (decision.ReviewVersion != ConstructionPurchaseApprovalService.RebasedReviewVersion(bridge.ReviewVersion,
            decision.CreatedBy, request.ApprovalId, reservation.ID, map.TargetBudgetVersionId)) return false;
        var order = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(x => x.ID == reservation.PurchaseOrderId &&
            x.CompanyID == c.CompanyId && x.ProjectId == project, ct);
        var approval = await db.InventoryApprovals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == request.ApprovalId && x.CompanyID == c.CompanyId, ct);
        return order?.Status is ("Approved" or "Received") && approval?.Status == "Approved" &&
            approval.DocType == ConstructionPurchaseApprovalService.DocumentType && approval.DecidedAt != null &&
            approval.DecidedByEmployeeId == decision.CreatedBy && approval.RequestedByEmployeeId == request.CreatedBy &&
            approval.PayloadJson == request.PayloadJson && approval.Amount == order.GrandTotal &&
            approval.ResultDocNo == order.OrderNo && approval.DecisionNote == decision.Reason;
    }
}
