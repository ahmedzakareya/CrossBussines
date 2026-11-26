using CrossBuy.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace CrossBuy.Tests.Communication;

public sealed class LegacyCommentRetirementTests
{
    [Fact]
    public void Retired_routes_never_resolve_identity_or_call_the_legacy_store()
    {
        var controller = new CommentsController();
        var list = Assert.IsType<ObjectResult>(controller.List("SalesInvoice", 1));
        var add = Assert.IsType<ObjectResult>(controller.Add("SalesInvoice", 1, "must not write", "2"));
        var delete = Assert.IsType<ObjectResult>(controller.Delete(1));
        Assert.Equal(410, list.StatusCode);
        Assert.Equal(410, add.StatusCode);
        Assert.Equal(410, delete.StatusCode);
        Assert.Equal("legacy_comment_reads_retired", list.Value!.GetType().GetProperty("code")!.GetValue(list.Value));

        Assert.Contains(typeof(CommentsController).GetMethod(nameof(CommentsController.Add))!.GetCustomAttributes(true),
            attribute => attribute is ValidateAntiForgeryTokenAttribute);
        Assert.Contains(typeof(CommentsController).GetMethod(nameof(CommentsController.Delete))!.GetCustomAttributes(true),
            attribute => attribute is ValidateAntiForgeryTokenAttribute);
    }
}
