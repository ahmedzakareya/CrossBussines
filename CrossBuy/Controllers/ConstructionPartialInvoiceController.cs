using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers;

[SessionValidation]
[Route("Inventory/ConstructionPartialInvoice")]
public sealed class ConstructionPartialInvoiceController(IBusinessContextAccessor contexts,
    IConstructionPartialInvoiceService invoices) : Controller
{
    private async Task<BusinessContext?> Actor() => await contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
    private static bool Valid(ConstructionPartialInvoiceInput input) => input.RequestId != Guid.Empty &&
        input.Date != default && input.Reason is { Length: > 0 and <= 1000 } &&
        input.Rows is { Count: > 0 and <= 500 } && input.Rows.All(x => x != null && x.ReceiptLineId > 0 && x.Quantity >= 0) &&
        input.Rows.Select(x => x.ReceiptLineId).Distinct().Count() == input.Rows.Count && input.Rows.Any(x => x.Quantity > 0);
    private static ConstructionInvoiceQuantityRequest[] Quantities(ConstructionPartialInvoiceInput input) =>
        input.Rows.Where(x => x.Quantity > 0).Select(x => new ConstructionInvoiceQuantityRequest(x.ReceiptLineId, x.Quantity)).ToArray();
    private async Task<IActionResult> Page(BusinessContext actor, int projectId, int orderId,
        ConstructionPartialInvoiceInput input, ConstructionPartialInvoiceResult? preview = null, string? error = null)
    {
        var source = await invoices.ReadAsync(actor, projectId, orderId, HttpContext.RequestAborted);
        if (source.Code == "AccessDenied") return Forbid();
        // A preview is usable only for the same source witness; a concurrent receipt/invoice requires a fresh preview.
        if (preview?.Ok == true && preview.Version != source.Version)
        { preview = null; error = "SourceChanged"; }
        input.Rows = (input.Rows ?? []).Where(x => x != null).Take(500).ToList();
        if (error != null) Response.StatusCode = StatusCodes.Status409Conflict;
        return View("~/Views/Inventory/ConstructionPartialInvoice.cshtml",
            new ConstructionPartialInvoicePage(projectId, orderId, source, input, preview, error));
    }
    [HttpGet, InvPerm("purchase")]
    public async Task<IActionResult> Index(int projectId, int orderId)
    {
        var actor = await Actor(); if (actor == null) return Forbid();
        var source = await invoices.ReadAsync(actor, projectId, orderId, HttpContext.RequestAborted);
        if (source.Code == "AccessDenied") return Forbid();
        var input = new ConstructionPartialInvoiceInput { RequestId = Guid.NewGuid(),
            Rows = source.Receipts?.Select(x => new ConstructionPartialInvoiceInputRow { ReceiptLineId = x.ReceiptLineId }).ToList() ?? [] };
        return View("~/Views/Inventory/ConstructionPartialInvoice.cshtml", new ConstructionPartialInvoicePage(projectId, orderId, source, input));
    }
    [HttpPost("Preview"), ValidateAntiForgeryToken, InvPerm("purchase"), RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Preview(int projectId, int orderId, ConstructionPartialInvoiceInput input)
    {
        var actor = await Actor(); if (actor == null) return Forbid();
        var result = ModelState.IsValid && Valid(input)
            ? await invoices.PreviewAsync(actor, projectId, orderId, input.RequestId, Quantities(input), HttpContext.RequestAborted)
            : new ConstructionPartialInvoiceResult(false, "InvalidPartialInvoiceRequest");
        if (result.Code == "AccessDenied") return Forbid();
        input.Version = result.Ok ? result.Version : null;
        // Never reuse a posted version value after server-side preview validation.
        ModelState.Remove(nameof(input.Version));
        return await Page(actor, projectId, orderId, input, result.Ok ? result : null, result.Ok ? null : result.Code);
    }
    [HttpPost("Issue"), ValidateAntiForgeryToken, InvPerm("purchase"), RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Issue(int projectId, int orderId, ConstructionPartialInvoiceInput input)
    {
        var actor = await Actor(); if (actor == null) return Forbid();
        var result = ModelState.IsValid && Valid(input) && input.Version is { Length: 64 }
            ? await invoices.IssueAsync(actor, projectId, orderId, input.Date, input.RequestId, input.Version,
                Quantities(input), input.Reason, HttpContext.RequestAborted)
            : new ConstructionPartialInvoiceResult(false, "InvalidPartialInvoiceRequest");
        if (result.Code == "AccessDenied") return Forbid();
        if (!result.Ok) return await Page(actor, projectId, orderId, input, error: result.Code);
        // Keep the original request identity on failed/ambiguous submissions. Only success uses PRG.
        TempData["ConstructionPartialInvoiceIssued"] = result.InvoiceId;
        return RedirectToAction(nameof(Index), new Microsoft.AspNetCore.Routing.RouteValueDictionary {
            ["projectId"] = projectId, ["orderId"] = orderId,
            ["culture"] = System.Globalization.CultureInfo.CurrentCulture.Name,
            ["ui-culture"] = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }
}
