using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «جيب مواعيد اليوم» — typed into the assistant and refused, with the catalogue offered instead.
	//
	// The refusal was right and the gap was ours: one table, 138 appointments, and no window onto it.
	//
	// Note what is NOT in the keywords below: no wording naming a VALUE. "المواعيد الملغاة" would
	// route a question here and simultaneously make "الملغاة" look like this surface's own
	// vocabulary, so the named-value filter would never look for it and the answer would be the count
	// of EVERY appointment under a question about the cancelled ones. The model's training data
	// carries those phrasings, which is where routing belongs.
	// =================================================================================================
	public sealed class AskAppointmentsQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.appointments";

		public AskAppointmentsQueryHandler(AskAppointmentsSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن مواعيد العيادات: العدد أو الانتظار أو مدة الكشف حسب الحالة أو الطبيب أو اليوم",
			"Any question about clinic appointments: the count, the wait or the consultation length by status, practitioner or day");

		public override AskText Example => AskText.Of(
			"جيب مواعيد اليوم", "Show today's appointments");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المواعيد", "مواعيد", "مواعيد اليوم", "جيب مواعيد اليوم", "كام موعد", "عدد المواعيد",
			"المواعيد حسب الطبيب", "المواعيد حسب القسم", "جدول العيادة", "الحجوزات",
			"مدة الانتظار", "متوسط الانتظار", "مدة الكشف",
			"appointments", "todays appointments", "how many appointments", "clinic schedule",
			"bookings", "waiting time", "average wait", "consultation time",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalAppointments", "HospitalPractitioners", "HospitalDepartments",
		};

		/// A WAIT THAT WAS NEVER TIMED IS NOT A SHORT WAIT.
		///
		/// 103 of 138 appointments record an arrival, 95 a start and 90 a finish. Someone who asks
		/// for the average wait is told the figure, and ought to be told it rests on the rows that
		/// were actually stamped — otherwise a clinic that stops recording arrivals looks like a
		/// clinic that stopped making people wait.
		private static readonly string[] Timing =
			{ "انتظار", "الانتظار", "استنى", "استنوا", "طابور", "مدة الكشف", "زمن الكشف", "مدة الزيارة",
			  "wait", "waiting", "consultation time", "consult time", "how long" };

		/// A PATIENT IS NOT AN APPOINTMENT.
		private static readonly string[] Patients =
			{ "مريض", "مرضى", "المرضى", "كام مريض", "patients", "how many patients" };

		/// A BOOKING IS NOT A VISIT, AND THE TOTAL QUIETLY TREATS THEM AS ONE.
		///
		/// «كام زيارة عندنا» used to be answered with the CASH BALANCE; teaching the router to
		/// bring it here fixed the routing and left a subtler fault behind. Asked for a number, the
		/// composer collapses to a grand total — and that total counts the cancelled, the no-shows
		/// and everything still only booked for a future date, none of which is a visit anybody
		/// made. The figure is right for the question the surface answers and wrong for the one
		/// that was asked, which is precisely when it has to say which is which.
		private static readonly string[] Visits =
			{ "زيارة", "زيارات", "الزيارات", "كشف", "كشوف", "الكشوف", "كام زيارة", "كام كشف",
			  "visit", "visits", "consultation", "consultations", "how many visits" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			bool Mentions(string[] words) =>
				words.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));

			if (Mentions(Patients))
				return AskText.Of(
					"ده عدد المواعيد مش عدد المرضى — المريض الواحد ممكن يكون ليه أكتر من موعد:",
					"This counts APPOINTMENTS, not patients — one patient can hold several:");

			if (Mentions(Visits))
				return AskText.Of(
					"ده عدد المواعيد كلها — والملغاة واللي محدش حضرها واللي لسه محجوزة لأيام جاية "
					+ "داخلة فيه، ودي مش زيارات حصلت. اللي اتعمل فعلًا هو اللي حالته «تم»، فاسأل "
					+ "عن المواعيد حسب الحالة علشان تشوف الفرق:",
					"This is the count of ALL appointments — the cancelled, the no-shows and those "
					+ "still booked for a future date are in it, and none of those is a visit anyone "
					+ "made. The ones that happened are those marked Done; ask for appointments by "
					+ "status to see the split:");

			if (Mentions(Timing))
				return AskText.Of(
					"المتوسط محسوب من المواعيد اللي اتسجّل ليها وقت فعلي؛ اللي مالهاش ختم مش داخلة "
					+ "في الحساب ومعدودة لوحدها:",
					"The average is taken from the appointments that were actually stamped; those "
					+ "without a time are excluded from it and counted separately:");

			return null;
		}

		// BY STATUS. "The appointments" names no grouping, and the first thing anyone running a
		// clinic wants is the shape of the day: how many are booked, arrived, done, missed.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskAppointmentsSurface.SurfaceCode,
			GroupBy = new[] { "status" },
			Measures = new[] { new AskMeasure { Column = "appointments", Agg = AskAgg.Sum } },
			OrderBy = "appointments",
			Descending = true,
			Limit = 20,
		};
	}
}
