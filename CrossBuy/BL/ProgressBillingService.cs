using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P4 progress billing (المستخلص). Built from a Confirmed ProjectProgress.
	// Posts ONLY via existing services: invoice (W+T) via ReceivableService.CreateSalesInvoiceAsync +
	// retention (Dr 1104) & advance recovery (Dr 2104) as ProjectId-tagged settlement receipts via
	// CreateReceiptAsync — so the AR subledger (ar_sub) stays reconciled. No new accounting writer.

	public class BillingLineView
	{
		public int? BoqItemId { get; set; }
		public string? Code { get; set; }
		public string Description { get; set; } = "";
		public decimal CumulativeExecutedValue { get; set; }
		public decimal PreviouslyBilledValue { get; set; }
		public decimal PeriodValue { get; set; }
	}

	public class BillingPreview
	{
		public Project? Project { get; set; }
		public ProjectProgress? Progress { get; set; }
		public int BillingId { get; set; }             // 0 = new
		public int BillingNo { get; set; }
		public DateTime BillingDate { get; set; }
		public string Status { get; set; } = "Draft";
		public string? Note { get; set; }
		public List<BillingLineView> Lines { get; set; } = new();
		public decimal GrossWork { get; set; }         // W
		public decimal TaxRate { get; set; }
		public decimal TaxAmount { get; set; }         // T
		public decimal RetentionPercent { get; set; }
		public decimal RetentionAmount { get; set; }   // R
		public decimal AdvancePercent { get; set; }
		public decimal AdvanceBalance { get; set; }    // remaining 2104 for project (cap)
		public decimal AdvanceRecoveryAmount { get; set; }  // A
		public decimal NetDue { get; set; }            // W+T−R−A
		public List<ProjectProgress> BillableMeasurements { get; set; } = new();   // Confirmed & not yet billed
	}

	public interface IProgressBillingService
	{
		Task<List<ProgressBilling>> GetBillingsAsync(int companyId, int projectId);
		Task<ProgressBilling?> GetAsync(int companyId, int id);
		Task<BillingPreview> BuildPreviewAsync(int companyId, int projectId, int? progressId, int? billingId, decimal? taxRateOverride);
		Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int billingId, int progressId, DateTime date, decimal taxRate, string? note, int? userId);
		Task<(bool ok, string? error)> ApproveAsync(int companyId, int id);
		Task<(bool ok, string? error)> PostAsync(int companyId, int id, int? userId);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
	}

	public class ProgressBillingService : IProgressBillingService
	{
		private readonly CrossDbContext _db;
		private readonly IReceivableService _ar;
		private readonly IProgressService _progress;
		private readonly IContractService _contract;
		public ProgressBillingService(CrossDbContext db, IReceivableService ar, IProgressService progress, IContractService contract)
		{ _db = db; _ar = ar; _progress = progress; _contract = contract; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private Task<int?> AccIdAsync(int companyId, string code) =>
			_db.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		public Task<List<ProgressBilling>> GetBillingsAsync(int companyId, int projectId) =>
			_db.ProgressBillings.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId)
				.OrderByDescending(b => b.BillingNo).ToListAsync();

		public Task<ProgressBilling?> GetAsync(int companyId, int id) =>
			_db.ProgressBillings.AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);

		// previously-billed value per BOQ item = Σ PeriodValue over POSTED billings of the project (excluding `exceptBillingId`)
		private async Task<Dictionary<string, decimal>> PreviouslyBilledAsync(int companyId, int projectId, int exceptBillingId)
		{
			var rows = await (from l in _db.ProgressBillingLines.AsNoTracking()
							  join h in _db.ProgressBillings.AsNoTracking() on l.BillingId equals h.ID
							  where h.CompanyID == companyId && h.ProjectId == projectId && h.Status == "Posted" && h.ID != exceptBillingId
							  select new { l.BoqItemId, l.PeriodValue }).ToListAsync();
			return rows.GroupBy(x => x.BoqItemId?.ToString() ?? "null")
					   .ToDictionary(g => g.Key, g => R(g.Sum(x => x.PeriodValue)));
		}

		// core computation: build the period lines + W/T/R/A/net from a Confirmed measurement
		private async Task<BillingPreview> ComputeAsync(int companyId, int projectId, int progressId, int billingId, decimal taxRate, string? note, DateTime date, int billingNo, string status)
		{
			var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			var progress = await _db.ProjectProgresses.AsNoTracking().FirstOrDefaultAsync(p => p.ID == progressId && p.CompanyID == companyId && p.ProjectId == projectId);
			var em = await _progress.BuildEditModelAsync(companyId, projectId, progressId);   // per-item cumulative executed value
			var prevBilled = await PreviouslyBilledAsync(companyId, projectId, billingId);

			var pv = new BillingPreview
			{
				Project = project, Progress = progress, BillingId = billingId, BillingNo = billingNo,
				BillingDate = date, Status = status, Note = note,
				TaxRate = taxRate,
				RetentionPercent = project?.RetentionPercent ?? 0m,
				AdvancePercent = project?.AdvancePercent ?? 0m
			};

			decimal w = 0m;
			foreach (var l in em.Lines)
			{
				var key = l.BoqItemId?.ToString() ?? "null";
				decimal cum = l.ExecutedValue;
				decimal prev = prevBilled.TryGetValue(key, out var pb) ? pb : 0m;
				decimal period = R(Math.Max(0m, cum - prev));
				if (cum == 0 && period == 0) continue;   // skip untouched items
				pv.Lines.Add(new BillingLineView { BoqItemId = l.BoqItemId, Code = l.Code, Description = l.Description, CumulativeExecutedValue = cum, PreviouslyBilledValue = prev, PeriodValue = period });
				w += period;
			}

			pv.GrossWork = R(w);
			pv.TaxAmount = R(w * taxRate / 100m);
			pv.RetentionAmount = R(w * pv.RetentionPercent / 100m);
			pv.AdvanceBalance = (await _contract.GetSummaryAsync(companyId, projectId)).AdvanceBalance;
			pv.AdvanceRecoveryAmount = R(Math.Min(w * pv.AdvancePercent / 100m, Math.Max(0m, pv.AdvanceBalance)));
			pv.NetDue = R(pv.GrossWork + pv.TaxAmount - pv.RetentionAmount - pv.AdvanceRecoveryAmount);
			return pv;
		}

		public async Task<BillingPreview> BuildPreviewAsync(int companyId, int projectId, int? progressId, int? billingId, decimal? taxRateOverride)
		{
			// billable measurements = Confirmed & not already billed (by any billing)
			var billedProgressIds = await _db.ProgressBillings.AsNoTracking().Where(b => b.CompanyID == companyId && b.ProjectId == projectId).Select(b => b.ProgressId).ToListAsync();
			var confirmed = await _db.ProjectProgresses.AsNoTracking()
				.Where(p => p.CompanyID == companyId && p.ProjectId == projectId && p.Status == "Confirmed")
				.OrderByDescending(p => p.MeasurementNo).ToListAsync();

			ProgressBilling? existing = billingId.HasValue && billingId.Value > 0
				? await _db.ProgressBillings.AsNoTracking().FirstOrDefaultAsync(b => b.ID == billingId.Value && b.CompanyID == companyId && b.ProjectId == projectId)
				: null;

			int pgId = existing?.ProgressId ?? progressId ?? confirmed.Select(c => (int?)c.ID).FirstOrDefault() ?? 0;
			decimal taxRate = taxRateOverride ?? existing?.TaxRate ?? 0m;
			int nextNo = existing?.BillingNo ?? ((await _db.ProgressBillings.Where(b => b.CompanyID == companyId && b.ProjectId == projectId).Select(b => (int?)b.BillingNo).MaxAsync() ?? 0) + 1);
			var date = existing?.BillingDate ?? DateTime.Today;
			var status = existing?.Status ?? "Draft";
			var note = existing?.Note;

			BillingPreview pv;
			if (pgId > 0) pv = await ComputeAsync(companyId, projectId, pgId, existing?.ID ?? 0, taxRate, note, date, nextNo, status);
			else { var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId); pv = new BillingPreview { Project = project, BillingNo = nextNo, BillingDate = date }; }

			// for the picker: measurements still billable (exclude those already billed, but keep the current billing's own)
			pv.BillableMeasurements = confirmed.Where(c => !billedProgressIds.Contains(c.ID) || c.ID == existing?.ProgressId).ToList();
			return pv;
		}

		public async Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int billingId, int progressId, DateTime date, decimal taxRate, string? note, int? userId)
		{
			var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (project == null) return (false, "المشروع غير موجود", 0);
			var progress = await _db.ProjectProgresses.AsNoTracking().FirstOrDefaultAsync(p => p.ID == progressId && p.CompanyID == companyId && p.ProjectId == projectId);
			if (progress == null) return (false, "القياس غير موجود", 0);
			if (progress.Status != "Confirmed") return (false, "لا يُفوتر إلا قياس مؤكَّد", 0);
			// no double billing: one billing per measurement (except the one being edited)
			var dupe = await _db.ProgressBillings.AnyAsync(b => b.CompanyID == companyId && b.ProjectId == projectId && b.ProgressId == progressId && b.ID != billingId);
			if (dupe) return (false, "هذا القياس له مستخلص بالفعل", 0);
			if (taxRate < 0) taxRate = 0;

			ProgressBilling hdr;
			if (billingId > 0)
			{
				hdr = await _db.ProgressBillings.Include(b => b.Lines).FirstOrDefaultAsync(b => b.ID == billingId && b.CompanyID == companyId) ?? throw new InvalidOperationException("المستخلص غير موجود");
				if (hdr.Status != "Draft") return (false, "لا يمكن تعديل مستخلص معتمد/مرحَّل", 0);
				_db.ProgressBillingLines.RemoveRange(hdr.Lines); hdr.Lines.Clear();
			}
			else
			{
				int nextNo = (await _db.ProgressBillings.Where(b => b.CompanyID == companyId && b.ProjectId == projectId).Select(b => (int?)b.BillingNo).MaxAsync() ?? 0) + 1;
				hdr = new ProgressBilling { CompanyID = companyId, ProjectId = projectId, BillingNo = nextNo, Status = "Draft", CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.ProgressBillings.Add(hdr);
			}

			var pv = await ComputeAsync(companyId, projectId, progressId, billingId, taxRate, note, date, hdr.BillingNo, "Draft");
			hdr.ProgressId = progressId; hdr.CustomerId = project.CustomerId; hdr.BillingDate = date; hdr.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			hdr.GrossWork = pv.GrossWork; hdr.TaxRate = taxRate; hdr.TaxAmount = pv.TaxAmount;
			hdr.RetentionPercent = pv.RetentionPercent; hdr.RetentionAmount = pv.RetentionAmount;
			hdr.AdvanceRecoveryAmount = pv.AdvanceRecoveryAmount; hdr.NetDue = pv.NetDue;
			foreach (var l in pv.Lines)
				hdr.Lines.Add(new ProgressBillingLine { BoqItemId = l.BoqItemId, CumulativeExecutedValue = l.CumulativeExecutedValue, PreviouslyBilledValue = l.PreviouslyBilledValue, PeriodValue = l.PeriodValue });

			await _db.SaveChangesAsync();
			return (true, null, hdr.ID);
		}

		public async Task<(bool ok, string? error)> ApproveAsync(int companyId, int id)
		{
			var hdr = await _db.ProgressBillings.FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (hdr == null) return (false, "المستخلص غير موجود");
			if (hdr.Status != "Draft") return (false, "الحالة لا تسمح بالاعتماد");
			if (hdr.GrossWork <= 0) return (false, "لا يوجد عمل لفوترته في هذه الفترة");
			hdr.Status = "Approved";
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> PostAsync(int companyId, int id, int? userId)
		{
			var hdr = await _db.ProgressBillings.Include(b => b.Lines).FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (hdr == null) return (false, "المستخلص غير موجود");
			if (hdr.Status == "Posted") return (false, "المستخلص مُرحَّل بالفعل");   // no double posting
			if (hdr.Status != "Approved") return (false, "يجب اعتماد المستخلص قبل الترحيل");
			var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == hdr.ProjectId && p.CompanyID == companyId);
			if (project?.CustomerId == null) return (false, "المشروع بلا عميل — عيّن عميلًا للمشروع أولًا");
			int customerId = project.CustomerId.Value;

			// recompute authoritatively (previously-billed may have changed since the draft)
			var pv = await ComputeAsync(companyId, hdr.ProjectId, hdr.ProgressId, hdr.ID, hdr.TaxRate, hdr.Note, hdr.BillingDate, hdr.BillingNo, hdr.Status);
			if (pv.GrossWork <= 0) return (false, "لا يوجد عمل لفوترته في هذه الفترة");

			var rev = await AccIdAsync(companyId, "4102");
			if (rev == null) return (false, "حساب إيراد عقود المقاولات (4102) غير مُهيّأ");
			var acc1104 = await AccIdAsync(companyId, ContractService.RetentionAccountCode);
			var acc2104 = await AccIdAsync(companyId, ContractService.AdvanceAccountCode);

			// (1) invoice for the gross work (W) + tax (T) via ReceivableService — Dr 1102 / Cr 4102 / Cr 210201, ProjectId
			var invLines = new List<SalesLineInput> {
				new() { ItemDescription = $"مستخلص #{hdr.BillingNo} - {project.Code}", Qty = 1m, UnitPrice = pv.GrossWork, DiscountAmount = 0m, TaxRate = hdr.TaxRate, RevenueAccountId = rev.Value }
			};
			var (iok, ierr, inv) = await _ar.CreateSalesInvoiceAsync(companyId, customerId, hdr.BillingDate, invLines, hdr.Note, userId, projectId: hdr.ProjectId);
			if (!iok || inv == null) return (false, "تعذّر إنشاء فاتورة المستخلص: " + ierr);

			// (2) retention withheld (Dr 1104 / Cr 1102) — settlement receipt, ProjectId-tagged
			int? retReceiptId = null, advReceiptId = null;
			if (pv.RetentionAmount > 0)
			{
				if (acc1104 == null) return (false, "حساب المحتجز (1104) غير مُهيّأ");
				int before = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == customerId).Select(r => (int?)r.ID).MaxAsync() ?? 0;
				var (rok, rerr) = await _ar.CreateReceiptAsync(companyId, customerId, hdr.BillingDate, pv.RetentionAmount, "Retention", acc1104.Value, hdr.Note, userId, projectId: hdr.ProjectId);
				if (!rok) return (false, "تعذّر ترحيل المحتجز: " + rerr);
				retReceiptId = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == customerId && r.ID > before).Select(r => (int?)r.ID).MaxAsync();
			}
			// (3) advance recovery (Dr 2104 / Cr 1102) — settlement receipt, ProjectId-tagged
			if (pv.AdvanceRecoveryAmount > 0)
			{
				if (acc2104 == null) return (false, "حساب المقدّم (2104) غير مُهيّأ");
				int before = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == customerId).Select(r => (int?)r.ID).MaxAsync() ?? 0;
				var (aok, aerr) = await _ar.CreateReceiptAsync(companyId, customerId, hdr.BillingDate, pv.AdvanceRecoveryAmount, "AdvanceRecovery", acc2104.Value, hdr.Note, userId, projectId: hdr.ProjectId);
				if (!aok) return (false, "تعذّر ترحيل استرداد المقدّم: " + aerr);
				advReceiptId = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == customerId && r.ID > before).Select(r => (int?)r.ID).MaxAsync();
			}

			// persist snapshot + links + status
			var track = await _db.ProgressBillings.Include(b => b.Lines).FirstAsync(b => b.ID == id);
			_db.ProgressBillingLines.RemoveRange(track.Lines); track.Lines.Clear();
			track.GrossWork = pv.GrossWork; track.TaxAmount = pv.TaxAmount; track.RetentionAmount = pv.RetentionAmount;
			track.AdvanceRecoveryAmount = pv.AdvanceRecoveryAmount; track.NetDue = pv.NetDue;
			track.SalesInvoiceId = inv.ID; track.RetentionReceiptId = retReceiptId; track.AdvanceReceiptId = advReceiptId;
			track.CustomerId = customerId; track.Status = "Posted"; track.PostedAt = DateTime.UtcNow; track.PostedBy = userId;
			foreach (var l in pv.Lines)
				track.Lines.Add(new ProgressBillingLine { BoqItemId = l.BoqItemId, CumulativeExecutedValue = l.CumulativeExecutedValue, PreviouslyBilledValue = l.PreviouslyBilledValue, PeriodValue = l.PeriodValue });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var hdr = await _db.ProgressBillings.Include(b => b.Lines).FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (hdr == null) return (false, "المستخلص غير موجود");
			if (hdr.Status == "Posted") return (false, "لا يمكن حذف مستخلص مرحَّل");
			_db.ProgressBillingLines.RemoveRange(hdr.Lines);
			_db.ProgressBillings.Remove(hdr);
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
