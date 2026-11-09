using CrossBuy.BL.Hr;
using CrossBuy.Models.Context;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HR — THE WORKING-HOURS SCHEDULE AS A REPORT (2026-11-09).
	//
	// The printable form of /Admin/WorkingHours: the schedule an employer records on PAM's Ashal portal (Resolution
	// 15/2025) and must display at the workplace once approved. Printing is Report Studio's job (owner rule: nothing
	// prints from a view), so the layout is a Studio template over this dataset. One row per schedule per weekday,
	// built from HrWorkingHoursService — the very computation the screen shows — so the print and the screen cannot
	// disagree. Same sensitivity as the roster datasets, so it reads under roster.reports.view.
	// ============================================================================================
	public static class WorkingHoursDatasetCodes
	{
		public const string Schedule = "Hr.WorkingHours";
	}

	public static class HrWorkingHoursDatasets
	{
		public static ReportDatasetDefinition Schedule() => new()
		{
			DatasetCode = WorkingHoursDatasetCodes.Schedule,
			Module = "Hr",
			TitleAr = "جدول ساعات العمل",
			TitleEn = "Working-hours schedule",
			DescriptionAr = "جدول الدوام المسجل في نظام «أسهل» ويُعلَّق في مكان العمل: لكل جدول دوام وكل يوم من الأسبوع، يوم عمل أو راحة، ساعة البداية والنهاية، فترة الراحة وصافي الساعات وعدد الموظفين.",
			DescriptionEn = "The working schedule recorded on Ashal and displayed at the workplace: per schedule and weekday, working or rest day, start and end, the rest break, net hours and the number of employees.",
			DataSourceKey = WorkingHoursDatasetCodes.Schedule,
			RequiredPermissionKey = RosterReportPermissions.View,
			MaxRows = 2000,
			RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
			AvailableInStudio = true,
			AvailableAsWidget = false,
			Fields = new[]
			{
				new ReportDatasetField { Key = "Schedule", TitleAr = "جدول الدوام", TitleEn = "Schedule", Groupable = true, WidthMm = 40 },
				new ReportDatasetField { Key = "Employees", TitleAr = "الموظفون", TitleEn = "Employees", Type = ReportFieldType.Integer, WidthMm = 18 },
				new ReportDatasetField { Key = "DayOrder", TitleAr = "ترتيب اليوم", TitleEn = "Day order", Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 12 },
				new ReportDatasetField { Key = "Day", TitleAr = "اليوم", TitleEn = "Day", WidthMm = 22 },
				new ReportDatasetField { Key = "IsWorkingDay", TitleAr = "يوم عمل", TitleEn = "Working day", Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 18 },
				new ReportDatasetField { Key = "Start", TitleAr = "البداية", TitleEn = "Start", WidthMm = 18 },
				new ReportDatasetField { Key = "End", TitleAr = "النهاية", TitleEn = "End", WidthMm = 18 },
				new ReportDatasetField { Key = "Break", TitleAr = "فترة الراحة", TitleEn = "Rest break", WidthMm = 26 },
				new ReportDatasetField { Key = "BreakMinutes", TitleAr = "دقائق الراحة", TitleEn = "Break minutes", Type = ReportFieldType.Integer, WidthMm = 18 },
				new ReportDatasetField { Key = "NetHours", TitleAr = "صافي الساعات", TitleEn = "Net hours", Type = ReportFieldType.Decimal, Format = "0.##", WidthMm = 20,
					SupportedAggregates = new[] { ReportAggregate.Sum } },
			},
		};

		public static IEnumerable<ReportDatasetDefinition> All() { yield return Schedule(); }
	}

	public sealed class WorkingHoursReportDefinitionProvider : IReportDefinitionProvider
	{
		public string ProviderName => "HrWorkingHours";

		public IEnumerable<ReportDefinition> GetDefinitions()
		{
			var dataset = HrWorkingHoursDatasets.Schedule();
			yield return new ReportDefinition
			{
				Code = dataset.DatasetCode, Module = dataset.Module,
				TitleAr = dataset.TitleAr, TitleEn = dataset.TitleEn, DescriptionAr = dataset.DescriptionAr, DescriptionEn = dataset.DescriptionEn,
				DataSourceKey = dataset.DataSourceKey, PermissionKey = dataset.RequiredPermissionKey,
				CategoryKey = "admin.reporting", Tags = new[] { "hr", "attendance", "ashal" },
				Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
				Parameters = dataset.Parameters,
				DefaultSorts = new[] { ReportSort.By("Schedule"), ReportSort.By("DayOrder") },
				DefaultFormat = ReportOutputFormat.Html,
				Capabilities = new ReportCapabilities
				{
					Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf, ReportOutputFormat.Xlsx },
					MaxRows = dataset.MaxRows, PreviewRows = 200, AllowSchedule = false, AllowArchive = true, AllowShare = true,
				},
				Icon = "ki-outline ki-time", Color = "primary", SortOrder = 720,
			};
		}
	}

	public sealed class WorkingHoursScheduleDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public WorkingHoursScheduleDataSource(CrossDbContext db) { _db = db; }
		public string Key => WorkingHoursDatasetCodes.Schedule;

		private static readonly Dictionary<DayOfWeek, (string Ar, string En)> DayNames = new()
		{
			[DayOfWeek.Saturday] = ("السبت", "Saturday"), [DayOfWeek.Sunday] = ("الأحد", "Sunday"), [DayOfWeek.Monday] = ("الاثنين", "Monday"),
			[DayOfWeek.Tuesday] = ("الثلاثاء", "Tuesday"), [DayOfWeek.Wednesday] = ("الأربعاء", "Wednesday"), [DayOfWeek.Thursday] = ("الخميس", "Thursday"), [DayOfWeek.Friday] = ("الجمعة", "Friday"),
		};

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
			var builder = new ReportDataSetBuilder(columns);
			if (query.Context.CompanyId <= 0) return builder.Build(totalRowCount: 0);   // fail closed on an unresolved tenant

			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var sheet = await new HrWorkingHoursService(_db).BuildAsync(query.Context.CompanyId, DateTime.Today.Year, cancellationToken);
			foreach (var s in sheet.Schedules)
				for (int i = 0; i < s.Days.Count; i++)
				{
					var d = s.Days[i];
					builder.AddRow(new Dictionary<string, object?>
					{
						["Schedule"] = ar ? s.NameAr : s.NameEn,
						["Employees"] = s.Employees,
						["DayOrder"] = i + 1,
						["Day"] = ar ? DayNames[d.Day].Ar : DayNames[d.Day].En,
						["IsWorkingDay"] = d.Working,
						["Start"] = d.Start?.ToString(@"hh\:mm"),
						["End"] = d.End?.ToString(@"hh\:mm"),
						["Break"] = d.BreakStart is { } bs && d.BreakEnd is { } be ? $"{bs:hh\\:mm}–{be:hh\\:mm}" : null,
						["BreakMinutes"] = d.Working ? d.BreakMinutes : null,
						["NetHours"] = d.Working ? d.NetHours : null,
					});
				}
			return builder.Build();
		}
	}
}
