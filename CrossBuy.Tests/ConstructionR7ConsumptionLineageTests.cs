using CrossBuy.BL.Construction;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ConsumptionLineageTests
{
    internal static ConstructionConsumptionReceipt Receipt => new(1,2,3,4,5,6,7,8,3,9,10,1m,10m);
    internal static ConstructionConsumptionIssue Issue => new(1,2,11,12,13,8,3,9,10,.4m,4m);
    internal static ConstructionConsumptionLink Link => new(1,2,3,4,6,7,11,12,13);
    private static ConstructionConsumptionCheck Check(ConstructionConsumptionReceipt? receipt=null,
        ConstructionConsumptionIssue? issue=null,ConstructionConsumptionLink? link=null) =>
        ConstructionConsumptionLineageRules.Validate(1,2,8,new[] { receipt ?? Receipt },new[] { issue ?? Issue },new[] { link ?? Link });

    [Theory][InlineData(0)][InlineData(3)][InlineData(8)]
    public void Exact_partial_cost_uses_captured_currency_precision_without_granting_admission(int decimals)
    {
        var result=Check(Receipt with { CurrencyDecimals=decimals },Issue with { CurrencyDecimals=decimals });
        Assert.True(result.Ok,result.Code);Assert.Equal("ValidConsumptionIdentityAndArithmeticOnly",result.Code);Assert.Equal(4m,result.CandidateNetBaseCost);
    }

    [Theory]
    [InlineData(3,true)][InlineData(2,false)]
    public void Fractional_currency_values_are_never_silently_rounded_into_a_match(int decimals,bool accepted)
    {
        var result=Check(Receipt with { CurrencyDecimals=decimals,NetBaseCost=10.125m },
            Issue with { CurrencyDecimals=decimals,QuantityBase=.2m,NetBaseCost=2.025m });
        Assert.Equal(accepted,result.Ok);
        if(accepted) Assert.Equal(2.025m,result.CandidateNetBaseCost);else Assert.Null(result.CandidateNetBaseCost);
    }

    [Theory]
    [InlineData("company","SourceScopeMismatch")][InlineData("project","SourceScopeMismatch")][InlineData("currency","SourceScopeMismatch")]
    [InlineData("item","ConsumptionSourceBasisMismatch")][InlineData("warehouse","ConsumptionSourceBasisMismatch")]
    [InlineData("precision","ConsumptionSourceBasisMismatch")][InlineData("zero","InvalidConsumptionAmounts")]
    [InlineData("rounding","InvalidConsumptionAmounts")][InlineData("negative","InvalidConsumptionAmounts")]
    [InlineData("issue-journal","ConsumptionSourceIdentityMismatch")][InlineData("receipt-movement","ConsumptionSourceIdentityMismatch")]
    [InlineData("order-line","ConsumptionSourceIdentityMismatch")][InlineData("expense-line","ConsumptionSourceIdentityMismatch")]
    [InlineData("variance","ConsumptionVarianceCoverageRequired")][InlineData("over","ReceiptConsumptionExceeded")]
    public void Equal_totals_never_replace_exact_owned_source_identities(string change,string expected)
    {
        var r=Receipt;var i=Issue;var l=Link;
        if(change=="company") i=i with { CompanyId=99 };
        if(change=="project") l=l with { ProjectId=99 };
        if(change=="currency") i=i with { CurrencyId=99 };
        if(change=="item") i=i with { ItemId=99 };
        if(change=="warehouse") i=i with { WarehouseId=99 };
        if(change=="precision") i=i with { CurrencyDecimals=2 };
        if(change=="zero") i=i with { QuantityBase=0 };
        if(change=="rounding") i=i with { NetBaseCost=4.0001m };
        if(change=="negative") i=i with { NetBaseCost=-4m };
        if(change=="issue-journal") l=l with { IssueJournalEntryId=99 };
        if(change=="receipt-movement") l=l with { ReceiptMovementId=99 };
        if(change=="order-line") l=l with { OrderLineId=99 };
        if(change=="expense-line") l=l with { ExpenseJournalLineId=99 };
        if(change=="variance") i=i with { NetBaseCost=6m };
        if(change=="over") i=i with { QuantityBase=1.1m,NetBaseCost=11m };
        var result=Check(r,i,l);Assert.False(result.Ok);Assert.Equal(expected,result.Code);Assert.Null(result.CandidateNetBaseCost);
    }

    [Fact]
    public void Complete_prior_and_proposed_graph_prevents_consuming_the_same_receipt_twice()
    {
        var second=Issue with { MovementId=21,JournalEntryId=22,ExpenseJournalLineId=23,QuantityBase=.6m,NetBaseCost=6m };
        var link=Link with { IssueMovementId=21,IssueJournalEntryId=22,ExpenseJournalLineId=23 };
        var first=ConstructionConsumptionLineageRules.Validate(1,2,8,new[] { Receipt },new[] { Issue,second },new[] { Link,link });
        Assert.True(first.Ok,first.Code);Assert.Equal(10m,first.CandidateNetBaseCost);
        var over=ConstructionConsumptionLineageRules.Validate(1,2,8,new[] { Receipt },new[] { Issue,second with { QuantityBase=.7m,NetBaseCost=7m } },new[] { Link,link });
        Assert.False(over.Ok);Assert.Equal("ReceiptConsumptionExceeded",over.Code);Assert.Null(over.CandidateNetBaseCost);
    }

    [Theory][InlineData("duplicate-issue")][InlineData("duplicate-link")][InlineData("duplicate-receipt")][InlineData("missing-link")]
    public void Missing_or_reused_facts_do_not_become_coverage(string kind)
    {
        var receipts=kind=="duplicate-receipt" ? new[] { Receipt,Receipt } : new[] { Receipt };
        var issues=kind=="duplicate-issue" ? new[] { Issue,Issue } : new[] { Issue };
        var links=kind=="duplicate-link" ? new[] { Link,Link } : kind=="missing-link" ? Array.Empty<ConstructionConsumptionLink>() : new[] { Link };
        var result=ConstructionConsumptionLineageRules.Validate(1,2,8,receipts,issues,links);Assert.False(result.Ok);Assert.Null(result.CandidateNetBaseCost);
    }

    [Fact]
    public void Intermediate_overflow_returns_no_candidate_instead_of_wrapping_or_rounding()
    {
        var result=Check(Receipt with { QuantityBase=decimal.MaxValue,NetBaseCost=decimal.MaxValue },Issue with { QuantityBase=decimal.MaxValue,NetBaseCost=decimal.MaxValue });
        Assert.False(result.Ok);Assert.Equal("ConsumptionAmountOverflow",result.Code);Assert.Null(result.CandidateNetBaseCost);
    }
}
