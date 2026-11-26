using System.Reflection;
using System.Xml.Linq;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RebaseUiTests
{
    private sealed class Gateway : IConstructionRebaseWorkspaceService
    {
        public bool Success, Denied;
        public ConstructionRebaseInput? Input;
        public Task<ConstructionRebasePage?> ReadAsync(int project, int? order, int? proposal, CancellationToken ct = default) =>
            Task.FromResult<ConstructionRebasePage?>(Denied ? null : new(project, "ZZ", "Project", null, [], false, null));
        public Task<ConstructionRebaseRequestResult> SubmitAsync(int project, int order, ConstructionRebaseOperation operation, ConstructionRebaseInput input, CancellationToken ct = default)
        { Input = input; return Task.FromResult(new ConstructionRebaseRequestResult(Success, Denied ? "AccessDenied" : Success ? "Saved" : "ConcurrencyConflict")); }
    }
    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
    [Theory]
    [InlineData("Request")][InlineData("Approve")][InlineData("Reject")][InlineData("Activate")][InlineData("PurchaseRequest")]
    public async Task Refusal_preserves_identity_version_reason_and_success_redirects(string action)
    {
        var gateway = new Gateway(); var controller = new ConstructionRebaseController(gateway)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new MemoryTempData());
        var input = new ConstructionRebaseInput { RequestId = Guid.NewGuid(), Version = new('A', 64), Reason = "Review", TargetId = 3 };
        async Task<IActionResult> Invoke() => await (Task<IActionResult>)typeof(ConstructionRebaseController).GetMethod(action)!.Invoke(controller, [11, 12, input])!;
        var refusal = Assert.IsType<ViewResult>(await Invoke());
        var page = Assert.IsType<ConstructionRebasePage>(refusal.Model);
        Assert.Equal(409, controller.Response.StatusCode); Assert.Same(input, page.RejectedInput); Assert.Same(input, gateway.Input);
        Assert.Equal(Enum.Parse<ConstructionRebaseOperation>(action), page.FailedOperation);
        gateway.Success = true; Assert.IsType<RedirectToActionResult>(await Invoke());
        gateway.Denied = true; gateway.Success = false; Assert.IsType<ForbidResult>(await Invoke());
    }
    [Fact]
    public void All_writes_have_antiforgery_and_explicit_project_gate_and_resources_match()
    {
        Assert.NotNull(typeof(ConstructionRebaseController).GetCustomAttribute<SessionValidationAttribute>());
        foreach (var method in typeof(ConstructionRebaseController).GetMethods().Where(x => x.GetCustomAttribute<HttpPostAttribute>() != null))
        { Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()); Assert.NotNull(method.GetCustomAttribute<ProjectPermAttribute>()); }
        Assert.NotNull(typeof(ConstructionRebaseController).GetMethod("PurchaseRequest")!.GetCustomAttribute<InvPermAttribute>());
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root); string[]? keys = null;
        foreach (var suffix in new[] { "", ".en", ".ar", ".fr" })
        {
            var rows = XDocument.Load(Path.Combine(root!.FullName, "CrossBuy/Resources/SharedResources" + suffix + ".resx"))
                .Root!.Elements("data").Where(x => ((string)x.Attribute("name")!).StartsWith("Construction.Rebase.")).ToArray();
            var actual = rows.Select(x => (string)x.Attribute("name")!).OrderBy(x => x).ToArray();
            Assert.True(actual.Length > 40); Assert.Equal(actual.Length, actual.Distinct().Count());
            if (keys != null) Assert.Equal(keys, actual); keys = actual;
            Assert.All(rows, x => Assert.False(string.IsNullOrWhiteSpace(x.Element("value")?.Value)));
        }
    }
    [Theory]
    [InlineData("permission")][InlineData("company")][InlineData("foreign-order")][InlineData("foreign-proposal")]
    public async Task Workspace_denies_unscoped_reads_and_writes_without_creating_history(string mode)
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7RebaseRequestTests.Seed(f.Db);
        var r = await f.Db.Set<CrossBuy.Models.Context.Construction.ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = mode == "company"
            ? new() { CompanyId = 2, EmployeeId = 7, UserId = "other", Source = CrossBuy.Models.Platform.BusinessContextSource.Test }
            : ConstructionR7OrderAllocationTests.Actor() };
        var access = new ConstructionR7OrderAllocationTests.Access(mode != "permission");
        var workspace = new ConstructionRebaseWorkspaceService(f.Db, actor, access,
            new(f.Db, access, f.Audit), ConstructionR7PurchaseApprovalTests.Service(f.Db, actor));
        var order = mode == "foreign-order" ? r.PurchaseOrderId + 1000 : r.PurchaseOrderId;
        var page = await workspace.ReadAsync(ids.Project, order, mode == "foreign-proposal" ? 9999 : null);
        Assert.Null(page);
        var input = new ConstructionRebaseInput { RequestId = Guid.NewGuid(), Version = new('A', 64), Reason = "Denied", ProposalId = 9999 };
        var refused = await workspace.SubmitAsync(ids.Project, order, ConstructionRebaseOperation.Approve, input);
        Assert.False(refused.Ok);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<CrossBuy.Models.Context.Construction.ConstructionReservationRebaseDecision>().AsNoTracking().ToListAsync());
    }
}
