using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests;

public class KpiRegistryTests
{
    private sealed class Permissions(params string[] denied) : IReportPermissionEvaluator
    {
        public Task<bool> HasPermissionAsync(string key, BusinessContext context, CancellationToken ct = default)
            => Task.FromResult(!denied.Contains(key));
    }
    private static ReportDatasetRegistry Datasets(IReportPermissionEvaluator? p = null)
        => new(AccountingDatasets.All().Concat(ProjectsDatasets.All()), p ?? new Permissions());
    private static readonly FixedReportClock Clock = new(new DateTime(2026, 10, 11));
    private static BusinessContext Actor(int company = 7) => new()
        { CompanyId = company, EmployeeId = 8, Source = BusinessContextSource.Test };
    public static IEnumerable<object[]> Catalog() => KpiCatalog.All().Select(k => new object[] { k.Key });

    private sealed class Source(string key, bool truncated = false) : IReportDataSource
    {
        public string Key => key;
        public int Reads { get; private set; }
        public int Company { get; private set; }
        public Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
        {
            Reads++; Company = query.Context.CompanyId;
            var b = new ReportDataSetBuilder(query.RequestedColumns);
            for (var i = 0; i < 3; i++)
            {
                if (query.Parameters.GetString("Status") == "Posted" && i == 1) continue;
                b.AddRow(query.RequestedColumns.ToDictionary(c => c.Key, c => (object?)(c.Type switch
                {
                    ReportFieldType.Boolean => i != 1,
                    ReportFieldType.String => i == 1 ? "Draft" : "Posted",
                    _ => 10m
                })));
            }
            return Task.FromResult(b.Build(truncated));
        }
    }
    private static KpiEvaluator Evaluate(KpiRegistry registry, ReportDatasetRegistry datasets,
        IReportDataSource source, IReportPermissionEvaluator permissions, BusinessContext? actor = null)
        => new(registry, datasets, new ReportDataSourceRegistry(new[] { source }), permissions,
            new ReportParameterBinder(Clock), new ReportDataShaper(), new StubContextAccessor(actor ?? Actor()), Clock);

    [Theory]
    [MemberData(nameof(Catalog))]
    public async Task Every_registered_kpi_requires_dataset_authority_before_fetch(string key)
    {
        var d = Datasets(); var r = new KpiRegistry(d); var k = r.Resolve(key);
        var permission = new Permissions(d.Resolve(k.DatasetCode).RequiredPermissionKey);
        var source = new Source(d.Resolve(k.DatasetCode).DataSourceKey);
        var result = await Evaluate(r, d, source, permission).EvaluateAsync(key, new Dictionary<string, string?>());
        Assert.Equal("DENIED", result.Status); Assert.Null(result.Value); Assert.Equal(0, source.Reads);
    }

    [Theory]
    [MemberData(nameof(Catalog))]
    public async Task Every_registered_kpi_uses_resolved_company_and_rejects_truncated_totals(string key)
    {
        var d = Datasets(); var r = new KpiRegistry(d); var k = r.Resolve(key);
        var source = new Source(d.Resolve(k.DatasetCode).DataSourceKey);
        var result = await Evaluate(r, d, source, new Permissions()).EvaluateAsync(key, new Dictionary<string, string?>());
        Assert.Equal("READY", result.Status); Assert.NotNull(result.Value); Assert.Equal(7, source.Company);
        var partial = new Source(source.Key, true);
        result = await Evaluate(r, d, partial, new Permissions()).EvaluateAsync(key, new Dictionary<string, string?>());
        Assert.Equal("TRUNCATED", result.Status); Assert.Null(result.Value);
    }

    [Fact]
    public async Task Confidential_margin_requires_its_own_authority_and_unresolved_company_never_fetches()
    {
        var d = Datasets(); var r = new KpiRegistry(d);
        var source = new Source(AccountingDatasetCodes.CustomerProfitability);
        var result = await Evaluate(r,d,source,new Permissions(AccountingReportPermissions.Profitability))
            .EvaluateAsync("accounting.customer-margin", new Dictionary<string,string?>());
        Assert.Equal("DENIED",result.Status); Assert.Equal(0,source.Reads);
        result = await Evaluate(r,d,source,new Permissions(),Actor(0))
            .EvaluateAsync("accounting.customer-margin", new Dictionary<string,string?>());
        Assert.Equal("DENIED",result.Status); Assert.Equal(0,source.Reads);
    }

    [Fact]
    public void Invalid_definitions_fail_at_registry_construction()
    {
        var d = Datasets(); var k = KpiCatalog.All().First();
        foreach (var invalid in new[]
        {
            k with { DatasetCode = "work.tasks" }, k with { ValueField = "not-a-field" },
            k with { RequiredPermissionKey = ReportPermissions.Public }, k with { DateField = "Status" },
            k with { Aggregate = ReportAggregate.CountDistinct }, k with { DrillTargetKey = "invented" },
            k with { FixedParameters = new Dictionary<string,string?> { ["CompanyId"] = "8" } },
            k with { FixedParameters = new Dictionary<string,string?> { ["Status"] = "Invented" } },
            k with { Filters = new[] { ReportFilter.Eq("unknown", "x") } },
            k with { Filters = new[] { ReportFilter.Eq("GrandTotalBase", "bad-number") } }
        }) Assert.Throws<ArgumentException>(() => new KpiRegistry(d,new[] {invalid}));
        Assert.Throws<ArgumentException>(() => new KpiRegistry(d,new[] {k,k}));
    }

    private sealed class ProjectAccess : IProjectsAccessService
    {
        public AccessScope Scope { get; set; } = AccessScope.Company(7);
        public Task<bool> CanAsync(BusinessContext c,string a,PermissionTarget? t=null,CancellationToken ct=default) => Task.FromResult(true);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c,string a,CancellationToken ct=default) => Task.FromResult(Scope);
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c,CancellationToken ct=default)
            => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }

    [Fact]
    public async Task Posted_sales_use_base_currency_and_cannot_be_widened_to_drafts_or_another_tenant()
    {
        using var h = new PlatformTestHost(companyId: 7);
        foreach (var (company, status, total) in new[] { (7,"Posted",10m), (7,"Posted",25m), (7,"Draft",900m), (8,"Posted",700m) })
            h.Seed.SalesInvoices.Add(new SalesInvoice
            { CompanyID=company, CustomerId=1, InvoiceDate=Clock.LocalNow.Date, Status=status, GrandTotal=999m, GrandTotalBase=total });
        h.Seed.SaveChanges();
        var d=Datasets(); var r=new KpiRegistry(d);
        var result=await Evaluate(r,d,new SalesRevenueDataSource(h.Db),new Permissions())
            .EvaluateAsync("accounting.posted-sales-base",new Dictionary<string,string?> { ["Status"]="Draft", ["CompanyId"]="8" });
        Assert.Equal("READY",result.Status); Assert.Equal(35m,result.Value); Assert.Equal(7,result.CompanyId);
    }

    [Fact]
    public async Task Real_project_source_preserves_tenant_and_member_scope_even_with_cross_company_breadth()
    {
        using var h = new ProjectsReportingTests.ProjectsFixture();
        h.AddProject("HOME-1"); h.AddProject("HOME-2");
        h.AddProject("FOREIGN-1",8); h.AddProject("FOREIGN-2",8); h.AddProject("FOREIGN-3",8);
        var access = new ProjectAccess { Scope = AccessScope.CrossCompany() };
        var d = Datasets(); var r = new KpiRegistry(d); var source = new ProjectsDataSource(h.Db,access);
        var result = await Evaluate(r,d,source,new Permissions()).EvaluateAsync("projects.active",new Dictionary<string,string?>());
        Assert.Equal("READY",result.Status); Assert.Equal(2m,result.Value);
        result = await Evaluate(r,d,source,new Permissions(),Actor(8)).EvaluateAsync("projects.active",new Dictionary<string,string?>());
        Assert.Equal(3m,result.Value);
        access.Scope = AccessScope.None();
        result = await Evaluate(r,d,source,new Permissions()).EvaluateAsync("projects.active",new Dictionary<string,string?>());
        Assert.Equal(0m,result.Value);
    }
}
