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
	// WHAT THE HOSPITAL CHARGED — the bill lines.
	//
	// The hospital is the largest module in the product and the assistant could see two things in it:
	// how many doctors there are, and how many medication orders were written. Neither of those is
	// money. Twenty-six billing tables carrying 458 rows had no window at all, so «كام إيراد
	// المستشفى» had no answer while «كام طبيب» did.
	//
	// THE GRAIN IS THE BILL LINE, AND THAT IS NOT THE SAME NUMBER AS A CLAIM.
	// A line is one charged service on one bill. What the hospital BILLED (4,253.91 across 104 lines)
	// and what an insurer ACCEPTED are different figures over different rows — 19 claim lines, most
	// of them still undecided — and answering one under the other's name is the quiet kind of wrong
	// this layer exists to stop. Claims live in their own surface; this one says so when asked.
	//
	// MONEY IS BEHIND `bill`, VOLUME IS BEHIND `read`.
	// The hospital's permission model already separates them: HospitalActions.Bill is a distinct
	// authority from HospitalActions.Read, held by the people who handle charges. So the surface
	// opens under `read` — a nurse may ask how many lines were raised on radiology — while every
	// money column carries Action = bill and is withheld, by column, from everyone else. Same shape
	// as the margin column in sales, for the same reason: what a place EARNED is more confidential
	// than what it DID.
	//
	// NO PATIENT DIMENSION, DELIBERATELY. Grouping by patient would put who-was-charged-what one
	// sentence from a chat box under the hospital's plain read. The aggregate carries nobody's name.
	// =================================================================================================
	public sealed class AskHospitalBillingSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.billing";

		private readonly CrossDbContext _db;
		public AskHospitalBillingSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.Read;

		public override AskText Description => AskText.Of(
			"فواتير المستشفى: المبلغ المفوتر وعدد البنود حسب التصنيف أو القسم أو حالة الفاتورة أو الشهر",
			"Hospital bills: the billed amount and line count by category, department, bill status or month",
			"Factures de l'hôpital : montants facturés et nombre de lignes par catégorie, service, statut de facture ou mois");

		public override AskText Rules => AskText.Of(
			"المبلغ هو المفوتر (الكمية × سعر الوحدة) قبل الضريبة — مش المحصَّل ومش اللي التأمين وافق عليه · "
			+ "قرارات التأمين في سطح المطالبات، وهي أرقام تانية على صفوف تانية · "
			+ "الفواتير المفتوحة والمقفولة الاتنين محسوبين وظاهرين بحالتهم · "
			+ "التاريخ هو تاريخ فتح الفاتورة · لا شيء هنا يخص مريضًا بعينه.",
			"The amount is what was BILLED (quantity x unit price) before tax — not what was collected "
			+ "and not what an insurer accepted · insurer decisions live in the claims surface and are "
			+ "different figures over different rows · open and closed bills are both counted and shown "
			+ "with their status · the date is when the bill was opened · nothing here is about an "
			+ "individual patient.");

		public static readonly string NoDeptAr = "(بلا قسم)";
		public static readonly string NoDeptEn = "(no department)";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// THE CATEGORY IS THE COLUMN; "أشعة" IS ONE OF ITS VALUES.
			//
			// The synonyms below name the CLASSIFICATION, never a member of it. Putting "اشعة" here
			// would make «فواتير الأشعة» a question ABOUT the category column — it would group and
			// rank every category instead of filtering to radiology. That exact shape has cost three
			// wrong answers in this layer already; the value words reach the filter through the row's
			// own search text, which is where they belong.
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension, Label = AskText.Of("التصنيف", "Category", "Catégorie"),
				Synonyms = new[] { "تصنيف", "التصنيف", "نوع الخدمة", "نوع البند", "بند", "البنود",
					"category", "categories", "service type", "line type" } },
			new AskColumn { Name = "department", Kind = AskColumnKind.Dimension, Label = AskText.Of("القسم", "Department", "Service"),
				Synonyms = new[] { "قسم", "القسم", "اقسام", "الاقسام", "حسب القسم",
					"department", "departments", "by department" } },
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("حالة الفاتورة", "Bill status", "Statut de facture"),
				Synonyms = new[] { "حالة", "الحالة", "حالة الفاتورة", "status", "bill status" } },
			new AskColumn { Name = "source", Kind = AskColumnKind.Dimension, Label = AskText.Of("مصدر البند", "Line source", "Origine de la ligne"),
				Synonyms = new[] { "مصدر", "المصدر", "مصدر البند", "source", "line source", "origin" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month", "Mois"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "بالشهر", "month", "monthly", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			// MONEY — withheld by column from anyone without the hospital's billing authority.
			new AskColumn { Name = "billed", Kind = AskColumnKind.Measure, IsMoney = true,
				Action = HospitalActions.Bill,
				Label = AskText.Of("المبلغ المفوتر", "Billed", "Facturé"),
				// THE VERB, NOT ONLY THE NOUN. «كام فوترنا» asked for MONEY and was answered with the
				// LINE COUNT, because "فوترنا" matched nothing here while "كام" sat in the count's
				// synonyms. A measure has to know the verb people use for it.
				Synonyms = new[] { "فوترنا", "فوترت", "فوتر", "بنفوتر", "اتفوتر",
					"مفوتر", "المفوتر", "فاتورة", "فواتير", "الفواتير", "مبلغ", "المبلغ",
					"ايراد", "الايراد", "ايرادات", "الايرادات", "فلوس", "قيمة", "القيمة",
					"billed", "billing", "revenue", "amount", "charges", "charged", "value" } },
			new AskColumn { Name = "lines", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد البنود", "Lines", "Lignes"),
				// "كام" IS DELIBERATELY ABSENT. On a money surface it means "how MUCH" far more
				// often than "how MANY", and leaving it here made «كام فوترنا» return 104 lines
				// instead of 4,253.91. The count is still reachable by naming it: "عدد البنود".
				Synonyms = new[] { "عدد", "العدد", "بنود", "عدد البنود", "خدمات", "الخدمات",
					"lines", "count", "how many", "services" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var lines = await _db.Set<HospitalBillLine>().AsNoTracking()
				.Where(l => l.CompanyId == scope.CompanyId)
				.Select(l => new { l.BillId, l.Category, l.Source, l.DepartmentId, l.Quantity, l.UnitPrice })
				.ToListAsync(ct);

			if (lines.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			// The bill carries the status and the date; the line carries neither. A line whose bill
			// is missing keeps its money and is labelled, never dropped — a revenue figure that
			// quietly loses rows is the failure this surface is here to avoid.
			var billIds = lines.Select(l => l.BillId).Distinct().ToList();
			var bills = (await _db.Set<HospitalBill>().AsNoTracking()
					.Where(b => b.CompanyId == scope.CompanyId && billIds.Contains(b.ID))
					.Select(b => new { b.ID, b.Status, b.OpenedAt })
					.ToListAsync(ct))
				.ToDictionary(b => b.ID);

			var deptIds = lines.Where(l => l.DepartmentId != null).Select(l => l.DepartmentId!.Value).Distinct().ToList();
			var depts = (await _db.HospitalDepartments.AsNoTracking()
					.Where(d => d.CompanyId == scope.CompanyId && deptIds.Contains(d.ID))
					.Select(d => new { d.ID, d.Name, d.NameEn })
					.ToListAsync(ct))
				.ToDictionary(d => d.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				bills.TryGetValue(l.BillId, out var bill);

				var dept = arabic ? NoDeptAr : NoDeptEn;
				var deptSearch = "";
				if (l.DepartmentId != null && depts.TryGetValue(l.DepartmentId.Value, out var d))
				{
					dept = (arabic ? (d.Name ?? d.NameEn) : (d.NameEn ?? d.Name)) ?? dept;
					deptSearch = ((d.Name ?? "") + " " + (d.NameEn ?? "")).Trim();
				}

				// Opened-at is the bill's; a line on a bill we could not read has no date rather
				// than today's, because "today" would silently put old money in this month.
				var opened = bill?.OpenedAt;

				rows.Add(new Dictionary<string, object?>
				{
					["category"] = Category(l.Category, arabic),
					[SearchKeyFor("category")] = (l.Category ?? "") + " " + Category(l.Category, true),
					["department"] = dept.Trim(),
					[SearchKeyFor("department")] = deptSearch,
					["status"] = BillStatus(bill?.Status, arabic),
					[SearchKeyFor("status")] = (bill?.Status ?? "") + " " + BillStatus(bill?.Status, true),
					["source"] = Source(l.Source, arabic),
					[SearchKeyFor("source")] = (l.Source ?? "") + " " + Source(l.Source, true),
					["month"] = opened?.ToString("yyyy-MM", CultureInfo.InvariantCulture)
						?? (arabic ? "(بلا تاريخ)" : "(no date)"),
					["date"] = opened?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
						?? (arabic ? "(بلا تاريخ)" : "(no date)"),
					["billed"] = l.Quantity * l.UnitPrice,
					["lines"] = 1m,
					[LinkFor("category")] = null,
					[LinkFor("department")] = null,
					[LinkFor("status")] = null,
					[LinkFor("source")] = null,
					[LinkFor("month")] = null,
					[LinkFor("date")] = null,
				});
			}
			return rows;
		}

		/// The stored category is English; shown in the reader's language and searchable in both.
		/// Anything the data grows later passes through rather than becoming "(other)".
		private static string Category(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير مصنَّف)" : "(uncategorised)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"accommodation" => "إقامة",
				"consultation" => "كشف",
				"lab" => "معمل",
				"radiology" => "أشعة",
				"pharmacy" => "صيدلية",
				"procedure" => "إجراء",
				"other" => "أخرى",
				_ => v,
			};
		}

		private static string BillStatus(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير معروفة)" : "(unknown)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"open" => "مفتوحة",
				"closed" => "مقفولة",
				"cancelled" => "ملغاة",
				_ => v,
			};
		}

		private static string Source(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محدد)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"accommodation" => "إقامة",
				"consultation" => "كشف",
				"order" => "أمر خدمة",
				"pharmacy" => "صيدلية",
				_ => v,
			};
		}
	}
}
