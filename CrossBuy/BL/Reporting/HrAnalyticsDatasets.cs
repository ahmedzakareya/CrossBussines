using CrossBuy.BL.Hr;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HR10 — PEOPLE ANALYTICS ON REPORT STUDIO (2026-11-09).
	//
	// Three datasets, so headcount, turnover and the gratuity liability are reports, widgets and Studio layouts rather than
	// a dashboard of their own (printing is Report Studio's, owner rule):
	//   Hr.Headcount          one row per ACTIVE employee — group by branch, department, job title, nationality, gender.
	//   Hr.Leavers            one row per posted final settlement — turnover by month, reason and length of service.
	//   Hr.GratuityLiability  one row per active employee, from HrGratuityProvisionService (the provision screen's own
	//                         computation), so the report and the booked provision cannot disagree.
	// All company-scoped and failing closed on an unresolved tenant. Headcount and leavers carry no money and read under
	// the roster/HR key; the gratuity liability is pay data and needs the payroll key.
	// ============================================================================================
	public static class HrAnalyticsCodes
	{
		public const string Headcount = "Hr.Headcount";
		public const string Leavers = "Hr.Leavers";
		public const string GratuityLiability = "Hr.GratuityLiability";
		public const string PayPermission = "hr.payroll.reports.view";
	}

	public static class HrAnalyticsDatasets
	{
		private static ReportDatasetField F(string key, string ar, string en, ReportFieldType type = ReportFieldType.String, bool group = false, string? fmt = null, int w = 30, params ReportAggregate[] aggs) =>
			new() { Key = key, TitleAr = ar, TitleEn = en, Type = type, Groupable = group, Format = fmt, WidthMm = w, SupportedAggregates = aggs };

		public static ReportDatasetDefinition Headcount() => new()
		{
			DatasetCode = HrAnalyticsCodes.Headcount, Module = "Hr",
			TitleAr = "عدد الموظفين", TitleEn = "Headcount",
			DescriptionAr = "موظف نشط في كل سطر: الفرع والإدارة والمسمى والجنسية والجنس وتاريخ التعيين وسنوات الخدمة. جمّع حسب أي منها.",
			DescriptionEn = "One active employee per row: branch, department, job title, nationality, gender, joining date and years of service. Group by any of them.",
			DataSourceKey = HrAnalyticsCodes.Headcount, RequiredPermissionKey = RosterReportPermissions.View,
			MaxRows = 20000, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare, AvailableInStudio = true, AvailableAsWidget = true,
			Fields = new[]
			{
				F("Employee", "الموظف", "Employee", w: 50, aggs: ReportAggregate.Count),
				F("Branch", "الفرع", "Branch", group: true, w: 36),
				F("Department", "الإدارة", "Department", group: true, w: 36),
				F("JobTitle", "المسمى", "Job title", group: true, w: 36),
				F("Nationality", "الجنسية", "Nationality", group: true, w: 28),
				F("Gender", "الجنس", "Gender", group: true, w: 18),
				F("Joined", "التعيين", "Joined", ReportFieldType.Date, fmt: "yyyy-MM-dd", w: 24),
				F("ServiceYears", "سنوات الخدمة", "Years of service", ReportFieldType.Decimal, fmt: "0.0", w: 22, aggs: new[] { ReportAggregate.Average, ReportAggregate.Min, ReportAggregate.Max }),
			},
		};

		public static ReportDatasetDefinition Leavers() => new()
		{
			DatasetCode = HrAnalyticsCodes.Leavers, Module = "Hr",
			TitleAr = "تاركو الخدمة", TitleEn = "Leavers",
			DescriptionAr = "كل مخالصة نهاية خدمة مرحّلة: تاريخ الانتهاء والسبب وسنوات الخدمة. جمّع حسب الشهر لقراءة الدوران.",
			DescriptionEn = "Every posted end-of-service settlement: termination date, reason and years of service. Group by month to read turnover.",
			DataSourceKey = HrAnalyticsCodes.Leavers, RequiredPermissionKey = RosterReportPermissions.View,
			MaxRows = 20000, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare, AvailableInStudio = true, AvailableAsWidget = true,
			Fields = new[]
			{
				F("Employee", "الموظف", "Employee", w: 50, aggs: ReportAggregate.Count),
				F("TerminationDate", "تاريخ الانتهاء", "Termination date", ReportFieldType.Date, fmt: "yyyy-MM-dd", w: 26),
				F("Month", "الشهر", "Month", group: true, w: 18),
				F("Reason", "السبب", "Reason", group: true, w: 40),
				F("ServiceYears", "سنوات الخدمة", "Years of service", ReportFieldType.Decimal, fmt: "0.0", w: 22, aggs: new[] { ReportAggregate.Average }),
			},
		};

		public static ReportDatasetDefinition GratuityLiability() => new()
		{
			DatasetCode = HrAnalyticsCodes.GratuityLiability, Module = "Hr",
			TitleAr = "التزام مكافأة نهاية الخدمة", TitleEn = "Gratuity liability",
			DescriptionAr = "لكل موظف نشط: الأجر وسنوات الخدمة والمكافأة المستحقة على أساس الإنهاء — نفس حساب شاشة المخصص.",
			DescriptionEn = "Per active employee: wage, years of service and the gratuity owed on the termination basis — the provision screen's own computation.",
			DataSourceKey = HrAnalyticsCodes.GratuityLiability, RequiredPermissionKey = HrAnalyticsCodes.PayPermission,
			MaxRows = 20000, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare, AvailableInStudio = true, AvailableAsWidget = true,
			Fields = new[]
			{
				F("Employee", "الموظف", "Employee", w: 50, aggs: ReportAggregate.Count),
				F("Joined", "التعيين", "Joined", ReportFieldType.Date, fmt: "yyyy-MM-dd", w: 24),
				F("ServiceYears", "سنوات الخدمة", "Years of service", ReportFieldType.Decimal, fmt: "0.00", w: 22, aggs: new[] { ReportAggregate.Average }),
				F("Wage", "الأجر", "Wage", ReportFieldType.Decimal, fmt: "#,##0.000", w: 26, aggs: new[] { ReportAggregate.Sum, ReportAggregate.Average }),
				F("Gratuity", "المكافأة المستحقة", "Gratuity owed", ReportFieldType.Decimal, fmt: "#,##0.000", w: 30, aggs: new[] { ReportAggregate.Sum, ReportAggregate.Average, ReportAggregate.Max }),
			},
		};

		public static IEnumerable<ReportDatasetDefinition> All() { yield return Headcount(); yield return Leavers(); yield return GratuityLiability(); }
	}

	public sealed class HrAnalyticsReportDefinitionProvider : IReportDefinitionProvider
	{
		public string ProviderName => "HrAnalytics";

		public IEnumerable<ReportDefinition> GetDefinitions()
		{
			int order = 730;
			foreach (var dataset in HrAnalyticsDatasets.All())
				yield return new ReportDefinition
				{
					Code = dataset.DatasetCode, Module = dataset.Module,
					TitleAr = dataset.TitleAr, TitleEn = dataset.TitleEn, DescriptionAr = dataset.DescriptionAr, DescriptionEn = dataset.DescriptionEn,
					DataSourceKey = dataset.DataSourceKey, PermissionKey = dataset.RequiredPermissionKey,
					CategoryKey = "admin.reporting", Tags = new[] { "hr", "analytics" },
					Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
					Parameters = dataset.Parameters,
					DefaultSorts = new[] { ReportSort.By("Employee") },
					DefaultFormat = ReportOutputFormat.Html,
					Capabilities = new ReportCapabilities
					{
						Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf, ReportOutputFormat.Xlsx, ReportOutputFormat.Csv },
						MaxRows = dataset.MaxRows, PreviewRows = 200, AllowSchedule = true, AllowArchive = true, AllowShare = true,
					},
					Icon = "ki-outline ki-chart-simple", Color = "primary", SortOrder = order++,
				};
		}
	}

	internal static class HrAnalyticsSource
	{
		internal static bool Ar => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		internal static ReportDataSetBuilder Builder(ReportDataQuery q) => new(q.RequestedColumns.Count > 0 ? q.RequestedColumns : q.Definition.Columns);
	}

	public sealed class HrHeadcountDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrHeadcountDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrAnalyticsCodes.Headcount;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = HrAnalyticsSource.Builder(query);
			var company = query.Context.CompanyId;
			if (company <= 0) return b.Build(totalRowCount: 0);
			bool ar = HrAnalyticsSource.Ar;
			var rows = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == company && e.IsActive)
				.Select(e => new
				{
					e.FullName, e.FullNameEn, e.DateOfJoining, e.Gender,
					BranchAr = e.Branch == null ? null : e.Branch.NameAr, BranchEn = e.Branch == null ? null : e.Branch.Name,
					JobAr = e.JobTitle == null ? null : e.JobTitle.TitleAr, JobEn = e.JobTitle == null ? null : e.JobTitle.Title,
					DeptAr = _db.Hierarchicals.Where(h => h.H_ID == e.DepartmentID).Select(h => h.H_Name).FirstOrDefault(),
					DeptEn = _db.Hierarchicals.Where(h => h.H_ID == e.DepartmentID).Select(h => h.H_NameEn).FirstOrDefault(),
					NatAr = _db.Set<CrossBuy.Models.Context.Admin.CountriesLookup>().Where(c => c.ID == e.CountryID).Select(c => c.CountryNameAr).FirstOrDefault(),
					NatEn = _db.Set<CrossBuy.Models.Context.Admin.CountriesLookup>().Where(c => c.ID == e.CountryID).Select(c => c.CountryName).FirstOrDefault(),
				}).ToListAsync(ct);
			var today = DateTime.Today;
			foreach (var r in rows)
				b.AddRow(new Dictionary<string, object?>
				{
					["Employee"] = ar ? r.FullName : (r.FullNameEn ?? r.FullName),
					["Branch"] = ar ? (r.BranchAr ?? r.BranchEn) : (r.BranchEn ?? r.BranchAr),
					["Department"] = ar ? (r.DeptAr ?? r.DeptEn) : (r.DeptEn ?? r.DeptAr),
					["JobTitle"] = ar ? (r.JobAr ?? r.JobEn) : (r.JobEn ?? r.JobAr),
					["Nationality"] = ar ? (r.NatAr ?? r.NatEn) : (r.NatEn ?? r.NatAr),
					["Gender"] = r.Gender == "Male" ? (ar ? "ذكر" : "Male") : r.Gender == "Female" ? (ar ? "أنثى" : "Female") : r.Gender,
					["Joined"] = r.DateOfJoining,
					["ServiceYears"] = Math.Round((decimal)Math.Max(0, (today - r.DateOfJoining).TotalDays / 365.25), 1),
				});
			return b.Build();
		}
	}

	public sealed class HrLeaversDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrLeaversDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrAnalyticsCodes.Leavers;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = HrAnalyticsSource.Builder(query);
			var company = query.Context.CompanyId;
			if (company <= 0) return b.Build(totalRowCount: 0);
			var rows = await _db.FinalSettlements.AsNoTracking().Where(s => s.CompanyID == company).OrderByDescending(s => s.TerminationDate)
				.Select(s => new { s.EmployeeName, s.TerminationDate, s.Reason, s.ServiceYears }).ToListAsync(ct);
			foreach (var r in rows)
				b.AddRow(new Dictionary<string, object?>
				{
					["Employee"] = r.EmployeeName, ["TerminationDate"] = r.TerminationDate, ["Month"] = r.TerminationDate.ToString("yyyy-MM"),
					["Reason"] = r.Reason, ["ServiceYears"] = Math.Round(r.ServiceYears, 1),
				});
			return b.Build();
		}
	}

	public sealed class HrGratuityLiabilityDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrGratuityLiabilityDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrAnalyticsCodes.GratuityLiability;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = HrAnalyticsSource.Builder(query);
			var company = query.Context.CompanyId;
			if (company <= 0) return b.Build(totalRowCount: 0);
			var pv = await new HrGratuityProvisionService(_db).PreviewAsync(company, DateTime.Today, ct);
			foreach (var r in pv.Rows)
				b.AddRow(new Dictionary<string, object?>
				{
					["Employee"] = r.Name, ["Joined"] = r.Joined, ["ServiceYears"] = r.Years, ["Wage"] = r.Wage, ["Gratuity"] = r.Amount,
				});
			return b.Build();
		}
	}
}
