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
	// THE DAY'S CLINIC — appointments, and how long people actually waited.
	//
	// The owner typed «جيب مواعيد اليوم» into the assistant and was told, correctly, that it was not
	// among the questions it could answer. The refusal was honest; the gap was ours. 138 appointments
	// sat in one table with arrival, start and finish stamps on most of them, and the assistant could
	// not say how many were booked today.
	//
	// WAITING AND CONSULTING ARE MEASURED, NOT ASSUMED. The wait is ArrivedAt -> StartedAt and the
	// consultation is StartedAt -> FinishedAt, both in minutes, both taken from the row. An
	// appointment missing either stamp has no such number, and the measures are Nullable so it is
	// EXCLUDED AND COUNTED rather than read as a zero-minute wait. 103 of 138 record an arrival, 95
	// a start and 90 a finish, so averaging zeros would have flattered the clinic by a third.
	//
	// NO MEASURE IS NAMED FOR A STATUS. There is deliberately no "no-shows" measure here: "NoShow" is
	// a VALUE of the status column, and a measure sharing that word would make «المواعيد اللي محدش
	// حضرها» a question ABOUT the measure, ranking every status instead of filtering to the one named.
	// That shape has cost this layer five wrong answers. Ask for the status by name and the filter
	// does it: the count is already there.
	//
	// NO PATIENT DIMENSION, DELIBERATELY. Who was seen, by whom and when is a patient record; the
	// aggregate is a clinic's workload and carries nobody's name.
	// =================================================================================================
	public sealed class AskAppointmentsSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.appointments";

		private readonly CrossDbContext _db;
		public AskAppointmentsSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.Read;

		public override AskText Description => AskText.Of(
			"مواعيد العيادات: العدد ومتوسط الانتظار ومدة الكشف حسب الحالة أو الطبيب أو القسم أو اليوم",
			"Clinic appointments: the count, average wait and consultation length by status, practitioner, department or day",
			"Rendez-vous des cliniques : nombre, attente moyenne et durée des consultations par statut, praticien, service ou jour");

		public override AskText Rules => AskText.Of(
			"العدد هو عدد المواعيد، مش عدد المرضى — المريض الواحد ممكن يكون ليه أكتر من موعد · "
			+ "الانتظار = من الوصول لبداية الكشف، والكشف = من البداية للنهاية، بالدقايق ومن واقع "
			+ "الأختام المسجَّلة · الموعد اللي مالوش ختم مابيدخلش في المتوسط ويتعدّ، لأن انتظار غير "
			+ "مسجَّل مش انتظار صفر · المواعيد الملغاة واللي محدش حضرها محسوبة في العدد وظاهرة بحالتها · "
			+ "التاريخ هو تاريخ الموعد نفسه، والمستقبل مشمول.",
			"The count is of APPOINTMENTS, not patients — one patient can hold several · the wait is "
			+ "arrival to consultation start and the consultation is start to finish, in minutes, from "
			+ "the recorded stamps · an appointment missing a stamp is excluded from that average and "
			+ "counted, because an unrecorded wait is not a zero wait · cancelled and no-show "
			+ "appointments are included in the count and shown with their status · the date is the "
			+ "appointment's own, and future bookings are included.");

		public static readonly string NoDeptAr = "(بلا قسم)";
		public static readonly string NoDeptEn = "(no department)";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// THE COLUMN IS THE STATE; "لم يحضر" IS ONE OF ITS VALUES.
			//
			// None of these synonyms is a status value, and none ever may be. A value word listed
			// here becomes surface vocabulary, the named-value filter stops looking for it, and a
			// question about one status is answered with the total for all of them.
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("حالة الموعد", "Status", "Statut"),
				Synonyms = new[] { "حالة", "الحالة", "حالة الموعد", "status", "appointment status" } },
			new AskColumn { Name = "visit", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع الزيارة", "Visit type", "Type de visite"),
				Synonyms = new[] { "نوع الزيارة", "نوع الموعد", "نوع", "visit type", "appointment type", "kind of visit" } },
			new AskColumn { Name = "practitioner", Kind = AskColumnKind.Dimension, Label = AskText.Of("الطبيب", "Practitioner", "Praticien"),
				Synonyms = new[] { "طبيب", "الطبيب", "دكتور", "الدكتور", "حسب الطبيب",
					"doctor", "practitioner", "by doctor", "per doctor" } },
			new AskColumn { Name = "department", Kind = AskColumnKind.Dimension, Label = AskText.Of("القسم", "Department", "Service"),
				Synonyms = new[] { "قسم", "القسم", "اقسام", "الاقسام", "حسب القسم",
					"department", "departments", "by department" } },
			new AskColumn { Name = "hour", Kind = AskColumnKind.Dimension, Label = AskText.Of("الساعة", "Hour", "Heure"),
				Synonyms = new[] { "ساعة", "الساعة", "ساعات", "بالساعة", "hour", "by hour", "time of day" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month", "Mois"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "بالشهر", "month", "monthly", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			new AskColumn { Name = "appointments", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد المواعيد", "Appointments", "Rendez-vous"),
				Synonyms = new[] { "عدد", "العدد", "كام", "مواعيد", "المواعيد", "حجوزات", "الحجوزات",
					"appointments", "bookings", "count", "how many" } },
			// MINUTES, AND ABSENT ON PURPOSE WHERE THEY WERE NEVER RECORDED.
			new AskColumn { Name = "wait", Kind = AskColumnKind.Measure, Nullable = true,
				Label = AskText.Of("دقائق الانتظار", "Wait minutes", "Minutes d'attente"),
				Synonyms = new[] { "انتظار", "الانتظار", "مدة الانتظار", "استنى", "استنوا", "طابور",
					"wait", "waiting", "waiting time", "wait minutes" } },
			new AskColumn { Name = "consult", Kind = AskColumnKind.Measure, Nullable = true,
				Label = AskText.Of("دقائق الكشف", "Consultation minutes", "Minutes de consultation"),
				Synonyms = new[] { "مدة الكشف", "زمن الكشف", "مدة الزيارة", "طول الكشف",
					"consultation", "consult", "consultation time", "visit length" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var appts = await _db.Set<HospitalAppointment>().AsNoTracking()
				.Where(a => a.CompanyId == scope.CompanyId)
				.Select(a => new
				{
					a.PractitionerId, a.AppointmentDate, a.StartMinute, a.VisitType, a.Status,
					a.ArrivedAt, a.StartedAt, a.FinishedAt,
				})
				.ToListAsync(ct);

			if (appts.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var docIds = appts.Select(a => a.PractitionerId).Distinct().ToList();
			var docs = (await _db.HospitalPractitioners.AsNoTracking()
					.Where(p => p.CompanyId == scope.CompanyId && docIds.Contains(p.ID))
					.Select(p => new { p.ID, p.Name, p.NameEn, p.DepartmentId })
					.ToListAsync(ct))
				.ToDictionary(p => p.ID);

			var deptIds = docs.Values.Where(p => p.DepartmentId != null).Select(p => p.DepartmentId!.Value).Distinct().ToList();
			var depts = (await _db.HospitalDepartments.AsNoTracking()
					.Where(d => d.CompanyId == scope.CompanyId && deptIds.Contains(d.ID))
					.Select(d => new { d.ID, d.Name, d.NameEn })
					.ToListAsync(ct))
				.ToDictionary(d => d.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(appts.Count);
			foreach (var a in appts)
			{
				var doc = arabic ? "(بلا طبيب)" : "(no practitioner)";
				var docSearch = "";
				var dept = arabic ? NoDeptAr : NoDeptEn;
				var deptSearch = "";
				if (docs.TryGetValue(a.PractitionerId, out var p))
				{
					doc = (arabic ? (p.Name ?? p.NameEn) : (p.NameEn ?? p.Name)) ?? doc;
					docSearch = ((p.Name ?? "") + " " + (p.NameEn ?? "")).Trim();
					if (p.DepartmentId != null && depts.TryGetValue(p.DepartmentId.Value, out var d))
					{
						dept = (arabic ? (d.Name ?? d.NameEn) : (d.NameEn ?? d.Name)) ?? dept;
						deptSearch = ((d.Name ?? "") + " " + (d.NameEn ?? "")).Trim();
					}
				}

				// NULL, never 0, when a stamp was not taken. The measures are Nullable so the layer
				// excludes and counts these instead of averaging an unrecorded wait as no wait.
				decimal? wait = (a.ArrivedAt != null && a.StartedAt != null)
					? (decimal)Math.Round((a.StartedAt.Value - a.ArrivedAt.Value).TotalMinutes, 1)
					: null;
				decimal? consult = (a.StartedAt != null && a.FinishedAt != null)
					? (decimal)Math.Round((a.FinishedAt.Value - a.StartedAt.Value).TotalMinutes, 1)
					: null;
				// A clock that runs backwards is a data fault, not a negative wait.
				if (wait < 0) wait = null;
				if (consult < 0) consult = null;

				// StartMinute is minutes from midnight; the hour is what a question about the busy
				// part of the day is actually asking for.
				var hour = Math.Max(0, Math.Min(23, a.StartMinute / 60));

				rows.Add(new Dictionary<string, object?>
				{
					["status"] = Status(a.Status, arabic),
					[SearchKeyFor("status")] = (a.Status ?? "") + " " + Status(a.Status, true) + " " + StatusSaid(a.Status),
					["visit"] = Visit(a.VisitType, arabic),
					[SearchKeyFor("visit")] = (a.VisitType ?? "") + " " + Visit(a.VisitType, true),
					["practitioner"] = doc.Trim(),
					[SearchKeyFor("practitioner")] = docSearch,
					["department"] = dept.Trim(),
					[SearchKeyFor("department")] = deptSearch,
					["hour"] = hour.ToString("00", CultureInfo.InvariantCulture) + ":00",
					["month"] = a.AppointmentDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = a.AppointmentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["appointments"] = 1m,
					["wait"] = wait,
					["consult"] = consult,
					[LinkFor("status")] = null,
					[LinkFor("visit")] = null,
					[LinkFor("practitioner")] = null,
					[LinkFor("department")] = null,
					[LinkFor("hour")] = null,
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
				"booked" => "محجوز",
				"arrived" => "حضر",
				"inroom" => "داخل الكشف",
				"done" => "تم",
				"noshow" => "لم يحضر",
				"cancelled" => "ملغي",
				_ => v,
			};
		}


		/// ONE RENDERING IS NOT ONE WORD. The status shows as «ملغي», and «المواعيد الملغاة» - the
		/// way anyone would actually ask - matched nothing, so the answer opened with "I could not
		/// find «الملغاة»" and covered all 138. English filtered correctly on the same question,
		/// because "Cancelled" is spelt one way. The search key carries the spoken forms too; this
		/// is what that key is for, and a surface that renders Arabic owes every one of them.
		private static string StatusSaid(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
		{
			"booked" => "محجوزة محجوزين حجز الحجوزات",
			"arrived" => "حضروا حضرت وصل وصلوا",
			"inroom" => "جوه الكشف مع الدكتور",
			"done" => "تمت خلصت منتهية منتهي اللي خلصوا",
			"noshow" => "لم تحضر ماحضرش ماحضروش ماجاش ماجوش غياب غائب",
			"cancelled" => "ملغاة ملغاه ملغية اتلغت اللي اتلغت الغاء",
			_ => "",
		};

		private static string Visit(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محدد)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"new" => "كشف جديد",
				"followup" => "متابعة",
				"procedure" => "إجراء",
				"video" => "كشف بالفيديو",
				_ => v,
			};
		}
	}
}
