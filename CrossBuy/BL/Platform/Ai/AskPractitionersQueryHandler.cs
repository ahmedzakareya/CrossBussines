using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE HOSPITAL'S FIRST QUESTION — «جيب قائمة الاطباء».
	//
	// The owner asked exactly that and the assistant refused, then offered him the revenue per item
	// and the stock per warehouse. The refusal was honest and the gap was ours: six surfaces had
	// been built over sales, purchases, stock, the till, the payroll roster and expenses, and none
	// over the hospital, which is the largest module in the product.
	//
	// Thin on purpose, like the five before it: everything about composing, validating, running,
	// shaping, speaking, coverage, naming one record and withholding a column lives in the shared
	// base. A hospital surface costs this file and the surface beside it.
	// =================================================================================================
	public sealed class AskPractitionersQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.practitioners";

		public AskPractitionersQueryHandler(AskPractitionersSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن الأطباء: العدد حسب القسم أو نوع التعاقد أو الدرجة، أو طبيب بعينه بالاسم",
			"Any question about the practitioners: the count by department, engagement or title, or one doctor by name",
			"Questions sur les praticiens : nombre par service, type de contrat ou titre, ou recherche d'un médecin par son nom");

		public override AskText Example => AskText.Of(
			"كام طبيب في كل قسم؟", "How many practitioners in each department?", "Combien de praticiens y a-t-il dans chaque service ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"الاطباء", "اطباء", "قائمة الاطباء", "جيب قائمة الاطباء", "كام طبيب", "عدد الاطباء",
			"الاطباء حسب القسم", "كام طبيب في كل قسم", "الدكاترة", "قائمة الدكاترة", "كام دكتور",
			"مين الاطباء عندنا", "الاطباء حسب التخصص", "اطباء القسم",
			// DELIBERATELY NOT HERE: "الاطباء الزائرين", "الاستشاريين", "الاطباء النشطين".
			//
			// A keyword routes a question here, and it is ALSO read as this surface's vocabulary
			// when deciding whether a word is one the surface knows. So a keyword naming a VALUE
			// makes that value invisible to the filter: "الاطباء الزائرين" counted "الزائرين" as
			// known, no named value was looked for, and the answer was the total for all eight
			// doctors under a question about the visiting ones. The model's training data carries
			// these phrasings for routing, which is where they belong.
			"practitioners", "doctors", "list the doctors", "how many doctors", "doctors by department",
			"physicians", "medical staff", "clinical staff",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalPractitioners", "HospitalDepartments", "Employee",
		};

		// BY DEPARTMENT. "The list of doctors" names no grouping, and the department is the answer a
		// hospital actually wants to it — a flat count of eight tells nobody where they are.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskPractitionersSurface.SurfaceCode,
			GroupBy = new[] { "department" },
			Measures = new[] { new AskMeasure { Column = "headcount", Agg = AskAgg.Sum } },
			OrderBy = "headcount",
			Descending = true,
			Limit = 20,
		};
	}
}
