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
	// THE BED BOARD — how many beds there are, where they are, and which of them are free RIGHT NOW.
	//
	// THIS SURFACE EXISTS BECAUSE I REFUSED THESE QUESTIONS AND I WAS WRONG. «كام سرير فاضي»,
	// «كام مريض منوم» and «نسبة اشغال الاسرة» were taught as out-of-scope in 555b19fe, on the finding
	// that bed status was seeded rather than derived: 22 beds carrying a Status, and one admission in
	// the whole database. The admission count came from a query that said
	// `COUNT(*) FROM sys.partitions` instead of `SUM(p.rows)` — it counted PARTITIONS. There are ten
	// admissions, seven of them open, and the beds agree exactly: seven open bed stays, seven beds
	// marked Occupied, seven active admissions. The data was sound the whole time and the refusal was
	// the defect, which is the same failure as an honest refusal that is wrong about the product.
	//
	// A SNAPSHOT, NOT A PERIOD, AND THE DIFFERENCE HAS A SCREEN. This counts beds as they stand this
	// instant. The hospital's occupancy INDICATOR on /Hospital/Quality is patient-days ÷ (beds × days)
	// over a month — a different number from a different question, and it reads 30.9% where this board
	// reads seven of twenty-two. Neither is wrong; answering one under the other's name would be, so
	// the caveat names the screen rather than competing with it.
	//
	// NO PATIENT DIMENSION, and here that matters more than anywhere else in this layer: a bed board
	// keyed by name is a list of who is in hospital. Which bed is occupied is operational; who is in
	// it is the patient's record.
	// =================================================================================================
	public sealed class AskWardSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.ward";

		private readonly CrossDbContext _db;
		public AskWardSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.Read;

		public override AskText Description => AskText.Of(
			"لوحة الأسرّة: كام سرير وفين، وإيه الفاضي والمشغول دلوقتي، حسب الجناح أو الغرفة",
			"The bed board: how many beds there are and where, and which are free or occupied right now, by ward or room");

		public override AskText Rules => AskText.Of(
			"كل سطر هنا سرير واحد شغّال · الأرقام دي لحظية — حالة السرير دلوقتي، مش متوسط فترة · "
			+ "نسبة الإشغال كمؤشر شهري (أيام المرضى ÷ الأسرّة × الأيام) موجودة في شاشة الجودة، ورقمها "
			+ "غير رقم اللوحة دي عن قصد · السرير المشغول مش هو المريض: السرير اللي فيه إقامة "
			+ "مفتوحة بيتحسب مشغول، والمريض المنوّم من غير سرير دلوقتي مش بيبان هنا · الأسرّة "
			+ "الموقوفة واللي تحت التنظيف محسوبة وظاهرة بحالتها، لأن سرير مش قابل للاستخدام "
			+ "مش سرير فاضي · مفيش أسماء مرضى هنا.",
			"Each row here is ONE active bed · these figures are a SNAPSHOT — the bed's state right "
			+ "now, not an average over a period · the monthly occupancy INDICATOR (patient-days ÷ "
			+ "beds × days) lives on the Quality screen and is deliberately a different number from "
			+ "this board · an occupied bed is not a patient: a bed with an open stay counts as "
			+ "occupied, and a patient admitted but not currently in a bed does not appear here · "
			+ "blocked and cleaning beds are counted and shown with their status, because a bed that "
			+ "cannot be used is not a free bed · no patient is named here.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// NOT ONE STATUS WORD IN THESE SYNONYMS. «فاضي», «مشغول», «تنظيف» and «مقفول» are VALUES,
			// and a value promoted to vocabulary is a value the named-value filter stops looking for —
			// «كام سرير فاضي» would then be read as a question ABOUT the status column and answered
			// with all twenty-two beds. The spoken spellings live on the row's search key instead.
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("حالة السرير", "Bed status"),
				// "اشغال" AND "occupancy" ARE THIS COLUMN, which is why they are here and the status
				// VALUES are not. «نسبة اشغال الاسرة» answered "عدد الأسرّة: 22" with a "مالقيتش
				// «اشغال»" opener - a false unknown, which staples a doubt to a correct answer and
				// means the word belongs to a column and was given to none. Claiming it does two
				// things at once: the doubt goes, and the question groups BY STATUS, so the reader
				// sees 7 occupied against 8 free instead of a grand total that shows no occupancy
				// at all. Neither word collides with a value: "اشغال" is not inside «مشغول», and
				// "occupancy" is not inside "Occupied".
				Synonyms = new[] { "حالة", "الحالة", "حالة السرير", "اشغال", "الاشغال", "نسبة الاشغال",
					"معدل الاشغال", "status", "bed status", "state", "occupancy", "occupancy rate" } },
			new AskColumn { Name = "ward", Kind = AskColumnKind.Dimension, Label = AskText.Of("الجناح", "Ward"),
				// "القسم" IS THE WARD HERE, and only here. On the diagnoses, appointments and
				// billing surfaces that word means the clinical DEPARTMENT, because that is what
				// those rows hang off. Beds hang off wards, and nobody asking «المنومين حسب القسم»
				// means the outpatient specialty — they mean where the patients are lying.
				Synonyms = new[] { "جناح", "الجناح", "اجنحة", "الاجنحة", "حسب الجناح",
					"قسم", "القسم", "اقسام", "الاقسام", "حسب القسم",
					"ward", "wards", "by ward", "unit", "department", "by department" } },
			new AskColumn { Name = "room", Kind = AskColumnKind.Dimension, Label = AskText.Of("الغرفة", "Room"),
				Synonyms = new[] { "غرفة", "الغرفة", "غرف", "الغرف", "اوضة", "حسب الغرفة",
					"room", "rooms", "by room" } },
			new AskColumn { Name = "roomtype", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع الغرفة", "Room type"),
				Synonyms = new[] { "نوع الغرفة", "نوع الاوضة", "فئة الغرفة", "room type", "room class" } },

			new AskColumn { Name = "beds", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الأسرّة", "Beds"),
				Synonyms = new[] { "عدد", "العدد", "كام", "سرير", "اسرة", "الاسرة", "سراير",
					"beds", "bed", "count", "how many" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// ACTIVE BEDS ONLY. A decommissioned bed is not capacity, and counting it would make the
			// board disagree with the Quality indicator, which counts IsActive beds too.
			var beds = await _db.Set<HospitalBed>().AsNoTracking()
				.Where(b => b.CompanyId == scope.CompanyId && b.IsActive)
				.Select(b => new { b.ID, b.RoomId, b.Code, b.Status })
				.ToListAsync(ct);

			if (beds.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var roomIds = beds.Select(b => b.RoomId).Distinct().ToList();
			var rooms = (await _db.Set<HospitalRoom>().AsNoTracking()
					.Where(r => r.CompanyId == scope.CompanyId && roomIds.Contains(r.ID))
					.Select(r => new { r.ID, r.Number, r.Name, r.NameEn, r.WardId, r.RoomTypeId })
					.ToListAsync(ct))
				.ToDictionary(r => r.ID);

			var wardIds = rooms.Values.Where(r => r.WardId != null).Select(r => r.WardId!.Value).Distinct().ToList();
			var wards = (await _db.Set<HospitalWard>().AsNoTracking()
					.Where(w => w.CompanyId == scope.CompanyId && wardIds.Contains(w.ID))
					.Select(w => new { w.ID, w.Name, w.NameEn })
					.ToListAsync(ct))
				.ToDictionary(w => w.ID);

			var typeIds = rooms.Values.Where(r => r.RoomTypeId != null).Select(r => r.RoomTypeId!.Value).Distinct().ToList();
			var types = (await _db.Set<HospitalRoomType>().AsNoTracking()
					.Where(t => t.CompanyId == scope.CompanyId && typeIds.Contains(t.ID))
					.Select(t => new { t.ID, t.Name, t.NameEn })
					.ToListAsync(ct))
				.ToDictionary(t => t.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(beds.Count);
			foreach (var b in beds)
			{
				var ward = arabic ? "(بلا جناح)" : "(no ward)";
				var wardSearch = "";
				var room = arabic ? "(بلا غرفة)" : "(no room)";
				var roomSearch = "";
				var type = arabic ? "(غير محدد)" : "(not set)";
				var typeSearch = "";

				if (rooms.TryGetValue(b.RoomId, out var r))
				{
					// A ward room is named by its NUMBER; a named space carries a name instead. Both
					// are what the desk says out loud, so whichever exists is what the answer shows.
					var label = (r.Number ?? "").Trim();
					var named = ((arabic ? (r.Name ?? r.NameEn) : (r.NameEn ?? r.Name)) ?? "").Trim();
					room = label.Length > 0 && named.Length > 0 ? label + " — " + named
						: label.Length > 0 ? label
						: named.Length > 0 ? named : room;
					roomSearch = ((r.Number ?? "") + " " + (r.Name ?? "") + " " + (r.NameEn ?? "")).Trim();

					if (r.WardId != null && wards.TryGetValue(r.WardId.Value, out var w))
					{
						ward = (arabic ? (w.Name ?? w.NameEn) : (w.NameEn ?? w.Name)) ?? ward;
						wardSearch = ((w.Name ?? "") + " " + (w.NameEn ?? "")).Trim();
					}
					if (r.RoomTypeId != null && types.TryGetValue(r.RoomTypeId.Value, out var t))
					{
						type = (arabic ? (t.Name ?? t.NameEn) : (t.NameEn ?? t.Name)) ?? type;
						typeSearch = ((t.Name ?? "") + " " + (t.NameEn ?? "")).Trim();
					}
				}

				rows.Add(new Dictionary<string, object?>
				{
					["status"] = Status(b.Status, arabic),
					[SearchKeyFor("status")] = (b.Status ?? "") + " " + Status(b.Status, true) + " " + StatusSaid(b.Status),
					["ward"] = ward.Trim(),
					[SearchKeyFor("ward")] = wardSearch,
					["room"] = room.Trim(),
					[SearchKeyFor("room")] = roomSearch,
					["roomtype"] = type.Trim(),
					[SearchKeyFor("roomtype")] = typeSearch,
					["beds"] = 1m,
					[LinkFor("status")] = null,
					[LinkFor("ward")] = null,
					[LinkFor("room")] = null,
					[LinkFor("roomtype")] = null,
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
				"vacant" => "فاضي",
				"occupied" => "مشغول",
				"cleaning" => "تحت التنظيف",
				"reserved" => "محجوز",
				"blocked" => "موقوف",
				_ => v,
			};
		}

		/// ONE RENDERING IS NOT ONE WORD — the lesson the appointment statuses cost. The board shows
		/// «فاضي» and the question says «الأسرّة الفاضية» or «المتاحة» or «الخالية»; without these the
		/// filter finds nothing and the answer covers all twenty-two beds under a "could not find".
		private static string StatusSaid(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
		{
			"vacant" => "فاضي فاضية فاضيه الفاضية متاح متاحة المتاحة خالي خالية الخالية شاغر شاغرة vacant free available empty",
			// «المنومين» IS A SPOKEN FORM OF "OCCUPIED", and it has to be here rather than in the
			// column's synonyms. «المرضى المنومين حسب القسم» grouped 22 beds by ward and called it
			// the admitted patients - the right shape over the wrong rows, with a "مالقيتش
			// «المنومين»" opener admitting the word had gone nowhere. As a VALUE spelling it
			// filters to the occupied beds, which is the 7 the question was asking for; as a
			// COLUMN synonym it would have been swallowed and the 22 would have stood.
			"occupied" => "مشغول مشغولة مشغوله المشغولة فيها مريض مليانة منوم منومة منومين المنومين "
				+ "occupied taken in use admitted inpatient inpatients",
			"cleaning" => "تنظيف التنظيف بتتنظف نضافة تحت النظافة cleaning being cleaned",
			"reserved" => "محجوز محجوزة المحجوزة متحجز reserved held",
			"blocked" => "موقوف موقوفة الموقوفة مقفول مقفولة مغلقة خارج الخدمة blocked closed out of service",
			_ => "",
		};
	}
}
