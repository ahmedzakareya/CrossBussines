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
		public const string LeaveLiability = "Hr.LeaveLiability";
		public const string Expiries = "Hr.Expiries";
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

		public static ReportDatasetDefinition LeaveLiability() => new()
		{
			DatasetCode = HrAnalyticsCodes.LeaveLiability, Module = "Hr",
			TitleAr = "التزام رصيد الإجازات", TitleEn = "Leave balance liability",
			DescriptionAr = "لكل موظف نشط ولكل إجازة سنوية في حزمة الدولة: الرصيد المتبقي هذا العام × الأجر اليومي (الأجر الكامل ÷ أيام الشهر في الحزمة) — ما تدين به الشركة لو صُرف الرصيد نقداً.",
			DescriptionEn = "Per active employee and annual-leave type in the country pack: this year's remaining balance x the daily wage (full wage / the pack's days per month) — what the company owes if the balance were paid out.",
			DataSourceKey = HrAnalyticsCodes.LeaveLiability, RequiredPermissionKey = HrAnalyticsCodes.PayPermission,
			MaxRows = 20000, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare, AvailableInStudio = true, AvailableAsWidget = true,
			Fields = new[]
			{
				F("Employee", "الموظف", "Employee", w: 50, aggs: ReportAggregate.Count),
				F("LeaveType", "نوع الإجازة", "Leave type", group: true, w: 30),
				F("Entitlement", "الاستحقاق", "Entitlement", ReportFieldType.Decimal, fmt: "0", w: 20, aggs: ReportAggregate.Sum),
				F("CarriedIn", "مرحّل", "Carried in", ReportFieldType.Decimal, fmt: "0", w: 18, aggs: ReportAggregate.Sum),
				F("Used", "المستخدم", "Used", ReportFieldType.Decimal, fmt: "0", w: 18, aggs: ReportAggregate.Sum),
				F("Remaining", "المتبقي", "Remaining", ReportFieldType.Decimal, fmt: "0", w: 20, aggs: new[] { ReportAggregate.Sum, ReportAggregate.Average }),
				F("DailyWage", "الأجر اليومي", "Daily wage", ReportFieldType.Decimal, fmt: "#,##0.000", w: 24),
				F("Liability", "الالتزام", "Liability", ReportFieldType.Decimal, fmt: "#,##0.000", w: 28, aggs: new[] { ReportAggregate.Sum, ReportAggregate.Max }),
			},
		};

		public static ReportDatasetDefinition Expiries() => new()
		{
			DatasetCode = HrAnalyticsCodes.Expiries, Module = "Hr",
			TitleAr = "انتهاء الوثائق والعقود", TitleEn = "Document and contract expiries",
			DescriptionAr = "كل وثيقة موظف (مدنية، جواز، إذن عمل…) وكل عقد ساري له تاريخ انتهاء، للموظفين النشطين: الأيام المتبقية والحالة (منتهية، خلال 30 يوماً، خلال 90 يوماً، سارية).",
			DescriptionEn = "Every employee document (civil ID, passport, work permit…) and every active contract with an end date, for active employees: days left and status (expired, within 30 days, within 90 days, valid).",
			DataSourceKey = HrAnalyticsCodes.Expiries, RequiredPermissionKey = RosterReportPermissions.View,
			MaxRows = 20000, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare, AvailableInStudio = true, AvailableAsWidget = true,
			Fields = new[]
			{
				F("Employee", "الموظف", "Employee", w: 50, aggs: ReportAggregate.Count),
				F("Kind", "النوع", "Kind", group: true, w: 30),
				F("Number", "الرقم", "Number", w: 28),
				F("ExpiryDate", "تاريخ الانتهاء", "Expiry date", ReportFieldType.Date, fmt: "yyyy-MM-dd", w: 24),
				F("DaysLeft", "الأيام المتبقية", "Days left", ReportFieldType.Integer, fmt: "0", w: 20, aggs: ReportAggregate.Min),
				F("Status", "الحالة", "Status", group: true, w: 26),
			},
		};

		public static IEnumerable<ReportDatasetDefinition> All() { yield return Headcount(); yield return Leavers(); yield return GratuityLiability(); yield return LeaveLiability(); yield return Expiries(); }
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

	public sealed class HrLeaveLiabilityDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrLeaveLiabilityDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrAnalyticsCodes.LeaveLiability;

		// The same balance arithmetic as LeaveDashboardService.RemainingForTypeInYearAsync (entitlement + carried in - used
		// - encashed), done in set queries for the whole company instead of five queries per employee.
		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = HrAnalyticsSource.Builder(query);
			var company = query.Context.CompanyId;
			if (company <= 0) return b.Build(totalRowCount: 0);
			var eff = await new HrRulePackService(_db).EffectiveAsync(company, ct);
			if (eff is not { } pack || pack.Rules.WageDaysPerMonth <= 0) return b.Build(totalRowCount: 0);
			bool ar = HrAnalyticsSource.Ar;
			int year = DateTime.Today.Year;

			var annual = (await _db.LeaveTypes.AsNoTracking().Select(t => new { t.ID, t.NameAr, t.NameEn }).ToListAsync(ct))
				.Where(t => HrLeaveMatch.For(pack.Rules, t.NameEn, t.NameAr)?.Code == "annual").ToList();
			if (annual.Count == 0) return b.Build(totalRowCount: 0);
			var typeIds = annual.Select(t => t.ID).ToList();

			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == company && e.IsActive)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var ids = emps.Select(e => e.ID).ToList();
			var policyOf = (await _db.PolicyAssignments.AsNoTracking().Where(p => ids.Contains(p.EmployeeID))
				.Select(p => new { p.EmployeeID, p.LeavePolicyTypeID }).ToListAsync(ct))
				.GroupBy(p => p.EmployeeID).ToDictionary(g => g.Key, g => g.First().LeavePolicyTypeID);
			var entitlement = (await _db.LeavePolicies.AsNoTracking().Where(lp => typeIds.Contains(lp.LeaveTypeID))
				.Select(lp => new { lp.LeavePolicyTypeID, lp.LeaveTypeID, lp.EntitlementDaysPerYear }).ToListAsync(ct))
				.GroupBy(x => (x.LeavePolicyTypeID, x.LeaveTypeID)).ToDictionary(g => g.Key, g => (decimal)g.Max(x => x.EntitlementDaysPerYear));
			var carried = (await _db.LeaveCarryOvers.AsNoTracking().Where(c => ids.Contains(c.EmployeeID) && typeIds.Contains(c.LeaveTypeID) && c.Year == year)
				.Select(c => new { c.EmployeeID, c.LeaveTypeID, c.Days }).ToListAsync(ct))
				.GroupBy(x => (x.EmployeeID, x.LeaveTypeID)).ToDictionary(g => g.Key, g => (decimal)g.Sum(x => x.Days));
			var used = (await _db.LeaveRequests.AsNoTracking().Where(r => ids.Contains(r.EmployeeID) && typeIds.Contains(r.LeaveTypeID) && r.Status == 1 && r.StartDate.Year == year)
				.Select(r => new { r.EmployeeID, r.LeaveTypeID, r.Days }).ToListAsync(ct))
				.GroupBy(x => (x.EmployeeID, x.LeaveTypeID)).ToDictionary(g => g.Key, g => (decimal)g.Sum(x => x.Days));
			var encashed = (await _db.LeaveEncashments.AsNoTracking().Where(x => ids.Contains(x.EmployeeID) && typeIds.Contains(x.LeaveTypeID) && x.Year == year)
				.Select(x => new { x.EmployeeID, x.LeaveTypeID, x.Days }).ToListAsync(ct))
				.GroupBy(x => (x.EmployeeID, x.LeaveTypeID)).ToDictionary(g => g.Key, g => (decimal)g.Sum(x => x.Days));
			var pay = await new HrSalaryService(_db).ForManyAsync(ids, DateTime.Today, ct);

			foreach (var e in emps)
			{
				if (!policyOf.TryGetValue(e.ID, out var policy)) continue;   // no policy, no balance (as the leave screen says)
				var daily = pay.TryGetValue(e.ID, out var p) ? Math.Round(p.Total / pack.Rules.WageDaysPerMonth, 3, MidpointRounding.AwayFromZero) : 0m;
				foreach (var t in annual)
				{
					var ent = entitlement.GetValueOrDefault((policy, t.ID));
					var cin = carried.GetValueOrDefault((e.ID, t.ID)); var u = used.GetValueOrDefault((e.ID, t.ID)); var x = encashed.GetValueOrDefault((e.ID, t.ID));
					if (ent == 0 && cin == 0 && u == 0) continue;
					var remaining = ent + cin - u - x;
					b.AddRow(new Dictionary<string, object?>
					{
						["Employee"] = ar ? e.FullName : (e.FullNameEn ?? e.FullName),
						["LeaveType"] = ar ? (t.NameAr ?? t.NameEn) : (t.NameEn ?? t.NameAr),
						["Entitlement"] = ent, ["CarriedIn"] = cin, ["Used"] = u, ["Remaining"] = remaining,
						["DailyWage"] = daily,
						["Liability"] = remaining > 0 ? Math.Round(remaining * daily, 3, MidpointRounding.AwayFromZero) : 0m,
					});
				}
			}
			return b.Build();
		}
	}

	public sealed class HrExpiriesDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrExpiriesDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrAnalyticsCodes.Expiries;

		private static readonly Dictionary<string, (string Ar, string En)> Kinds = new(StringComparer.OrdinalIgnoreCase)
		{
			["NationalId"] = ("البطاقة المدنية", "Civil ID"), ["Passport"] = ("جواز السفر", "Passport"), ["Qualification"] = ("المؤهل", "Qualification"),
			["Insurance"] = ("التأمين", "Insurance"), ["WorkPermit"] = ("إذن العمل", "Work permit"), ["Contract"] = ("العقد", "Contract"), ["Other"] = ("وثيقة", "Document"),
			["Permanent"] = ("عقد دائم", "Permanent contract"), ["FixedTerm"] = ("عقد محدد المدة", "Fixed-term contract"), ["Probation"] = ("عقد التجربة", "Probation contract"), ["PartTime"] = ("عقد دوام جزئي", "Part-time contract"),
		};

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = HrAnalyticsSource.Builder(query);
			var company = query.Context.CompanyId;
			if (company <= 0) return b.Build(totalRowCount: 0);
			bool ar = HrAnalyticsSource.Ar;
			var today = DateTime.Today;
			var active = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == company && e.IsActive);
			var docs = await _db.EmployeeDocuments.AsNoTracking().Where(d => d.CompanyID == company && d.ExpiryDate != null)
				.Join(active, d => d.EmployeeID, e => e.ID, (d, e) => new { e.FullName, e.FullNameEn, Kind = d.DocType, Number = d.DocNumber, Expiry = d.ExpiryDate!.Value })
				.ToListAsync(ct);
			var contracts = await _db.EmploymentContracts.AsNoTracking().Where(c => c.CompanyID == company && c.EndDate != null && c.Status == "Active")
				.Join(active, c => c.EmployeeID, e => e.ID, (c, e) => new { e.FullName, e.FullNameEn, Kind = c.ContractType, Number = (string?)null, Expiry = c.EndDate!.Value })
				.ToListAsync(ct);
			foreach (var r in docs.Concat(contracts).OrderBy(r => r.Expiry))
			{
				var left = (r.Expiry.Date - today).Days;
				var k = Kinds.TryGetValue(r.Kind ?? "Other", out var kn) ? kn : Kinds["Other"];
				b.AddRow(new Dictionary<string, object?>
				{
					["Employee"] = ar ? r.FullName : (r.FullNameEn ?? r.FullName),
					["Kind"] = ar ? k.Ar : k.En, ["Number"] = r.Number, ["ExpiryDate"] = r.Expiry, ["DaysLeft"] = left,
					["Status"] = left < 0 ? (ar ? "منتهية" : "Expired") : left <= 30 ? (ar ? "خلال 30 يوماً" : "Within 30 days")
						: left <= 90 ? (ar ? "خلال 90 يوماً" : "Within 90 days") : (ar ? "سارية" : "Valid"),
				});
			}
			return b.Build();
		}
	}
}
