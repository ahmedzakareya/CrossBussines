using System.Reflection;
using System.Xml.Linq;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PartialInvoiceUiTests
{
    private sealed class Gateway : IConstructionPartialInvoiceService
    {
        public string SourceVersion = new('A', 64);
        public string? PreviewVersion;
        public string? Error;
        public int Previews, Issues;
        public BusinessContext? Actor;
        public Guid Request;
        public ConstructionInvoiceQuantityRequest[] Quantities = [];
        public Task<ConstructionPartialInvoiceSourcePage> ReadAsync(BusinessContext c, int project, int order, CancellationToken ct = default) =>
            Task.FromResult(new ConstructionPartialInvoiceSourcePage(true, "Ready", SourceVersion, 1, 3,
                [new(20, 21, 30, "Receipt item", 1m, .4m, .6m)]));
        public Task<ConstructionPartialInvoiceResult> PreviewAsync(BusinessContext c, int project, int order, Guid request,
            IReadOnlyList<ConstructionInvoiceQuantityRequest> requested, CancellationToken ct = default)
        {
            Previews++; Actor = c; Request = request; Quantities = requested.ToArray();
            return Task.FromResult(Error == null ? new ConstructionPartialInvoiceResult(true, "Ready", PreviewVersion ?? SourceVersion, Net: 4m, Tax: .8m) : new(false, Error));
        }
        public Task<ConstructionPartialInvoiceResult> IssueAsync(BusinessContext c, int project, int order, DateTime date,
            Guid request, string version, IReadOnlyList<ConstructionInvoiceQuantityRequest> requested, string reason, CancellationToken ct = default)
        {
            Issues++; Actor = c; Request = request; Quantities = requested.ToArray();
            return Task.FromResult(Error == null ? new ConstructionPartialInvoiceResult(true, "Issued", InvoiceId: 44) : new(false, Error));
        }
    }
    private static ConstructionPartialInvoiceController Controller(Gateway gateway)
    {
        var controller = new ConstructionPartialInvoiceController(new ConstructionR7PurchaseApprovalTests.ActorAccessor(), gateway)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new MemoryTempData());
        return controller;
    }
    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
    private static ConstructionPartialInvoiceInput Input() => new() { RequestId = Guid.NewGuid(), Reason = "Partial receipt invoice",
        Version = new('A', 64), Rows = [new() { ReceiptLineId = 21, Quantity = .4m }] };

    [Fact]
    public async Task Preview_uses_resolved_actor_and_server_witness_without_issuing()
    {
        var gateway = new Gateway(); var controller = Controller(gateway); var input = Input();
        controller.ModelState.SetModelValue("Version", "client-value", "client-value");
        controller.ModelState.MarkFieldValid("Version");
        var view = Assert.IsType<ViewResult>(await controller.Preview(11, 12, input));
        var page = Assert.IsType<ConstructionPartialInvoicePage>(view.Model);
        Assert.True(page.Preview!.Ok); Assert.Equal(gateway.SourceVersion, page.Input.Version);
        Assert.False(controller.ModelState.ContainsKey("Version"));
        Assert.Equal(1, gateway.Actor!.CompanyId); Assert.Equal(input.RequestId, gateway.Request);
        Assert.Equal(.4m, Assert.Single(gateway.Quantities).Quantity); Assert.Equal(0, gateway.Issues);
    }
    [Fact]
    public async Task Concurrent_source_change_disables_confirmation_and_preserves_request()
    {
        var gateway = new Gateway { PreviewVersion = new('B', 64) }; var controller = Controller(gateway); var input = Input();
        var page = Assert.IsType<ConstructionPartialInvoicePage>(Assert.IsType<ViewResult>(await controller.Preview(11, 12, input)).Model);
        Assert.Null(page.Preview); Assert.Equal("SourceChanged", page.Error); Assert.Equal(input.RequestId, page.Input.RequestId);
        Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode); Assert.Equal(0, gateway.Issues);
    }
    [Theory]
    [InlineData("duplicate")][InlineData("negative")][InlineData("empty")][InlineData("version")][InlineData("model")]
    public async Task Invalid_issue_never_calls_writer(string kind)
    {
        var gateway = new Gateway(); var controller = Controller(gateway); var input = Input();
        if (kind == "duplicate") input.Rows.Add(new() { ReceiptLineId = 21, Quantity = .1m });
        if (kind == "negative") input.Rows[0].Quantity = -.1m;
        if (kind == "empty") input.Rows.Clear();
        if (kind == "version") input.Version = "invalid";
        if (kind == "model") controller.ModelState.AddModelError("Rows[0].Quantity", "Invalid decimal");
        var result = Assert.IsType<ViewResult>(await controller.Issue(11, 12, input));
        Assert.NotNull(Assert.IsType<ConstructionPartialInvoicePage>(result.Model).Error); Assert.Equal(0, gateway.Issues);
    }
    [Fact]
    public async Task Refused_issue_preserves_identity_and_success_redirects_without_reposting()
    {
        var gateway = new Gateway { Error = "ConcurrencyConflict" }; var controller = Controller(gateway); var input = Input();
        var page = Assert.IsType<ConstructionPartialInvoicePage>(Assert.IsType<ViewResult>(await controller.Issue(11, 12, input)).Model);
        Assert.Equal(input.RequestId, page.Input.RequestId); Assert.Equal(.4m, Assert.Single(page.Input.Rows).Quantity);
        gateway.Error = null;
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Issue(11, 12, input));
        Assert.Equal("Index", redirect.ActionName); Assert.Equal(44, controller.TempData["ConstructionPartialInvoiceIssued"]);
        Assert.Equal(input.RequestId, gateway.Request); Assert.Equal(2, gateway.Issues);
    }
    [Fact]
    public void Browser_posts_require_antiforgery_and_inventory_permission()
    {
        Assert.NotNull(typeof(ConstructionPartialInvoiceController).GetCustomAttribute<SessionValidationAttribute>());
        foreach (var name in new[] { "Preview", "Issue" })
        {
            var method = typeof(ConstructionPartialInvoiceController).GetMethod(name)!;
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.NotNull(method.GetCustomAttribute<InvPermAttribute>());
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        }
    }
    [Fact]
    public void Resources_cover_all_view_labels_and_navigation_preserves_prior_destinations()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "CrossBuy");
        string[]? keys = null;
        foreach (var suffix in new[] { "", ".en", ".ar", ".fr" })
        {
            var entries = XDocument.Load(Path.Combine(path, "Resources", "SharedResources" + suffix + ".resx")).Root!.Elements("data")
                .Where(x => ((string)x.Attribute("name")!).StartsWith("Construction.PartialInvoice.")).ToArray();
            var actual = entries.Select(x => (string)x.Attribute("name")!).OrderBy(x => x).ToArray();
            Assert.Equal(24, actual.Length); if (keys != null) Assert.Equal(keys, actual); keys = actual;
            Assert.All(entries, x => Assert.False(string.IsNullOrWhiteSpace(x.Element("value")?.Value)));
        }
        var menu = File.ReadAllText(Path.Combine(path, "Models", "Menu", "MainMenu.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(menu, "ConstructionRoute\\(\"partial-invoice\"\\)").Count);
        Assert.Contains("ConstructionRoute(\"approval\")", menu); Assert.Contains("ConstructionRoute(\"allocation\")", menu);
    }
}
