using CrossBuy.BL.Hr;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// THE PAYSLIP AS A REPORT STUDIO DOCUMENT (2026-11-10).
	//
	// Printing is Report Studio's (owner rule): the employee's payslip screen used window.print(), which is the thing
	// that rule forbids. This dataset is the document instead — one row per payslip line (earnings, then deductions),
	// with the slip's header and totals on every row for the template's bands.
	//
	// WHO MAY READ IT. Every employee must be able to print their OWN payslip, and 18 of 19 employees hold no identity
	// role, so the report reads under ReportPermissions.Public ("any authenticated caller in a resolved company") and the
	// RECORD is enforced here, fail-closed: the slip must belong to the caller's company AND either to the caller
	// (BusinessContext.EmployeeId) or the caller must hold an HR/administration identity role. Anything else returns an
	// empty document — never another employee's pay.
	// ============================================================================================
	public static class HrPayslipDatasetCodes
	{
		public const string Payslip = "Hr.Payslip";
		public const string IdParameter = "PayslipId";
		internal static readonly string[] ReadAnyRoles = { "Admin", "SuperAdmin", "HrManager", "HrOfficer" };
	}

	public static class HrPayslipDataset
	{
		private static ReportDatasetField F(string key, string ar, string en, ReportFieldType type = ReportFieldType.String, int w = 30, bool visible = false, ReportAlign? align = null) =>
			new()
			{
				Key = key, TitleAr = ar, TitleEn = en, Type = type, WidthMm = w, VisibleByDefault = visible,
				Align = align ?? (type is ReportFieldType.Money or ReportFieldType.Decimal or ReportFieldType.Integer ? ReportAlign.End : ReportAlign.Start),
			};

		public static ReportDatasetDefinition Definition() => new()
		{
			DatasetCode = HrPayslipDatasetCodes.Payslip, Module = "Hr",
			TitleAr = "قسيمة الراتب", TitleEn = "Payslip",
			DescriptionAr = "قسيمة راتب موظف لشهر: الاستحقاقات ثم الاستقطاعات، مع الصافي وحصة صاحب العمل في التأمينات. يطبعها الموظف لنفسه، والموارد البشرية لأي موظف.",
			DescriptionEn = "One employee's payslip for a month: earnings, then deductions, with the net and the employer's insurance share. An employee prints their own; HR prints anyone's.",
			DataSourceKey = HrPayslipDatasetCodes.Payslip, RequiredPermissionKey = ReportPermissions.Public,
			MaxRows = 50, RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
			Fields = new[]
			{
				F("LineNo", "م", "#", ReportFieldType.Integer, 10, true, ReportAlign.Center),
				F("Section", "القسم", "Section", w: 26, visible: true),
				F("Item", "البند", "Item", w: 60, visible: true),
				F("Detail", "التفاصيل", "Detail", w: 30, visible: true),
				F("Amount", "المبلغ", "Amount", ReportFieldType.Money, 28, true),
				F("SlipNo", "رقم القسيمة", "Slip no."),
				F("Period", "الفترة", "Period", w: 18),
				F("EmployeeName", "الموظف", "Employee", w: 50),
				F("EmployeeNo", "رقم الموظف", "Employee no.", w: 18),
				F("CivilId", "الرقم المدني", "Civil ID"),
				F("JobTitle", "المسمى", "Job title"),
				F("Branch", "الفرع", "Branch"),
				F("Iban", "IBAN", "IBAN", w: 50),
				F("Currency", "العملة", "Currency", w: 14),
				F("TotalEarnings", "إجمالي الاستحقاقات", "Total earnings", ReportFieldType.Money),
				F("TotalDeductions", "إجمالي الاستقطاعات", "Total deductions", ReportFieldType.Money),
				F("NetPay", "الصافي", "Net pay", ReportFieldType.Money),
				F("EmployerInsurance", "حصة صاحب العمل في التأمينات", "Employer insurance", ReportFieldType.Money),
			},
			Parameters = new[]
			{
				new ReportParameterDescriptor
				{
					Key = HrPayslipDatasetCodes.IdParameter, TitleAr = "القسيمة", TitleEn = "Payslip", Type = ReportFieldType.Integer, Required = true,
					HelpTextAr = "معرّف القسيمة — تصل إليه الشاشة تلقائيًا.", HelpTextEn = "The payslip's id — the screen fills this in for you.",
				},
				AccountingDatasets.CompanyParam(),
			},
		};
	}

	public sealed class HrPayslipReportDefinitionProvider : IReportDefinitionProvider
	{
		public string ProviderName => "HrPayslip";

		public IEnumerable<ReportDefinition> GetDefinitions()
		{
			var d = HrPayslipDataset.Definition();
			yield return new ReportDefinition
			{
				Code = d.DatasetCode, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn, DescriptionAr = d.DescriptionAr, DescriptionEn = d.DescriptionEn,
				DataSourceKey = d.DataSourceKey, PermissionKey = d.RequiredPermissionKey, CategoryKey = "admin.reporting", Tags = new[] { "hr", "payroll", "document" },
				Columns = d.Fields.Select(f => f.ToColumn()).ToList(), Parameters = d.Parameters,
				DefaultSorts = new[] { ReportSort.By("LineNo") }, DefaultFormat = ReportOutputFormat.Html,
				Capabilities = new ReportCapabilities
				{
					Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf },
					MaxRows = d.MaxRows, PreviewRows = 50, AllowSchedule = false, AllowArchive = true, AllowShare = false,
				},
				Icon = "ki-outline ki-wallet", Color = "primary", SortOrder = 760,
			};
		}
	}

	public sealed class HrPayslipDataSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HrPayslipDataSource(CrossDbContext db) { _db = db; }
		public string Key => HrPayslipDatasetCodes.Payslip;

		/// May this caller read this slip? Same company, and the slip is theirs or they hold an HR/administration role.
		public static bool MayRead(Models.Platform.BusinessContext ctx, int slipCompany, int slipEmployee) =>
			ctx.CompanyId > 0 && slipCompany == ctx.CompanyId
			&& ((ctx.EmployeeId is int me && me == slipEmployee) || ctx.Roles.Any(r => HrPayslipDatasetCodes.ReadAnyRoles.Contains(r, StringComparer.OrdinalIgnoreCase)));

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
		{
			var b = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			var id = query.Parameters.GetInt(HrPayslipDatasetCodes.IdParameter);
			if (id is not int slipId || query.Context.CompanyId <= 0) return b.Build(totalRowCount: 0);
			var s = await _db.Payslips.AsNoTracking().FirstOrDefaultAsync(p => p.ID == slipId && p.CompanyID == query.Context.CompanyId, ct);
			if (s == null || !MayRead(query.Context, s.CompanyID, s.EmployeeID)) return b.Build(totalRowCount: 0);

			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var e = await _db.Employee.AsNoTracking().Where(x => x.ID == s.EmployeeID)
				.Select(x => new { x.FullName, x.FullNameEn, x.CivilIdNumber, x.Iban, JobAr = x.JobTitle.TitleAr, JobEn = x.JobTitle.Title,
					BranchAr = x.Branch == null ? null : x.Branch.NameAr, BranchEn = x.Branch == null ? null : x.Branch.Name }).FirstOrDefaultAsync(ct);
			var currency = (await new HrRulePackService(_db).EffectiveAsync(s.CompanyID, ct))?.Rules.Currency ?? "";
			var earnings = s.BaseSalary + s.Allowances + s.OvertimePay;
			var deductions = s.SiEmployee + s.Tax + s.LatePenalty + s.AbsencePenalty + s.LoanDeduction + s.SickLeaveDeduction;
			string L(string a, string en) => ar ? a : en;
			var lines = new List<(string Section, string Item, string? Detail, decimal Amount)>
			{
				(L("الاستحقاقات", "Earnings"), L("الراتب الأساسي", "Basic salary"), null, s.BaseSalary),
				(L("الاستحقاقات", "Earnings"), L("البدلات", "Allowances"), null, s.Allowances),
				(L("الاستحقاقات", "Earnings"), L("العمل الإضافي", "Overtime"), null, s.OvertimePay),
				(L("الاستقطاعات", "Deductions"), L("التأمينات الاجتماعية (حصة الموظف)", "Social insurance (employee)"), null, s.SiEmployee),
				(L("الاستقطاعات", "Deductions"), L("ضريبة الدخل", "Income tax"), null, s.Tax),
				(L("الاستقطاعات", "Deductions"), L("خصم التأخير", "Late penalty"), s.LateMinutes > 0 ? $"{s.LateMinutes} {L("دقيقة", "min")}" : null, s.LatePenalty),
				(L("الاستقطاعات", "Deductions"), L("خصم الغياب", "Absence penalty"), s.AbsentDays > 0 ? $"{s.AbsentDays} {L("يوم", "day(s)")}" : null, s.AbsencePenalty),
				(L("الاستقطاعات", "Deductions"), L("قسط قرض / سلفة", "Loan / advance instalment"), null, s.LoanDeduction),
				(L("الاستقطاعات", "Deductions"), L("إجازة بأجر جزئي", "Part-paid leave"), null, s.SickLeaveDeduction),
			};
			int n = 0;
			foreach (var l in lines.Where(x => x.Amount != 0 || x.Item == L("الراتب الأساسي", "Basic salary")))
				b.AddRow(new Dictionary<string, object?>
				{
					["LineNo"] = ++n, ["Section"] = l.Section, ["Item"] = l.Item, ["Detail"] = l.Detail, ["Amount"] = l.Amount,
					["SlipNo"] = $"PS-{s.Year}-{s.ID:D5}", ["Period"] = $"{s.Year}/{s.Month:00}",
					["EmployeeName"] = ar ? (e?.FullName ?? s.EmployeeName) : (e?.FullNameEn ?? e?.FullName ?? s.EmployeeName), ["EmployeeNo"] = s.EmployeeID,
					["CivilId"] = e?.CivilIdNumber, ["JobTitle"] = ar ? (e?.JobAr ?? e?.JobEn) : (e?.JobEn ?? e?.JobAr),
					["Branch"] = ar ? (e?.BranchAr ?? e?.BranchEn) : (e?.BranchEn ?? e?.BranchAr), ["Iban"] = HrBankDetailsImport.Mask(e?.Iban),
					["Currency"] = currency, ["TotalEarnings"] = earnings, ["TotalDeductions"] = deductions, ["NetPay"] = s.Net, ["EmployerInsurance"] = s.SiCompany,
				});
			return b.Build();
		}
	}
}
