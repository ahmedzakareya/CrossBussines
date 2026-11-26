using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BudgetVersionTests
{
    private static BusinessContext Actor(int employee = 7, int company = 1) => new()
        { CompanyId = company, EmployeeId = employee, UserId = "R7-budget-" + employee, Source = BusinessContextSource.Test };
    private static ConstructionBudgetVersionService Service(ConstructionTestFixture f, bool allow = true)
        => new(f.Db, new Access(allow), f.Audit, new CurrencyRounding(f.Db, new CurrencyService(f.Db), new Localizer()));

    internal static async Task<(int Project, int Node, int Code)> Seed(ConstructionTestFixture f, bool policy = true, byte decimals = 3)
    {
        var project = await f.SeedProjectAsync();
        var currency = new Currency { Code = "TST", Name = "Test", NameEn = "Test", DecimalPlaces = decimals };
        f.Db.Add(currency); await f.Db.SaveChangesAsync();
        f.Db.Companies.Add(new Companies { CompanyID = 1, CompanyName = "Test", Address = "", PhoneNumber = "", Email = "",
            DefaultCurrencyId = currency.ID });
        if (policy) f.Db.Add(new ConstructionBudgetPolicy { CompanyID = 1, Mode = "HardBlock", Reason = "Explicit owner choice" });
        f.Db.Add(new ConstructionStructureSettings { CompanyID = 1, MaxWbsDepth = 8, Reason = "Explicit hierarchy", UpdatedBy = 7 });
        var node = new WbsNode { CompanyID = 1, ProjectId = project.ID, Code = "A", Name = "Work", Path = "/1/", Depth = 1,
            Status = "Active", ProgressMethod = "Quantity", OwnsBudget = true, OwnsCost = true, CreatedBy = 7 };
        var code = new ConstructionCostCode { CompanyID = 1, Code = "MAT", Name = "Materials", Category = "Material", IsActive = true, AllowDirectPosting = true };
        f.Db.AddRange(node, code); await f.Db.SaveChangesAsync();
        return (project.ID, node.ID, code.ID);
    }
    private static List<ConstructionBudgetDraftLine> Lines((int Project, int Node, int Code) ids, decimal amount)
        => new() { new(ids.Node, ids.Code, amount, null, null, false, "Estimate review") };

    [Fact]
    public async Task Missing_company_policy_never_creates_a_budget()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f, policy: false);
        var result = await Service(f).CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate");
        Assert.False(result.Ok); Assert.Equal("PolicyNotConfigured", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0, "123", "123.1")]
    [InlineData(3, "123.456", "123.4567")]
    public async Task Company_currency_precision_is_authoritative_and_rejection_preserves_current_lines(byte decimals, string valid, string invalid)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f, decimals: decimals); var service = Service(f);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var amount = decimal.Parse(valid, System.Globalization.CultureInfo.InvariantCulture);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!, Lines(ids, amount), "Initial rows");
        Assert.True(saved.Ok, saved.Code);
        var refused = await service.SaveDraftAsync(Actor(), ids.Project, created.Id.Value, saved.Version!,
            Lines(ids, decimal.Parse(invalid, System.Globalization.CultureInfo.InvariantCulture)), "Invalid precision");
        Assert.False(refused.Ok); Assert.Equal("InvalidCurrencyPrecision", refused.Code);
        await using var verify = f.NewContext(); Assert.Equal(amount, (await verify.Set<ConstructionBudgetLine>().AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task Both_creator_and_submitter_are_refused_as_approver_and_approved_lines_are_frozen()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var service = Service(f);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!, Lines(ids, 100), "Rows"); Assert.True(saved.Ok, saved.Code);
        var submitted = await service.SubmitAsync(Actor(8), ids.Project, created.Id.Value, saved.Version!, "Review"); Assert.True(submitted.Ok, submitted.Code);
        foreach (var person in new[] { 7, 8 })
        {
            var refused = await service.ApproveAsync(Actor(person), ids.Project, created.Id.Value, submitted.Version!, "Approve own work");
            Assert.False(refused.Ok); Assert.Equal("SeparateApproverRequired", refused.Code);
        }
        var approved = await service.ApproveAsync(Actor(9), ids.Project, created.Id.Value, submitted.Version!, "Independent approval"); Assert.True(approved.Ok, approved.Code);
        var edit = await service.SaveDraftAsync(Actor(), ids.Project, created.Id.Value, approved.Version!, Lines(ids, 900), "Illegal edit");
        Assert.False(edit.Ok); Assert.Equal("ImmutableBudget", edit.Code);
        await using var verify = f.NewContext(); var header = await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync();
        Assert.Equal("Approved", header.Status); Assert.True(header.IsBaseline); Assert.Equal(9, header.ApprovedBy);
        Assert.Equal(100m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking().SingleAsync()).Amount);
    }

    private static async Task<StructureResult> Approved(ConstructionBudgetVersionService service, (int Project, int Node, int Code) ids)
    {
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Initial estimate"); Assert.True(created.Ok, created.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!, Lines(ids, 100), "Initial budget"); Assert.True(saved.Ok, saved.Code);
        var sent = await service.SubmitAsync(Actor(), ids.Project, created.Id.Value, saved.Version!, "Submit budget"); Assert.True(sent.Ok, sent.Code);
        var approved = await service.ApproveAsync(Actor(9), ids.Project, created.Id.Value, sent.Version!, "Independent approval"); Assert.True(approved.Ok, approved.Code);
        return approved;
    }

    [Fact]
    public async Task Revision_retains_previous_values_and_supersedes_the_baseline_atomically()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var service = Service(f); var first = await Approved(service, ids);
        var revised = await service.CreateAsync(Actor(), ids.Project, "Revised", first.Id, first.Version, "Revised scope"); Assert.True(revised.Ok, revised.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, revised.Id!.Value, revised.Version!, Lines(ids, 125), "New estimate"); Assert.True(saved.Ok, saved.Code);
        var submitted = await service.SubmitAsync(Actor(), ids.Project, revised.Id.Value, saved.Version!, "Submit revision"); Assert.True(submitted.Ok, submitted.Code);
        Assert.True((await service.ApproveAsync(Actor(9), ids.Project, revised.Id.Value, submitted.Version!, "Independent approval")).Ok);
        await using var verify = f.NewContext(); var headers = await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync();
        Assert.Equal(revised.Id, Assert.Single(headers.Where(x => x.IsBaseline)).ID);
        Assert.Equal("Superseded", headers.Single(x => x.ID == first.Id).Status);
        var lines = await verify.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync();
        Assert.Equal(100m, lines.Single(x => x.BudgetVersionId == first.Id).Amount);
        var replacement = lines.Single(x => x.BudgetVersionId == revised.Id); Assert.Equal(100m, replacement.PreviousAmount); Assert.Equal(125m, replacement.Amount);
    }

    [Fact]
    public async Task Competing_revision_cannot_replace_a_new_baseline_and_leaves_no_partial_approval()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f);
        var service = Service(f); var original = await Approved(service, ids);
        var submitted = new List<StructureResult>();
        foreach (var amount in new[] { 125m, 150m })
        {
            var draft = await service.CreateAsync(Actor(), ids.Project, "Revised", original.Id,
                original.Version, "Competing estimate");
            Assert.True(draft.Ok, draft.Code);
            var saved = await service.SaveDraftAsync(Actor(), ids.Project, draft.Id!.Value,
                draft.Version!, Lines(ids, amount), "Estimate rows");
            Assert.True(saved.Ok, saved.Code);
            var sent = await service.SubmitAsync(Actor(8), ids.Project, draft.Id.Value,
                saved.Version!, "Independent review");
            Assert.True(sent.Ok, sent.Code); submitted.Add(sent);
        }
        var winner = await service.ApproveAsync(Actor(9), ids.Project, submitted[0].Id!.Value,
            submitted[0].Version!, "Approve first estimate");
        Assert.True(winner.Ok, winner.Code);
        var refused = await service.ApproveAsync(Actor(10), ids.Project, submitted[1].Id!.Value,
            submitted[1].Version!, "Approve obsolete estimate");
        Assert.False(refused.Ok); Assert.Equal("BaselineChanged", refused.Code);
        await using var verify = f.NewContext();
        var headers = await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync();
        Assert.Equal(winner.Id, Assert.Single(headers.Where(x => x.IsBaseline)).ID);
        var loser = headers.Single(x => x.ID == submitted[1].Id);
        Assert.Equal("Submitted", loser.Status); Assert.Null(loser.ApprovedBy); Assert.Null(loser.ApprovedAt);
        Assert.Equal(100m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking()
            .SingleAsync(x => x.BudgetVersionId == original.Id)).Amount);
        Assert.False(await verify.ConstructionAuditEntries.AsNoTracking()
            .AnyAsync(x => x.EntityType == nameof(ConstructionBudgetVersion)
                && x.EntityId == loser.ID && x.FieldName == "ApproveBudget"));
    }

    [Fact]
    public async Task Stale_forms_and_foreign_dimensions_do_not_modify_the_draft()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var service = Service(f);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!, Lines(ids, 100), "Rows"); Assert.True(saved.Ok, saved.Code);
        Assert.Equal("ConcurrencyConflict", (await service.SaveDraftAsync(Actor(), ids.Project, created.Id.Value, created.Version!, Lines(ids, 200), "Stale form")).Code);
        var foreign = new ConstructionCostCode { CompanyID = 2, Code = "FOR", Name = "Foreign", Category = "Material" };
        var foreignScope = new CrossBuy.BL.Platform.CompanyScopeHolder(); foreignScope.Set(2, null);
        await using (var foreignDb = new CrossBuy.Models.Context.CrossDbContext(new DbContextOptionsBuilder<CrossBuy.Models.Context.CrossDbContext>()
            .UseSqlite(f.Db.Database.GetDbConnection()).Options, foreignScope))
        { foreignDb.Add(foreign); await foreignDb.SaveChangesAsync(); }
        var rows = Lines(ids, 200); rows.Add(new(ids.Node, foreign.ID, 10, null, null, false, "Foreign category"));
        Assert.False((await service.SaveDraftAsync(Actor(), ids.Project, created.Id.Value, saved.Version!, rows, "Tampered reference")).Ok);
        Assert.Null(await service.GetAsync(Actor(company: 2), ids.Project, created.Id.Value));
        Assert.Null(await Service(f, false).GetAsync(Actor(), ids.Project, created.Id.Value));
        await using var verify = f.NewContext(); Assert.Equal(100m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task Both_budget_entities_have_real_write_guards_and_executing_context_read_filters()
    {
        using var f = new ConstructionTestFixture();
        foreach (var foreign in new object[] { new ConstructionBudgetVersion { CompanyID = 2 }, new ConstructionBudgetLine { CompanyID = 2 } })
        {
            f.Db.Add(foreign);
            await Assert.ThrowsAsync<CrossBuy.BL.Platform.CompanyWriteDeniedException>(() => f.Db.SaveChangesAsync());
            f.Db.ChangeTracker.Clear();
        }
        var ids = await Seed(f); var service = Service(f);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        Assert.True((await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!, Lines(ids, 100), "Rows")).Ok);
        foreach (var company in new int?[] { 2, null })
        {
            var scope = new CrossBuy.BL.Platform.CompanyScopeHolder();
            if (company.HasValue) scope.Set(company.Value, null);
            await using var other = new CrossBuy.Models.Context.CrossDbContext(new DbContextOptionsBuilder<CrossBuy.Models.Context.CrossDbContext>()
                .UseSqlite(f.Db.Database.GetDbConnection()).Options, scope);
            Assert.Empty(await other.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync());
            Assert.Empty(await other.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync());
        }
        await using var own = f.NewContext();
        Assert.Single(await own.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync());
        Assert.Single(await own.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_null_draft_line_is_refused_at_the_service_boundary_without_mutation()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var service = Service(f);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var result = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!,
            new List<ConstructionBudgetDraftLine> { null! }, "Invalid caller");
        Assert.False(result.Ok); Assert.Equal("InvalidBudgetLines", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().Where(x => x.FieldName == "BudgetLines").ToListAsync());
    }

    [Theory]
    [InlineData(9)]
    [InlineData(29)]
    public async Task A_currency_precision_change_cannot_silently_truncate_an_existing_budget(byte decimals)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var service = Service(f);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!, Lines(ids, 1.234m), "Stored amount"); Assert.True(saved.Ok, saved.Code);
        var currency = await f.Db.Currencies.SingleAsync(); currency.DecimalPlaces = decimals; await f.Db.SaveChangesAsync();
        var result = await service.SaveDraftAsync(Actor(), ids.Project, created.Id.Value, saved.Version!, Lines(ids, 1.234567891m), "Unsupported precision");
        Assert.False(result.Ok); Assert.Equal("InvalidCurrencyPrecision", result.Code);
        await using var verify = f.NewContext(); Assert.Equal(1.234m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking().SingleAsync()).Amount);
    }


    [Theory]
    [InlineData("Disabled", 80, 20)]
    [InlineData("IndependentApproval", 80, 20)]
    [InlineData("Missing", 80, 20)]
    [InlineData("IndependentApproval", 80, 50)]
    [InlineData("IndependentApproval", 50, 20)]
    [InlineData("IndependentApproval", 0, 100)]
    public async Task Ordinary_revision_cannot_disguise_a_transfer_even_when_net_total_changes(
        string transferMode, int fromAmount, int toAmount)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f);
        if (transferMode != "Missing")
        {
            f.Db.Add(new ConstructionCompanyPolicy { CompanyID = 1, PolicyKey = "BudgetTransfer",
                Mode = transferMode, Reason = "Explicit company choice", UpdatedBy = 7 });
            await f.Db.SaveChangesAsync();
        }
        var service = Service(f); var baseline = await Approved(service, ids);
        var target = new ConstructionCostCode { CompanyID = 1, Code = "LAB-GUARD", Name = "Labour",
            Category = "Labor", IsActive = true, AllowDirectPosting = true };
        f.Db.Add(target); await f.Db.SaveChangesAsync();
        var draft = await service.CreateAsync(Actor(), ids.Project, "Revised", baseline.Id,
            baseline.Version, "Scope revision"); Assert.True(draft.Ok, draft.Code);
        var lines = new List<ConstructionBudgetDraftLine> {
            new(ids.Node, ids.Code, fromAmount, null, null, false, "Decrease source"),
            new(ids.Node, target.ID, toAmount, null, null, false, "Increase destination") };
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, draft.Id!.Value,
            draft.Version!, lines, "Revised allocations"); Assert.True(saved.Ok, saved.Code);
        var sent = await service.SubmitAsync(Actor(8), ids.Project, draft.Id.Value,
            saved.Version!, "Submit revision"); Assert.True(sent.Ok, sent.Code);
        var beforeAudit = await f.Db.ConstructionAuditEntries.AsNoTracking().CountAsync();
        var result = await service.ApproveAsync(Actor(9), ids.Project, draft.Id.Value,
            sent.Version!, "Approve via ordinary path");
        Assert.False(result.Ok); Assert.Equal("BudgetTransferWorkflowRequired", result.Code);
        await using var verify = f.NewContext();
        var old = await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x => x.ID == baseline.Id);
        var proposed = await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x => x.ID == draft.Id);
        Assert.True(old.IsBaseline); Assert.Equal("Approved", old.Status); Assert.Equal(baseline.Version, old.RowVersion);
        Assert.False(proposed.IsBaseline); Assert.Equal("Submitted", proposed.Status);
        Assert.Null(proposed.ApprovedBy); Assert.Null(proposed.ApprovedAt); Assert.Equal(sent.Version, proposed.RowVersion);
        Assert.Equal(beforeAudit, await verify.ConstructionAuditEntries.AsNoTracking().CountAsync());
        Assert.Equal(100m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking()
            .SingleAsync(x => x.BudgetVersionId == baseline.Id)).Amount);
        Assert.Empty(await verify.Set<ConstructionBudgetTransferDecision>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(75)]
    public async Task Ordinary_revision_without_a_mixed_allocation_movement_remains_usable(int amount)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var service = Service(f);
        var first = await Approved(service, ids);
        var draft = await service.CreateAsync(Actor(), ids.Project, "Revised", first.Id, first.Version, "Estimate revision");
        Assert.True(draft.Ok, draft.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, draft.Id!.Value, draft.Version!, Lines(ids, amount), "Review estimate");
        Assert.True(saved.Ok, saved.Code);
        var sent = await service.SubmitAsync(Actor(8), ids.Project, draft.Id.Value, saved.Version!, "Submit estimate");
        Assert.True(sent.Ok, sent.Code);
        var result = await service.ApproveAsync(Actor(9), ids.Project, draft.Id.Value, sent.Version!, "Independent estimate approval");
        Assert.True(result.Ok, result.Code);
        await using var verify = f.NewContext();
        Assert.Equal(draft.Id, (await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x => x.IsBaseline)).ID);
    }

    private sealed class Access(bool allow) : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(allow);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}
