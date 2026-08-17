using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/ai")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class AiController : ControllerBase
	{
		private readonly IAiService _ai;
		private readonly CrossDbContext _context;
		private readonly IAiInsightsService _insights;
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private readonly CrossBuy.BL.Platform.Ai.IAiEgressPolicy _egress;
		private readonly IConfiguration _config;
		private readonly CrossBuy.BL.Platform.Ai.IAiProviderAuthority _providerAuthority;

		public AiController(IAiService ai, CrossDbContext context, IAiInsightsService insights,
			CrossBuy.BL.Platform.IRequestCompanyResolver company,
			CrossBuy.BL.Platform.Ai.IAiEgressPolicy egress, IConfiguration config,
			CrossBuy.BL.Platform.Ai.IAiProviderAuthority providerAuthority)
		{
			_ai = ai;
			_context = context;
			_insights = insights;
			_company = company;
			_egress = egress;
			_config = config;
			_providerAuthority = providerAuthority ?? throw new ArgumentNullException(nameof(providerAuthority));
		}

		private IActionResult PassThrough(AiProxyResult r)
			=> new ContentResult { StatusCode = r.Status, Content = r.Json, ContentType = "application/json" };

		// ---------------------------------------------------------------------------------------------
		// SECURITY REMEDIATION (AI Foundation Increment 3).
		//
		// THE DEFECT. Every endpoint below used to take `int companyId = 1` STRAIGHT FROM THE QUERY
		// STRING. The controller is JWT-authenticated but carried no company guard and no permission
		// attribute, so any token holder could name any company — and four of the tables read here
		// (Account, CostCenter, BankAccount, CashBox) carry no Stage-1 global company filter, so the
		// supplied value was the ONLY company control. `?companyId=7` returned company 7's chart of
		// accounts, cost centres, bank accounts and cash boxes, including their GL account ids. Three of
		// the endpoints additionally forward the named company's data to the external processor.
		//
		// This was strictly worse than the AiInsights constant repaired in the previous increment: that
		// one was pinned to company 1, this one was attacker-chosen. It was invisible to the
		// CORRECTION-005 ratchet, whose regex matches `const int` declarations and never sees a
		// PARAMETER DEFAULT.
		//
		// THE FIX. The company is resolved from the authenticated request context, exactly as the MVC
		// controllers do, and the `companyId` parameter is GONE from every signature — so there is
		// nothing left to supply. A JSON 403 is the refusal shape for an [ApiController]; a redirect
		// would answer a fetch() caller with an HTML login page.
		// ---------------------------------------------------------------------------------------------
		private async Task<(bool Ok, int CompanyId, IActionResult? Refusal)> ResolveCompanyAsync()
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
				// The resolver's own reason is not returned: it names companies, and that discloses
				// whether a record exists somewhere the caller cannot see.
				return (false, 0, StatusCode(403, new { success = false, error = "company_unresolved" }));

			return (true, scope.CompanyId, null);
		}

		// ---------- Read-only tools for the accounting-journal assistant (Phase 1.1) ----------
		// All scoped by companyId and JWT-gated. The AI proposes; these only let it
		// look up real IDs (accounts / cost centers / cash accounts) to build a Draft.

		// GET /api/ai/accounts/search?q=إيجار → postable accounts matching q, in the CALLER'S company.
		[HttpGet("accounts/search")]
		public async Task<IActionResult> SearchAccounts(string? q)
		{
			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;

			var query = _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.IsPostable && a.IsActive);
			if (!string.IsNullOrWhiteSpace(q))
				query = query.Where(a => a.Code.Contains(q) || a.Name.Contains(q) || a.NameEn.Contains(q));

			var data = await query.OrderBy(a => a.Code).Take(10)
				.Select(a => new { id = a.ID, code = a.Code, name = a.Name, nameEn = a.NameEn,
					requireCostCenter = a.RequireCostCenter })
				.ToListAsync();
			return Ok(new { success = true, count = data.Count, data });
		}

		// GET /api/ai/costcenters/search?q=القاهرة → active cost centers, in the CALLER'S company.
		[HttpGet("costcenters/search")]
		public async Task<IActionResult> SearchCostCenters(string? q)
		{
			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;

			var query = _context.CostCenters.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.IsActive);
			if (!string.IsNullOrWhiteSpace(q))
				query = query.Where(c => c.Code.Contains(q) || c.Name.Contains(q) || c.NameEn.Contains(q));

			var data = await query.OrderBy(c => c.Code).Take(10)
				.Select(c => new { id = c.ID, code = c.Code, name = c.Name, nameEn = c.NameEn,
					sourceHierarchicalId = c.SourceHierarchicalId })
				.ToListAsync();
			return Ok(new { success = true, count = data.Count, data });
		}

		// GET /api/ai/cash-accounts → cash boxes + bank accounts with their GL account, CALLER'S company.
		[HttpGet("cash-accounts")]
		public async Task<IActionResult> CashAccounts()
		{
			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;

			var banks = await _context.BankAccounts.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.IsActive)
				.Select(b => new { kind = "Bank", id = b.ID, name = b.BankName, nameEn = b.BankNameEn,
					glAccountId = b.GlAccountId })
				.ToListAsync();
			var boxes = await _context.CashBoxes.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.IsActive)
				.Select(c => new { kind = "Cash", id = c.ID, name = c.Name, nameEn = c.NameEn,
					glAccountId = c.GlAccountId })
				.ToListAsync();
			return Ok(new { success = true, data = new { banks, cashBoxes = boxes } });
		}

		// ---------- ML: journal anomaly scan (Phase 1, no LLM / no API key) ----------
		// .NET gathers posted entries (company-scoped) and forwards them to the Python
		// ML service, which flags possible anomalies for human review. Read-only.
		[HttpPost("anomaly/journal-scan")]
		public async Task<IActionResult> AnomalyJournalScan()
		{
			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;
			return PassThrough(await _insights.ScanJournalAnomaliesAsync(companyId));
		}

		// ---------- ML: cash-flow projection (Phase 1, no LLM / no API key) ----------
		// Opening cash + outstanding AR/AP (FIFO settlement, mirroring the aging logic)
		// with expected due dates → forwarded to Python which projects the running balance.
		[HttpPost("forecast/cashflow")]
		public async Task<IActionResult> ForecastCashflow(int horizonDays = 90)
		{
			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;
			return PassThrough(await _insights.ForecastCashflowAsync(companyId, horizonDays));
		}

		// ---------- ML: inventory analysis (Phase 1, no LLM / no API key) ----------
		// Per-item on-hand/value/outbound demand + reorder points → Python classifies
		// slow-moving / reorder / stockout-risk and suggests reorder quantities.
		[HttpPost("inventory/analyze")]
		public async Task<IActionResult> InventoryAnalyze(int slowDays = 90)
		{
			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;
			return PassThrough(await _insights.AnalyzeInventoryAsync(companyId, slowDays));
		}

		public class DiagInput
		{
			public string? Message { get; set; }
			public string? Tier { get; set; }
		}

		// POST /api/ai/diag — TEMPORARY connectivity test (.NET -> Python -> Claude).
		// Identity/permissions are enforced here via the JWT; the AI layer is never
		// trusted for authorization. Remove once Phase 1 capabilities exist.
		//
		// ---------------------------------------------------------------------------------------------
		// INCREMENT 4.1 — THIS ROUTE NO LONGER EXISTS IN PRODUCTION.
		//
		// It is the ONLY path in the product that relays caller-supplied free text to a third-party LLM,
		// and the ten mandatory provider facts about that destination are still unknown. The egress policy
		// already denies it, but a DENIED route is still an accepted request, a reachable surface and an
		// answer that tells a caller the endpoint exists. An unnecessary external-diagnostic surface
		// should not be present in production at all.
		//
		// [DevOnly] is the repository's existing mechanism (DevSeedController uses it): it reads
		// IWebHostEnvironment — which comes from ASPNETCORE_ENVIRONMENT, i.e. HOST configuration — and
		// returns a bare 404 outside Development. Nothing a caller sends can influence it, and the 404
		// discloses no architecture: not "Anthropic disabled", not "provider not configured", just absent.
		//
		// Removal is NOT provider approval. Anthropic remains OwnerDecisionRequired and denied; this
		// increment only removes an unnecessary production surface while that decision is outstanding.
		// Nothing in the product calls this endpoint — verified: no view, script or service references it.
		// ---------------------------------------------------------------------------------------------
		[HttpPost("diag")]
		[CrossBuy.Models.DevOnly]
		public async Task<IActionResult> Diag([FromBody] DiagInput? input)
		{
			var msg = string.IsNullOrWhiteSpace(input?.Message)
				? "من فضلك رد بجملة عربية قصيرة تؤكد أن الاتصال يعمل."
				: input!.Message!;
			var tier = input?.Tier == "smart" ? "smart" : "fast";

			var (ok, companyId, refusal) = await ResolveCompanyAsync();
			if (!ok) return refusal!;

			// The diagnostic forwards CALLER-SUPPLIED TEXT to a destination that relays onward to a
			// third-party LLM. Whatever a caller types is, by definition, unclassifiable free text — it
			// could be anything they can see — so it is declared FreeTextBusinessContent and governed by
			// the same matrix as everything else. Under that matrix it is refused at any external
			// destination, which is the correct answer for an arbitrary-text channel and is why this
			// endpoint is reported as blocked rather than quietly exempted.
			var decision = await _egress.EvaluateAsync(new CrossBuy.Models.Platform.AiEgressRequest
			{
				Purpose = CrossBuy.Models.Platform.AiEgressPurpose.ConnectivityDiagnostic,
				// Increment 4.3: the relay route is external by construction, so the governance record —
				// not the settings file — decides whether an approved processor exists to relay to.
				Destination = CrossBuy.BL.Platform.Ai.AiDestinationResolver.Resolve(
					_config, CrossBuy.Models.Platform.AiEgressPurpose.ConnectivityDiagnostic,
					// Increment 4.9: the authority is asked about a SPECIFIC account context. The scope
					// comes from configuration — an INTENT, not an approval — and is unconfigured here,
					// so it resolves to an incomplete scope that denies.
					_providerAuthority.Assess(System.DateTime.UtcNow,
						CrossBuy.BL.Platform.Ai.OpenAiOptions.FromConfiguration(_config).Scope).State),
					ProviderScope = CrossBuy.BL.Platform.Ai.OpenAiOptions.FromConfiguration(_config).Scope,
				Classification = CrossBuy.Models.Platform.AiDataClassification.FreeTextBusinessContent,
				DataCompanyId = companyId,
				PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(msg) + System.Text.Encoding.UTF8.GetByteCount(tier) + 32,
			});

			if (!decision.Allowed || decision.Approval == null)
				return StatusCode(403, new { success = false, error = "ai_egress_denied", reason = decision.Reason?.ToString() });

			var result = await _ai.EchoAsync(decision.Approval, msg, tier);
			return new ContentResult
			{
				StatusCode = result.Status,
				Content = result.Json,
				ContentType = "application/json"
			};
		}
	}
}
