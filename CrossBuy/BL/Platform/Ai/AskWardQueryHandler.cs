using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «كام سرير فاضي» — the question the ward desk asks all day, and the one I taught the assistant to
	// refuse on a miscount. See AskWardSurface's header for how that happened.
	//
	// ROUTING WORDS ONLY. No status value appears below: «الفاضية» as a keyword would make the filter
	// blind to the very status being asked about, which is the mistake this layer has now paid for
	// five times.
	// =================================================================================================
	public sealed class AskWardQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.ward";

		public AskWardQueryHandler(AskWardSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن الأسرّة: العدد والمتاح والمشغول دلوقتي، حسب الجناح أو الغرفة أو نوع الغرفة",
			"Any question about beds: how many, and which are free or occupied right now, by ward, room or room type",
			"Questions sur les lits : nombre, lits libres ou occupés, par unité, chambre ou type de chambre");

		public override AskText Example => AskText.Of(
			"كام سرير فاضي", "How many beds are free", "Combien de lits sont libres ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"الاسرة", "اسرة", "سرير", "سراير", "كام سرير", "عدد الاسرة", "لوحة الاسرة",
			"الاسرة حسب الجناح", "الاجنحة", "الغرف", "غرف المستشفى", "سعة المستشفى",
			"beds", "how many beds", "bed board", "beds by ward", "wards", "rooms",
			"bed capacity", "hospital capacity",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalBeds", "HospitalRooms", "HospitalWards", "HospitalRoomTypes",
		};

		/// A BED IS NOT A PATIENT, even on a day when the two numbers agree.
		///
		/// Seven beds are occupied and seven patients are admitted, so «كام مريض منوم» answered from
		/// this surface is right today and right by coincidence of bookkeeping: a patient admitted
		/// but between beds has no open stay, and would be missing from a count that looks correct.
		private static readonly string[] Patients =
			{ "مريض", "مرضى", "المرضى", "منوم", "منومين", "المنومين", "كام مريض",
			  "patient", "patients", "inpatient", "inpatients", "admitted" };

		/// A SNAPSHOT IS NOT A RATE, AND THE RATE HAS A SCREEN.
		///
		/// This board says how many of the beds are occupied this instant. The hospital's occupancy
		/// INDICATOR is patient-days ÷ (beds × days) across a month, and it reads a different number
		/// on purpose. Answering one under the other's name is the failure; naming the screen is not.
		private static readonly string[] Occupancy =
			{ "نسبة", "النسبة", "اشغال", "الاشغال", "نسبة الاشغال", "معدل",
			  "occupancy", "occupancy rate", "utilisation", "utilization", "percentage" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			bool Mentions(string[] words) =>
				words.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));

			if (Mentions(Occupancy))
				return AskText.Of(
					"دي صورة لحظية للأسرّة دلوقتي. نسبة الإشغال كمؤشر شهري (أيام المرضى ÷ الأسرّة × "
					+ "الأيام) محسوبة في شاشة جودة الخدمة، ورقمها بيختلف عن ده عن قصد:",
					"This is a snapshot of the beds right now. The monthly occupancy indicator "
					+ "(patient-days ÷ beds × days) is computed on the Quality screen, and reads a "
					+ "different number on purpose:");

			if (Mentions(Patients))
				return AskText.Of(
					"ده عدد الأسرّة مش عدد المرضى — السرير اللي فيه إقامة مفتوحة بيتحسب مشغول، "
					+ "والمريض المنوّم اللي مش في سرير دلوقتي مش بيبان هنا:",
					"This counts BEDS, not patients — a bed with an open stay counts as occupied, and "
					+ "a patient admitted but not currently in a bed does not appear here:");

			return null;
		}

		// BY STATUS. "The beds" names no grouping, and the shape of the board — free, occupied,
		// cleaning, blocked — is the first thing the ward desk wants to see.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskWardSurface.SurfaceCode,
			GroupBy = new[] { "status" },
			Measures = new[] { new AskMeasure { Column = "beds", Agg = AskAgg.Sum } },
			OrderBy = "beds",
			Descending = true,
			Limit = 20,
		};
	}
}
