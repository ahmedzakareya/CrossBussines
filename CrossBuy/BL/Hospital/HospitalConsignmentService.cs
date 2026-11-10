using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H38 — consignment implants. The rules are HospitalConsignmentRules' (no database). Receiving registers
	// the supplier's serials (no stock, no journal: they are not the hospital's); using one on a case — in ONE
	// transaction — claims the serial, charges the patient's bill (the stay's when admitted, else the open bill, as the
	// theatre does) and raises a DRAFT purchase order to the supplier through IProcurementService, the procurement
	// module's own writer. The prototype's states follow the purchase order: Draft PO, then Invoiced once it is closed.
	// ==========================================================================================
	public sealed record HospitalConsignmentRow(HospitalConsignment Row, string ItemCode, string Item, string ItemEn, string Vendor, string VendorEn,
		string? PatientName, string? PatientNameEn, string? PatientPhoto, string? Mrn, string? CaseNo, string? PoNo, string? PoStatus);

	public static class HospitalConsignmentRules
	{
		/// Why these serials cannot be received, or null.
		public static string? ReceiveRefusal(IReadOnlyCollection<string> serials, decimal price)
		{
			if (serials.Count == 0) return "Enter at least one serial number";
			if (serials.Any(s => s.Length > 60)) return "A serial number is up to 60 characters";
			if (serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Count) return "A serial number is entered twice";
			if (price <= 0) return "Enter the price agreed with the supplier";
			return null;
		}

		/// Why this implant cannot be used on this case, or null: it is in the store, not expired, and the case is
		/// going on or has just closed.
		public static string? UseRefusal(string status, DateTime? expiry, string caseStatus, DateTime caseDate)
		{
			if (status != HospitalConsignmentStatus.InStore) return "This implant is no longer in the store";
			if (caseStatus is not (HospitalSurgeryStatus.InProgress or HospitalSurgeryStatus.Done)) return "An implant is recorded on a case in progress or just closed";
			if (expiry is DateTime e && e.Date < caseDate.Date) return "This implant has expired";
			return null;
		}

		public static string? ReturnRefusal(string status) => status == HospitalConsignmentStatus.InStore ? null : "Only an implant still in the store goes back to the supplier";

		/// The prototype's state: in the store (consignment), then the purchase order's: draft, approved, invoiced.
		public static string State(string status, string? poStatus) => status switch
		{
			HospitalConsignmentStatus.InStore => "Consignment",
			HospitalConsignmentStatus.Returned => "Returned",
			_ => poStatus switch { null => "Used", "Draft" => "DraftPO", "Approved" => "POApproved", "Cancelled" => "POCancelled", _ => "Invoiced" },
		};
	}

	public sealed class HospitalConsignmentService
	{
		public const string NoTables = "The consignment table is not created yet (hospital_h38_consignment.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;
		private readonly IProcurementService _procurement;

		public HospitalConsignmentService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing, IProcurementService procurement)
		{ _db = db; _access = access; _billing = billing; _procurement = procurement; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalConsignment> Rows(BusinessContext ctx) => _db.Set<HospitalConsignment>().Where(c => c.CompanyId == ctx.CompanyId);
		private async Task<bool> StoreAsync(BusinessContext ctx, CancellationToken ct) => await Can(ctx, HospitalActions.Manage, ct) || await Can(ctx, HospitalActions.Pharmacy, ct);

		/// The consignment register (newest first), or of one case. Empty when the table is not there yet.
		public async Task<(IReadOnlyList<HospitalConsignmentRow> Rows, bool Ready)> ListAsync(BusinessContext ctx, int? surgeryId = null, bool inStoreOnly = false, CancellationToken ct = default)
		{
			try
			{
				var q = Rows(ctx).AsNoTracking();
				if (surgeryId is int sid) q = q.Where(c => c.SurgeryId == sid);
				if (inStoreOnly) q = q.Where(c => c.Status == HospitalConsignmentStatus.InStore);
				var rows = await q.OrderByDescending(c => c.ID).Take(200).ToListAsync(ct);
				var itemIds = rows.Select(r => r.ItemId).Distinct().ToList(); var venIds = rows.Select(r => r.VendorId).Distinct().ToList();
				var patIds = rows.Where(r => r.PatientId != null).Select(r => r.PatientId!.Value).Distinct().ToList();
				var caseIds = rows.Where(r => r.SurgeryId != null).Select(r => r.SurgeryId!.Value).Distinct().ToList();
				var poIds = rows.Where(r => r.PurchaseOrderId != null).Select(r => r.PurchaseOrderId!.Value).Distinct().ToList();
				var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID) && i.CompanyID == ctx.CompanyId).ToDictionaryAsync(i => i.ID, ct);
				var vens = await _db.Vendors.AsNoTracking().Where(v => venIds.Contains(v.ID) && v.CompanyID == ctx.CompanyId).Select(v => new { v.ID, v.Name, v.NameEn }).ToDictionaryAsync(v => v.ID, ct);
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => patIds.Contains(p.ID) && p.CompanyId == ctx.CompanyId).Select(p => new { p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn }).ToDictionaryAsync(p => p.ID, ct);
				var cases = await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => caseIds.Contains(s.ID) && s.CompanyId == ctx.CompanyId).Select(s => new { s.ID, s.CaseNo }).ToDictionaryAsync(s => s.ID, s => s.CaseNo, ct);
				var pos = await _db.PurchaseOrders.AsNoTracking().Where(p => poIds.Contains(p.ID) && p.CompanyID == ctx.CompanyId).Select(p => new { p.ID, p.OrderNo, p.Status }).ToDictionaryAsync(p => p.ID, ct);
				return (rows.Select(r =>
				{
					var it = items.GetValueOrDefault(r.ItemId); var v = vens.GetValueOrDefault(r.VendorId);
					var p = r.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					var po = r.PurchaseOrderId is int oid ? pos.GetValueOrDefault(oid) : null;
					return new HospitalConsignmentRow(r, it?.ItemCode ?? "", it?.Name ?? "", it?.NameEn ?? it?.Name ?? "", v?.Name ?? "", v?.NameEn ?? v?.Name ?? "",
						p?.Name, p?.NameEn, p?.PhotoPath, p?.Mrn, r.SurgeryId is int c ? cases.GetValueOrDefault(c) : null, po?.OrderNo, po?.Status);
				}).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (Array.Empty<HospitalConsignmentRow>(), false); }
		}

		/// The supplier's implants arrive (`pharmacy` / `manage`): one row per serial, no stock, no journal.
		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, int vendorId, int itemId, IReadOnlyList<string> serials, string? lot, DateTime? expiry, decimal price, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StoreAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var clean = serials.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();
			if (HospitalConsignmentRules.ReceiveRefusal(clean, price) is string why) return HospitalResult.Fail(why);
			if (!await _db.Vendors.AnyAsync(v => v.ID == vendorId && v.CompanyID == ctx.CompanyId, ct) || !await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId, ct))
				return HospitalResult.Fail("not_found");
			try
			{
				var taken = await Rows(ctx).Where(c => c.ItemId == itemId && clean.Contains(c.SerialNo)).Select(c => c.SerialNo).FirstOrDefaultAsync(ct);
				if (taken != null) return HospitalResult.Fail("Serial {0} is already registered for this item", taken);
				foreach (var s in clean)
					_db.Add(new HospitalConsignment { CompanyId = ctx.CompanyId, VendorId = vendorId, ItemId = itemId, SerialNo = s, Lot = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim()[..Math.Min(60, lot.Trim().Length)],
						ExpiryDate = expiry?.Date, UnitPrice = price, Status = HospitalConsignmentStatus.InStore, ReceivedAt = nowUtc, ReceivedByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(clean.Count);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// An implant used on a case (`consult` — the theatre team — or `manage`): claimed, charged to the patient, and a
		/// draft purchase order raised to its supplier; all or nothing.
		public async Task<HospitalResult> UseAsync(BusinessContext ctx, int consignmentId, int surgeryId, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var row = await Rows(ctx).AsNoTracking().FirstOrDefaultAsync(c => c.ID == consignmentId, ct);
			var s = await _db.Set<HospitalSurgery>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId && x.CompanyId == ctx.CompanyId, ct);
			if (row == null || s == null) return HospitalResult.Fail("not_found");
			if (HospitalConsignmentRules.UseRefusal(row.Status, row.ExpiryDate, s.Status, s.CaseDate) is string why) return HospitalResult.Fail(why);
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == row.ItemId && i.CompanyID == ctx.CompanyId, ct);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// claimed first: one serial goes into one patient
			int claimed = await Rows(ctx).Where(c => c.ID == consignmentId && c.Status == HospitalConsignmentStatus.InStore)
				.ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, HospitalConsignmentStatus.Used).SetProperty(c => c.UsedAt, nowUtc).SetProperty(c => c.UsedByEmployeeId, employeeId)
					.SetProperty(c => c.PatientId, s.PatientId).SetProperty(c => c.SurgeryId, s.ID), ct);
			if (claimed != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This implant is no longer in the store"); }
			// the patient's bill: the stay's when admitted, else the open bill (as the theatre charges its procedure)
			var local = nowUtc.ToLocalTime();
			HospitalBill? bill = null;
			if (s.AdmissionId is int adm && (await _billing.EnsureAdmissionBillAsync(ctx, adm, employeeId, local, ct)).Id is int billId)
				bill = await _db.HospitalBills.FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId && b.Status == HospitalBillStatus.Open, ct);
			bill ??= await _billing.BillForDispenseAsync(ctx, s.PatientId, null, employeeId, local, ct);
			var line = await _billing.AddItemChargeAsync(ctx, bill, item.ID, item.ItemCode, item.Name, item.NameEn ?? item.Name, 1, employeeId, local, ct);
			await _db.SaveChangesAsync(ct);
			// the supplier's draft purchase order, through procurement's own writer
			var (ok, error, po) = await _procurement.CreatePurchaseOrderAsync(ctx.CompanyId, row.VendorId, null, local.Date, null,
				"Consignment implant " + row.SerialNo + " used on " + s.CaseNo,
				new List<PoLineInput> { new() { ItemId = item.ID, ItemDescription = item.Name + " — SN " + row.SerialNo, Qty = 1, UnitPrice = row.UnitPrice } }, userId);
			if (!ok || po == null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The purchase order could not be raised: {0}", error ?? ""); }
			await Rows(ctx).Where(c => c.ID == consignmentId).ExecuteUpdateAsync(u => u.SetProperty(c => c.PurchaseOrderId, po.ID).SetProperty(c => c.BillLineId, line.ID), ct);
			await tx.CommitAsync();
			return HospitalResult.Success(consignmentId);
		}

		/// An implant still in the store goes back to its supplier (`pharmacy` / `manage`).
		public async Task<HospitalResult> ReturnAsync(BusinessContext ctx, int consignmentId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await StoreAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var row = await Rows(ctx).AsNoTracking().FirstOrDefaultAsync(c => c.ID == consignmentId, ct);
			if (row == null) return HospitalResult.Fail("not_found");
			if (HospitalConsignmentRules.ReturnRefusal(row.Status) is string why) return HospitalResult.Fail(why);
			int n = await Rows(ctx).Where(c => c.ID == consignmentId && c.Status == HospitalConsignmentStatus.InStore)
				.ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, HospitalConsignmentStatus.Returned).SetProperty(c => c.ReturnedAt, nowUtc), ct);
			return n == 1 ? HospitalResult.Success(consignmentId) : HospitalResult.Fail("This implant is no longer in the store");
		}
	}
}
