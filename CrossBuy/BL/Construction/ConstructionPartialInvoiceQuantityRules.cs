namespace CrossBuy.BL.Construction;

public sealed record ConstructionInvoiceScope(int CompanyId, int ProjectId, int OrderId);
public sealed record ConstructionInvoiceOrderQuantity(int LineId, int ItemId, int? UnitId, decimal Ordered);
public sealed record ConstructionInvoiceReceiptQuantity(ConstructionInvoiceScope Scope, int ReceiptId, int ReceiptLineId,
    int OrderLineId, int ItemId, int? UnitId, decimal Received, string Status);
public sealed record ConstructionInvoiceBilledQuantity(ConstructionInvoiceScope Scope, int FactId, Guid RequestId,
    int InvoiceId, int InvoiceLineId, int ReceiptLineId, decimal Billed, string Status);
public sealed record ConstructionInvoiceQuantityRequest(int ReceiptLineId, decimal Quantity);
public sealed record ConstructionInvoiceLineQuantity(int OrderLineId, decimal Ordered, decimal Received,
    decimal PreviouslyBilled, decimal Requested, decimal UnbilledAfterRequest);
public sealed record ConstructionInvoiceReceiptBalance(int ReceiptLineId, decimal Received, decimal PreviouslyBilled,
    decimal Requested, decimal UnbilledAfterRequest);
public sealed record ConstructionInvoiceQuantityCheck(bool Ok, string Code,
    IReadOnlyList<ConstructionInvoiceLineQuantity>? Lines = null,
    IReadOnlyList<ConstructionInvoiceReceiptBalance>? Receipts = null);

// Quantity planning only, not authorization, valuation, posting, persistence or a replay acknowledgement.
// A future writer must load and validate the complete source graph under its owner transaction/locks,
// verify immutable invoice/receipt/GL provenance, and handle request replay before calling these rules.
// Legacy full-invoice relations must never be mixed into a new partial-invoice request.
public static class ConstructionPartialInvoiceQuantityRules
{
    public static ConstructionInvoiceQuantityCheck Validate(ConstructionInvoiceScope scope, string orderStatus,
        bool hasLegacyInvoice, Guid requestId, int quantityPrecision, int quantityScale,
        IReadOnlyCollection<ConstructionInvoiceOrderQuantity> orderLines,
        IReadOnlyCollection<ConstructionInvoiceReceiptQuantity> receiptLines,
        IReadOnlyCollection<ConstructionInvoiceBilledQuantity> history,
        IReadOnlyCollection<ConstructionInvoiceQuantityRequest> requested)
    {
        ConstructionInvoiceQuantityCheck Refuse(string code) => new(false, code);
        if (scope.CompanyId <= 0 || scope.ProjectId <= 0 || scope.OrderId <= 0) return Refuse("InvoiceScopeUnresolved");
        if (orderStatus is not ("Approved" or "Received") || hasLegacyInvoice) return Refuse("PartialInvoiceSourceNotOpen");
        if (requestId == Guid.Empty) return Refuse("InvoiceRequestIdentityRequired");
        if (quantityPrecision is < 1 or > 28 || quantityScale < 0 || quantityScale > quantityPrecision)
            return Refuse("InvoiceQuantityPrecisionUnproven");
        decimal limit = 1;
        for (var i = 0; i < quantityPrecision - quantityScale; i++) limit *= 10;
        bool Fits(decimal value) => value > 0 && value < limit &&
            decimal.Round(value, quantityScale, MidpointRounding.AwayFromZero) == value;
        if (orderLines.Count == 0 || orderLines.Any(x => x.LineId <= 0 || x.ItemId <= 0 || x.UnitId is <= 0 || !Fits(x.Ordered)) ||
            orderLines.Select(x => x.LineId).Distinct().Count() != orderLines.Count)
            return Refuse("InvoiceOrderQuantitiesInvalid");
        if (receiptLines.Count == 0 || receiptLines.Any(x => x.Scope != scope || x.ReceiptId <= 0 || x.ReceiptLineId <= 0 ||
            x.Status != "Posted" || !Fits(x.Received)) ||
            receiptLines.Select(x => x.ReceiptLineId).Distinct().Count() != receiptLines.Count)
            return Refuse("InvoiceReceiptQuantitiesUnproven");
        var orders = orderLines.ToDictionary(x => x.LineId);
        var receipts = receiptLines.ToDictionary(x => x.ReceiptLineId);
        if (receiptLines.Any(x => !orders.TryGetValue(x.OrderLineId, out var line) ||
            line.ItemId != x.ItemId || line.UnitId != x.UnitId)) return Refuse("InvoiceReceiptSourceMismatch");
        if (history.Any(x => x.Scope != scope || x.FactId <= 0 || x.RequestId == Guid.Empty || x.InvoiceId <= 0 ||
            x.InvoiceLineId <= 0 || x.Status != "Posted" || !Fits(x.Billed) || !receipts.ContainsKey(x.ReceiptLineId)) ||
            history.Select(x => x.FactId).Distinct().Count() != history.Count ||
            history.GroupBy(x => (x.InvoiceLineId, x.ReceiptLineId)).Any(x => x.Count() != 1) ||
            history.GroupBy(x => x.RequestId).Any(x => x.Select(y => y.InvoiceId).Distinct().Count() != 1) ||
            history.GroupBy(x => x.InvoiceId).Any(x => x.Select(y => y.RequestId).Distinct().Count() != 1))
            return Refuse("InvoiceQuantityHistoryUnproven");
        if (history.GroupBy(x => x.InvoiceLineId).Any(x => x.Select(y => y.InvoiceId).Distinct().Count() != 1 ||
            x.Select(y => receipts[y.ReceiptLineId].OrderLineId).Distinct().Count() != 1))
            return Refuse("InvoiceLineSourceAmbiguous");
        if (requested.Count == 0 || requested.Any(x => !Fits(x.Quantity) || !receipts.ContainsKey(x.ReceiptLineId)) ||
            requested.Select(x => x.ReceiptLineId).Distinct().Count() != requested.Count)
            return Refuse("InvoiceQuantityRequestInvalid");
        // Only a persisted writer acknowledgement with a matching request digest may authorize replay.
        if (history.Any(x => x.RequestId == requestId)) return Refuse("InvoiceRequestAlreadyRecorded");
        try
        {
            var balances = new List<ConstructionInvoiceReceiptBalance>();
            foreach (var receipt in receiptLines.OrderBy(x => x.ReceiptLineId))
            {
                var billed = history.Where(x => x.ReceiptLineId == receipt.ReceiptLineId).Sum(x => x.Billed);
                if (billed > receipt.Received) return Refuse("InvoiceHistoryExceedsReceipt");
                var take = requested.Where(x => x.ReceiptLineId == receipt.ReceiptLineId).Sum(x => x.Quantity);
                if (take > receipt.Received - billed) return Refuse("InvoiceRequestExceedsReceipt");
                balances.Add(new(receipt.ReceiptLineId, receipt.Received, billed, take, receipt.Received - billed - take));
            }
            var lines = new List<ConstructionInvoiceLineQuantity>();
            foreach (var line in orderLines.OrderBy(x => x.LineId))
            {
                var own = balances.Where(x => receipts[x.ReceiptLineId].OrderLineId == line.LineId).ToArray();
                var received = own.Sum(x => x.Received);
                var billed = own.Sum(x => x.PreviouslyBilled);
                var take = own.Sum(x => x.Requested);
                if (received > line.Ordered) return Refuse("InvoiceReceiptsExceedOrder");
                if (billed > line.Ordered || take > line.Ordered - billed) return Refuse("InvoiceQuantityExceedsOrder");
                lines.Add(new(line.LineId, line.Ordered, received, billed, take, received - billed - take));
            }
            return new(true, "ValidPartialInvoiceQuantities", lines.AsReadOnly(), balances.AsReadOnly());
        }
        catch (OverflowException) { return Refuse("InvoiceQuantityOverflow"); }
    }
}
