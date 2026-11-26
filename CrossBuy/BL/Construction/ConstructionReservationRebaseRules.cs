namespace CrossBuy.BL.Construction;

public sealed record ConstructionRebaseDimension(int SourceLineId, int WbsNodeId, int CostCodeId, string Category, decimal Amount);
public sealed record ConstructionRebaseCheck(bool Ok, string Code);

// Baseline-only planning, never spending authorization. The future transactional writer must
// prove scope, approved baseline lineage, source freshness, policy and independent approval.
// Original reservation and realized financial witnesses must never be rewritten by a rebase.
public static class ConstructionReservationRebaseRules
{
    public static ConstructionRebaseCheck Validate(int originalCurrency, int targetCurrency, int decimalPlaces,
        decimal reservedAmount, IReadOnlyCollection<ConstructionRebaseDimension> original,
        IReadOnlyCollection<ConstructionRebaseDimension> replacement)
    {
        if (originalCurrency <= 0 || targetCurrency != originalCurrency)
            return new(false, "BudgetCurrencyMismatch");
        if (decimalPlaces is < 0 or > 8) return new(false, "InvalidCurrencyPrecision");
        if (reservedAmount < 0 || decimal.Round(reservedAmount, decimalPlaces, MidpointRounding.AwayFromZero) != reservedAmount)
            return new(false, "InvalidReservationAmount");
        if (original == null || replacement == null || original.Count == 0 || original.Count > 500 || replacement.Count > 500)
            return new(false, "ReservationCoverageRequired");
        static bool Invalid(ConstructionRebaseDimension x) => x == null || x.SourceLineId <= 0 || x.WbsNodeId <= 0 || x.CostCodeId <= 0 ||
            string.IsNullOrWhiteSpace(x.Category) || x.Amount < 0;
        if (original.Any(Invalid) || replacement.Any(Invalid)) return new(false, "InvalidBudgetDimension");
        if (original.Concat(replacement).Any(x => decimal.Round(x.Amount, decimalPlaces, MidpointRounding.AwayFromZero) != x.Amount))
            return new(false, "InvalidCurrencyPrecision");
        try
        {
            // Aggregate splits before comparison: row order and split count are not financial identity.
            var before = original.GroupBy(x => (x.SourceLineId, x.WbsNodeId, x.CostCodeId, x.Category))
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));
            var after = replacement.GroupBy(x => (x.SourceLineId, x.WbsNodeId, x.CostCodeId, x.Category))
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));
            if (before.Values.Sum() != reservedAmount) return new(false, "ReservationCoverageRequired");
            if (before.Count != after.Count || before.Any(x => !after.TryGetValue(x.Key, out var value) || value != x.Value))
                return new(false, "ReservationAmendmentRequired");
            return new(true, "BaselineOnlyRebase");
        }
        catch (OverflowException) { return new(false, "AmountOverflow"); }
    }
}

