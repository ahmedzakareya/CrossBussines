using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE WORKFORCE SURFACE — who works here, and nothing about what they are paid.
	//
	// MEASURED BEFORE BUILDING, and the measurement is what shaped it:
	//
	//   21 employees · 19 active · 5 job titles, carried by ALL 21 · a branch on 10 of 21
	//   a department on 4 of 21 · 69 attendance rows covering THREE employees · 20 leave requests
	//   covering 7 · 6 payslips · a recorded salary for ONE employee
	//
	// WHAT THAT MEANS, AND WHY THIS SURFACE IS NARROW ON PURPOSE. Attendance covers 14% of the
	// workforce and salary covers 5%. A surface exposing "hours worked by employee" or "payroll by
	// department" would answer about three people and one person respectively, and would read as a
	// complete answer about the company. The only HR dimension with real coverage is the ROSTER
	// itself, so the roster is what this surface is, and the questions it cannot answer it does not
	// pretend to.
	//
	// THERE IS NO MONEY IN THIS SURFACE AT ALL, and that is a decision rather than an omission.
	// Salary is the most sensitive field in the product: the HR change log masks IBANs, puts
	// OldSalary/NewSalary behind a second permission, and records every edit precisely because
	// moving a salary figure is how money gets redirected. A chat assistant that will answer any
	// question composed at runtime is the wrong place to make those numbers reachable, and the
	// existing reporting datasets already serve them under a dedicated permission. Headcount is
	// the measure here; there is no second one.
	//
	// READ UNDER Hr AUTHORITY, not Inventory or Accounting. Being allowed to see stock or takings
	// says nothing about being allowed to see the staff list.
	// =================================================================================================
	public sealed class AskWorkforceSurface : AskSurface
	{
		public const string SurfaceCode = "hr.workforce";

		private readonly CrossDbContext _db;
		public AskWorkforceSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHr;
		public override string Action => "read";

		public override AskText Description => AskText.Of(
			"قائمة الموظفين: العدد حسب المسمى الوظيفي أو الفرع أو نوع التوظيف أو سنة الالتحاق",
			"The employee roster: headcount by job title, branch, employment type or year of joining",
			"Effectifs du personnel par intitulé de poste, succursale, type d'emploi ou année d'embauche");

		public override AskText Rules => AskText.Of(
			"العدد فقط — لا يوجد في هذا المصدر أي بيان عن الرواتب · الموظفون غير النشطين مشمولون "
			+ "وظاهرون بحالتهم · الفرع غير مسجَّل على نحو نصف الموظفين، وهؤلاء يظهرون باسم خاص لا "
			+ "يُستبعَدون · الحضور والرواتب لا يمكن سؤالهما هنا: الحضور مسجَّل على ٣ موظفين فقط "
			+ "والراتب على موظف واحد، وأي إجابة عنهما ستصف الشركة بما لا يمثلها.",
			"Headcount only — this surface carries no salary data of any kind · inactive employees are "
			+ "included and shown with their status · the branch is not recorded for about half the "
			+ "roster, and those employees appear under their own label rather than being dropped · "
			+ "attendance and pay cannot be asked here: attendance is recorded for 3 employees and a "
			+ "salary for 1, and any answer about them would describe the company by what does not "
			+ "represent it.");

		public static readonly string NoBranchAr = "(الفرع غير مسجَّل)";
		public static readonly string NoBranchEn = "(branch not recorded)";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// JOB TITLE IS THE ONE DIMENSION AT FULL COVERAGE — all 21 employees carry one — which
			// makes it the only grouping here whose answer describes the whole company.
			new AskColumn { Name = "jobtitle", Kind = AskColumnKind.Dimension, Label = AskText.Of("المسمى الوظيفي", "Job title", "Intitulé du poste"),
				Synonyms = new[] { "وظيفة", "الوظيفة", "وظائف", "الوظائف", "مسمى", "المسمى", "المسميات",
					"مسمى وظيفي", "منصب", "المناصب", "دور", "الادوار",
					"job title", "job titles", "title", "titles", "role", "roles", "position", "positions" } },
			new AskColumn { Name = "branch", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفرع", "Branch", "Succursale"),
				Synonyms = new[] { "فرع", "فروع", "الفروع", "الفرع", "موقع", "المواقع",
					"branch", "branches", "site", "location" } },
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("الحالة", "Status", "Statut"),
				Synonyms = new[] { "حالة", "الحالة", "نشط", "النشطين", "غير نشط", "على رأس العمل", "مستقيل",
					"status", "active", "inactive", "employment status", "still employed" } },
			new AskColumn { Name = "employmenttype", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع التوظيف", "Employment type", "Type d'emploi"),
				Synonyms = new[] { "نوع التوظيف", "دوام", "دوام كامل", "دوام جزئي", "تعاقد", "نوع التعاقد",
					"employment type", "full time", "part time", "contract type", "contract" } },
			new AskColumn { Name = "gender", Kind = AskColumnKind.Dimension, Label = AskText.Of("النوع", "Gender", "Genre"),
				Synonyms = new[] { "نوع", "النوع", "ذكور", "اناث", "رجال", "ستات",
					"gender", "male", "female", "men", "women" } },
			// THE YEAR, NOT THE DATE. A roster of 21 grouped by exact joining date is 21 groups of
			// one, which is a list pretending to be an analysis.
			new AskColumn { Name = "joinedyear", Kind = AskColumnKind.Dimension, Label = AskText.Of("سنة الالتحاق", "Year joined", "Année d'embauche"),
				Synonyms = new[] { "سنة الالتحاق", "سنة التعيين", "التحقوا", "اتعينوا", "سنة",
					"year joined", "joining year", "hired", "year of hire", "tenure" } },

			// ONE MEASURE. Headcount. Deliberately no salary, no hours, no cost — see the header.
			new AskColumn { Name = "headcount", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الموظفين", "Headcount", "Effectif"),
				Synonyms = new[] { "عدد الموظفين", "كام موظف", "عدد", "العدد", "كام واحد", "حجم الفريق",
					"headcount", "how many employees", "staff count", "number of staff", "team size",
					"how many people", "employees" } },
		};

		/// A row here IS one employee, so this surface can answer about a person by name — not only
		/// count them by job title. See AskSurface.NameKey for why that path exists.
		public override string? NameKey => "__name";

		/// Gender as the reader's language writes it. The column holds "Male", "M", "ذكر" and blanks
		/// across 21 rows, so the raw value is both untranslated and inconsistent; anything this
		/// does not recognise is passed through rather than guessed at or dropped.
		private static string Gender(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محدد)" : "(not recorded)";
			var k = v.ToLowerInvariant();
			if (k is "m" or "male" or "ذكر") return arabic ? "ذكر" : "Male";
			if (k is "f" or "female" or "انثى" or "أنثى") return arabic ? "أنثى" : "Female";
			return v;
		}

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// Employee's company column is EmpCompanyID, not CompanyID. Read from the model rather
			// than assumed from the convention every other table follows — that assumption cost a
			// red shared tree twice today.
			var staff = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == scope.CompanyId)
				.Select(e => new
				{
					e.ID, e.FullName, e.FullNameEn, e.JobTitleID, e.BranchID,
					e.IsActive, e.EmploymentType, e.Gender, e.DateOfJoining,
					// SHOW THE PERSON. An answer about a named colleague that renders a letter in a
					// circle is the house rule's exact failure case, and the photo costs one column.
					e.ProfileImage,
				})
				.ToListAsync(ct);

			if (staff.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var titleIds = staff.Select(s => s.JobTitleID).Distinct().ToList();
			// A THIRD NAMING CONVENTION IN ONE SURFACE: Item uses Name/NameEn, Branch uses
			// Name/NameAr, and JobTitle uses Title/TitleAr. There is no convention to rely on in
			// this codebase, only the model in front of you.
			var titles = (await _db.JobTitles.AsNoTracking()
					.Where(j => titleIds.Contains(j.ID))
					.Select(j => new { j.ID, j.Title, j.TitleAr })
					.ToListAsync(ct))
				.ToDictionary(j => j.ID);

			var branchIds = staff.Where(s => s.BranchID != null).Select(s => s.BranchID!.Value).Distinct().ToList();
			// Branch carries Name (English) and NameAr — the reverse of Item and JobTitle.
			var branches = (await _db.Branches.AsNoTracking()
					.Where(b => b.CompanyID == scope.CompanyId && branchIds.Contains(b.ID))
					.Select(b => new { b.ID, b.Name, b.NameAr })
					.ToListAsync(ct))
				.ToDictionary(b => b.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(staff.Count);
			foreach (var e in staff)
			{
				var name = AskItems.Readable(arabic ? e.FullName : e.FullNameEn,
											 arabic ? e.FullNameEn : e.FullName) ?? ("#" + e.ID);

				titles.TryGetValue(e.JobTitleID, out var jt);
				var titleLabel = jt == null
					? (arabic ? "(بلا مسمى)" : "(no job title)")
					: (AskItems.Readable(arabic ? jt.TitleAr : jt.Title, arabic ? jt.Title : jt.TitleAr) ?? ("#" + jt.ID));

				// HALF THE ROSTER HAS NO BRANCH. Labelled, not dropped: a headcount by branch that
				// quietly covered ten of twenty-one people would read as the whole company.
				var branchLabel = arabic ? NoBranchAr : NoBranchEn;
				if (e.BranchID != null && branches.TryGetValue(e.BranchID.Value, out var br))
					branchLabel = AskItems.Readable(arabic ? br.NameAr : br.Name, arabic ? br.Name : br.NameAr)
						?? ("#" + br.ID);

				rows.Add(new Dictionary<string, object?>
				{
					["jobtitle"] = titleLabel.Trim(),
					["branch"] = branchLabel.Trim(),
					["status"] = e.IsActive ? (arabic ? "على رأس العمل" : "Active") : (arabic ? "غير نشط" : "Inactive"),
					["employmenttype"] = string.IsNullOrWhiteSpace(e.EmploymentType)
						? (arabic ? "(غير محدد)" : "(not recorded)")
						: e.EmploymentType,
					// THE STORED VALUE IS ENGLISH AND SOMETIMES A SINGLE LETTER. Printing it raw put
					// "Male" and "M" inside an Arabic sentence about a colleague, and left the two
					// spellings reading as two different groups in a headcount by gender.
					["gender"] = Gender(e.Gender, arabic),
					["joinedyear"] = e.DateOfJoining.Year <= 1 || e.DateOfJoining == default
						? (arabic ? "(غير محدد)" : "(not recorded)")
						: e.DateOfJoining.Year.ToString(CultureInfo.InvariantCulture),
					["headcount"] = 1m,
					// The name travels only as the row's own identity for a record query; it is not
					// a dimension, so nobody can group a headcount by person and get a list of ones.
					["__name"] = name,
					// Both spellings, so the name is findable in the script the asker typed.
					[SearchNameKey] = ((e.FullName ?? "") + " " + (e.FullNameEn ?? "")).Trim(),
					[PhotoKey] = CrossBuy.BL.Hr.PersonTile.Photo(e.ProfileImage),
					// The way into the record, so the answer is a door rather than a dead end. The
					// destination runs its OWN gate (HrActions.EmployeeView) and will refuse a
					// caller who may read a headcount but not a person — which is the point: the
					// assistant must never be the way around a screen's authority.
					[RecordHrefKey] = "/Admin/EmployeeProfile/" + e.ID.ToString(CultureInfo.InvariantCulture),
					[LinkFor("jobtitle")] = null,
				});
			}
			return rows;
		}
	}
}
