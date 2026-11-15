using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.ViewModel.Ai;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// "Is there anything odd in the journal?"
	//
	// THE ONLY HANDLER THAT DOES NOT READ THE DATABASE ITSELF. It asks IAiInsightsService, which is the
	// governed path: the egress policy decides whether the payload may leave the process at all, and
	// the on-premises model answers. Re-implementing the detection here would have produced a second
	// opinion that could drift from the screen's — and an assistant that disagrees with the screen is
	// worse than one that cannot answer.
	//
	// WHAT IT REFUSES TO SAY. The detector reports three different things and this answer keeps them
	// apart, because collapsing them is exactly how the old screen came to accuse 1,113 of 2,489
	// entries:
	//   - DUPLICATES are a specific, checkable claim about one pair of postings.
	//   - AMOUNT OUTLIERS are a judgement that is often legitimate.
	//   - THE REVIEW QUEUE is a ranking by strangeness and is not a finding at all.
	//   - NOT CHECKED is the part of the ledger duplication could not be judged on, and saying
	//     nothing about it would let the rest read as a statement about the whole book.
	// =================================================================================================
	public sealed class AskAnomaliesHandler : IAskHandler
	{
		public const string IntentId = "accounting.journal-anomalies";

		private readonly IAiInsightsService _insights;
		public AskAnomaliesHandler(IAiInsightsService insights)
			=> _insights = insights ?? throw new ArgumentNullException(nameof(insights));

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeAccounting;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"القيود التي تستحق مراجعة، من النموذج المحلي",
			"Journal entries worth reviewing, from the on-premises model");

		public AskText Example => AskText.Of("فيه قيود شاذة؟", "Are there any unusual journal entries?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"شاذة", "شاذ", "الشاذة", "شذوذ", "غريبة", "مشبوهة", "مراجعة القيود", "قيود",
			"anomaly", "anomalies", "unusual", "suspicious", "odd", "journal entries", "review entries",
		};

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			AnomalyResult? parsed;
			try
			{
				var response = await _insights.ScanJournalAnomaliesAsync(scope.CompanyId, ct);
				if (response.Status != 200)
					// 403 here means the egress policy refused, which is a governance answer and not a
					// fault. Either way the honest report is that nothing was analysed.
					return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
						"خدمة التحليل المحلية لم تُجِب، فلا يمكن قول شيء عن القيود.",
						"The on-premises analysis service did not answer, so nothing can be said about the entries."));

				parsed = JsonSerializer.Deserialize<AnomalyResult>(
					response.Json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
			}
			catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
			{
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"تعذّر الوصول إلى خدمة التحليل المحلية.",
					"The on-premises analysis service could not be reached."));
			}

			if (parsed == null)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"ردّت خدمة التحليل بنتيجة تعذّرت قراءتها.",
					"The analysis service replied with a result that could not be read."));

			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var s = parsed.Summary ?? new AnomalySummary();

			var rows = new List<IReadOnlyDictionary<string, object?>>
			{
				Row(isAr ? "قيود تستحق مراجعة" : "Entries worth reviewing",
					isAr ? $"من أصل {parsed.Scanned}" : $"of {parsed.Scanned} scanned", parsed.AnomalyCount),
				Row(isAr ? "ترحيل مكرر للمستند نفسه" : "Same document posted twice",
					isAr ? "ادعاء محدد" : "a specific claim", s.Duplicates),
				Row(isAr ? "مبلغ شاذ مقارنة بنوعه" : "Amount out of character for its type",
					isAr ? "حكم، وكثيرًا ما يكون مشروعًا" : "a judgement, often legitimate", s.AmountOutliers),
				Row(isAr ? "طابور المراجعة" : "Review queue",
					isAr ? "ترتيب، وليس نتيجة" : "a ranking, not a finding", s.ReviewQueueSize),
			};

			// The three most severe, named, so the answer is actionable rather than a scoreboard.
			foreach (var a in parsed.Anomalies.Take(3))
			{
				var why = a.Reasons.Count > 0 ? (isAr ? a.Reasons[0].Ar : a.Reasons[0].En) : "";
				rows.Add(Row(a.EntryNo ?? "", why.Length > 70 ? why[..70] + "…" : why, a.Amount));
			}

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "JournalEntries", "JournalEntryLines", "local-ml" },
					Authority = EntityRegistry.ScopeAccounting + ":read",
					// The unchecked portion is carried as "withheld" so the panel marks the answer
					// partial. It is not a permission matter, but it is the same promise: a figure
					// that does not cover everything must never read as though it does.
					RowsWithheld = s.NotCheckedForDuplicates,
					WithheldNote = s.NotCheckedForDuplicates == 0 ? null : AskText.Of(
						$"{s.NotCheckedForDuplicates} قيد بلا مستند مصدر، فتعذّر الحكم على تكرارها"
						+ " — ولا يُدَّعى عنها شيء في أي اتجاه.",
						$"{s.NotCheckedForDuplicates} entries carry no source document, so duplication"
						+ " could not be judged for them, and nothing is claimed either way."),
				},
			};
		}

		private static IReadOnlyDictionary<string, object?> Row(string name, string code, decimal value) =>
			new Dictionary<string, object?> { ["name"] = name, ["code"] = code, ["onHand"] = value };
	}
}
