using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BudgetPolicyTests
{
    private static BusinessContext Context(int company = 1) => new()
        { CompanyId = company, EmployeeId = 7, UserId = "R7-policy-test", Source = BusinessContextSource.Test };
    private static ConstructionBudgetPolicyService Service(ConstructionTestFixture f, bool allow = true) =>
        new(f.Db, new Access(allow), f.Audit);

    [Fact]
    public async Task Unconfigured_company_is_refused_even_within_budget_and_no_policy_is_seeded()
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync();
        var result = await Service(f).EvaluateAsync(Context(), project.ID, 100, 0, 1);
        Assert.False(result.Allowed); Assert.Equal("PolicyNotConfigured", result.Code);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionBudgetPolicy>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("Warning", true, true, false)]
    [InlineData("ApprovalRequired", false, false, true)]
    [InlineData("HardBlock", false, false, false)]
    public async Task Explicit_mode_controls_an_over_budget_proposal(string mode, bool allowed, bool warning, bool approval)
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync();
        var service = Service(f);
        var configured = await service.ConfigureAsync(Context(), mode, "Owner selected company policy");
        Assert.True(configured.ok, configured.error);
        var result = await service.EvaluateAsync(Context(), project.ID, 100, 80, 21);
        Assert.Equal(allowed, result.Allowed); Assert.Equal(warning, result.Warning); Assert.Equal(approval, result.RequiresApproval);
        await using var verify = f.NewContext();
        var persisted = await verify.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleAsync();
        Assert.Equal(mode, persisted.Mode);
        Assert.Single(await verify.ConstructionAuditEntries.AsNoTracking().Where(x => x.EntityType == nameof(ConstructionBudgetPolicy)).ToListAsync());
    }

    [Fact]
    public async Task Exact_remaining_budget_is_allowed_without_a_warning()
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync(); var service = Service(f);
        Assert.True((await service.ConfigureAsync(Context(), "HardBlock", "Explicit company choice")).ok);
        var result = await service.EvaluateAsync(Context(), project.ID, 100, 80, 20);
        Assert.Equal("WithinBudget", result.Code); Assert.True(result.Allowed); Assert.False(result.Warning);
    }

    [Fact]
    public async Task A_stale_or_missing_policy_token_cannot_replace_the_current_company_choice()
    {
        using var f = new ConstructionTestFixture(); var service = Service(f);
        Assert.True((await service.ConfigureAsync(Context(), "Warning", "Initial choice")).ok);
        await using var read = f.NewContext();
        var old = (await read.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleAsync()).ConcurrencyToken;
        Assert.True((await service.ConfigureAsync(Context(), "HardBlock", "Stricter explicit choice", old)).ok);
        Assert.False((await service.ConfigureAsync(Context(), "Warning", "Stale choice", old)).ok);
        Assert.False((await service.ConfigureAsync(Context(), "Warning", "Missing token")).ok);
        await using var verify = f.NewContext();
        Assert.Equal("HardBlock", (await verify.Set<ConstructionBudgetPolicy>().AsNoTracking().SingleAsync()).Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("warning")]
    public async Task Missing_or_unknown_modes_never_create_an_implicit_policy(string mode)
    {
        using var f = new ConstructionTestFixture();
        Assert.False((await Service(f).ConfigureAsync(Context(), mode, "A reason")).ok);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetPolicy>().ToListAsync());
    }

    [Fact]
    public async Task Unauthorized_or_mismatched_company_cannot_read_or_configure_policy()
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync();
        Assert.False((await Service(f, false).ConfigureAsync(Context(), "Warning", "Denied caller")).ok);
        Assert.False((await Service(f).ConfigureAsync(Context(2), "Warning", "Wrong company")).ok);
        Assert.Equal("AccessDenied", (await Service(f).EvaluateAsync(Context(2), project.ID, 100, 0, 1)).Code);
        Assert.Equal("AccessDenied", (await Service(f, false).EvaluateAsync(Context(), project.ID, 100, 0, 1)).Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetPolicy>().ToListAsync());
    }

    [Fact]
    public async Task A_policy_never_grants_access_to_an_unknown_project_or_invalid_amounts()
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync(); var service = Service(f);
        Assert.True((await service.ConfigureAsync(Context(), "Warning", "Explicit selection")).ok);
        Assert.Equal("ProjectNotFound", (await service.EvaluateAsync(Context(), int.MaxValue, 100, 0, 1)).Code);
        Assert.Equal("InvalidAmounts", (await service.EvaluateAsync(Context(), project.ID, 100, 0, -1)).Code);
    }

    [Fact]
    public async Task The_registered_write_guard_refuses_a_foreign_company_policy_even_without_the_service()
    {
        using var host = new PlatformTestHost(1);
        host.Db.Add(new ConstructionBudgetPolicy {CompanyID=2,Mode="Warning",Reason="Rejected fixture",UpdatedAt=DateTime.UtcNow});
        await Assert.ThrowsAsync<CompanyWriteDeniedException>(()=>host.Db.SaveChangesAsync());
        host.Db.ChangeTracker.Clear();
        using var verify=host.AllCompanies();
        Assert.Empty(await verify.Set<ConstructionBudgetPolicy>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(1,1)]
    [InlineData(null,0)]
    public async Task Policy_reads_are_filtered_by_the_executing_context_including_an_unresolved_scope(int? company,int expected)
    {
        using var host=new PlatformTestHost(company);
        host.Seed.AddRange(
            new ConstructionBudgetPolicy{CompanyID=1,Mode="Warning",Reason="Company one test",UpdatedAt=DateTime.UtcNow},
            new ConstructionBudgetPolicy{CompanyID=2,Mode="HardBlock",Reason="Company two test",UpdatedAt=DateTime.UtcNow});
        await host.Seed.SaveChangesAsync();
        var rows=await host.Db.Set<ConstructionBudgetPolicy>().AsNoTracking().ToListAsync();
        Assert.Equal(expected,rows.Count);
        if(company is >0)Assert.All(rows,x=>Assert.Equal(company.Value,x.CompanyID));
    }

    [Fact]
    public async Task Non_financial_project_management_does_not_authorize_changing_the_company_budget_policy()
    {
        using var f = new ConstructionTestFixture();
        var service = new ConstructionBudgetPolicyService(f.Db, new Access(true, ProjectsActions.Manage), f.Audit);
        Assert.False((await service.ConfigureAsync(Context(), "Warning", "Non financial manager attempt")).ok);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionBudgetPolicy>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().ToListAsync());
    }

    private sealed class Access(bool allow, string? onlyAction = null) : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(allow && (onlyAction == null || action == onlyAction));
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default)
            => Task.FromResult(allow ? AccessScope.Company(context.CompanyId) : AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
}
