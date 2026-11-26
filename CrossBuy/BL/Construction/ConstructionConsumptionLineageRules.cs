namespace CrossBuy.BL.Construction;

// Necessary identity/arithmetic checks, not database provenance, valuation policy or spending authority.
// A future owner must load the COMPLETE prior+proposed graph under locks and prove each stock/GL source,
// immutable admission, receipt/invoice coverage and analytical dimension before persisting a mapping.
// This kernel is deliberately NOT connected to budget exposure or reservation release.
public sealed record ConstructionConsumptionReceipt(int CompanyId, int ProjectId, int OrderId, int OrderLineId,
    int ReceiptId, int ReceiptLineId, int MovementId, int CurrencyId, int CurrencyDecimals,
    int ItemId, int WarehouseId, decimal QuantityBase, decimal NetBaseCost);
public sealed record ConstructionConsumptionIssue(int CompanyId, int ProjectId, int MovementId,
    int JournalEntryId, int ExpenseJournalLineId, int CurrencyId, int CurrencyDecimals,
    int ItemId, int WarehouseId, decimal QuantityBase, decimal NetBaseCost);
public sealed record ConstructionConsumptionLink(int CompanyId, int ProjectId, int OrderId, int OrderLineId,
    int ReceiptLineId, int ReceiptMovementId, int IssueMovementId, int IssueJournalEntryId, int ExpenseJournalLineId);
public sealed record ConstructionConsumptionCheck(bool Ok, string Code, decimal? CandidateNetBaseCost = null);

public static class ConstructionConsumptionLineageRules
{
    // Initial contract: one whole posted issue maps to one receipt line. No inferred FIFO/WAC attribution,
    // mixed-receipt issue splits, currencies, reversals, price variances or rounded-away remainders.
    public static ConstructionConsumptionCheck Validate(int companyId, int projectId, int currencyId,
        IReadOnlyList<ConstructionConsumptionReceipt> receipts, IReadOnlyList<ConstructionConsumptionIssue> issues,
        IReadOnlyList<ConstructionConsumptionLink> links)
    {
        ConstructionConsumptionCheck Refuse(string code) => new(false, code);
        if (companyId <= 0 || projectId <= 0 || currencyId <= 0) return Refuse("InvalidSourceIdentity");
        if (receipts.Count == 0 || issues.Count == 0 || links.Count == 0) return Refuse("ConsumptionLineageRequired");
        if (receipts.Any(x => x.CompanyId != companyId || x.ProjectId != projectId || x.CurrencyId != currencyId) ||
            issues.Any(x => x.CompanyId != companyId || x.ProjectId != projectId || x.CurrencyId != currencyId) ||
            links.Any(x => x.CompanyId != companyId || x.ProjectId != projectId)) return Refuse("SourceScopeMismatch");
        if (receipts.Any(x => x.OrderId <= 0 || x.OrderLineId <= 0 || x.ReceiptId <= 0 || x.ReceiptLineId <= 0 ||
                x.MovementId <= 0 || x.ItemId <= 0 || x.WarehouseId <= 0) ||
            issues.Any(x => x.MovementId <= 0 || x.JournalEntryId <= 0 || x.ExpenseJournalLineId <= 0 || x.ItemId <= 0 || x.WarehouseId <= 0))
            return Refuse("InvalidSourceIdentity");
        if (receipts.Select(x => x.ReceiptLineId).Distinct().Count() != receipts.Count ||
            receipts.Select(x => x.MovementId).Distinct().Count() != receipts.Count ||
            issues.Select(x => x.MovementId).Distinct().Count() != issues.Count ||
            issues.Select(x => x.JournalEntryId).Distinct().Count() != issues.Count ||
            issues.Select(x => x.ExpenseJournalLineId).Distinct().Count() != issues.Count ||
            receipts.Select(x => x.MovementId).Intersect(issues.Select(x => x.MovementId)).Any() ||
            links.Select(x => x.IssueMovementId).Distinct().Count() != links.Count)
            return Refuse("AmbiguousConsumptionLineage");
        if (receipts.Any(x => x.QuantityBase <= 0 || x.NetBaseCost <= 0 || x.CurrencyDecimals is < 0 or > 8 ||
                decimal.Round(x.NetBaseCost, x.CurrencyDecimals, MidpointRounding.AwayFromZero) != x.NetBaseCost) ||
            issues.Any(x => x.QuantityBase <= 0 || x.NetBaseCost <= 0 || x.CurrencyDecimals is < 0 or > 8 ||
                decimal.Round(x.NetBaseCost, x.CurrencyDecimals, MidpointRounding.AwayFromZero) != x.NetBaseCost))
            return Refuse("InvalidConsumptionAmounts");
        if (!issues.Select(x => x.MovementId).ToHashSet().SetEquals(links.Select(x => x.IssueMovementId)))
            return Refuse("IncompleteConsumptionLineage");
        var used = receipts.ToDictionary(x => x.ReceiptLineId, _ => (Quantity: 0m, Cost: 0m));
        var receiptById = receipts.ToDictionary(x => x.ReceiptLineId);
        var issueById = issues.ToDictionary(x => x.MovementId);
        try
        {
            foreach (var link in links)
            {
                receiptById.TryGetValue(link.ReceiptLineId, out var receipt);
                issueById.TryGetValue(link.IssueMovementId, out var issue);
                if (receipt == null || issue == null || receipt.OrderId != link.OrderId || receipt.OrderLineId != link.OrderLineId ||
                    receipt.MovementId != link.ReceiptMovementId || issue.JournalEntryId != link.IssueJournalEntryId ||
                    issue.ExpenseJournalLineId != link.ExpenseJournalLineId || receipt.MovementId == issue.MovementId)
                    return Refuse("ConsumptionSourceIdentityMismatch");
                if (receipt.ItemId != issue.ItemId || receipt.WarehouseId != issue.WarehouseId || receipt.CurrencyDecimals != issue.CurrencyDecimals)
                    return Refuse("ConsumptionSourceBasisMismatch");
                // Cross multiplication preserves exact fractional values; no artificial currency/unit rounding.
                if (checked(issue.NetBaseCost * receipt.QuantityBase) != checked(receipt.NetBaseCost * issue.QuantityBase))
                    return Refuse("ConsumptionVarianceCoverageRequired");
                var before = used[receipt.ReceiptLineId];
                var after = (Quantity: checked(before.Quantity + issue.QuantityBase), Cost: checked(before.Cost + issue.NetBaseCost));
                if (after.Quantity > receipt.QuantityBase || after.Cost > receipt.NetBaseCost) return Refuse("ReceiptConsumptionExceeded");
                used[receipt.ReceiptLineId] = after;
            }
            return new(true, "ValidConsumptionIdentityAndArithmeticOnly", checked(used.Values.Sum(x => x.Cost)));
        }
        catch (OverflowException) { return Refuse("ConsumptionAmountOverflow"); }
    }
}
