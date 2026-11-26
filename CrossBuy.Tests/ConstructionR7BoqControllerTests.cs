using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BoqControllerTests
{
    [Fact]
    public async Task Stale_post_returns_original_submission_and_fresh_database_values_without_overwriting()
    {
        using var f = new ConstructionTestFixture();
        var project = await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1, project.ID,
            new() { new() { Code = "A", Description = "Work", Quantity = 10, UnitPrice = 2, MaterialCost = 1 } }, 7, "Initial")).ok);
        var opened = await f.Boq.GetEditorAsync(1, project.ID);
        var line = Assert.Single(opened.Lines);
        Assert.True((await f.Boq.SaveLinesAsync(1, project.ID,
            new() { new() { Id = line.ID, Code = "A", Description = "Work", Quantity = 10, UnitPrice = 2, MaterialCost = 3 } }, 8, "Concurrent estimate")).ok);
        var posted = new List<BoqRowInput> { new() { Id = line.ID, Code = "A", Description = "Rejected estimate", Quantity = 10, UnitPrice = 2, MaterialCost = 9 } };
        var http = new DefaultHttpContext();
        var controller = new ProjectController(new ProjectService(f.Db), f.Boq,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, f.Db,
            new Access(), null!, null!, new Context(), new Company(), null!, new Localizer(), null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new MemoryTempData())
        };

        var response = Assert.IsType<ViewResult>(await controller.SaveBoq(project.ID,
            System.Text.Json.JsonSerializer.Serialize(posted), opened.Snapshot));
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        Assert.Equal(nameof(ProjectController.Boq), response.ViewName);
        var current = Assert.Single(Assert.IsType<List<BoqItem>>(response.Model));
        Assert.Equal(3m, current.MaterialCost);
        var rejected = Assert.Single(Assert.IsType<List<BoqRowInput>>(response.ViewData["RejectedRows"]));
        Assert.Equal(9m, rejected.MaterialCost);
        Assert.Equal("Rejected estimate", rejected.Description);
        Assert.NotEqual(opened.Snapshot, Assert.IsType<string>(response.ViewData["EditorSnapshot"]));
        await using var verify = f.NewContext();
        var persisted = await verify.BoqItems.AsNoTracking().SingleAsync();
        Assert.Equal(3m, persisted.MaterialCost);
        Assert.Equal("Work", persisted.Description);
    }

    private sealed class Context : IBusinessContextAccessor
    {
        public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BusinessContext
            { CompanyId = 1, EmployeeId = 7, UserId = "R7-controller", Source = BusinessContextSource.Test });
        public async Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) => await GetCurrentAsync(cancellationToken);
    }
    private sealed class Company : IRequestCompanyResolver
    {
        public Task<CompanyResolution> ResolveAsync(int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CompanyResolution.Resolved(1, 7, null));
    }
    private sealed class Access : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(context.CompanyId == 1 && action == ProjectsActions.BudgetManage);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default)
            => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
