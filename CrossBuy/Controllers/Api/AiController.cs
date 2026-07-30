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

		public AiController(IAiService ai, CrossDbContext context, IAiInsightsService insights)
		{
			_ai = ai;
			_context = context;
			_insights = insights;
		}

		private IActionResult PassThrough(AiProxyResult r)
			=> new ContentResult { StatusCode = r.Status, Content = r.Json, ContentType = "application/json" };

		// ---------- Read-only tools for the accounting-journal assistant (Phase 1.1) ----------
		// All scoped by companyId and JWT-gated. The AI proposes; these only let it
		// look up real IDs (accounts / cost centers / cash accounts) to build a Draft.

		// GET /api/ai/accounts/search?q=إيجار&companyId=1 → postable accounts matching q
		[HttpGet("accounts/search")]
		public async Task<IActionResult> SearchAccounts(string? q, int companyId = 1)
		{
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

		// GET /api/ai/costcenters/search?q=القاهرة&companyId=1 → active cost centers (org-tree linked)
		[HttpGet("costcenters/search")]
		public async Task<IActionResult> SearchCostCenters(string? q, int companyId = 1)
		{
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

		// GET /api/ai/cash-accounts?companyId=1 → cash boxes + bank accounts with their GL account
		[HttpGet("cash-accounts")]
		public async Task<IActionResult> CashAccounts(int companyId = 1)
		{
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
		public async Task<IActionResult> AnomalyJournalScan(int companyId = 1)
			=> PassThrough(await _insights.ScanJournalAnomaliesAsync(companyId));

		// ---------- ML: cash-flow projection (Phase 1, no LLM / no API key) ----------
		// Opening cash + outstanding AR/AP (FIFO settlement, mirroring the aging logic)
		// with expected due dates → forwarded to Python which projects the running balance.
		[HttpPost("forecast/cashflow")]
		public async Task<IActionResult> ForecastCashflow(int companyId = 1, int horizonDays = 90)
			=> PassThrough(await _insights.ForecastCashflowAsync(companyId, horizonDays));

		// ---------- ML: inventory analysis (Phase 1, no LLM / no API key) ----------
		// Per-item on-hand/value/outbound demand + reorder points → Python classifies
		// slow-moving / reorder / stockout-risk and suggests reorder quantities.
		[HttpPost("inventory/analyze")]
		public async Task<IActionResult> InventoryAnalyze(int companyId = 1, int slowDays = 90)
			=> PassThrough(await _insights.AnalyzeInventoryAsync(companyId, slowDays));

		public class DiagInput
		{
			public string? Message { get; set; }
			public string? Tier { get; set; }
		}

		// POST /api/ai/diag — TEMPORARY connectivity test (.NET -> Python -> Claude).
		// Identity/permissions are enforced here via the JWT; the AI layer is never
		// trusted for authorization. Remove once Phase 1 capabilities exist.
		[HttpPost("diag")]
		public async Task<IActionResult> Diag([FromBody] DiagInput? input)
		{
			var msg = string.IsNullOrWhiteSpace(input?.Message)
				? "من فضلك رد بجملة عربية قصيرة تؤكد أن الاتصال يعمل."
				: input!.Message!;
			var tier = input?.Tier == "smart" ? "smart" : "fast";

			var result = await _ai.EchoAsync(msg, tier);
			return new ContentResult
			{
				StatusCode = result.Status,
				Content = result.Json,
				ContentType = "application/json"
			};
		}
	}
}
