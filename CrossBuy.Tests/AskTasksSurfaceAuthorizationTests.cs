using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class AskTasksSurfaceAuthorizationTests
{
    private sealed class StubTaskReadAuthorizer(TaskReadAuthorization? authorization) : ITaskReadAuthorizer
    {
        public int ApplyCalls { get; private set; }

        public Task<TaskReadAuthorization?> ForListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(authorization);

        public Task<IQueryable<TaskItem>> ApplyAsync(IQueryable<TaskItem> source,
            TaskReadAuthorization auth, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult(source);
        }
    }

    [Fact]
    public async Task RefusesRowsWhenTaskListAuthorizationIsAbsent()
    {
        var authorizer = new StubTaskReadAuthorizer(null);
        var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().Options);
        var surface = new AskTasksSurface(db, authorizer);

        var rows = await surface.RowsAsync(new AskScope { CompanyId = 41 });

        Assert.Empty(rows);
        Assert.Equal(0, authorizer.ApplyCalls);
        Assert.Equal(EntityRegistry.ScopeTasks, surface.Module);
        Assert.Equal(TasksActions.Read, surface.Action);
    }

    [Fact]
    public async Task RefusesAContextFromAnotherCompanyBeforeBuildingTheTaskQuery()
    {
        var context = new BusinessContext { CompanyId = 42, EmployeeId = 7 };
        var authorization = new TaskReadAuthorization
        {
            Context = context,
            Scope = AccessScope.Own(42),
            CallerEmployeeId = 7,
        };
        var authorizer = new StubTaskReadAuthorizer(authorization);
        var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().Options);
        var surface = new AskTasksSurface(db, authorizer);

        var rows = await surface.RowsAsync(new AskScope { CompanyId = 41 });

        Assert.Empty(rows);
        Assert.Equal(0, authorizer.ApplyCalls);
    }

    [Fact]
    public void TaskSurfaceDoesNotExposeDescriptionOrBillingColumns()
    {
        var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().Options);
        var surface = new AskTasksSurface(db, new StubTaskReadAuthorizer(null));

        Assert.Contains(surface.Columns, column => column.Name == "title");
        Assert.Contains(surface.Columns, column => column.Name == "status");
        Assert.DoesNotContain(surface.Columns, column => column.Name.Contains("description", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(surface.Columns, column => column.Name is "billrate" or "estimatedhours" or "actualhours");
    }
}
