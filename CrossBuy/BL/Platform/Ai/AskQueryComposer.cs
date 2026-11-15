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
	// SENTENCE -> QUERY, over a surface that declares what may be asked of it.
	//
	// This is the piece that answers "you are assuming I can predict the question". It does not pick
	// from a list of questions; it builds one, from columns the surface declared, for a question
	// nobody wrote down.
	//
	// AND IT STILL REACHES NOTHING. What comes back is four values — a grouping, a measure, a
	// direction, a row count — each of which must be one of the literals the surface just sent. The
	// grammar on the model side admits nothing else, this code checks it again, and
	// AskQueryValidator checks the assembled query a third time against the same declaration. Three
	// checks sounds like paranoia until you notice that each one is in a different process and the
	// middle one is the only one a reviewer can read at a glance.
	//
	// A FAILURE HERE IS NOT AN ERROR. No model, the service down, governance refusing, a line that
	// does not parse: all of them mean "no query composed", and the caller answers its default
	// question and SAYS it substituted. The alternative — guessing — produces real figures for a
	// question nobody asked, which is the only outcome worse than refusing.
	// =================================================================================================
	public sealed record AskComposedQuery(
		string? GroupBy, string Measure, bool Descending, int Limit, string Engine, string Raw,
		bool Compare = false);

	public interface IAskQueryComposer
	{
		Task<AskComposedQuery?> ComposeAsync(string question, AskSurface surface, CancellationToken ct = default);
	}

	public sealed class AskQueryComposer : IAskQueryComposer
	{
		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IAiProviderAuthority _providerAuthority;
		private readonly IBusinessContextAccessor _context;
		private readonly IConfiguration _config;
		private readonly ILogger<AskQueryComposer> _log;

		public AskQueryComposer(
			IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providerAuthority,
			IBusinessContextAccessor context, IConfiguration config, ILogger<AskQueryComposer> log)
		{
			_ai = ai ?? throw new ArgumentNullException(nameof(ai));
			_egress = egress ?? throw new ArgumentNullException(nameof(egress));
			_providerAuthority = providerAuthority ?? throw new ArgumentNullException(nameof(providerAuthority));
			_context = context ?? throw new ArgumentNullException(nameof(context));
			_config = config ?? throw new ArgumentNullException(nameof(config));
			_log = log;
		}

		public async Task<AskComposedQuery?> ComposeAsync(
			string question, AskSurface surface, CancellationToken ct = default)
		{
			if (surface == null || string.IsNullOrWhiteSpace(question)) return null;
			if (!_config.GetValue(ServiceIntentResolver.EnabledKey, false)) return null;

			var dimensions = surface.Columns.Where(c => c.Kind == AskColumnKind.Dimension).ToList();
			var measures = surface.Columns.Where(c => c.Kind == AskColumnKind.Measure).ToList();
			if (dimensions.Count == 0 || measures.Count == 0) return null;

			var isArabic = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName == "ar";

			var payload = new
			{
				question,
				surface = surface.Code,
				description = isArabic ? surface.Description.Ar : surface.Description.En,
				dimensions = dimensions.Select(c => new
				{ name = c.Name, label = isArabic ? c.Label.Ar : c.Label.En }).ToArray(),
				measures = measures.Select(c => new
				{ name = c.Name, label = isArabic ? c.Label.Ar : c.Label.En }).ToArray(),
			};

			AiProxyResult result;
			try
			{
				var ctx = await _context.TryGetCurrentAsync(ct);
				if (ctx == null || ctx.CompanyId <= 0) return null;

				var scope = OpenAiOptions.FromConfiguration(_config).Scope;
				var decision = await _egress.EvaluateAsync(new AiEgressRequest
				{
					// The same purpose as choosing an intent: it is the person's own words either
					// way, and governing the two differently would let an approval for one carry
					// the other.
					Purpose = AiEgressPurpose.QuestionUnderstanding,
					Destination = AiDestinationResolver.Resolve(
						_config, AiEgressPurpose.QuestionUnderstanding,
						_providerAuthority.Assess(DateTime.UtcNow, scope).State),
					ProviderScope = scope,
					Classification = AiDataClassification.FreeTextBusinessContent,
					DataCompanyId = ctx.CompanyId,
					PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)),
				}, ct);

				if (!decision.Allowed || decision.Approval == null)
				{
					_log?.LogInformation("Query composition not used: egress denied ({Reason}).", decision.Reason);
					return null;
				}

				result = await _ai.PostAsync(decision.Approval, "/compose", payload, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				_log?.LogWarning(ex, "Query composition failed; the caller will substitute its default question.");
				return null;
			}

			if (result.Status != 200) return null;

			try
			{
				using var doc = JsonDocument.Parse(result.Json);
				var root = doc.RootElement;
				if (!root.TryGetProperty("composed", out var okEl) || okEl.ValueKind != JsonValueKind.True)
					return null;

				var groupBy = root.TryGetProperty("groupBy", out var g) && g.ValueKind == JsonValueKind.String
					? g.GetString() : null;
				var measure = root.TryGetProperty("measure", out var m) && m.ValueKind == JsonValueKind.String
					? m.GetString() : null;
				if (string.IsNullOrWhiteSpace(measure)) return null;

				// CHECKED AGAIN HERE, against the surface that is about to be read. The grammar made
				// an invented name unspellable and this makes it unusable, in a different process.
				if (surface.Column(measure!) is not { Kind: AskColumnKind.Measure }) return null;
				if (groupBy != null && surface.Column(groupBy) is not { Kind: AskColumnKind.Dimension })
					return null;

				var descending = !root.TryGetProperty("descending", out var d)
					|| d.ValueKind != JsonValueKind.False;
				var limit = root.TryGetProperty("limit", out var l) && l.TryGetInt32(out var li) ? li : 10;
				var engine = root.TryGetProperty("engine", out var e) ? (e.GetString() ?? "compose") : "compose";
				var raw = root.TryGetProperty("raw", out var r) ? (r.GetString() ?? "") : "";

				return new AskComposedQuery(groupBy, measure!, descending, limit, engine, raw);
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
