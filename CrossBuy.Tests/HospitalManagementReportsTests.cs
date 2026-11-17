using CrossBuy.BL.Reporting;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H49/H50 — the management reports and the dashboard's KPIs, with no database: the prototype's fifteen
	// reports in its four groups, each a well-formed Report Studio dataset whose source answers to its own code; the
	// money behind the billing desk's key; the period parameters; the provider's definitions agreeing with the
	// datasets; and every dashboard KPI accepted by the platform's own registry over those datasets.
	// ==========================================================================================
	public class HospitalManagementReportsTests
	{
		private static readonly IReadOnlyList<HospitalManagementReport> Reports = HospitalManagementDatasets.All();

		[Fact]
		public void The_prototypes_fifteen_reports_in_its_four_groups()
		{
			Assert.Equal(15, Reports.Count);
			Assert.Equal(new[] { 4, 4, 4, 3 }, HospitalReportGroups.All.Select(g => Reports.Count(r => r.Group == g)));
			Assert.Equal(15, Reports.Select(r => r.Dataset.DatasetCode).Distinct().Count());
			Assert.All(Reports, r => Assert.StartsWith("Hospital.", r.Dataset.DatasetCode));
		}

		[Fact]
		public void Money_is_the_billing_desks_and_the_rest_is_the_care_teams()
		{
			// H156: the clinical and operational analyses carry patients and diagnoses — never a reception-only reader's
			foreach (var r in Reports)
				Assert.Equal(r.Group is HospitalReportGroups.Financial or HospitalReportGroups.Insurance ? HospitalReportPermissions.Finance : HospitalReportPermissions.Clinical,
					r.Dataset.RequiredPermissionKey);
		}

		[Fact]
		public void Every_dataset_is_complete_and_declares_the_company_parameter()
		{
			foreach (var d in Reports.Select(r => r.Dataset))
			{
				Assert.Equal(d.DatasetCode, d.DataSourceKey);
				Assert.False(string.IsNullOrWhiteSpace(d.TitleAr)); Assert.False(string.IsNullOrWhiteSpace(d.TitleEn));
				Assert.False(string.IsNullOrWhiteSpace(d.DescriptionAr)); Assert.False(string.IsNullOrWhiteSpace(d.DescriptionEn));
				Assert.Equal(d.Fields.Count, d.Fields.Select(f => f.Key).Distinct().Count());
				Assert.Contains(d.Parameters, p => p.SystemSupplied);
				Assert.True(d.MaxRows > 0);
			}
		}

		[Fact]
		public void A_period_report_asks_from_and_to_the_ageing_asks_as_of_and_the_waiting_list_asks_nothing()
		{
			string Keys(string code) => string.Join(",", Reports.Single(r => r.Dataset.DatasetCode == code).Dataset.Parameters.Where(p => !p.SystemSupplied).Select(p => p.Key));
			Assert.Equal("AsOf", Keys(HospitalReportCodes.InsuranceAgeing));
			Assert.Equal("", Keys(HospitalReportCodes.PendingApprovals));
			foreach (var r in Reports.Where(r => r.Dataset.DatasetCode is not (HospitalReportCodes.InsuranceAgeing or HospitalReportCodes.PendingApprovals)))
				Assert.Equal("From,To", Keys(r.Dataset.DatasetCode));
		}

		[Fact]
		public void The_default_sort_is_a_field_of_the_report_and_the_provider_agrees_with_the_datasets()
		{
			var defs = new HospitalManagementReportProvider().GetDefinitions().ToList();
			Assert.Equal(15, defs.Count);
			foreach (var r in Reports)
			{
				Assert.Contains(r.Dataset.Fields, f => f.Key == r.Sort);
				var def = defs.Single(x => x.Code == r.Dataset.DatasetCode);
				Assert.Equal(r.Dataset.RequiredPermissionKey, def.PermissionKey);
				Assert.Equal(r.Dataset.Fields.Select(f => f.Key), def.Columns.Select(c => c.Key));
			}
		}

		[Fact]
		public void Every_dashboard_kpi_is_accepted_by_the_platform_registry_over_the_hospital_datasets()
		{
			var datasets = new ReportDatasetRegistry(HospitalManagementDatasets.Datasets(), new AllowAll());
			var registry = new KpiRegistry(datasets, HospitalKpis.All());   // throws on an unknown field, aggregate, filter or dataset
			Assert.Equal(12, registry.Definitions.Count);
			Assert.Contains(HospitalKpis.All(), k => k.Key == HospitalKpis.Revenue && k.RequiredPermissionKey == HospitalReportPermissions.Finance);
			Assert.Contains(KpiCatalog.All(), k => k.Key == HospitalKpis.BedsOccupied);
		}

		[Fact]
		public void A_sum_tile_reads_a_field_its_source_never_leaves_empty()
		{
			// KpiEvaluator refuses a Sum over a null value, so a Sum tile must read a field every row fills
			var filled = new HashSet<string> { "Appointments", "Seen", "WaitingNow", "Beds", "OccupiedNow", "Gross", "Amount", "Claimed", "Rejected" };
			foreach (var k in HospitalKpis.All().Where(k => k.Aggregate == ReportAggregate.Sum))
				Assert.Contains(k.ValueField, filled);
		}

		private sealed class AllowAll : IReportPermissionEvaluator
		{
			public Task<bool> HasPermissionAsync(string permissionKey, CrossBuy.Models.Platform.BusinessContext context, CancellationToken cancellationToken = default) => Task.FromResult(true);
		}
	}
}
