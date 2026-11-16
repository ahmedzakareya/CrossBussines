using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE LABORATORY — what was ordered, where it has reached, and the results nobody has acknowledged.
	//
	// A CRITICAL RESULT THAT NOBODY HAS SEEN IS THE MOST IMPORTANT ROW IN THIS DATABASE. The order
	// carries IsCritical and, beside it, CriticalAcknowledgedAt — so the hospital already records both
	// that a result was alarming and whether a human took it. Until now no question could reach that
	// pair, and «نتايج التحاليل الحرجة» was refused. An exception list does not need a large dataset
	// to be worth having: two criticals of sixteen orders is as actionable as two hundred of sixteen
	// thousand, and the one that matters is the one with no acknowledgement.
	//
	// THE GRAIN IS THE ORDER, NOT THE RESULT, and the Rules say so. Sixteen orders carry twenty-six
	// results; «كام تحليل» means the order (a panel someone requested), and per-analyte flags are a
	// different count over different rows. This surface deliberately does NOT carry the result-level
	// flag — 17 normal, 5 high, 2 low, 1 critical-low, 1 critical-high — because mixing the two grains
	// behind one count measure is the mistake the diagnoses surface already paid for.
	//
	// TURNAROUND IS MEASURED ON WHAT WAS STAMPED. Sixteen orders record an order time, nine a
	// collection, eight a result and seven a verification. The measure is Nullable, so an order that
	// has not been verified is EXCLUDED AND COUNTED rather than averaged in as a zero-minute
	// turnaround — otherwise a lab that stops verifying looks like a lab that got faster.
	//
	// NO PATIENT DIMENSION. Which patient had which test is the patient's record; this is a bench's
	// workload and a safety queue.
	// =================================================================================================
	public sealed class AskLabSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.lab";

		private readonly CrossDbContext _db;
		public AskLabSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		// The bench's own authority. A lab order says what a doctor suspected, so it reads under the
		// action that governs the laboratory rather than under the module's general read.
		public override string Action => HospitalActions.Lab;

		public override AskText Description => AskText.Of(
			"طلبات المعمل: العدد وفين وصلت وزمن التنفيذ، والنتايج الحرجة واللي لسه محدش استلمها",
			"Laboratory orders: how many, how far they have got, how long they take, and the critical results — including any nobody has acknowledged");

		public override AskText Rules => AskText.Of(
			"كل سطر هنا طلب تحليل واحد، مش نتيجة واحدة — الطلب الواحد فيه أكتر من تحليل، فالعدد ده "
			+ "عدد الطلبات · علامات النتايج نفسها (مرتفع/منخفض لكل تحليل) مش محسوبة هنا، دي على "
			+ "مستوى تاني · زمن التنفيذ = من الطلب للاعتماد بالدقايق، ومن واقع الأختام المسجَّلة؛ "
			+ "الطلب اللي لسه ماتعتمدش مابيدخلش في المتوسط ويتعدّ لوحده · «حرجة» معناها إن المعمل "
			+ "علّم النتيجة كحرجة، و«اتبلّغ» معناها إن فيه حد استلمها وسجّل ده · الطلبات الملغاة "
			+ "مش موجودة، وكل الطلبات في كل الحالات داخلة في العد.",
			"Each row here is ONE lab order, not one result — an order carries several analytes, so "
			+ "this counts ORDERS · the per-analyte flags (high/low on each result) are not counted "
			+ "here; they live at a different grain · turnaround is order to verification in minutes, "
			+ "from the recorded stamps, and an order not yet verified is excluded from that average "
			+ "and counted separately · \"critical\" means the laboratory flagged the result as "
			+ "critical, and \"acknowledged\" means a human took it and recorded doing so · orders in "
			+ "every status are counted.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// THE SAFETY PAIR, AND THE REASON THIS SURFACE EXISTS. Neither column's synonyms contain
			// any of its own values: «حرجة», «اتبلّغ» and «لسه» all have to stay findable by the
			// named-value filter, or a question about the unacknowledged criticals is answered with
			// the count of every order.
			// DECLARED FIRST so «محدش استلمها» wins the filter over «حرجة». The question this surface
			// exists for names BOTH, no single column holds both words, and the filter then falls to
			// its one-word pass and takes the FIRST dimension that matches. With criticality first,
			// «في نتايج حرجة محدش استلمها» filtered to the criticals and silently dropped the half
			// that was actually being asked — answering "2 critical" to "has anyone seen them".
			new AskColumn { Name = "acknowledgement", Kind = AskColumnKind.Dimension, Label = AskText.Of("الاستلام", "Acknowledgement"),
				Synonyms = new[] { "استلام", "الاستلام", "تبليغ", "التبليغ",
					"acknowledgement", "acknowledgment", "sign off on result" } },
			// NAMED "severity", NOT "criticality", AND THE NAME IS THE WHOLE POINT.
			// `"criticality".Contains("critical")` is true, so the column's own name made "critical"
			// surface VOCABULARY — the value filter never looked for it, and «critical lab results»
			// came back as the entire pipeline, 16 orders, with nothing on screen to say it had not
			// filtered. The Arabic twin passed, because «حرجة» is not inside «تصنيف النتيجة».
			// Same defect as "accepted" on the claims surface, in the other language.
			new AskColumn { Name = "severity", Kind = AskColumnKind.Dimension, Label = AskText.Of("تصنيف النتيجة", "Result grade"),
				Synonyms = new[] { "تصنيف", "التصنيف", "تصنيف النتيجة", "درجة النتيجة", "grade", "result grade" } },
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("حالة الطلب", "Order status"),
				Synonyms = new[] { "حالة", "الحالة", "حالة الطلب", "المرحلة", "status", "stage", "order status" } },
			new AskColumn { Name = "priority", Kind = AskColumnKind.Dimension, Label = AskText.Of("الأولوية", "Priority"),
				Synonyms = new[] { "اولوية", "الاولوية", "حسب الاولوية", "priority", "by priority" } },
			new AskColumn { Name = "panel", Kind = AskColumnKind.Dimension, Label = AskText.Of("التحليل", "Test"),
				Synonyms = new[] { "تحليل", "التحليل", "نوع التحليل", "اسم التحليل", "فحص", "الفحص",
					"test", "tests", "panel", "assay", "by test" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "بالشهر", "month", "monthly", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			new AskColumn { Name = "orders", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الطلبات", "Orders"),
				Synonyms = new[] { "عدد", "العدد", "كام", "طلبات", "الطلبات", "عينات", "العينات",
					"orders", "count", "how many", "requests", "samples" } },
			// MINUTES, AND ABSENT WHERE THE CHAIN WAS NEVER FINISHED.
			new AskColumn { Name = "turnaround", Kind = AskColumnKind.Measure, Nullable = true,
				Label = AskText.Of("دقائق التنفيذ", "Turnaround minutes"),
				Synonyms = new[] { "زمن", "الزمن", "مدة", "المدة", "زمن التنفيذ", "مدة التحليل", "بياخد قد ايه",
					"turnaround", "turn around", "tat", "how long", "lead time" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var orders = await _db.Set<HospitalLabOrder>().AsNoTracking()
				.Where(o => o.CompanyId == scope.CompanyId)
				.Select(o => new
				{
					o.Name, o.NameEn, o.Code, o.Priority, o.Status,
					o.OrderedAt, o.VerifiedAt, o.IsCritical, o.CriticalAcknowledgedAt,
				})
				.ToListAsync(ct);

			if (orders.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var rows = new List<IReadOnlyDictionary<string, object?>>(orders.Count);
			foreach (var o in orders)
			{
				var panel = (arabic ? (o.Name ?? o.NameEn) : (o.NameEn ?? o.Name)) ?? "";
				if (panel.Trim().Length == 0) panel = o.Code ?? (arabic ? "(بلا اسم)" : "(unnamed)");

				// NULL, never 0, when the chain stops short. Nullable, so the layer excludes and
				// counts it instead of reporting an unverified order as an instant one.
				decimal? turnaround = o.VerifiedAt != null
					? (decimal)Math.Round((o.VerifiedAt.Value - o.OrderedAt).TotalMinutes, 1)
					: null;
				if (turnaround < 0) turnaround = null;   // a clock that runs backwards is a fault

				// ACKNOWLEDGEMENT ONLY MEANS ANYTHING FOR A CRITICAL RESULT. An ordinary order has
				// nothing to acknowledge, and rendering it as "not acknowledged" would invent a
				// queue of hundreds of safety exceptions out of routine work.
				var ack = !o.IsCritical
					? (arabic ? "(لا ينطبق)" : "(not applicable)")
					: o.CriticalAcknowledgedAt != null
						? (arabic ? "اتبلّغ" : "Acknowledged")
						: (arabic ? "لسه محدش استلمها" : "Not acknowledged");
				// THE TWO KEYS MUST SHARE NO WORD, OR THE FILTER PICKS THE OPPOSITE ANSWER.
				// «استلمها» was in BOTH, so «في نتايج حرجة محدش استلمها» matched the ACKNOWLEDGED
				// rows and reported "2 اتبلّغ" to a question asking which ones nobody had taken.
				// It read as correct because both criticals happen to be acknowledged — the right
				// number for the wrong reason, which is the failure that survives a row count.
				// The negative side keeps «محدش» and «مااتبلغش»; the positive side keeps neither.
				var ackSearch = !o.IsCritical
					? "na not applicable"
					: o.CriticalAcknowledgedAt != null
						? "اتبلغ اتبلغت اتاخدت اتشافت اترد عليها acknowledged taken seen"
						: "محدش مااتبلغش مااتاخدتش معلقة متعلقة منتظرة مهملة "
						  + "not acknowledged unacknowledged outstanding nobody pending";

				rows.Add(new Dictionary<string, object?>
				{
					["severity"] = o.IsCritical ? (arabic ? "حرجة" : "Critical") : (arabic ? "عادية" : "Routine result"),
					[SearchKeyFor("severity")] = o.IsCritical
						? "حرجة حرج حرجه الحرجة خطيرة خطرة critical panic alarming"
						: "عادية عادي العادية طبيعية normal routine ordinary",
					["acknowledgement"] = ack,
					[SearchKeyFor("acknowledgement")] = ackSearch,
					["status"] = Status(o.Status, arabic),
					[SearchKeyFor("status")] = (o.Status ?? "") + " " + Status(o.Status, true) + " " + StatusSaid(o.Status),
					["priority"] = Priority(o.Priority, arabic),
					[SearchKeyFor("priority")] = (o.Priority ?? "") + " " + Priority(o.Priority, true) + " " + PrioritySaid(o.Priority),
					["panel"] = panel.Trim(),
					[SearchKeyFor("panel")] = ((o.Code ?? "") + " " + (o.Name ?? "") + " " + (o.NameEn ?? "")).Trim(),
					["month"] = o.OrderedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = o.OrderedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["orders"] = 1m,
					["turnaround"] = turnaround,
					[LinkFor("severity")] = null,
					[LinkFor("acknowledgement")] = null,
					[LinkFor("status")] = null,
					[LinkFor("priority")] = null,
					[LinkFor("panel")] = null,
					[LinkFor("month")] = null,
					[LinkFor("date")] = null,
				});
			}
			return rows;
		}

		private static string Status(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محددة)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"ordered" => "مطلوب",
				"collected" => "اتسحبت العينة",
				"received" => "وصلت المعمل",
				"resulted" => "طلعت النتيجة",
				"verified" => "معتمدة",
				_ => v,
			};
		}

		private static string StatusSaid(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
		{
			"ordered" => "مطلوب مطلوبة اتطلب لسه ماتسحبتش",
			"collected" => "اتسحبت السحب العينة اتاخدت",
			"received" => "وصلت استلمها المعمل",
			"resulted" => "طلعت النتيجة جاهزة لسه ماتعتمدتش",
			"verified" => "معتمدة اعتمدت اتعتمدت خلصت نهائية",
			_ => "",
		};

		private static string Priority(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محددة)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"routine" => "عادية",
				"urgent" => "مستعجلة",
				"stat" => "فورية",
				_ => v,
			};
		}

		private static string PrioritySaid(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
		{
			"routine" => "عادية عادي روتين",
			"urgent" => "مستعجلة مستعجل عاجلة عاجل urgent",
			"stat" => "فورية فوري حالا دلوقتي stat immediate",
			_ => "",
		};
	}
}
