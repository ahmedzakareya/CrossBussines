using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-5: bill a task's uninvoiced billable hours to its customer as an hourly SERVICE line, via the EXISTING
	// ReceivableService.CreateSalesInvoiceAsync (ItemId=null → revenue + VAT + AR, NO stock). No new accounting writer.
	// Double-billing is prevented per-entry: invoicing stamps InvoicedInvoiceId on the billed entries.
	public class TaskBillingDto
	{
		public bool IsBillable { get; set; }
		public decimal? BillRate { get; set; }
		public int? CustomerId { get; set; }
		public string? CustomerName { get; set; }
		public decimal BillableHours { get; set; }   // uninvoiced
		public decimal Amount { get; set; }           // BillableHours × BillRate
		public bool CanInvoice { get; set; }
		public string? Reason { get; set; }           // why it can't be invoiced (for the UI hint)
	}

	public interface ITaskBillingService
	{
		Task<TaskBillingDto> GetBillingAsync(int companyId, int taskId);
		Task<(bool ok, string? error, int? invoiceId)> GenerateInvoiceAsync(int companyId, int taskId, int? userId);
	}

	public class TaskBillingService : ITaskBillingService
	{
		private readonly CrossDbContext _db;
		private readonly IReceivableService _receivables;
		public TaskBillingService(CrossDbContext db, IReceivableService receivables) { _db = db; _receivables = receivables; }

		private async Task<decimal> UninvoicedHoursAsync(int companyId, int taskId) =>
			await _db.TimesheetEntries.Where(e => e.CompanyId == companyId && e.TaskId == taskId && e.Hours > 0 && e.InvoicedInvoiceId == null).SumAsync(e => (decimal?)e.Hours) ?? 0m;

		public async Task<TaskBillingDto> GetBillingAsync(int companyId, int taskId)
		{
			var t = await _db.TaskItems.AsNoTracking().FirstOrDefaultAsync(x => x.ID == taskId && x.CompanyId == companyId);
			var dto = new TaskBillingDto();
			if (t == null) { dto.Reason = "Task not found"; return dto; }
			dto.IsBillable = t.IsBillable; dto.BillRate = t.BillRate; dto.CustomerId = t.CustomerId;
			if (t.CustomerId != null) dto.CustomerName = await _db.Customers.AsNoTracking().Where(c => c.ID == t.CustomerId).Select(c => c.Name).FirstOrDefaultAsync();
			dto.BillableHours = await UninvoicedHoursAsync(companyId, taskId);
			dto.Amount = Math.Round(dto.BillableHours * (t.BillRate ?? 0m), 2);
			if (!t.IsBillable) dto.Reason = "The task is not billable";
			else if (t.CustomerId == null || !(t.BillRate > 0)) dto.Reason = "Set the customer and the billing hourly rate";
			else if (dto.BillableHours <= 0) dto.Reason = "There are no billable (unbilled) hours";
			else dto.CanInvoice = true;
			return dto;
		}

		public async Task<(bool ok, string? error, int? invoiceId)> GenerateInvoiceAsync(int companyId, int taskId, int? userId)
		{
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == taskId && x.CompanyId == companyId);
			if (t == null) return (false, "Task not found", null);
			if (!t.IsBillable) return (false, "The task is not billable", null);
			if (t.CustomerId == null || t.CustomerId <= 0) return (false, "A customer must be selected", null);
			if (!(t.BillRate > 0)) return (false, "A billing hourly rate must be set", null);

			var entries = await _db.TimesheetEntries.Where(e => e.CompanyId == companyId && e.TaskId == taskId && e.Hours > 0 && e.InvoicedInvoiceId == null).ToListAsync();
			decimal hours = entries.Sum(e => e.Hours);
			if (hours <= 0) return (false, "There are no billable hours", null);

			int revenue = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "4101").Select(a => a.ID).FirstOrDefaultAsync();
			if (revenue == 0) return (false, "There is no revenue account (4101)", null);
			decimal vat = await _db.TaxCodes.AsNoTracking().Where(x => x.CompanyID == companyId && x.Kind == "VAT" && x.IsDefault && x.IsActive).Select(x => (decimal?)x.Rate).FirstOrDefaultAsync() ?? 0m;

			var line = new SalesLineInput
			{
				ItemDescription = $"{t.Title} — {hours:0.##} work hour(s)",
				Qty = hours, UnitPrice = t.BillRate!.Value, DiscountAmount = 0, TaxRate = vat,
				RevenueAccountId = revenue, ItemId = null, WarehouseId = null   // service line → no stock, no COGS
			};
			var (ok, err, inv) = await _receivables.CreateSalesInvoiceAsync(companyId, t.CustomerId.Value, DateTime.Today, new List<SalesLineInput> { line }, $"Billing for task #{t.ID}", userId, null, null, null);
			if (!ok || inv == null) return (false, err ?? "Could not create the invoice", null);

			// stamp the billed entries → they can't be billed again (double-billing guard)
			foreach (var e in entries) e.InvoicedInvoiceId = inv.ID;
			await _db.SaveChangesAsync();
			return (true, null, inv.ID);
		}
	}
}
