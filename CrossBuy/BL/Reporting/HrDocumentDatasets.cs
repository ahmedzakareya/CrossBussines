using System.Globalization;
using CrossBuy.BL.Hr;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HR DOCUMENTS ON REPORT STUDIO (2026-11-10) — two screens that printed with window.print(), which the owner forbids:
	//
	//   Hr.Letter              an approved HR letter (salary / experience / employment certificate). Every employee prints
	//                          their OWN, so it reads under ReportPermissions.Public and the RECORD is enforced here,
	//                          fail-closed: an approved letter request of the caller's company, the caller's own or the
	//                          caller holds an HR/administration identity role.
	//   Hr.EmployeeDocuments   one employee's documents and contracts register (HR's documents screen), under the HR
	//                          reports key, company-scoped.
	// ============================================================================================
	public static class HrDocumentDatasetCodes
	{
		public const string Letter = "Hr.Letter";
		public const string LetterId = "RequestId";
		public const string EmployeeDocuments = "Hr.EmployeeDocuments";
		// NOT "EmployeeId": that name is a SYSTEM parameter (ReportSystemParameters.EmployeeId, the caller), filled by the
		// platform and never read from the URL, so the screen's value was silently dropped.
		public const string EmployeeId = "SubjectEmployeeId";
	}

	public static class HrDocumentDatasets
	{
		private static ReportDatasetField F(string key, string ar, string en, ReportFieldType type = ReportFieldType.String, int w = 30, bool visible = false) =>
			new()
			{
				Key = key, TitleAr = ar, TitleEn = en, Type = type, WidthMm = w, VisibleByDefault = visible,
				Align = type is ReportFieldType.Money or ReportFieldType.Decimal or ReportFieldType.Integer ? ReportAlign.End : ReportAlign.Start,
			};

		private static ReportParameterDescriptor Id(string key, string ar, string en) => new()
		{
			Key = key, TitleAr = ar, TitleEn = en, Type = ReportFieldType.Integer, Required = true,
			HelpTextAr = "يملؤه النظام من الشاشة تلقائيًا.", HelpTextEn = "The screen fills this in for you.",
		};

		public static ReportDatasetDefinition Letter() => new()
		{
			DatasetCode = HrDocumentDatasetCodes.Letter, Module = "Hr",
			TitleAr = "خطاب الموظف", TitleEn = "Employee letter",
			DescriptionAr = "خطاب معتمد لموظف: شهادة راتب أو خبرة أو تعريف بالعمل، بنص الخطاب وجدول الراتب لشهادة الراتب. يطبعه الموظف لنفسه، والموارد البشرية لأي موظف.",
			DescriptionEn = "An approved employee letter — salary, experience or employment certificate — with its text and, for a salary certificate, the salary table. An employee prints their own; HR prints anyone's.",
			DataSourceKey = HrDocumentDatasetCodes.Letter, RequiredPermissionKey = ReportPermissions.Public,
			MaxRows = 20, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
			Fields = new[]
			{
				F("LineNo", "م", "#", ReportFieldType.Integer, 10),
				F("Item", "البند", "Item", w: 60, visible: true),
				F("Value", "القيمة", "Value", w: 40, visible: true),
				F("Addressee", "إلى", "To", w: 60),
				F("Body", "نص الخطاب", "Letter text", w: 120),
				F("Purpose", "الغرض", "Purpose", w: 60),
				F("Ref", "المرجع", "Reference"),
				F("Date", "التاريخ", "Date", ReportFieldType.Date),
				F("CompanyName", "الشركة", "Company", w: 60),
				F("EmployeeName", "الموظف", "Employee", w: 50),
				F("JobTitle", "المسمى", "Job title"),
				F("JoinDate", "تاريخ التعيين", "Joined", ReportFieldType.Date),
				F("LetterType", "نوع الخطاب", "Letter type"),
			},
			Parameters = new[] { Id(HrDocumentDatasetCodes.LetterId, "الطلب", "Request"), AccountingDatasets.CompanyParam() },
		};

		public static ReportDatasetDefinition EmployeeDocuments() => new()
		{
			DatasetCode = HrDocumentDatasetCodes.EmployeeDocuments, Module = "Hr",
			TitleAr = "وثائق وعقود الموظف", TitleEn = "Employee documents and contracts",
			DescriptionAr = "كل وثائق موظف (مدنية، جواز، إذن عمل، مؤهل…) وعقوده، بأرقامها وتواريخ إصدارها وانتهائها وحالتها.",
			DescriptionEn = "All of one employee's documents (civil ID, passport, work permit, qualification…) and contracts, with numbers, issue and expiry dates and status.",
			DataSourceKey = HrDocumentDatasetCodes.EmployeeDocuments, RequiredPermissionKey = RosterReportPermissions.View,
			MaxRows = 500, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
			Fields = new[]
			{
				F("LineNo", "م", "#", ReportFieldType.Integer, 10),
				F("Kind", "النوع", "Kind", w: 36, visible: true),
				F("Number", "الرقم", "Number", w: 30, visible: true),
				F("IssueDate", "تاريخ الإصدار / البدء", "Issued / starts", ReportFieldType.Date, 24, true),
				F("ExpiryDate", "تاريخ الانتهاء", "Expires", ReportFieldType.Date, 24, true),
				F("Status", "الحالة", "Status", w: 24, visible: true),
				F("Notes", "ملاحظات", "Notes", w: 50),
				F("EmployeeName", "الموظف", "Employee", w: 50),
				F("CivilId", "الرقم المدني", "Civil ID"),
				F("JobTitle", "المسمى", "Job title"),
			},
			Parameters = new[] { Id(HrDocumentDatasetCodes.EmployeeId, "الموظف", "Employee"), AccountingDatasets.CompanyParam() },
		};
	}

	public sealed class HrDocumentReportDefinitionProvider : IReportDefinitionProvider
	{
		public string ProviderName => "HrDocuments";

		public IEnumerable<ReportDefinition> GetDefinitions()
		{
			int order = 770;
			foreach (var d in new[] { HrDocumentDatasets.Letter(), HrDocumentDatasets.EmployeeDocuments() })
				yield return new ReportDefinition
				{
					Code = d.DatasetCode, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn, DescriptionAr = d.DescriptionAr, DescriptionEn = d.DescriptionEn,
					DataSourceKey = d.DataSourceKey, PermissionKey = d.RequiredPermissionKey, CategoryKey = "admin.reporting", Tags = new[] { "hr", "document" },
					Columns = d.Fields.Select(f => f.ToColumn()).ToList(), Parameters = d.Parameters,
					DefaultSorts = new[] { ReportSort.By("LineNo") }, DefaultFormat = ReportOutputFormat.Html,
					Capabilities = new ReportCapabilities
					{
						Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf },
						MaxRows = d.MaxRows, PreviewRows = 100, AllowSchedule = false, AllowArchive = true, AllowShare = false,
					},
					Icon = "ki-outline ki-document", Color = "primary", SortOrder = order++,
				};
		}
	}

	public sealed class HrLetterDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrLetterDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrDocumentDatasetCodes.Letter;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			var ctx = query.Context;
			if (query.Parameters.GetInt(HrDocumentDatasetCodes.LetterId) is not int id || ctx.CompanyId <= 0) return b.Build(totalRowCount: 0);
			var req = await _db.EmployeeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == ctx.CompanyId, ct);
			// an APPROVED letter only, and the caller's own unless HR (the payslip document's rule)
			if (req == null || req.RequestType != "Letter" || req.Status != 1 || !HrPayslipDataSource.MayRead(ctx, req.CompanyID, req.EmployeeID)) return b.Build(totalRowCount: 0);

			bool ar = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string L(string a, string en) => ar ? a : en;
			var e = await _db.Employee.AsNoTracking().Where(x => x.ID == req.EmployeeID)
				.Select(x => new { x.FullName, x.FullNameEn, x.DateOfJoining, JobAr = x.JobTitle.TitleAr, JobEn = x.JobTitle.Title }).FirstOrDefaultAsync(ct);
			var co = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == req.CompanyID).Select(c => new { c.CompanyName, c.ComoanyNameAr }).FirstOrDefaultAsync(ct);
			var name = ar ? e?.FullName : (e?.FullNameEn ?? e?.FullName);
			var job = ar ? (e?.JobAr ?? e?.JobEn) : (e?.JobEn ?? e?.JobAr);
			var join = e?.DateOfJoining.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			var body = req.LetterType switch
			{
				"Salary" => L($"نشهد بأن {name} يعمل لدينا بوظيفة {job} منذ {join}، وراتبه الشهري كما يلي:", $"This is to certify that {name} is employed with us as {job} since {join}, with the following monthly salary:"),
				"Experience" => L($"نشهد بأن {name} عمل لدينا بوظيفة {job} منذ {join} وحتى تاريخه، وأظهر كفاءة والتزاماً.", $"This is to certify that {name} has worked with us as {job} from {join} to date, demonstrating competence and commitment."),
				_ => L($"نشهد بأن {name} يعمل لدينا بوظيفة {job} منذ {join}. وقد أُعطي هذا الخطاب بناءً على طلبه.", $"This is to certify that {name} is employed with us as {job} since {join}. This letter is issued upon their request."),
			};
			var header = new Dictionary<string, object?>
			{
				["Addressee"] = string.IsNullOrWhiteSpace(req.Addressee) ? L("إلى من يهمه الأمر", "To whom it may concern") : req.Addressee,
				["Body"] = body, ["Purpose"] = req.Reason, ["Ref"] = $"HR-L-{req.ID}", ["Date"] = req.DecisionAt ?? req.CreatedAt,
				["CompanyName"] = ar ? (co?.ComoanyNameAr ?? co?.CompanyName) : (co?.CompanyName ?? co?.ComoanyNameAr),
				["EmployeeName"] = name, ["JobTitle"] = job, ["JoinDate"] = e?.DateOfJoining, ["LetterType"] = req.LetterType,
			};
			var rows = new List<(string Item, string Value)>();
			if (req.LetterType == "Salary")
			{
				var slip = await _db.Payslips.AsNoTracking().Where(p => p.EmployeeID == req.EmployeeID && p.CompanyID == req.CompanyID)
					.OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).FirstOrDefaultAsync(ct);
				var cur = (await new HrRulePackService(_db).EffectiveAsync(req.CompanyID, ct))?.Rules.Currency ?? "";
				int dec = cur is "KWD" or "BHD" or "OMR" or "JOD" ? 3 : 2;
				string M(decimal v) => v.ToString("N" + dec, CultureInfo.InvariantCulture) + " " + cur;
				if (slip != null)
				{
					rows.Add((L("الراتب الأساسي", "Base salary"), M(slip.BaseSalary)));
					rows.Add((L("البدلات", "Allowances"), M(slip.Allowances)));
					rows.Add((L("إجمالي الاستحقاقات", "Gross earnings"), M(slip.GrossEarnings)));
					rows.Add((L("صافي الراتب", "Net salary"), M(slip.Net)));
					rows.Add((L("عن شهر", "For month"), $"{slip.Month:00}/{slip.Year}"));
				}
				else rows.Add((L("لا توجد قسيمة راتب مرحّلة بعد", "No posted payslip yet"), ""));
			}
			else rows.Add(("", ""));   // the letter's header and text carry it; one row so the bands print once
			int n = 0;
			foreach (var r in rows)
			{
				var row = new Dictionary<string, object?>(header) { ["LineNo"] = ++n, ["Item"] = r.Item, ["Value"] = r.Value };
				b.AddRow(row);
			}
			return b.Build();
		}
	}

	public sealed class HrEmployeeDocumentsDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrEmployeeDocumentsDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrDocumentDatasetCodes.EmployeeDocuments;

		private static readonly Dictionary<string, (string Ar, string En)> Kinds = new(StringComparer.OrdinalIgnoreCase)
		{
			["NationalId"] = ("البطاقة المدنية", "Civil ID"), ["Passport"] = ("جواز السفر", "Passport"), ["Qualification"] = ("المؤهل", "Qualification"),
			["Insurance"] = ("التأمين", "Insurance"), ["WorkPermit"] = ("إذن العمل", "Work permit"), ["Contract"] = ("العقد", "Contract"), ["Other"] = ("وثيقة", "Document"),
			["Permanent"] = ("عقد دائم", "Permanent contract"), ["FixedTerm"] = ("عقد محدد المدة", "Fixed-term contract"), ["Probation"] = ("عقد التجربة", "Probation contract"), ["PartTime"] = ("عقد دوام جزئي", "Part-time contract"),
		};

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			var company = query.Context.CompanyId;
			if (query.Parameters.GetInt(HrDocumentDatasetCodes.EmployeeId) is not int empId || company <= 0) return b.Build(totalRowCount: 0);
			var e = await _db.Employee.AsNoTracking().Where(x => x.ID == empId && x.EmpCompanyID == company)
				.Select(x => new { x.FullName, x.FullNameEn, x.CivilIdNumber, JobAr = x.JobTitle.TitleAr, JobEn = x.JobTitle.Title }).FirstOrDefaultAsync(ct);
			if (e == null) return b.Build(totalRowCount: 0);   // another company's employee reads as nobody
			bool ar = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string L(string a, string en) => ar ? a : en;
			var today = DateTime.Today;
			var docs = await _db.EmployeeDocuments.AsNoTracking().Where(d => d.CompanyID == company && d.EmployeeID == empId)
				.Select(d => new { Kind = d.DocType, Number = d.DocNumber, Start = d.IssueDate, End = d.ExpiryDate, d.Notes, Status = (string?)null }).ToListAsync(ct);
			var contracts = await _db.EmploymentContracts.AsNoTracking().Where(c => c.CompanyID == company && c.EmployeeID == empId)
				.Select(c => new { Kind = c.ContractType, Number = (string?)null, Start = (DateTime?)c.StartDate, End = c.EndDate, c.Notes, Status = (string?)c.Status }).ToListAsync(ct);
			int n = 0;
			foreach (var d in docs.Concat(contracts).OrderBy(x => x.End ?? DateTime.MaxValue))
			{
				var k = Kinds.TryGetValue(d.Kind ?? "Other", out var kn) ? kn : Kinds["Other"];
				string status = d.Status is "Terminated" ? L("منتهٍ", "Terminated")
					: d.End is DateTime x ? ((x.Date - today).Days < 0 ? L("منتهية", "Expired") : (x.Date - today).Days <= 30 ? L("تنتهي خلال 30 يوماً", "Expires within 30 days") : L("سارية", "Valid"))
					: L("بلا تاريخ انتهاء", "No expiry");
				b.AddRow(new Dictionary<string, object?>
				{
					["LineNo"] = ++n, ["Kind"] = ar ? k.Ar : k.En, ["Number"] = d.Number, ["IssueDate"] = d.Start, ["ExpiryDate"] = d.End, ["Status"] = status, ["Notes"] = d.Notes,
					["EmployeeName"] = ar ? e.FullName : (e.FullNameEn ?? e.FullName), ["CivilId"] = e.CivilIdNumber, ["JobTitle"] = ar ? (e.JobAr ?? e.JobEn) : (e.JobEn ?? e.JobAr),
				});
			}
			return b.Build();
		}
	}
}
