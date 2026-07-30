using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class AgingBuckets
	{
		public decimal Current { get; set; }   // 0-30
		public decimal D30 { get; set; }        // 31-60
		public decimal D60 { get; set; }        // 61-90
		public decimal D90 { get; set; }        // 90+
		public decimal Total => Current + D30 + D60 + D90;
	}

	public class ExecTopCustomer
	{
		public string Name { get; set; } = "";
		public decimal Revenue { get; set; }
		public decimal Margin { get; set; }
		public decimal MarginPct { get; set; }
		public decimal Outstanding { get; set; }
	}

	/// Cross-module executive command center. Composes the existing per-module aggregates
	/// (no new GL math) and derives company-level ratios. Single company (CompanyID=1).
	public class ExecutiveDashboardDto
	{
		public int Year { get; set; }
		// P&L (current year)
		public decimal Revenue { get; set; }
		public decimal Expense { get; set; }
		public decimal NetIncome { get; set; }
		public decimal NetMarginPct { get; set; }
		// gross margin (from customer analytics: net revenue & cogs)
		public decimal NetRevenue { get; set; }
		public decimal Cogs { get; set; }
		public decimal GrossMargin { get; set; }
		public decimal GrossMarginPct { get; set; }
		// position
		public decimal Cash { get; set; }
		public decimal Receivables { get; set; }
		public decimal Payables { get; set; }
		public decimal InventoryValue { get; set; }
		public decimal AssetNbv { get; set; }
		public decimal WorkingCapital { get; set; }
		// liquidity / efficiency ratios
		public decimal QuickRatio { get; set; }
		public decimal CurrentRatio { get; set; }
		public decimal Dso { get; set; }   // days sales outstanding (run-rate)
		public decimal Dpo { get; set; }   // days payables outstanding (run-rate)
		// operations
		public int Headcount { get; set; }
		public int OpenLeads { get; set; }
		public decimal OpenPipeline { get; set; }
		public decimal WonValue { get; set; }
		// trend
		public List<AccMonthPoint> Months { get; set; } = new();
		public decimal RevenueMoMPct { get; set; }
		// detail
		public AgingBuckets ArAging { get; set; } = new();
		public AgingBuckets ApAging { get; set; } = new();
		public List<PipelineStage> Pipeline { get; set; } = new();
		public List<ExecTopCustomer> TopCustomers { get; set; } = new();
	}

	public interface IExecutiveDashboardService
	{
		Task<ExecutiveDashboardDto> BuildAsync(int companyId);
	}

	public class ExecutiveDashboardService : IExecutiveDashboardService
	{
		private readonly CrossDbContext _context;
		private readonly IAccountingDashboardService _accDash;
		private readonly IReceivableService _ar;
		private readonly IPayableService _ap;
		private readonly ICrmService _crm;

		public ExecutiveDashboardService(CrossDbContext context, IAccountingDashboardService accDash,
			IReceivableService ar, IPayableService ap, ICrmService crm)
		{ _context = context; _accDash = accDash; _ar = ar; _ap = ap; _crm = crm; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private static decimal Ratio(decimal num, decimal den) => den == 0 ? 0 : R(num / den);

		public async Task<ExecutiveDashboardDto> BuildAsync(int companyId)
		{
			var d = new ExecutiveDashboardDto { Year = DateTime.UtcNow.Year };

			// 1) financial core — reuse the accounting dashboard aggregates (no recompute)
			var acc = await _accDash.BuildAsync(companyId);
			d.Revenue = acc.Revenue; d.Expense = acc.Expense; d.NetIncome = acc.NetIncome;
			d.Cash = acc.Cash; d.Receivables = acc.Receivables; d.Payables = acc.Payables;
			d.AssetNbv = acc.AssetNbv; d.Months = acc.Months;
			d.NetMarginPct = d.Revenue == 0 ? 0 : R(100m * d.NetIncome / d.Revenue);

			// month-over-month revenue delta from the 6-month trend
			if (d.Months.Count >= 2)
			{
				var last = d.Months[^1].Revenue; var prev = d.Months[^2].Revenue;
				d.RevenueMoMPct = prev == 0 ? 0 : R(100m * (last - prev) / prev);
			}

			// 2) gross margin from customer analytics (net revenue & COGS)
			var an = await _ar.GetCustomerAnalyticsAsync(companyId);
			d.NetRevenue = R(an.TotalRevenue);
			d.GrossMargin = R(an.TotalMargin);
			d.Cogs = R(an.TotalRevenue - an.TotalMargin);
			d.GrossMarginPct = an.TotalRevenue == 0 ? 0 : R(100m * an.TotalMargin / an.TotalRevenue);
			d.TopCustomers = an.Rows.OrderByDescending(r => r.Revenue).Take(5)
				.Select(r => new ExecTopCustomer { Name = r.Name, Revenue = R(r.Revenue), Margin = R(r.Margin), MarginPct = r.MarginPct, Outstanding = R(r.Outstanding) })
				.ToList();

			// 3) inventory value on hand
			d.InventoryValue = R(await _context.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == companyId).Select(b => (decimal?)b.TotalValue).SumAsync() ?? 0m);

			// 4) headcount (active employees)
			d.Headcount = await _context.Employee.AsNoTracking().CountAsync(e => e.IsActive);

			// 5) CRM pipeline
			d.Pipeline = await _crm.PipelineSummaryAsync(companyId);
			d.OpenPipeline = R(d.Pipeline.Where(s => s.Stage != "Won" && s.Stage != "Lost").Sum(s => s.Amount));
			d.WonValue = R(d.Pipeline.Where(s => s.Stage == "Won").Sum(s => s.Amount));
			d.OpenLeads = await _context.Leads.AsNoTracking().CountAsync(l => l.CompanyID == companyId && l.Status != "Converted" && l.Status != "Lost");

			// 6) AR / AP aging buckets
			var today = DateTime.UtcNow.Date;
			foreach (var a in await _ar.AgingAsync(companyId, today))
			{ d.ArAging.Current += a.Current; d.ArAging.D30 += a.D30; d.ArAging.D60 += a.D60; d.ArAging.D90 += a.D90; }
			foreach (var a in await _ap.AgingAsync(companyId, today))
			{ d.ApAging.Current += a.Current; d.ApAging.D30 += a.D30; d.ApAging.D60 += a.D60; d.ApAging.D90 += a.D90; }

			// 7) derived liquidity / efficiency ratios
			d.WorkingCapital = R(d.Cash + d.Receivables + d.InventoryValue - d.Payables);
			d.QuickRatio = Ratio(d.Cash + d.Receivables, d.Payables);
			d.CurrentRatio = Ratio(d.Cash + d.Receivables + d.InventoryValue, d.Payables);
			var daysElapsed = Math.Max(1, (today - new DateTime(d.Year, 1, 1)).Days + 1);
			d.Dso = d.Revenue <= 0 ? 0 : R(d.Receivables / (d.Revenue / daysElapsed));
			d.Dpo = d.Expense <= 0 ? 0 : R(d.Payables / (d.Expense / daysElapsed));

			return d;
		}
	}
}
