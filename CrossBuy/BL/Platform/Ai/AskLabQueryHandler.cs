using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «نتايج التحاليل الحرجة» — refused until now, and of every question this layer has been asked it
	// is the one with a patient on the end of it.
	//
	// The value wordings — «الحرجة», «اللي محدش استلمها», «المستعجلة» — are NOT here. They are routed
	// by the model and matched as VALUES on the surface's own rows; a value word promoted to a routing
	// keyword is read as vocabulary and makes the filter blind to the thing being asked about.
	// =================================================================================================
	public sealed class AskLabQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.lab";

		public AskLabQueryHandler(AskLabSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن طلبات المعمل: العدد وفين وصلت وزمن التنفيذ، والنتايج الحرجة واللي لسه محدش استلمها",
			"Any question about laboratory orders: the count, the stage, the turnaround, and the critical results including any not yet acknowledged");

		public override AskText Example => AskText.Of(
			"في نتايج تحاليل حرجة محدش استلمها", "Are there critical lab results nobody has acknowledged");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"التحاليل", "تحاليل", "طلبات التحاليل", "تحاليل المعمل", "المعمل", "المختبر",
			"عدد التحاليل", "التحاليل حسب الحالة", "زمن التحاليل", "العينات",
			"lab", "laboratory", "lab orders", "lab tests", "specimens", "accessions",
			"lab turnaround", "lab status", "tests by status",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[] { "HospitalLabOrders" };

		/// AN ORDER IS NOT A RESULT, AND THE TWO COUNTS ARE BOTH SMALL AND BOTH PLAUSIBLE.
		///
		/// Sixteen orders carry twenty-six results. Someone asking how many RESULTS came back and
		/// handed sixteen has nothing on screen to tell them it was the orders — and the per-analyte
		/// flags (17 normal, 5 high, 2 low, 2 critical) are not on this surface at all.
		private static readonly string[] Results =
			{ "نتيجة", "نتايج", "النتايج", "نتائج", "النتائج", "مرتفع", "منخفض", "خارج المعدل",
			  "result", "results", "abnormal", "out of range", "high or low" };

		/// TURNAROUND IS AN AVERAGE OVER THE ORDERS THAT REACHED THE END.
		/// A question that names the acknowledgement AND the criticality at once — the exact shape
		/// the safety question takes in real speech, and the one the composer cannot fully honour.
		private static readonly string[] Acknowledgement =
			{ "استلمها", "استلم", "اتبلغ", "مااتبلغش", "محدش", "شافها", "معلقة", "معلق",
			  "acknowledg", "unacknowledged", "outstanding", "nobody has seen", "not seen" };

		private static readonly string[] Timing =
			{ "زمن", "مدة", "بياخد قد ايه", "بتاخد قد ايه", "سرعة",
			  "turnaround", "tat", "how long", "lead time", "speed" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			bool Mentions(string[] words) =>
				words.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));

			// THE QUESTION THIS SURFACE WAS BUILT FOR IS THE ONE THE COMPOSER HANDLES WORST.
			// «في نتايج حرجة محدش استلمها» names a filter value on TWO different dimensions at
			// once, and the composer takes one of them; whichever it takes, the reader is shown a
			// number that answers half the question. The honest move is to say so and name the
			// wording that splits them, rather than let a figure stand for an answer it is not.
			if (Mentions(Acknowledgement))
				return AskText.Of(
					"سؤالك بيجمع شرطين — حرجة، ومحدش استلمها — والعدّ هنا بيتصفّى على واحد بس. "
					+ "علشان تشوف التقسيمة بجد اسأل «التحاليل الحرجة حسب الاستلام»، وهي هتفصل "
					+ "اللي اتبلّغ عن اللي لسه معلّق:",
					"Your question names two conditions — critical, and not acknowledged — and this "
					+ "count filters on one of them. For the real split ask «critical lab orders by "
					+ "acknowledgement», which separates the ones taken from the ones still "
					+ "outstanding:");

			if (Mentions(Results))
				return AskText.Of(
					"ده عدد طلبات التحاليل مش عدد النتايج — الطلب الواحد فيه أكتر من تحليل، وعلامات "
					+ "كل نتيجة على حدة (مرتفع/منخفض) مش محسوبة هنا:",
					"This counts lab ORDERS, not results — one order carries several analytes, and "
					+ "the per-result flags (high/low) are not counted on this surface:");

			// THE CAVEAT MUST NOT PROMISE AN AVERAGE IT DOES NOT CONTROL. The first wording said
			// "المتوسط محسوب من…" while the composer, asked for a duration without the word
			// «متوسط», had summed: 15,800 minutes presented under a sentence about an average.
			// A caveat that names the wrong aggregation is worse than none, because it tells the
			// reader the figure is something it is not. This one is true of either aggregation.
			if (Mentions(Timing))
				return AskText.Of(
					"الحساب ده من الطلبات اللي اتعتمدت فعلًا؛ اللي لسه في الطريق مش داخلة فيه "
					+ "ومعدودة لوحدها، لأن طلب ماخلصش مش طلب زمنه صفر. لو عايز المتوسط اطلبه "
					+ "بالاسم «متوسط زمن التحاليل»:",
					"This is taken from the orders that were actually verified; those still in "
					+ "flight are excluded and counted separately, because an unfinished order is "
					+ "not a zero-minute one. For the average, ask for it by name:");

			return null;
		}

		// BY STATUS, which is the bench's own shape: what is waiting to be collected, what is in the
		// machine, what is verified. Deliberately NOT by acknowledgement, although that is the pair
		// this surface was built for — acknowledgement is "(not applicable)" on every routine order,
		// so a bare question would be answered with one enormous line meaning nothing and two small
		// ones. The safety question names its own words and the value filter finds them.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskLabSurface.SurfaceCode,
			GroupBy = new[] { "status" },
			Measures = new[] { new AskMeasure { Column = "orders", Agg = AskAgg.Sum } },
			OrderBy = "orders",
			Descending = true,
			Limit = 20,
		};
	}
}
