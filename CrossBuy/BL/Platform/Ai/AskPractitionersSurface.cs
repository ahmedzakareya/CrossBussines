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
	// THE DOCTORS — the assistant's first window onto the hospital.
	//
	// The owner asked it «جيب قائمة الاطباء» and was refused, and the questions it offered him
	// instead were about revenue, stock and invoices. Nothing was broken: the hospital is the
	// largest module in this product and the assistant had never been given a single surface over
	// it. Six surfaces answered for sales, purchases, stock, the till, the payroll roster and
	// expenses, and none for the thing the hospital runs on.
	//
	// WHY THE PRACTITIONERS AND NOT THE PATIENTS, FIRST. This surface groups and counts, and it
	// carries a NameKey so one doctor can be named. A patient surface would have to answer to
	// confidential-view rather than read, would cross the record-access log that every patient open
	// is written to, and would put a person's clinical file one sentence away from a chat box. That
	// is a bigger decision than a first surface should quietly make. The doctors are staff, their
	// list is already on a screen anyone with hospital read may open, and the question the owner
	// actually asked is this one.
	// =================================================================================================
	public sealed class AskPractitionersSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.practitioners";

		private readonly CrossDbContext _db;
		public AskPractitionersSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;

		// THE HOSPITAL'S OWN READ, not the HR one. A practitioner is not an employee row — some have
		// no employee behind them at all — and borrowing Hr:read would have let anyone who may see a
		// headcount see the clinical roster.
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.Read;

		public override AskText Description => AskText.Of(
			"الأطباء المسجَّلون: العدد حسب القسم أو نوع التعاقد أو الحالة، أو البحث عن طبيب بالاسم",
			"Registered practitioners: the count by department, engagement or status, or one doctor by name",
			"Praticiens enregistrés : effectifs par service, type de contrat ou statut, ou recherche d'un médecin par son nom");

		public override AskText Rules => AskText.Of(
			"الأطباء المسجَّلون في هذه الشركة · غير النشطين مشمولون وظاهرون بحالتهم، فالعدد هو السجل "
			+ "لا المناوبة · القسم غير مسجَّل على بعضهم وهؤلاء يظهرون باسم خاص ولا يُستبعَدون · "
			+ "لا شيء هنا عن المرضى أو المواعيد أو الأجر.",
			"Practitioners registered in this company · inactive ones are included and shown with their "
			+ "status, so this is the register and not who is on duty · some carry no department and are "
			+ "shown under their own label rather than dropped · nothing here is about patients, "
			+ "appointments or pay.");

		public static readonly string NoDeptAr = "(بلا قسم)";
		public static readonly string NoDeptEn = "(no department)";

		public override string? NameKey => "__name";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "department", Kind = AskColumnKind.Dimension, Label = AskText.Of("القسم", "Department", "Service"),
				// "عيادة" IS NOT A WORDING FOR "قسم". The owner asked «جيب قائمة العيادات» and was
				// handed the list of doctors, because this column had claimed the word. A clinic is
				// a room a doctor sits in; it is its own dimension, below.
				Synonyms = new[] { "قسم", "اقسام", "الاقسام", "تخصص", "التخصص", "تخصصات",
					"department", "departments", "specialty", "specialties", "unit" } },
			new AskColumn { Name = "clinic", Kind = AskColumnKind.Dimension, Label = AskText.Of("العيادة", "Clinic", "Clinique"),
				Synonyms = new[] { "عيادة", "العيادة", "عيادات", "العيادات", "غرفة", "الغرفة",
					"clinic", "clinics", "room", "rooms" } },
			// ITS VALUES ARE NOT ITS SYNONYMS. "زائر" and "متفرغ" name a KIND of engagement, not the
			// column, and listing them here made "الاطباء الزائرين" group by engagement and rank
			// all three kinds — answering "which engagement has the most doctors" to a question
			// about one of them. They belong in the row's search text, where they become a filter.
			new AskColumn { Name = "engagement", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع التعاقد", "Engagement", "Type de contrat"),
				Synonyms = new[] { "تعاقد", "التعاقد", "نوع التعاقد",
					"engagement", "contract type" } },
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("الحالة", "Status", "Statut"),
				Synonyms = new[] { "حالة", "الحالة", "status" } },
			new AskColumn { Name = "title", Kind = AskColumnKind.Dimension, Label = AskText.Of("الدرجة", "Title", "Titre"),
				Synonyms = new[] { "درجة", "الدرجة", "لقب", "اللقب", "title", "grade" } },

			new AskColumn { Name = "headcount", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الأطباء", "Practitioners", "Praticiens"),
				Synonyms = new[] { "عدد", "العدد", "كام", "اطباء", "الاطباء", "طبيب", "الطبيب", "دكاترة",
					"الدكاترة", "دكتور", "الدكتور", "practitioners", "doctors", "physicians", "count", "how many" } },
		};

		/// The engagement as the reader's language writes it. The column stores Permanent, Visiting
		/// and PartTime; anything else is passed through rather than guessed at.
		private static string Engagement(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محدد)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"permanent" => "متفرغ",
				"visiting" => "زائر",
				"parttime" or "part time" => "دوام جزئي",
				_ => v,
			};
		}

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var people = await _db.HospitalPractitioners.AsNoTracking()
				.Where(p => p.CompanyId == scope.CompanyId)
				.Select(p => new
				{
					p.ID, p.Name, p.NameEn, p.Title, p.TitleEn, p.DepartmentId, p.Engagement, p.IsActive,
					p.EmployeeId, p.ClinicRoomId,
				})
				.ToListAsync(ct);

			if (people.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var deptIds = people.Where(p => p.DepartmentId != null).Select(p => p.DepartmentId!.Value).Distinct().ToList();
			// HospitalDepartments carries Name and NameEn — read from the table, not assumed from the
			// three other conventions this codebase uses for the same idea.
			var depts = (await _db.HospitalDepartments.AsNoTracking()
					.Where(d => d.CompanyId == scope.CompanyId && deptIds.Contains(d.ID))
					.Select(d => new { d.ID, d.Name, d.NameEn })
					.ToListAsync(ct))
				.ToDictionary(d => d.ID);

			// The clinic room: 6 of the 8 practitioners here carry one, so the two without are
			// labelled rather than dropped — a list of clinics that quietly lost a doctor would be
			// the same defect in a smaller place.
			var roomIds = people.Where(p => p.ClinicRoomId != null).Select(p => p.ClinicRoomId!.Value).Distinct().ToList();
			var rooms = (await _db.HospitalRooms.AsNoTracking()
					.Where(r => r.CompanyId == scope.CompanyId && roomIds.Contains(r.ID))
					.Select(r => new { r.ID, r.Number, r.Name, r.NameEn })
					.ToListAsync(ct))
				.ToDictionary(r => r.ID);

			// The photo comes from the EMPLOYEE behind the practitioner, and plenty have none.
			var empIds = people.Where(p => p.EmployeeId != null).Select(p => p.EmployeeId!.Value).Distinct().ToList();
			var photos = (await _db.Employee.AsNoTracking()
					.Where(e => empIds.Contains(e.ID))
					.Select(e => new { e.ID, e.ProfileImage })
					.ToListAsync(ct))
				.ToDictionary(e => e.ID, e => e.ProfileImage);

			var rows = new List<IReadOnlyDictionary<string, object?>>(people.Count);
			foreach (var p in people)
			{
				var name = AskItems.Readable(arabic ? p.Name : p.NameEn, arabic ? p.NameEn : p.Name) ?? ("#" + p.ID);

				var dept = arabic ? NoDeptAr : NoDeptEn;
				if (p.DepartmentId != null && depts.TryGetValue(p.DepartmentId.Value, out var d))
					dept = AskItems.Readable(arabic ? d.Name : d.NameEn, arabic ? d.NameEn : d.Name) ?? ("#" + d.ID);

				var title = AskItems.Readable(arabic ? p.Title : p.TitleEn, arabic ? p.TitleEn : p.Title);

				string? photo = null;
				if (p.EmployeeId != null && photos.TryGetValue(p.EmployeeId.Value, out var img))
					photo = CrossBuy.BL.Hr.PersonTile.Photo(img);

				var clinic = arabic ? "(بلا عيادة)" : "(no clinic)";
				string clinicSearch = "";
				if (p.ClinicRoomId != null && rooms.TryGetValue(p.ClinicRoomId.Value, out var rm))
				{
					var rn = AskItems.Readable(arabic ? rm.Name : rm.NameEn, arabic ? rm.NameEn : rm.Name);
					clinic = string.IsNullOrWhiteSpace(rn) ? (rm.Number ?? ("#" + rm.ID)) : rn!;
					clinicSearch = ((rm.Name ?? "") + " " + (rm.NameEn ?? "") + " " + (rm.Number ?? "")).Trim();
				}

				rows.Add(new Dictionary<string, object?>
				{
					["department"] = dept.Trim(),
					["clinic"] = clinic.Trim(),
					[SearchKeyFor("clinic")] = clinicSearch,
					[SearchKeyFor("department")] = ((p.DepartmentId != null && depts.TryGetValue(p.DepartmentId.Value, out var d2)
						? (d2.Name ?? "") + " " + (d2.NameEn ?? "") : "")).Trim(),
					// THE STORED VALUE IS ENGLISH. Printing "Permanent" inside an Arabic sentence is
					// the same wart gender had on the workforce surface; the search text keeps BOTH
					// spellings so "الزائرين" and "visiting" each find the same doctors.
					["engagement"] = Engagement(p.Engagement, arabic),
					[SearchKeyFor("engagement")] = (p.Engagement ?? "") + " " + Engagement(p.Engagement, true),
					["status"] = p.IsActive ? (arabic ? "نشط" : "Active") : (arabic ? "غير نشط" : "Inactive"),
					[SearchKeyFor("status")] = p.IsActive ? "نشط Active" : "غير نشط موقوف Inactive",
					["title"] = string.IsNullOrWhiteSpace(title) ? (arabic ? "(بلا درجة)" : "(no title)") : title!.Trim(),
					["headcount"] = 1m,
					["__name"] = name,
					// Both spellings, so a doctor is findable in the script the asker types in.
					[SearchNameKey] = ((p.Name ?? "") + " " + (p.NameEn ?? "")).Trim(),
					[PhotoKey] = photo,
					// The destination runs its own hospital gate; the assistant is never the way round it.
					[RecordHrefKey] = "/Hospital/Practitioners",
					[LinkFor("department")] = null,
					[LinkFor("clinic")] = null,
					[LinkFor("engagement")] = null,
					[LinkFor("status")] = null,
					[LinkFor("title")] = null,
				});
			}
			return rows;
		}
	}
}
