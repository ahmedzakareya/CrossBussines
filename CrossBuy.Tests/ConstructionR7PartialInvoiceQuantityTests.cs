using CrossBuy.BL.Construction;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PartialInvoiceQuantityTests
{
    private static readonly ConstructionInvoiceScope Scope = new(1, 10, 20);
    private static readonly Guid Prior = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Next = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static ConstructionInvoiceOrderQuantity[] Orders() => [new(11, 7, 3, 1m)];
    private static ConstructionInvoiceReceiptQuantity[] Receipts() =>
        [new(Scope, 31, 21, 11, 7, 3, .4m, "Posted"), new(Scope, 32, 22, 11, 7, 3, .6m, "Posted")];
    private static ConstructionInvoiceBilledQuantity[] History() => [new(Scope, 1, Prior, 40, 50, 21, .2m, "Posted")];
    private static ConstructionInvoiceQuantityRequest[] Request() => [new(21, .2m), new(22, .3m)];
    private static ConstructionInvoiceQuantityCheck Check(ConstructionInvoiceOrderQuantity[]? orders = null,
        ConstructionInvoiceReceiptQuantity[]? receipts = null, ConstructionInvoiceBilledQuantity[]? history = null,
        ConstructionInvoiceQuantityRequest[]? request = null, Guid? id = null) =>
        ConstructionPartialInvoiceQuantityRules.Validate(Scope, "Received", false, id ?? Next, 18, 4,
            orders ?? Orders(), receipts ?? Receipts(), history ?? History(), request ?? Request());

    [Fact]
    public void Split_receipts_and_existing_partial_invoice_leave_only_the_unbilled_received_quantity()
    {
        var result = Check(); Assert.True(result.Ok, result.Code);
        var line = Assert.Single(result.Lines!);
        Assert.Equal(.2m, line.PreviouslyBilled); Assert.Equal(.5m, line.Requested); Assert.Equal(.3m, line.UnbilledAfterRequest);
        Assert.Equal(0m, result.Receipts!.Single(x => x.ReceiptLineId == 21).UnbilledAfterRequest);
        Assert.Equal(.3m, result.Receipts!.Single(x => x.ReceiptLineId == 22).UnbilledAfterRequest);
    }

    [Fact]
    public void Later_receipt_adds_capacity_without_making_the_first_billed_quantity_available_again()
    {
        var first = Check(receipts: [Receipts()[0]], request: [new(21, .2m)]);
        Assert.True(first.Ok); Assert.Equal(0m, Assert.Single(first.Lines!).UnbilledAfterRequest);
        var history = History().Append(new(Scope, 2, Next, 41, 51, 21, .2m, "Posted")).ToArray();
        var second = Check(history: history, request: [new(22, .6m)], id: Guid.NewGuid());
        Assert.True(second.Ok, second.Code); Assert.Equal(0m, Assert.Single(second.Lines!).UnbilledAfterRequest);
        var repeatQuantity = Check(history: history, request: [new(21, .0001m)], id: Guid.NewGuid());
        Refused(repeatQuantity, "InvoiceRequestExceedsReceipt");
    }

    [Fact]
    public void An_invoice_line_may_span_two_receipts_of_its_original_order_line()
    {
        var history = History().Append(new(Scope, 2, Prior, 40, 50, 22, .1m, "Posted")).ToArray();
        var result = Check(history: history); Assert.True(result.Ok, result.Code);
        Assert.Equal(.3m, Assert.Single(result.Lines!).PreviouslyBilled);
    }

    [Theory]
    [InlineData("Draft")][InlineData("Closed")][InlineData("Cancelled")][InlineData("Unknown")]
    public void Non_open_sources_cannot_be_planned(string status) => Refused(
        ConstructionPartialInvoiceQuantityRules.Validate(Scope, status, false, Next, 18, 4, Orders(), Receipts(), History(), Request()),
        "PartialInvoiceSourceNotOpen");

    [Fact]
    public void Legacy_conversion_and_request_replay_cannot_be_reinterpreted_as_a_new_partial_request()
    {
        Refused(ConstructionPartialInvoiceQuantityRules.Validate(Scope, "Received", true, Next, 18, 4,
            Orders(), Receipts(), History(), Request()), "PartialInvoiceSourceNotOpen");
        Refused(Check(id: Prior), "InvoiceRequestAlreadyRecorded");
        Refused(Check(id: Guid.Empty), "InvoiceRequestIdentityRequired");
    }

    [Theory][InlineData(0, 4)][InlineData(29, 4)][InlineData(18, -1)][InlineData(4, 5)]
    public void Quantity_precision_must_be_proven(int precision, int scale) => Refused(
        ConstructionPartialInvoiceQuantityRules.Validate(Scope, "Received", false, Next, precision, scale,
            Orders(), Receipts(), History(), Request()), "InvoiceQuantityPrecisionUnproven");

    [Theory][InlineData("company")][InlineData("project")][InlineData("order")]
    public void Receipt_and_invoice_history_cannot_cross_any_scope_boundary(string dimension)
    {
        var foreign = dimension switch { "company" => Scope with { CompanyId = 2 }, "project" => Scope with { ProjectId = 12 }, _ => Scope with { OrderId = 24 } };
        var receipts = Receipts(); receipts[0] = receipts[0] with { Scope = foreign };
        Refused(Check(receipts: receipts), "InvoiceReceiptQuantitiesUnproven");
        var history = History(); history[0] = history[0] with { Scope = foreign };
        Refused(Check(history: history), "InvoiceQuantityHistoryUnproven");
    }

    [Theory][InlineData("identity")][InlineData("order-line")][InlineData("item")][InlineData("unit")]
    [InlineData("unposted")][InlineData("negative")][InlineData("zero")][InlineData("precision")]
    public void Invalid_receipt_evidence_never_returns_quantities(string kind)
    {
        var r = Receipts();
        r[0] = kind switch
        {
            "identity" => r[0] with { ReceiptLineId = 0 }, "order-line" => r[0] with { OrderLineId = 99 },
            "item" => r[0] with { ItemId = 99 }, "unit" => r[0] with { UnitId = 99 },
            "unposted" => r[0] with { Status = "Draft" }, "negative" => r[0] with { Received = -.4m },
            "zero" => r[0] with { Received = 0 }, _ => r[0] with { Received = .40001m }
        };
        AssertRefused(Check(receipts: r));
    }

    [Theory][InlineData("duplicate-fact")][InlineData("duplicate-edge")][InlineData("reversed")]
    [InlineData("missing-receipt")][InlineData("negative")][InlineData("precision")]
    [InlineData("request-two-invoices")][InlineData("invoice-two-requests")][InlineData("line-two-invoices")]
    public void Inconsistent_history_cannot_return_available_quantities(string kind)
    {
        var h = History();
        h = kind switch
        {
            "duplicate-fact" => [h[0], h[0] with { ReceiptLineId = 22 }],
            "duplicate-edge" => [h[0], h[0] with { FactId = 2 }],
            "reversed" => [h[0] with { Status = "Reversed" }],
            "missing-receipt" => [h[0] with { ReceiptLineId = 99 }],
            "negative" => [h[0] with { Billed = -.2m }],
            "precision" => [h[0] with { Billed = .20001m }],
            "request-two-invoices" => [h[0], h[0] with { FactId = 2, InvoiceId = 41, InvoiceLineId = 51, ReceiptLineId = 22 }],
            "invoice-two-requests" => [h[0], h[0] with { FactId = 2, RequestId = Guid.NewGuid(), InvoiceLineId = 51, ReceiptLineId = 22 }],
            _ => [h[0], h[0] with { FactId = 2, RequestId = Guid.NewGuid(), InvoiceId = 41, ReceiptLineId = 22 }]
        };
        AssertRefused(Check(history: h));
    }

    [Theory][InlineData("unknown")][InlineData("duplicate")][InlineData("zero")][InlineData("negative")][InlineData("precision")]
    public void Requests_are_explicit_positive_unique_receipt_slices(string kind)
    {
        ConstructionInvoiceQuantityRequest[] request = kind switch
        {
            "unknown" => [new(99, .1m)], "duplicate" => [new(21, .1m), new(21, .1m)],
            "zero" => [new(21, 0)], "negative" => [new(21, -.1m)], _ => [new(21, .00001m)]
        };
        Refused(Check(request: request), "InvoiceQuantityRequestInvalid");
    }

    [Fact]
    public void Unused_quantity_from_another_receipt_or_order_line_cannot_cover_overbilling()
    {
        Refused(Check(request: [new(21, .3m)]), "InvoiceRequestExceedsReceipt");
        var orders = Orders().Append(new(12, 8, 3, 1m)).ToArray();
        var receipts = Receipts().Append(new(Scope, 33, 23, 12, 8, 3, 1m, "Posted")).ToArray();
        Refused(Check(orders, receipts, request: [new(21, .3m)]), "InvoiceRequestExceedsReceipt");
        var ambiguous = History().Append(new(Scope, 2, Prior, 40, 50, 23, .1m, "Posted")).ToArray();
        Refused(Check(orders, receipts, ambiguous), "InvoiceLineSourceAmbiguous");
    }

    [Fact]
    public void Overbilled_history_and_overreceived_orders_are_not_repaired_by_planning()
    {
        Refused(Check(history: [History()[0] with { Billed = .5m }]), "InvoiceHistoryExceedsReceipt");
        var receipts = Receipts(); receipts[1] = receipts[1] with { Received = .7m };
        Refused(Check(receipts: receipts), "InvoiceReceiptsExceedOrder");
    }

    [Fact]
    public void All_tenth_unit_partitions_conserve_quantity_and_reject_the_next_unit()
    {
        for (var received = 1; received <= 10; received++)
        for (var billed = 0; billed < received; billed++)
        for (var take = 1; take <= received - billed; take++)
        {
            var receipt = Receipts()[0] with { Received = received / 10m };
            ConstructionInvoiceBilledQuantity[] history = billed == 0 ? [] : [History()[0] with { Billed = billed / 10m }];
            var result = Check(receipts: [receipt], history: history, request: [new(21, take / 10m)]);
            Assert.True(result.Ok, result.Code);
            var line = Assert.Single(result.Lines!);
            Assert.Equal(line.Received, line.PreviouslyBilled + line.Requested + line.UnbilledAfterRequest);
            Refused(Check(receipts: [receipt], history: history, request: [new(21, (received - billed) / 10m + .0001m)]), "InvoiceRequestExceedsReceipt");
        }
    }

    [Fact]
    public void Source_ordering_is_irrelevant_and_results_do_not_retain_mutable_input_collections()
    {
        var receipts = Receipts(); var request = Request();
        var result = Check(receipts: receipts, request: request);
        var reordered = Check(receipts: receipts.Reverse().ToArray(), request: request.Reverse().ToArray());
        Assert.Equal(result.Lines, reordered.Lines); Assert.Equal(result.Receipts, reordered.Receipts);
        receipts[0] = receipts[0] with { Received = 100 }; request[0] = new(21, 100);
        Assert.Equal(.3m, Assert.Single(result.Lines!).UnbilledAfterRequest);
    }

    [Theory][InlineData("identity")][InlineData("item")][InlineData("unit")]
    [InlineData("zero")][InlineData("negative")][InlineData("precision")]
    public void Ordered_quantities_and_identities_must_fit_the_actual_storage_basis(string kind)
    {
        var line = Orders()[0];
        line = kind switch
        {
            "identity" => line with { LineId = 0 }, "item" => line with { ItemId = 0 },
            "unit" => line with { UnitId = 0 }, "zero" => line with { Ordered = 0 },
            "negative" => line with { Ordered = -1 }, _ => line with { Ordered = 1.00001m }
        };
        Refused(Check(orders: [line]), "InvoiceOrderQuantitiesInvalid");
    }

    [Fact]
    public void Duplicate_and_empty_source_sets_are_not_silently_collapsed()
    {
        Refused(Check(orders: []), "InvoiceOrderQuantitiesInvalid");
        Refused(Check(orders: [Orders()[0], Orders()[0]]), "InvoiceOrderQuantitiesInvalid");
        Refused(Check(receipts: []), "InvoiceReceiptQuantitiesUnproven");
        Refused(Check(receipts: [Receipts()[0], Receipts()[0]]), "InvoiceReceiptQuantitiesUnproven");
        Refused(Check(request: []), "InvoiceQuantityRequestInvalid");
    }

    [Theory][InlineData(0, 10, 20)][InlineData(1, 0, 20)][InlineData(1, 10, 0)]
    public void An_unresolved_scope_never_returns_balances(int company, int project, int order) => Refused(
        ConstructionPartialInvoiceQuantityRules.Validate(new(company, project, order), "Received", false, Next,
            18, 4, Orders(), Receipts(), History(), Request()), "InvoiceScopeUnresolved");

    [Fact]
    public void Individually_representable_quantities_cannot_overflow_a_cumulative_balance()
    {
        const decimal large = 9000000000000000000000000000m;
        var receipts = Enumerable.Range(1, 10).Select(i =>
            new ConstructionInvoiceReceiptQuantity(Scope, 30 + i, 20 + i, 11, 7, 3, large, "Posted")).ToArray();
        var result = ConstructionPartialInvoiceQuantityRules.Validate(Scope, "Received", false, Next,
            28, 0, [new(11, 7, 3, large)], receipts, [], [new(21, 1)]);
        Refused(result, "InvoiceQuantityOverflow");
    }

    [Fact]
    public void Quantity_precision_does_not_assume_two_decimal_places()
    {
        var result = ConstructionPartialInvoiceQuantityRules.Validate(Scope, "Received", false, Next,
            18, 8, [new(11, 7, 3, 1)], [Receipts()[0] with { Received = .12345678m }], [], [new(21, .00000001m)]);
        Assert.True(result.Ok, result.Code);
        Assert.Equal(.12345677m, Assert.Single(result.Receipts!).UnbilledAfterRequest);
    }

    private static void Refused(ConstructionInvoiceQuantityCheck result, string code)
    {
        AssertRefused(result); Assert.Equal(code, result.Code);
    }
    private static void AssertRefused(ConstructionInvoiceQuantityCheck result)
    {
        Assert.False(result.Ok); Assert.Null(result.Lines); Assert.Null(result.Receipts);
    }
}
