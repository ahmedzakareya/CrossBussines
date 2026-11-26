using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT IS PRESCRIBED — the inpatient medication orders.
	//
	// The owner asked «طيب الادوية» and was refused. Same gap as the doctors: the hospital's data had
	// no window onto it.
	//
	// WHAT THIS IS AND WHAT IT IS NOT. "الأدوية" could mean three different tables here: what was
	// ORDERED (HospitalMedOrders, 15 rows), what was DISPENSED (HospitalDispenseLines, 18) and what
	// was ADMINISTERED (HospitalMarEntries, 3). They are not the same number and answering one under
	// the name of another would be the quiet kind of wrong. This surface is the ORDERS, and its
	// rules say so, so nobody reads a prescribing figure as a dispensing one.
	//
	// NO PATIENT DIMENSION, DELIBERATELY. Counting by drug, route or status tells you how the
	// hospital prescribes; grouping by patient would put who-is-on-what one sentence from a chat
	// box, under the hospital's plain read rather than confidential-view. The aggregate is useful
	// and carries nobody's name.
	// =================================================================================================
	public sealed class AskMedicationsSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.medications";

		private readonly CrossDbContext _db;
		public AskMedicationsSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.ClinicalRead;   // H156: a patient's medicines are the care team's

		public override AskText Description => AskText.Of(
			"أوامر الأدوية للمرضى المنوَّمين: العدد حسب الدواء أو طريقة الإعطاء أو الحالة",
			"Inpatient medication orders: the count by drug, route or status",
			"Prescriptions des patients hospitalisés : nombre par médicament, voie d'administration ou statut");

		public override AskText Rules => AskText.Of(
			"أوامر الوصف فقط — لا الصرف من الصيدلية ولا تسجيل الإعطاء، وهي ثلاثة أرقام مختلفة · "
			+ "العدد هو عدد الأوامر لا عدد الجرعات ولا عدد المرضى · الأوامر الموقوفة مشمولة وظاهرة "
			+ "بحالتها · لا شيء هنا يخص مريضًا بعينه.",
			"Prescribing orders only — not pharmacy dispensing and not administration records, which "
			+ "are three different numbers · the count is of ORDERS, not doses and not patients · "
			+ "stopped orders are included and shown with their status · nothing here is about an "
			+ "individual patient.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "drug", Kind = AskColumnKind.Dimension, Label = AskText.Of("الدواء", "Drug", "Médicament"),
				Synonyms = new[] { "دواء", "الدواء", "ادوية", "الادوية", "علاج", "العلاج", "عقار", "العقاقير",
					"drug", "drugs", "medicine", "medicines", "medication", "medications" } },
			new AskColumn { Name = "route", Kind = AskColumnKind.Dimension, Label = AskText.Of("طريقة الإعطاء", "Route", "Voie d'administration"),
				Synonyms = new[] { "طريقة", "الطريقة", "طريق الاعطاء", "route", "administration route" } },
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("الحالة", "Status", "Statut"),
				Synonyms = new[] { "حالة", "الحالة", "status" } },
			// THE COLUMN IS THE CLASSIFICATION; "مخدِّر" IS ONE OF ITS VALUES.
			//
			// Third time this exact shape has cost an answer today. Labelling the column "مخدِّر"
			// and listing "مخدرة" among its synonyms made "الأدوية المخدرة" a question ABOUT the
			// column, so it grouped and ranked every drug instead of filtering to the controlled
			// ones. The value words live in the row's search text, where they become a filter.
			new AskColumn { Name = "controlled", Kind = AskColumnKind.Dimension, Label = AskText.Of("التصنيف الرقابي", "Controlled class", "Catégorie réglementée"),
				Synonyms = new[] { "تصنيف رقابي", "التصنيف الرقابي", "رقابي", "controlled class", "classification" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month", "Mois"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "بالشهر", "month", "monthly", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			new AskColumn { Name = "orders", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الأوامر", "Orders", "Prescriptions"),
				Synonyms = new[] { "عدد", "العدد", "كام", "اوامر", "الاوامر", "وصفات", "الوصفات",
					"orders", "prescriptions", "count", "how many" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var orders = await _db.Set<HospitalMedOrder>().AsNoTracking()
				.Where(o => o.CompanyId == scope.CompanyId)
				.Select(o => new { o.Drug, o.Route, o.Status, o.Controlled, o.StartAt })
				.ToListAsync(ct);

			if (orders.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var rows = new List<IReadOnlyDictionary<string, object?>>(orders.Count);
			foreach (var o in orders)
			{
				rows.Add(new Dictionary<string, object?>
				{
					["drug"] = string.IsNullOrWhiteSpace(o.Drug)
						? (arabic ? "(بلا اسم)" : "(unnamed)") : o.Drug.Trim(),
					["route"] = string.IsNullOrWhiteSpace(o.Route)
						? (arabic ? "(غير محدد)" : "(not recorded)") : o.Route.Trim(),
					// The stored status is English; shown in the reader's language and searchable in both.
					["status"] = Status(o.Status, arabic),
					[SearchKeyFor("status")] = (o.Status ?? "") + " " + Status(o.Status, true),
					["controlled"] = o.Controlled ? (arabic ? "مخدِّر" : "Controlled") : (arabic ? "عادي" : "Ordinary"),
					[SearchKeyFor("controlled")] = o.Controlled ? "مخدر مخدِّر Controlled narcotic" : "عادي Ordinary",
					["month"] = o.StartAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = o.StartAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["orders"] = 1m,
					[LinkFor("drug")] = null,
					[LinkFor("route")] = null,
					[LinkFor("status")] = null,
					[LinkFor("controlled")] = null,
					[LinkFor("month")] = null,
					[LinkFor("date")] = null,
				});
			}
			return rows;
		}

		/// The order's state as the reader's language writes it; anything unrecognised passes through.
		private static string Status(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محدد)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"active" => "ساري",
				"stopped" => "موقوف",
				"completed" => "منتهٍ",
				_ => v,
			};
		}
	}
}
