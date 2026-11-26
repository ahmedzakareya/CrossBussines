namespace CrossBuy.BL.Construction;

// Calculation contract only. The future storage service owns authorization, locked reads,
// company-policy/source witnesses, immutable snapshots and audit; this grants no authority.
public sealed record ConstructionForecastInput(int WbsNodeId, int CostCodeId,
    decimal Budget, decimal Actual, decimal Commitment, decimal RemainingCostIncludingCommitments);
public sealed record ConstructionForecastLine(int WbsNodeId, int CostCodeId,
    decimal Budget, decimal Actual, decimal Commitment, decimal RemainingCostIncludingCommitments,
    decimal EstimateAtCompletion, decimal BudgetVariance, bool BelowOutstandingCommitments);
public sealed record ConstructionForecastCalculation(bool Ok, string Code,
    IReadOnlyList<ConstructionForecastLine>? Lines = null, decimal? EstimateAtCompletion = null,
    decimal? BudgetVariance = null, bool HasCommitmentWarning = false);

public static class ConstructionCostForecastRules
{
    public const string Method = "ManualRemainingIncludingCommitmentsV1";
    private const decimal Limit = 100000000000000000000m; // decimal(28,8) storage boundary

    public static ConstructionForecastCalculation Evaluate(string? companyMode, string? method,
        bool completeCoverage, int currencyDecimals, IReadOnlyList<ConstructionForecastInput>? inputs)
    {
        // No default mode, inferred estimate, partial-coverage zero or implicit currency conversion.
        if (companyMode == "Disabled") return new(false, "ForecastDisabled");
        if (companyMode != "ManualAuthorOwned") return new(false, "ForecastPolicyNotConfigured");
        if (method != Method) return new(false, "ForecastMethodRequired");
        if (!completeCoverage) return new(false, "ForecastCoverageRequired");
        if (currencyDecimals is < 0 or > 8) return new(false, "InvalidCurrencyPrecision");
        if (inputs == null || inputs.Count == 0) return new(false, "ForecastDimensionsRequired");
        var dimensions = new HashSet<(int, int)>();
        var result = new List<ConstructionForecastLine>(inputs.Count);
        decimal totalBudget = 0, totalActual = 0, totalCommitment = 0, totalRemaining = 0;
        try
        {
            foreach (var row in inputs)
            {
                if (row == null || row.WbsNodeId <= 0 || row.CostCodeId <= 0 ||
                    !dimensions.Add((row.WbsNodeId, row.CostCodeId)))
                    return new(false, "ForecastDimensionsRequired");
                if (row.Budget < 0 || row.Commitment < 0 || row.RemainingCostIncludingCommitments < 0)
                    return new(false, "InvalidForecastAmount");
                // Net actual costs may include credits. Bounds apply symmetrically.
                foreach (var amount in new[] { row.Budget, row.Actual, row.Commitment, row.RemainingCostIncludingCommitments })
                {
                    if (!InRange(amount)) return new(false, "AmountOverflow");
                    if (decimal.Round(amount, currencyDecimals, MidpointRounding.AwayFromZero) != amount)
                        return new(false, "InvalidCurrencyPrecision");
                }
                var eac = checked(row.Actual + row.RemainingCostIncludingCommitments);
                var variance = checked(row.Budget - eac); // positive = forecast below budget
                if (!InRange(eac) || !InRange(variance)) return new(false, "AmountOverflow");
                totalBudget = checked(totalBudget + row.Budget);
                totalActual = checked(totalActual + row.Actual);
                totalCommitment = checked(totalCommitment + row.Commitment);
                totalRemaining = checked(totalRemaining + row.RemainingCostIncludingCommitments);
                // A shortfall is an explicit diagnostic, not an invented company approval policy.
                result.Add(new(row.WbsNodeId, row.CostCodeId, row.Budget, row.Actual, row.Commitment,
                    row.RemainingCostIncludingCommitments, eac, variance,
                    row.RemainingCostIncludingCommitments < row.Commitment));
            }
            var total = checked(totalActual + totalRemaining);
            var delta = checked(totalBudget - total);
            if (new[] { totalBudget, totalActual, totalCommitment, totalRemaining, total, delta }.Any(x => !InRange(x)))
                return new(false, "AmountOverflow");
            return new(true, "Ready", result.AsReadOnly(), total, delta, result.Any(x => x.BelowOutstandingCommitments));
        }
        catch (OverflowException) { return new(false, "AmountOverflow"); }
    }
    private static bool InRange(decimal value) => value > -Limit && value < Limit;
}
