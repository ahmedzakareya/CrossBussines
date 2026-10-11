using CrossBuy.BL.Documents;
using CrossBuy.BL;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers;

[SessionValidation]
[Route("Documents")]
public sealed class CustomerDocumentsController(IClientDocumentPublicationService publications) : Controller
{
    [HttpPost("{id:long}/publish")]
    [ValidateAntiForgeryToken]
    [AccPerm(AccountingActions.CustomerDocumentPublish)]
    public async Task<IActionResult> Publish(long id, CancellationToken ct = default)
        => Result(await publications.PublishAsync(id, ct));

    [HttpPost("{id:long}/revoke")]
    [ValidateAntiForgeryToken]
    [AccPerm(AccountingActions.CustomerDocumentPublish)]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct = default)
        => Result(await publications.RevokeAsync(id, ct));

    private IActionResult Result(DocumentResult result) => result.Ok ? Ok(new { ok = true }) : NotFound();
}
