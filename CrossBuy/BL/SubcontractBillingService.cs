using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P6-ج: subcontractor / equipment-rental progress billing (مستخلص باطن).
	// Posts ONLY via PayableService: purchase invoice (W+T, service line → 510104, ProjectId) + retention as a settlement
	// payment into 2105 (ProjectId) → ap_sub intact, cost tagged ProjectId. No new writer. Cumulative, post-once.
	public class SubBillingPreview
	{
		public Subcontract? Subcontract { get; set; }
		public string VendorName { get; set; } = "";
		public int BillingId { get; set; }
		public int BillingNo { get; set; }
		public DateTime BillingDate { get; set; }
		public string Status { get; set; } = "Draft";
		public string? Note { get; set; }
		public decimal PreviouslyBilled { get; set; }
		public decimal CumulativeWork { get; set; }
		public decimal GrossWork { get; set; }       // W = cumulative − previously billed
		public decimal TaxRate { get; set; }
		public decimal TaxAmount { get; set; }        // T
		public decimal RetentionPercent { get; set; }
		public decimal RetentionAmount { get; set; }  // R
		public decimal NetPayable { get; set; }       // W+T−R
	}

	public interface ISubcontractBillingService
	{
		Task<List<Subcontract>> GetSubcontractsAsync(int companyId, int projectId);
		Task<(bool ok, string? error, int id)> SaveSubcontractAsync(int companyId, int projectId, int id, int vendorId, string? description, decimal? value, decimal? retentionPct, int? userId);
		Task<List<SubcontractBilling>> GetBillingsAsync(int companyId, int subcontractId);
		Task<SubcontractBilling?> GetBillingAsync(int companyId, int id);
		Task<SubBillingPreview> BuildPreviewAsync(int companyId, int subcontractId, int? billingId, decimal? cumulativeWork, decimal? taxRate);
		Task<(bool ok, string? error, int id)> SaveBillingDraftAsync(int companyId, int subcontractId, int billingId, DateTime date, decimal cumulativeWork, decimal taxRate, string? note, int? userId);
		Task<(bool ok, string? error)> ApproveBillingAsync(int companyId, int id);
		Task<(bool ok, string? error)> PostBillingAsync(int companyId, int id, int? userId);
		Task<(bool ok, string? error)> DeleteBillingAsync(int companyId, int id);
	}

	public class SubcontractBillingService : ISubcontractBillingService
	{
		public const string ExpenseAccountCode = "510104";     // Dr — project execution cost (subcontract)
		public const string RetentionAccountCode = "2105";     // Cr — subcontractor retention payable
		private readonly CrossDbContext _db;
		private readonly IPayableService _ap;
		public SubcontractBillingService(CrossDbContext db, IPayableService ap) { _db = db; _ap = ap; }
		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public Task<List<Subcontract>> GetSubcontractsAsync(int companyId, int projectId) =>
			_db.Subcontracts.AsNoTracking().Where(s => s.CompanyID == companyId && s.ProjectId == projectId).OrderBy(s => s.ID).ToListAsync();

		public async Task<(bool ok, string? error, int id)> SaveSubcontractAsync(int companyId, int projectId, int id, int vendorId, string? description, decimal? value, decimal? retentionPct, int? userId)
		{
			if (!await _db.Projects.AnyAsync(p => p.ID == projectId && p.CompanyID == companyId)) return (false, "Project not found", 0);
			if (!await _db.Vendors.AnyAsync(v => v.ID == vendorId && v.CompanyID == companyId)) return (false, "The subcontractor was not found", 0);
			Subcontract sc;
			if (id > 0) sc = await _db.Subcontracts.FirstOrDefaultAsync(s => s.ID == id && s.CompanyID == companyId) ?? throw new InvalidOperationException("Contract not found");
			else { sc = new Subcontract { CompanyID = companyId, ProjectId = projectId, Status = "Active", CreatedAt = DateTime.UtcNow, CreatedBy = userId }; _db.Subcontracts.Add(sc); }
			sc.VendorId = vendorId; sc.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
			sc.ContractValue = value; sc.RetentionPercent = retentionPct;
			await _db.SaveChangesAsync();
			return (true, null, sc.ID);
		}

		public Task<List<SubcontractBilling>> GetBillingsAsync(int companyId, int subcontractId) =>
			_db.SubcontractBillings.AsNoTracking().Where(b => b.CompanyID == companyId && b.SubcontractId == subcontractId).OrderByDescending(b => b.BillingNo).ToListAsync();

		public Task<SubcontractBilling?> GetBillingAsync(int companyId, int id) =>
			_db.SubcontractBillings.AsNoTracking().FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);

		private async Task<decimal> PreviouslyBilledAsync(int companyId, int subcontractId, int exceptBillingId) =>
			R(await _db.SubcontractBillings.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.SubcontractId == subcontractId && b.Status == "Posted" && b.ID != exceptBillingId)
				.SumAsync(b => (decimal?)b.GrossWork) ?? 0m);

		private SubBillingPreview Compute(Subcontract sc, string vendorName, int billingId, int billingNo, DateTime date, string status, string? note, decimal cumulative, decimal taxRate, decimal prevBilled)
		{
			decimal w = R(Math.Max(0m, cumulative - prevBilled));
			decimal retPct = sc.RetentionPercent ?? 0m;
			var pv = new SubBillingPreview
			{
				Subcontract = sc, VendorName = vendorName, BillingId = billingId, BillingNo = billingNo, BillingDate = date, Status = status, Note = note,
				PreviouslyBilled = prevBilled, CumulativeWork = cumulative, GrossWork = w, TaxRate = taxRate,
				TaxAmount = R(w * taxRate / 100m), RetentionPercent = retPct, RetentionAmount = R(w * retPct / 100m)
			};
			pv.NetPayable = R(pv.GrossWork + pv.TaxAmount - pv.RetentionAmount);
			return pv;
		}

		public async Task<SubBillingPreview> BuildPreviewAsync(int companyId, int subcontractId, int? billingId, decimal? cumulativeWork, decimal? taxRate)
		{
			var sc = await _db.Subcontracts.AsNoTracking().FirstAsync(s => s.ID == subcontractId && s.CompanyID == companyId);
			var vname = await _db.Vendors.AsNoTracking().Where(v => v.ID == sc.VendorId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
			var existing = billingId.HasValue && billingId.Value > 0 ? await _db.SubcontractBillings.AsNoTracking().FirstOrDefaultAsync(b => b.ID == billingId.Value && b.CompanyID == companyId) : null;
			decimal prev = await PreviouslyBilledAsync(companyId, subcontractId, existing?.ID ?? 0);
			int nextNo = existing?.BillingNo ?? ((await _db.SubcontractBillings.Where(b => b.CompanyID == companyId && b.SubcontractId == subcontractId).Select(b => (int?)b.BillingNo).MaxAsync() ?? 0) + 1);
			decimal cum = cumulativeWork ?? existing?.CumulativeWork ?? prev;   // default: cumulative = previously billed → W starts 0
			decimal tax = taxRate ?? existing?.TaxRate ?? 0m;
			return Compute(sc, vname, existing?.ID ?? 0, nextNo, existing?.BillingDate ?? DateTime.Today, existing?.Status ?? "Draft", existing?.Note, cum, tax, prev);
		}

		public async Task<(bool ok, string? error, int id)> SaveBillingDraftAsync(int companyId, int subcontractId, int billingId, DateTime date, decimal cumulativeWork, decimal taxRate, string? note, int? userId)
		{
			var sc = await _db.Subcontracts.AsNoTracking().FirstOrDefaultAsync(s => s.ID == subcontractId && s.CompanyID == companyId);
			if (sc == null) return (false, "Subcontract not found", 0);
			if (taxRate < 0) taxRate = 0;
			SubcontractBilling hdr;
			if (billingId > 0)
			{
				hdr = await _db.SubcontractBillings.FirstOrDefaultAsync(b => b.ID == billingId && b.CompanyID == companyId) ?? throw new InvalidOperationException("Certificate not found");
				if (hdr.Status != "Draft") return (false, "An approved or posted certificate cannot be edited", 0);
			}
			else
			{
				int nextNo = (await _db.SubcontractBillings.Where(b => b.CompanyID == companyId && b.SubcontractId == subcontractId).Select(b => (int?)b.BillingNo).MaxAsync() ?? 0) + 1;
				hdr = new SubcontractBilling { CompanyID = companyId, SubcontractId = subcontractId, ProjectId = sc.ProjectId, VendorId = sc.VendorId, BillingNo = nextNo, Status = "Draft", CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.SubcontractBillings.Add(hdr);
			}
			decimal prev = await PreviouslyBilledAsync(companyId, subcontractId, hdr.ID);
			var pv = Compute(sc, "", hdr.ID, hdr.BillingNo, date, "Draft", note, cumulativeWork, taxRate, prev);
			if (pv.GrossWork <= 0) return (false, "The period value is zero — the cumulative amount must exceed what was previously billed", 0);
			hdr.BillingDate = date; hdr.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			hdr.CumulativeWork = R(cumulativeWork); hdr.GrossWork = pv.GrossWork; hdr.TaxRate = taxRate; hdr.TaxAmount = pv.TaxAmount;
			hdr.RetentionPercent = pv.RetentionPercent; hdr.RetentionAmount = pv.RetentionAmount; hdr.NetPayable = pv.NetPayable;
			await _db.SaveChangesAsync();
			return (true, null, hdr.ID);
		}

		public async Task<(bool ok, string? error)> ApproveBillingAsync(int companyId, int id)
		{
			var hdr = await _db.SubcontractBillings.FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (hdr == null) return (false, "Certificate not found");
			if (hdr.Status != "Draft") return (false, "The current status does not allow approval");
			if (hdr.GrossWork <= 0) return (false, "There is no work to bill");
			hdr.Status = "Approved"; await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> PostBillingAsync(int companyId, int id, int? userId)
		{
			var hdr = await _db.SubcontractBillings.FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (hdr == null) return (false, "Certificate not found");
			if (hdr.Status == "Posted") return (false, "The certificate is already posted");
			if (hdr.Status != "Approved") return (false, "The certificate must be approved before posting");
			var sc = await _db.Subcontracts.AsNoTracking().FirstAsync(s => s.ID == hdr.SubcontractId);
			var expAcc = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == ExpenseAccountCode).Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (expAcc == null) return (false, $"The execution cost account ({ExpenseAccountCode}) is not configured");
			var retAcc = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == RetentionAccountCode).Select(a => (int?)a.ID).FirstOrDefaultAsync();

			// (1) purchase invoice for the period work (W) + tax → Dr 510104 [ProjectId] + Dr VAT input / Cr AP
			var invLines = new List<PurchaseLineInput> {
				new() { ItemDescription = $"Subcontract certificate #{hdr.BillingNo}", Qty = 1m, UnitPrice = hdr.GrossWork, DiscountAmount = 0m, TaxRate = hdr.TaxRate, ExpenseAccountId = expAcc.Value }
			};
			var (iok, ierr, inv) = await _ap.CreatePurchaseInvoiceAsync(companyId, hdr.VendorId, hdr.BillingDate, invLines, hdr.Note, userId, projectId: hdr.ProjectId);
			if (!iok || inv == null) return (false, "Could not create the subcontractor invoice: " + ierr);

			// (2) retention withheld → settlement payment into 2105 (Dr AP / Cr 2105), ProjectId-tagged
			int? retPayId = null;
			if (hdr.RetentionAmount > 0)
			{
				if (retAcc == null) return (false, "The subcontractor retention account (2105) is not configured");
				int before = await _db.Payments.Where(p => p.CompanyID == companyId && p.VendorId == hdr.VendorId).Select(p => (int?)p.ID).MaxAsync() ?? 0;
				var (pok, perr) = await _ap.CreatePaymentAsync(companyId, hdr.VendorId, hdr.BillingDate, hdr.RetentionAmount, "Retention", retAcc.Value, hdr.Note, userId, 0m, null, null, projectId: hdr.ProjectId);
				if (!pok) return (false, "Could not post the subcontractor retention: " + perr);
				retPayId = await _db.Payments.Where(p => p.CompanyID == companyId && p.VendorId == hdr.VendorId && p.ID > before).Select(p => (int?)p.ID).MaxAsync();
			}

			hdr.PurchaseInvoiceId = inv.ID; hdr.RetentionPaymentId = retPayId;
			hdr.Status = "Posted"; hdr.PostedAt = DateTime.UtcNow; hdr.PostedBy = userId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteBillingAsync(int companyId, int id)
		{
			var hdr = await _db.SubcontractBillings.FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (hdr == null) return (false, "Certificate not found");
			if (hdr.Status == "Posted") return (false, "A posted certificate cannot be deleted");
			_db.SubcontractBillings.Remove(hdr); await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
