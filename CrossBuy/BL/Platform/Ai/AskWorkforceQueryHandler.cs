using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE ROSTER, as the fifth case of the surface query handler — and the first under HR authority.
	//
	// Every other surface reads under Inventory or Accounting. Being allowed to see stock or takings
	// says nothing about being allowed to see who works here, so this one declares Hr and inherits
	// whatever that scope decides. The handler forwards the surface's authority rather than naming
	// its own, so a surface can never be read under a weaker permission than it asked for.
	//
	// WHAT IT DELIBERATELY CANNOT ANSWER: anything about pay, and anything about attendance. Not
	// because they are hard, but because the data covers 1 and 3 employees out of 21 — see the
	// surface header. A question it cannot answer is refused, which is the behaviour the whole
	// layer is built around.
	// =================================================================================================
	public sealed class AskWorkforceQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hr.workforce";

		public AskWorkforceQueryHandler(AskWorkforceSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"عدد الموظفين وتوزيعهم: حسب المسمى الوظيفي أو الفرع أو الحالة أو سنة الالتحاق — بلا أي بيانات رواتب",
			"Headcount and how the roster is distributed: by job title, branch, status or year joined — no pay data");

		public override AskText Example => AskText.Of(
			"كام موظف عندنا في كل وظيفة؟", "How many employees in each job title?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"كام موظف", "عدد الموظفين", "الموظفين", "موظفينا", "فريق العمل", "حجم الفريق",
			"الموظفين حسب الوظيفة", "المسميات الوظيفية", "الوظائف اللي عندنا",
			"كام موظف في كل فرع", "الموظفين حسب الفرع", "مين على رأس العمل",
			"كام واحد شغال", "الموظفين النشطين", "اللي سابوا الشغل",
			"headcount", "how many employees", "staff count", "team size", "the roster",
			"employees by job title", "employees by branch", "who works here",
			"active employees", "job titles", "staff by role", "how many staff",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"Employee", "JobTitles", "Branches",
		};

		// BY JOB TITLE, because it is the only dimension every employee carries. Defaulting to
		// branch would answer for ten of twenty-one people and look like the whole company.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskWorkforceSurface.SurfaceCode,
			GroupBy = new[] { "jobtitle" },
			Measures = new[] { new AskMeasure { Column = "headcount", Agg = AskAgg.Sum } },
			OrderBy = "headcount",
			Descending = true,
			Limit = 10,
		};
	}
}
