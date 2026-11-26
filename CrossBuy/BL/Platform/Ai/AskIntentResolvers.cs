using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHICH QUESTION IS THIS? — the seam.
	//
	// WHAT THIS IS FOR. Choosing the intent was welded into AskService as a keyword matcher, so
	// improving it meant editing the gate, and replacing it meant rewriting the gate. It is now one
	// small interface with interchangeable implementations, which is what lets the understanding get
	// better without the security properties being touched at all.
	//
	// THE SECURITY PROPERTY DOES NOT LIVE HERE, AND THAT IS THE POINT. A resolver receives the
	// question text and a list of intent ids. It cannot read a row, cannot reach the database, and
	// cannot widen anything: the list it chooses from is ALREADY filtered to what this asker may use,
	// and whatever it returns is re-checked downstream — company re-resolved from the server, module
	// authority re-asked, rows re-scoped. So a resolver that is wrong gives a wrong ANSWER, never a
	// forbidden one; and a resolver that is malicious or hallucinating has nothing to reach.
	//
	// That is why an external language model can one day sit here without the governance argument
	// starting over. It would gain exactly the reach a keyword matcher has, which is none.
	// =================================================================================================

	public sealed record AskIntentScore(string Intent, double Score);

	/// What a resolver decided.
	///
	/// `Ran` distinguishes "I looked and found nothing" from "I could not look" — a service that is
	/// down, denied by governance, or not configured. The difference matters: the first is an answer
	/// and the second must fall through to something that can answer.
	public sealed record AskIntentChoice(
		string? Intent,
		double Confidence,
		IReadOnlyList<AskIntentScore> Ranked,
		string Engine,
		bool Ran,
		bool Final = false)
	{
		public static AskIntentChoice NotRun(string engine) =>
			new(null, 0, Array.Empty<AskIntentScore>(), engine, false);

		public static AskIntentChoice Nothing(string engine, IReadOnlyList<AskIntentScore>? ranked = null) =>
			new(null, 0, ranked ?? Array.Empty<AskIntentScore>(), engine, true);

		/// A REFUSAL THAT IS A DECISION RATHER THAN AN ABSENCE OF ONE.
		///
		/// The chain otherwise treats "no intent" as "no opinion" and gives the next link a turn.
		/// That is right for a link that could not judge the question, and wrong for the trained
		/// model, which was given this exact catalogue and answered "none of these". Letting the
		/// keyword matcher overrule it put a confident answer under a question the model had
		/// correctly refused: "اعملي تقرير مبيعات سنوي" came back as a revenue total because the
		/// word "مبيعات" is in the sales keywords.
		public static AskIntentChoice No(string engine, IReadOnlyList<AskIntentScore>? ranked = null) =>
			new(null, 0, ranked ?? Array.Empty<AskIntentScore>(), engine, true, true);
	}

	public interface IAskIntentResolver
	{
		/// Named in diagnostics so an answer can be traced to the thing that chose it.
		string Engine { get; }

		/// `candidates` is already filtered to the intents this asker holds authority for. A resolver
		/// must never return an id that is not in it; callers verify, but the contract is here.
		Task<AskIntentChoice> ChooseAsync(
			string question, IReadOnlyList<IAskHandler> candidates, CancellationToken ct = default);
	}

	// =================================================================================================
	// 1. KEYWORDS — exact, narrow, and still useful.
	//
	// It only fires when a phrase the handler declared is literally present, which makes it precise
	// where it fires and silent everywhere else. That is a real complement to a statistical matcher:
	// one generalises and the other is certain, and they disagree in opposite directions.
	//
	// SCORED BY MATCHED LENGTH, NOT BY HIT COUNT. "كام رصيد النقدية" hits "رصيد" for item stock and
	// "النقدية" for the cash balance — one hit each, so counting makes it a coin toss decided by
	// registration order. Weighing the characters that matched lets the more specific word win.
	// =================================================================================================
	public sealed class KeywordIntentResolver : IAskIntentResolver
	{
		public string Engine => "keyword";

		public Task<AskIntentChoice> ChooseAsync(
			string question, IReadOnlyList<IAskHandler> candidates, CancellationToken ct = default)
		{
			var text = AskSearch.Normalise(question);
			if (text.Length == 0 || candidates.Count == 0)
				return Task.FromResult(AskIntentChoice.Nothing(Engine));

			var scored = new List<AskIntentScore>();
			foreach (var h in candidates)
			{
				var score = 0;
				foreach (var k in h.Keywords)
				{
					var nk = AskSearch.Normalise(k);
					if (nk.Length > 0 && text.Contains(nk, StringComparison.Ordinal)) score += nk.Length;
				}
				if (score > 0)
					// Normalised against the question so the number is comparable between questions
					// of different lengths, as the other resolvers' confidences are.
					scored.Add(new AskIntentScore(h.Intent, Math.Min(1.0, (double)score / text.Length)));
			}

			if (scored.Count == 0) return Task.FromResult(AskIntentChoice.Nothing(Engine));

			var ranked = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Intent, StringComparer.Ordinal).ToList();
			return Task.FromResult(new AskIntentChoice(ranked[0].Intent, ranked[0].Score, ranked, Engine, true));
		}
	}

	// =================================================================================================
	// 2. THE UNDERSTANDING SERVICE — character n-grams, on loopback.
	//
	// WHY IT IS WORTH A NETWORK HOP. The keyword matcher fails on a shape of input, not on a word:
	// a prefix, a suffix, or a typo all mean "nearly the string we know", and `contains` cannot see
	// nearly. Four wordings defeated it in one afternoon and each was fixed with a hand-written rule
	// for one shape. Character n-grams measure "nearly" directly, so the rules stop being needed.
	//
	// WHAT CROSSES THE WIRE: the question text and the intent ids this asker may use. No rows, no
	// figures, no company id, no identity. The question is nonetheless FREE-TEXT BUSINESS CONTENT —
	// people type customer names and amounts into chat boxes — so it goes through IAiEgressPolicy
	// like everything else, under its OWN purpose. ConnectivityDiagnostic is separated for the same
	// reason: caller-supplied text is a different risk from a computed payload and must be
	// governable on its own, not as a rider on inventory analysis.
	//
	// A DENIAL IS NOT A FAILURE. If governance refuses, the service is down, or nothing is
	// configured, this reports `Ran = false` and the chain falls back. An assistant that stops
	// answering because an optional component is unavailable has made the optional component
	// mandatory.
	// =================================================================================================
	public sealed class ServiceIntentResolver : IAskIntentResolver
	{
		public const string EnabledKey = "AiService:Understanding:Enabled";

		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IAiProviderAuthority _providerAuthority;
		private readonly IBusinessContextAccessor _context;
		private readonly IConfiguration _config;
		private readonly ILogger<ServiceIntentResolver> _log;

		public ServiceIntentResolver(
			IAiService ai,
			IAiEgressPolicy egress,
			IAiProviderAuthority providerAuthority,
			IBusinessContextAccessor context,
			IConfiguration config,
			ILogger<ServiceIntentResolver> log)
		{
			_ai = ai ?? throw new ArgumentNullException(nameof(ai));
			_egress = egress ?? throw new ArgumentNullException(nameof(egress));
			_providerAuthority = providerAuthority ?? throw new ArgumentNullException(nameof(providerAuthority));
			_context = context ?? throw new ArgumentNullException(nameof(context));
			_config = config ?? throw new ArgumentNullException(nameof(config));
			_log = log;
		}

		public string Engine => "service";

		/// Off unless switched on. A new path to an outbound call should not start carrying traffic
		/// because it was merged; it starts carrying traffic because someone decided it should.
		private bool Enabled => _config.GetValue(EnabledKey, false);

		public async Task<AskIntentChoice> ChooseAsync(
			string question, IReadOnlyList<IAskHandler> candidates, CancellationToken ct = default)
		{
			if (!Enabled || candidates.Count == 0 || string.IsNullOrWhiteSpace(question))
				return AskIntentChoice.NotRun(Engine);

			// EVERYTHING A PERSON MIGHT SAY TO MEAN THIS QUESTION, in one list. The service does not
			// care which string is a keyword and which is the example — it measures how close the
			// question came to any known way of asking, so the distinction would only be noise.
			var payload = new
			{
				question,
				intents = candidates.Select(h => new
				{
					id = h.Intent,
					phrasings = h.Keywords
						.Concat(new[] { h.Example.Ar, h.Example.En, h.Example.Fr,
							h.Description.Ar, h.Description.En, h.Description.Fr })
						.Where(p => !string.IsNullOrWhiteSpace(p))
						.ToArray(),
				}).ToArray(),
			};

			AiProxyResult result;
			try
			{
				var ctx = await _context.TryGetCurrentAsync(ct);
				if (ctx == null || ctx.CompanyId <= 0) return AskIntentChoice.NotRun(Engine);

				var scope = OpenAiOptions.FromConfiguration(_config).Scope;
				var decision = await _egress.EvaluateAsync(new AiEgressRequest
				{
					Purpose = AiEgressPurpose.QuestionUnderstanding,
					Destination = AiDestinationResolver.Resolve(
						_config, AiEgressPurpose.QuestionUnderstanding,
						_providerAuthority.Assess(DateTime.UtcNow, scope).State),
					ProviderScope = scope,
					// THE QUESTION IS THE PERSON'S OWN WORDS. Anything typed into a chat box may
					// name a customer, an amount or a grievance, so it is classified as what it is
					// and the matrix keeps it inside the building.
					Classification = AiDataClassification.FreeTextBusinessContent,
					DataCompanyId = ctx.CompanyId,
					PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)),
				}, ct);

				if (!decision.Allowed || decision.Approval == null)
				{
					_log?.LogInformation(
						"Question understanding not used: egress denied ({Reason}). Falling back to keywords.",
						decision.Reason);
					return AskIntentChoice.NotRun(Engine);
				}

				result = await _ai.PostAsync(decision.Approval, "/understand", payload, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				// The assistant must survive its optional parts. Logged, not surfaced.
				_log?.LogWarning(ex, "Question understanding failed; falling back to keywords.");
				return AskIntentChoice.NotRun(Engine);
			}

			if (result.Status != 200) return AskIntentChoice.NotRun(Engine);

			try
			{
				using var doc = JsonDocument.Parse(result.Json);
				var root = doc.RootElement;

				var ranked = new List<AskIntentScore>();
				if (root.TryGetProperty("ranked", out var rankedEl)
					&& rankedEl.ValueKind == JsonValueKind.Array)
				{
					foreach (var m in rankedEl.EnumerateArray())
					{
						var id = m.TryGetProperty("intent", out var i) ? i.GetString() : null;
						var sc = m.TryGetProperty("score", out var s) && s.TryGetDouble(out var d) ? d : 0;
						if (!string.IsNullOrEmpty(id)) ranked.Add(new AskIntentScore(id!, sc));
					}
				}

				var chosen = root.TryGetProperty("intent", out var ie) && ie.ValueKind == JsonValueKind.String
					? ie.GetString()
					: null;
				var confidence = root.TryGetProperty("confidence", out var ce) && ce.TryGetDouble(out var cd)
					? cd : 0;

				// THE CATALOGUE IS CLOSED, AND THIS IS WHERE THAT IS ENFORCED RATHER THAN TRUSTED.
				// A service — or, later, a model — that returns an id nobody offered gets it dropped
				// here, not passed on for something downstream to notice.
				var allowed = new HashSet<string>(candidates.Select(c => c.Intent), StringComparer.OrdinalIgnoreCase);
				ranked = ranked.Where(r => allowed.Contains(r.Intent)).ToList();
				if (chosen != null && !allowed.Contains(chosen))
				{
					_log?.LogWarning("Question understanding returned an intent outside the offered catalogue. Ignored.");
					chosen = null;
				}

				// WHICH ENGINE ANSWERED DECIDES WHETHER ITS "NO" IS BINDING.
				//
				// The service runs two: a model TRAINED on this catalogue, and an untrained
				// nearest-neighbour matcher for catalogues it has never seen. Only the first is
				// entitled to refuse on the product's behalf — it was shown these exact intents and
				// said none of them. The second is a fallback and its silence means "I could not
				// tell", which is precisely what the keyword matcher below exists to handle.
				var engineName = root.TryGetProperty("engine", out var en) ? en.GetString() : null;
				var trained = engineName != null
					&& engineName.StartsWith("intent-model", StringComparison.OrdinalIgnoreCase);

				return chosen == null
					? (trained ? AskIntentChoice.No(Engine, ranked) : AskIntentChoice.Nothing(Engine, ranked))
					: new AskIntentChoice(chosen, confidence, ranked, Engine, true);
			}
			catch (JsonException)
			{
				return AskIntentChoice.NotRun(Engine);
			}
		}
	}

	// =================================================================================================
	// 3. THE CHAIN — ask each in turn, and let silence mean silence.
	//
	// Order is capability-first: the statistical matcher generalises, so it gets the question first.
	// The keyword matcher follows, because a declared phrase appearing literally is strong evidence
	// and is exactly the case the first one can under-score.
	//
	// A resolver that RAN and chose nothing does NOT stop the chain. The two engines fail in
	// opposite directions — one is loose and one is exact — so a refusal from either is worth a
	// second opinion. Only when every resolver has declined is the question genuinely unmatched,
	// and only then does the asker get a refusal.
	//
	// The RANKED list from the first resolver that produced one is kept whatever happens, because a
	// refusal that can still offer the nearest questions is the difference between a dead end and a
	// next step.
	// =================================================================================================
	public sealed class ChainedIntentResolver : IAskIntentResolver
	{
		private readonly IReadOnlyList<IAskIntentResolver> _links;

		public ChainedIntentResolver(IEnumerable<IAskIntentResolver> links)
		{
			_links = (links ?? throw new ArgumentNullException(nameof(links)))
				.Where(l => l is not ChainedIntentResolver)   // never contain yourself
				.ToList();
		}

		public string Engine => "chain";

		public async Task<AskIntentChoice> ChooseAsync(
			string question, IReadOnlyList<IAskHandler> candidates, CancellationToken ct = default)
		{
			IReadOnlyList<AskIntentScore> bestRanked = Array.Empty<AskIntentScore>();
			var lastEngine = Engine;

			foreach (var link in _links)
			{
				var choice = await link.ChooseAsync(question, candidates, ct);
				if (!choice.Ran) continue;

				lastEngine = choice.Engine;
				if (bestRanked.Count == 0 && choice.Ranked.Count > 0) bestRanked = choice.Ranked;
				if (choice.Intent != null) return choice with { Ranked = choice.Ranked.Count > 0 ? choice.Ranked : bestRanked };

				// A LINK THAT DECIDED "NO" ENDS THE CHAIN. Only a link that KNOWS this catalogue may
				// say this — see AskIntentChoice.No. Everything below it exists to cover questions
				// the link could not judge, not to overturn the ones it could.
				if (choice.Final)
					return AskIntentChoice.Nothing(choice.Engine, choice.Ranked.Count > 0 ? choice.Ranked : bestRanked);
			}

			return AskIntentChoice.Nothing(lastEngine, bestRanked);
		}
	}
}
