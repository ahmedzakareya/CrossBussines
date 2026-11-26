namespace CrossBuy.BL.Construction;

public sealed record ConstructionCommitmentSplit(int SourceLineId, int WbsNodeId, int CostCodeId, decimal NetDocumentAmount);
public sealed record ConstructionCommitmentDimension(int WbsNodeId, int CostCodeId, bool Active, bool OwnsCost, bool Budgeted);
public sealed record ConstructionCommitmentAllocationCheck(bool Ok, string Code);

// Arithmetic and identity validation only. Inputs must be loaded under the reservation transaction's
// locks by its authorized caller. Neither a successful result nor a report fingerprint grants spending.
public static class ConstructionCommitmentAllocationRules
{
    public static ConstructionCommitmentAllocationCheck Validate(
        ConstructionOrderSource source, int baselineCurrencyId,
        IReadOnlyCollection<ConstructionCommitmentSplit> splits,
        IReadOnlyCollection<ConstructionCommitmentDimension> dimensions)
    {
        if (!(source.Classification == "Open" || source.Classification == "DraftProposal" && source.Status == "Draft") || source.OpenCommitmentNetDocumentAmount is null)
            return new(false, "SourceNotOpenAndReconciled");
        if (baselineCurrencyId <= 0 || source.CurrencyId is not > 0 || string.IsNullOrWhiteSpace(source.CurrencyCode))
            return new(false, "DocumentCurrencyNotProven");
        if (source.CurrencyId != baselineCurrencyId) return new(false, "BudgetCurrencyMismatch");
        if (source.Lines.Count == 0 || source.Lines.Any(x => x.Id <= 0 || x.NetDocumentAmount < 0) ||
            source.Lines.Select(x => x.Id).Distinct().Count() != source.Lines.Count)
            return new(false, "InvalidSourceLines");
        if (splits.Count == 0 || splits.Any(x => x.NetDocumentAmount < 0 || x.WbsNodeId <= 0 || x.CostCodeId <= 0))
            return new(false, "InvalidAllocation");
        // Persistence uses decimal(28,8). Never silently round a proposed split into a different allocation.
        if (splits.Any(x => decimal.Round(x.NetDocumentAmount, 8, MidpointRounding.AwayFromZero) != x.NetDocumentAmount ||
            x.NetDocumentAmount >= 100000000000000000000m))
            return new(false, "AllocationPrecisionInvalid");
        if (splits.GroupBy(x => (x.SourceLineId, x.WbsNodeId, x.CostCodeId)).Any(x => x.Count() != 1))
            return new(false, "DuplicateAllocation");
        var ids = source.Lines.Select(x => x.Id).ToHashSet();
        if (splits.Any(x => !ids.Contains(x.SourceLineId))) return new(false, "ForeignSourceLine");
        foreach (var split in splits)
        {
            var matches = dimensions.Where(x => x.WbsNodeId == split.WbsNodeId && x.CostCodeId == split.CostCodeId).ToArray();
            if (matches.Length != 1 || !matches[0].Active || !matches[0].OwnsCost || !matches[0].Budgeted)
                return new(false, "DimensionNotEligible");
        }
        try
        {
            if (source.Lines.Sum(x => x.NetDocumentAmount) != source.OpenCommitmentNetDocumentAmount ||
                source.OpenCommitmentNetDocumentAmount != source.StoredSubtotal)
                return new(false, "SourceTotalsChanged");
            foreach (var line in source.Lines)
            {
                var allocations = splits.Where(x => x.SourceLineId == line.Id).ToArray();
                if (allocations.Length == 0 || allocations.Sum(x => x.NetDocumentAmount) != line.NetDocumentAmount)
                    return new(false, "LineAllocationIncomplete");
            }
        }
        catch (OverflowException) { return new(false, "AllocationOverflow"); }
        return new(true, "ValidAllocation");
    }
}
