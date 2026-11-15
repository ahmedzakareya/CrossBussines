using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT IT COULD NOT ANSWER.
	//
	// WHY THIS EXISTS. Every gap in this assistant has been found the same way: the owner asked
	// something, it refused, he sent a screenshot. "برجر دجاج", "اعلي الاصناف بيعا", "اخر فاتورة بيع",
	// "انت معايا؟", "قول المخزن اللي متسجله عندنا" — five rounds, each one a real cause, each one
	// found by a person doing the assistant's accounting for it.
	//
	// That loop cannot keep up, because it only ever surfaces the questions ONE person happened to
	// ask on the day he was looking. A refusal that nobody screenshots is a refusal nobody knows
	// about, and the catalogue grows by anecdote.
	//
	// So a refusal is now written down. Not to apologise for it — to be able to sort by how often it
	// happens and fix the common one first. A gap that occurs forty times is a different problem
	// from a gap that occurred once, and today there is no way at all to tell those apart.
	//
	// WHAT IS RECORDED, AND WHAT IS DELIBERATELY NOT.
	//
	//   Recorded: the question as typed, the refusal code, the intent that was tried if any, the
	//   company, and the time. That is what it takes to count and group them.
	//
	//   NOT recorded: who asked. It is not needed to fix coverage — the question is the signal, not
	//   the questioner — and a log of what each named person asked their accounting system is a
	//   surveillance artifact that nobody requested and that would be hard to justify afterwards.
	//   The company is kept because coverage genuinely differs per tenant.
	//
	// IT STAYS INSIDE. A typed question is free-text business content; the egress matrix permits that
	// to an Internal destination only, and a file on this machine is as internal as it gets. Nothing
	// here is sent anywhere.
	//
	// AND IT CANNOT BREAK AN ANSWER. Every failure to write is swallowed. A diagnostic that can take
	// down the thing it is diagnosing is worse than no diagnostic.
	// =================================================================================================
	public interface IAskRefusalLog
	{
		/// Record a question the assistant did not answer. Never throws.
		void Record(string question, AskRefusal refusal, string? triedIntent, int companyId, string engine,
			string? conversationId = null);

		/// Read back what has been refused, most frequent first. For the diagnostic screen and for
		/// whoever is deciding what to build next.
		IReadOnlyList<AskRefusalGroup> Summarise(int days = 14, int top = 40);
	}

	/// One wording, however many times it was refused.
	public sealed class AskRefusalGroup
	{
		/// The folded form the grouping was done on, so near-identical wordings count together.
		public string Normalised { get; init; } = "";
		/// One real example, for a human to read.
		public string Example { get; init; } = "";
		public int Count { get; init; }
		public string Refusal { get; init; } = "";
		public string? TriedIntent { get; init; }
		public DateTime FirstSeen { get; init; }
		public DateTime LastSeen { get; init; }
	}

	public sealed class AskRefusalLog : IAskRefusalLog
	{
		public const string DirectoryKey = "AiService:RefusalLog:Directory";
		public const string EnabledKey = "AiService:RefusalLog:Enabled";

		/// A cap per day. A log that can grow without limit on a machine nobody is watching is an
		/// outage waiting for a quiet week.
		private const long MaxBytesPerDay = 8L * 1024 * 1024;

		/// Long enough for any real question, short enough that a paste cannot fill the disk.
		private const int MaxQuestionLength = 400;

		private readonly IConfiguration _config;
		private readonly ILogger<AskRefusalLog> _log;
		private static readonly object Gate = new();

		public AskRefusalLog(IConfiguration config, ILogger<AskRefusalLog> log)
		{
			_config = config ?? throw new ArgumentNullException(nameof(config));
			_log = log;
		}

		private bool Enabled => _config.GetValue(EnabledKey, true);

		private string Directory()
		{
			var configured = _config[DirectoryKey];
			return string.IsNullOrWhiteSpace(configured)
				? Path.Combine(AppContext.BaseDirectory, "App_Data", "ask-refusals")
				: configured!;
		}

		private string PathFor(DateTime dayUtc) =>
			Path.Combine(Directory(), "refused-" + dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

		// A VERIFICATION TOOL MUST NOT CORRUPT THE DIAGNOSTIC IT SITS BESIDE.
		//
		// `tools/ui-conformance/ask-capability-probe.mjs` asks six questions that MUST be refused —
		// that is the half of it that catches the expensive failure. Every run therefore wrote six
		// refusals into this log, and on 2026-11-10 that made 33 of 37 entries my own testing: the
		// screen that is supposed to say "build this next" was ranking the developer's probe.
		//
		// So a conversation id beginning with this marker is not recorded. The marker is explicit
		// and reserved: the assistant's own UI generates ids from a timestamp and never produces
		// one, so a real question cannot be silently dropped by matching it.
		public const string ProbeConversationPrefix = "probe:";

		public void Record(string question, AskRefusal refusal, string? triedIntent, int companyId, string engine,
			string? conversationId = null)
		{
			if (!Enabled || string.IsNullOrWhiteSpace(question)) return;
			if (conversationId != null
				&& conversationId.StartsWith(ProbeConversationPrefix, StringComparison.Ordinal)) return;

			try
			{
				var dir = Directory();
				System.IO.Directory.CreateDirectory(dir);
				var path = PathFor(DateTime.UtcNow);

				lock (Gate)
				{
					var file = new FileInfo(path);
					if (file.Exists && file.Length >= MaxBytesPerDay) return;

					var line = JsonSerializer.Serialize(new
					{
						at = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
						company = companyId,
						// Trimmed, never truncated silently in the middle of a word that matters —
						// 400 characters is already past any real question.
						q = question.Length > MaxQuestionLength
							? question[..MaxQuestionLength]
							: question,
						// The FOLDED form is stored alongside, so grouping does not depend on the
						// reader folding it the same way later.
						n = string.Join(' ', AskSearch.Tokens(question)),
						refusal = refusal.ToString(),
						intent = triedIntent,
						engine,
					});
					File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
				}
			}
			catch (Exception ex)
			{
				// Swallowed on purpose. See the header: a diagnostic must never be able to fail the
				// thing it diagnoses. Logged once at debug so it is findable without being noisy.
				_log?.LogDebug(ex, "Could not record a refused question.");
			}
		}

		public IReadOnlyList<AskRefusalGroup> Summarise(int days = 14, int top = 40)
		{
			if (days < 1) days = 1;
			if (top < 1) top = 1;

			var rows = new List<(string Normalised, string Question, string Refusal, string? Intent, DateTime At)>();
			try
			{
				for (var i = 0; i < days; i++)
				{
					var path = PathFor(DateTime.UtcNow.AddDays(-i));
					if (!File.Exists(path)) continue;

					foreach (var line in File.ReadLines(path))
					{
						if (string.IsNullOrWhiteSpace(line)) continue;
						try
						{
							using var doc = JsonDocument.Parse(line);
							var root = doc.RootElement;
							var q = root.TryGetProperty("q", out var qe) ? qe.GetString() ?? "" : "";
							if (q.Length == 0) continue;
							var n = root.TryGetProperty("n", out var ne) ? ne.GetString() ?? q : q;
							var refusal = root.TryGetProperty("refusal", out var re) ? re.GetString() ?? "" : "";
							var intent = root.TryGetProperty("intent", out var ie) ? ie.GetString() : null;
							var at = root.TryGetProperty("at", out var ae)
								&& DateTime.TryParse(ae.GetString(), CultureInfo.InvariantCulture,
									DateTimeStyles.RoundtripKind, out var parsed)
								? parsed : DateTime.UtcNow;
							rows.Add((n, q, refusal, intent, at));
						}
						catch (JsonException)
						{
							// A half-written line from a kill mid-append. Skipped, not fatal.
						}
					}
				}
			}
			catch (Exception ex)
			{
				_log?.LogDebug(ex, "Could not read the refused-question log.");
				return Array.Empty<AskRefusalGroup>();
			}

			return rows
				.GroupBy(r => r.Normalised, StringComparer.Ordinal)
				.Select(g => new AskRefusalGroup
				{
					Normalised = g.Key,
					Example = g.First().Question,
					Count = g.Count(),
					// The most common refusal for this wording; a wording can be refused for more
					// than one reason as coverage changes underneath it.
					Refusal = g.GroupBy(x => x.Refusal).OrderByDescending(x => x.Count()).First().Key,
					TriedIntent = g.Select(x => x.Intent).FirstOrDefault(x => x != null),
					FirstSeen = g.Min(x => x.At),
					LastSeen = g.Max(x => x.At),
				})
				.OrderByDescending(x => x.Count)
				.ThenByDescending(x => x.LastSeen)
				.Take(top)
				.ToList();
		}
	}
}
