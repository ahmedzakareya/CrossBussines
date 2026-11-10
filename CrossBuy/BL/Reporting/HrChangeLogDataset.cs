using System.Globalization;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HR CHANGE LOG (2026-11-10) — every sensitive change to an employee, company-wide, in one Report Studio report:
	// job / branch / department / employment type / hire (EmployeeHistory), civil ID / IBAN / bank (EmployeeHistory, the IBAN
	// masked when it was written), and salary (EmployeeSalaries, each effective-dated row against the one before it).
	// For audit and to spot tampering: who changed what, when it was entered, and from when it applies.
	//
	// The report reads under the HR reports key. The salary AMOUNTS are separate Confidential fields that need the payroll
	// key, so an HR officer sees THAT a salary changed and by whom, and only payroll sees the figures (the platform's
	// field-level rule, ReportDataset.IsFieldVisible). The text Old/New columns never carry a salary.
	// ============================================================================================
	public static class HrChangeLogCodes
	{
		public const string ChangeLog = "Hr.ChangeLog";
	}

	public static class HrChangeLogDataset
	{
		private static ReportDatasetField F(string key, string ar, string en, ReportFieldType type = ReportFieldType.String, int w = 30, bool visible = true,
			bool group = false, bool pay = false) => new()
		{
			Key = key, TitleAr = ar, TitleEn = en, Type = type, WidthMm = w, VisibleByDefault = visible, Groupable = group,
			Align = type is ReportFieldType.Money or ReportFieldType.Decimal or ReportFieldType.Integer ? ReportAlign.End : ReportAlign.Start,
			Sensitivity = pay ? ReportFieldSensitivity.Confidential : ReportFieldSensitivity.Normal,
			RequiredPermissionKey = pay ? HrAnalyticsCodes.PayPermission : null,
		};

		public static ReportDatasetDefinition Definition() => new()
		{
			DatasetCode = HrChangeLogCodes.ChangeLog, Module = "Hr",
			TitleAr = "سجل تغييرات الموارد البشرية", TitleEn = "HR change log",
			DescriptionAr = "كل تغيير حساس على بيانات الموظفين في الفترة: الوظيفة والفرع والإدارة، الرقم المدني والـ IBAN والبنك، والراتب — القديم والجديد ومن غيّر ومتى. مبالغ الراتب تظهر لصلاحية الرواتب فقط.",
			DescriptionEn = "Every sensitive change to employee data in the period: job, branch, department, civil ID, IBAN, bank and salary — old and new, who changed it and when. Salary figures show only to payroll.",
			DataSourceKey = HrChangeLogCodes.ChangeLog, RequiredPermissionKey = RosterReportPermissions.View,
			MaxRows = 20000, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare, AvailableInStudio = true,
			Fields = new[]
			{
				F("ChangedAt", "وقت التغيير", "Changed at", ReportFieldType.DateTime, 30),
				F("EffectiveFrom", "يسري من", "Effective from", ReportFieldType.Date, 22),
				F("Employee", "الموظف", "Employee", w: 46, group: true),
				F("EmployeeNo", "رقم الموظف", "Employee no.", ReportFieldType.Integer, 16, visible: false),
				F("Area", "المجال", "Area", w: 26, group: true),
				F("Field", "البند", "Item", w: 30, group: true),
				F("OldValue", "القيمة السابقة", "Old value", w: 40),
				F("NewValue", "القيمة الجديدة", "New value", w: 40),
				F("OldSalary", "الراتب السابق", "Old salary", ReportFieldType.Money, 26, pay: true),
				F("NewSalary", "الراتب الجديد", "New salary", ReportFieldType.Money, 26, pay: true),
				F("ChangedBy", "بواسطة", "Changed by", w: 40, group: true),
			},
			Parameters = new[]
			{
				new ReportParameterDescriptor { Key = "From", TitleAr = "من تاريخ", TitleEn = "From", Type = ReportFieldType.Date, Required = true, DefaultValue = "month-start",
					HelpTextAr = "بداية الفترة (شاملة).", HelpTextEn = "Start of the period (inclusive)." },
				new ReportParameterDescriptor { Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To", Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
					HelpTextAr = "نهاية الفترة (شاملة).", HelpTextEn = "End of the period (inclusive)." },
				AccountingDatasets.CompanyParam(),
			},
		};
	}

	public sealed class HrChangeLogReportDefinitionProvider : IReportDefinitionProvider
	{
		public string ProviderName => "HrChangeLog";

		public IEnumerable<ReportDefinition> GetDefinitions()
		{
			var d = HrChangeLogDataset.Definition();
			yield return new ReportDefinition
			{
				Code = d.DatasetCode, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn, DescriptionAr = d.DescriptionAr, DescriptionEn = d.DescriptionEn,
				DataSourceKey = d.DataSourceKey, PermissionKey = d.RequiredPermissionKey, CategoryKey = "admin.reporting", Tags = new[] { "hr", "audit" },
				Columns = d.Fields.Select(f => f.ToColumn()).ToList(), Parameters = d.Parameters,
				DefaultSorts = new[] { ReportSort.By("ChangedAt", descending: true) }, DefaultFormat = ReportOutputFormat.Html,
				Capabilities = new ReportCapabilities
				{
					Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf, ReportOutputFormat.Xlsx, ReportOutputFormat.Csv },
					MaxRows = d.MaxRows, PreviewRows = 200, AllowSchedule = true, AllowArchive = true, AllowShare = true,
				},
				Icon = "ki-outline ki-shield-search", Color = "primary", SortOrder = 780,
			};
		}
	}

	public sealed class HrChangeLogDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrChangeLogDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrChangeLogCodes.ChangeLog;

		private static readonly Dictionary<string, (string Ar, string En, string AreaAr, string AreaEn)> Fields = new()
		{
			["Hire"] = ("تعيين", "Hire", "الوظيفة والهيكل", "Job & organisation"),
			["JobTitle"] = ("المسمى الوظيفي", "Job title", "الوظيفة والهيكل", "Job & organisation"),
			["Branch"] = ("الفرع", "Branch", "الوظيفة والهيكل", "Job & organisation"),
			["Department"] = ("الإدارة", "Department", "الوظيفة والهيكل", "Job & organisation"),
			["EmploymentType"] = ("نوع التوظيف", "Employment type", "الوظيفة والهيكل", "Job & organisation"),
			["CivilId"] = ("الرقم المدني", "Civil ID", "الهوية والبنك", "Identity & bank"),
			["Iban"] = ("IBAN", "IBAN", "الهوية والبنك", "Identity & bank"),
			["BankName"] = ("البنك", "Bank", "الهوية والبنك", "Identity & bank"),
		};

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			var company = query.Context.CompanyId;
			if (company <= 0) return b.Build(totalRowCount: 0);
			var today = DateTime.Today;
			var from = (query.Parameters.GetDate("From") ?? new DateTime(today.Year, today.Month, 1)).Date;
			var toEx = (query.Parameters.GetDate("To") ?? today).Date.AddDays(1);
			bool ar = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var people = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == company)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			string Who(int? id) => id is int i && people.TryGetValue(i, out var p) ? (ar ? p.FullName : (p.FullNameEn ?? p.FullName)) : (ar ? "النظام" : "System");

			// names for the org ids EmployeeHistory stores
			var titles = await _db.JobTitles.AsNoTracking().Select(t => new { t.ID, t.TitleAr, t.Title }).ToDictionaryAsync(t => t.ID, ct);
			var branches = await _db.Branches.AsNoTracking().Select(x => new { x.ID, x.NameAr, x.Name }).ToDictionaryAsync(x => x.ID, ct);
			var units = await _db.Hierarchicals.AsNoTracking().Select(h => new { h.H_ID, h.H_Name, h.H_NameEn }).ToDictionaryAsync(h => h.H_ID, ct);
			string? Name(string field, string? v)
			{
				if (v == null || !int.TryParse(v, out var id)) return v;
				return field switch
				{
					"JobTitle" when titles.TryGetValue(id, out var t) => ar ? (t.TitleAr ?? t.Title) : (t.Title ?? t.TitleAr),
					"Branch" when branches.TryGetValue(id, out var x) => ar ? (x.NameAr ?? x.Name) : (x.Name ?? x.NameAr),
					"Department" when units.TryGetValue(id, out var u) => ar ? (u.H_Name ?? u.H_NameEn) : (u.H_NameEn ?? u.H_Name),
					_ => v,
				};
			}

			var rows = new List<(DateTime At, Dictionary<string, object?> Row)>();
			// A row written before 2026-11-10 carries no time (EmployeeService did not set ChangedAt, so EF wrote 0001-01-01):
			// its effective date is the day it was entered, so it stands in.
			var legacy = new DateTime(2000, 1, 1);
			var hist = await _db.EmployeeHistory.AsNoTracking()
				.Where(h => h.CompanyID == company && ((h.ChangedAt >= from && h.ChangedAt < toEx) || (h.ChangedAt < legacy && h.EffectiveFrom >= from && h.EffectiveFrom < toEx)))
				.ToListAsync(ct);
			foreach (var h in hist)
			{
				var at = h.ChangedAt < legacy ? h.EffectiveFrom : h.ChangedAt;
				var f = Fields.TryGetValue(h.Field, out var fl) ? fl : (h.Field, h.Field, "أخرى", "Other");
				rows.Add((at, new Dictionary<string, object?>
				{
					["ChangedAt"] = at, ["EffectiveFrom"] = h.EffectiveFrom, ["Employee"] = Who(h.EmployeeID), ["EmployeeNo"] = h.EmployeeID,
					["Area"] = ar ? f.Item3 : f.Item4, ["Field"] = ar ? f.Item1 : f.Item2,
					["OldValue"] = Name(h.Field, h.OldValue), ["NewValue"] = h.Field == "Hire" ? h.NewValue : Name(h.Field, h.NewValue),
					["ChangedBy"] = Who(h.ChangedByEmployeeId),
				}));
			}

			// salary: each row entered in the period, against the employee's row in force before it
			var salaries = await _db.EmployeeSalaries.AsNoTracking().Where(s => s.CompanyID == company).OrderBy(s => s.EmployeeID).ThenBy(s => s.EffectiveFrom).ThenBy(s => s.ID).ToListAsync(ct);
			foreach (var g in salaries.GroupBy(s => s.EmployeeID))
			{
				decimal? prev = null;
				foreach (var s in g)
				{
					var total = s.BasicSalary + s.HousingAllowance + s.TransportAllowance + s.OtherAllowances;
					if (s.CreatedAt >= from && s.CreatedAt < toEx)
						rows.Add((s.CreatedAt, new Dictionary<string, object?>
						{
							["ChangedAt"] = s.CreatedAt, ["EffectiveFrom"] = s.EffectiveFrom, ["Employee"] = Who(s.EmployeeID), ["EmployeeNo"] = s.EmployeeID,
							["Area"] = ar ? "الراتب" : "Salary", ["Field"] = prev == null ? (ar ? "راتب أول" : "First salary") : (ar ? "تعديل الراتب" : "Salary change"),
							["OldValue"] = null, ["NewValue"] = s.Note, ["OldSalary"] = prev, ["NewSalary"] = total, ["ChangedBy"] = Who(s.CreatedByEmployeeId),
						}));
					prev = total;
				}
			}
			foreach (var r in rows.OrderByDescending(r => r.At)) b.AddRow(r.Row);
			return b.Build();
		}
	}
}
