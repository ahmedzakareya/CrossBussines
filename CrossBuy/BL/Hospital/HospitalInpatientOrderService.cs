using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H75 — lab tests and imaging for inpatients. An order creates the lab's or radiology's own order (it joins
	// their worklists like a visit's), charges its service to the stay's bill (EnsureAdmissionBillAsync +
	// AddServiceChargeAsync — the same pricing as any order), and keeps this record. Cancelling — only before anything
	// happened to it (not collected, not arrived) — removes the order and its charge from the open bill, and keeps why.
	// ==========================================================================================
	public sealed class HospitalInpatientOrderInput
	{
		public int AdmissionId { get; set; }
		public List<int> ServiceIds { get; set; } = new();
		public string? Priority { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalDiagnosticChoice(int ServiceId, string Kind, string Code, string Name, string NameEn, bool Contrast);
	public sealed record HospitalInpatientOrderRow(HospitalInpatientOrder Order, string? Stage, bool Cancellable, bool ContrastAlert);
	/// What was placed: how many, and whether a contrast exam meets a contrast allergy on the file (shown, never stopping).
	public sealed record HospitalInpatientOrderOutcome(int Placed, IReadOnlyList<string> ContrastWarnings);

	public static class HospitalInpatientOrderRules
	{
		public static string? Refusal(HospitalInpatientOrderInput i)
		{
			if (i.ServiceIds.Count == 0) return "Choose at least one test or exam";
			if (i.ServiceIds.Count > 30) return "At most 30 tests and exams at once";
			if (!new[] { HospitalLabPriority.Routine, HospitalLabPriority.Urgent, HospitalLabPriority.Stat }.Contains(i.Priority ?? HospitalLabPriority.Routine)) return "Choose the priority";
			return null;
		}

		/// Only an order nothing has happened to yet can be cancelled: a lab test not collected, an exam the patient has not arrived for.
		public static bool Cancellable(string kind, string? stage) =>
			kind == HospitalInpatientOrderKind.Lab ? stage == HospitalLabStatus.Ordered : stage == HospitalRadStatus.Scheduled;
	}

	public sealed class HospitalInpatientOrderService
	{
		public const string NoTables = "Inpatient lab and imaging orders are not installed yet (hospital_h75_inpatient_orders.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;
		public HospitalInpatientOrderService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing) { _db = db; _access = access; _billing = billing; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// What can be ordered: the active lab and radiology services of the catalogue (an exam with its contrast flag).
		public async Task<IReadOnlyList<HospitalDiagnosticChoice>> CatalogAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var services = await _db.HospitalServices.AsNoTracking()
				.Where(s => s.CompanyId == ctx.CompanyId && s.IsActive && (s.Category == HospitalServiceCategories.Lab || s.Category == HospitalServiceCategories.Radiology))
				.OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Code).ToListAsync(ct);
			var exams = await _db.HospitalRadExams.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.ServiceId, ct);
			return services.Select(s => new HospitalDiagnosticChoice(s.ID, s.Category == HospitalServiceCategories.Lab ? HospitalInpatientOrderKind.Lab : HospitalInpatientOrderKind.Imaging,
				s.Code, s.Name, s.NameEn, exams.TryGetValue(s.ID, out var e) && e.Contrast)).ToList();
		}

		/// Places the tests and exams for an admission; the result's id is how many were placed.
		public async Task<(HospitalResult Result, HospitalInpatientOrderOutcome? Outcome)> OrderAsync(BusinessContext ctx, HospitalInpatientOrderInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			if (HospitalInpatientOrderRules.Refusal(input) is string why) return (HospitalResult.Fail(why), null);
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return (HospitalResult.Fail("not_found"), null);
			if (adm.Status != HospitalAdmissionStatus.Active) return (HospitalResult.Fail("This admission is not active"), null);
			var ids = input.ServiceIds.Distinct().ToList();
			var services = await _db.HospitalServices.AsNoTracking()
				.Where(s => s.CompanyId == ctx.CompanyId && s.IsActive && ids.Contains(s.ID) && (s.Category == HospitalServiceCategories.Lab || s.Category == HospitalServiceCategories.Radiology)).ToListAsync(ct);
			if (services.Count != ids.Count) return (HospitalResult.Fail("not_found"), null);
			var allergies = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == adm.PatientId).Select(p => p.Allergies).FirstOrDefaultAsync(ct);
			var exams = await _db.HospitalRadExams.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.ServiceId, ct);
			var priority = input.Priority ?? HospitalLabPriority.Routine;
			try
			{
				var billRes = await _billing.EnsureAdmissionBillAsync(ctx, adm.ID, employeeId, nowUtc, ct);
				if (!billRes.Ok) return (billRes, null);
				var bill = await _db.HospitalBills.FirstAsync(b => b.ID == billRes.Id && b.CompanyId == ctx.CompanyId, ct);
				if (bill.Status != HospitalBillStatus.Open) return (HospitalResult.Fail("The stay's bill is closed"), null);
				var labNos = await _db.HospitalLabOrders.Where(o => o.CompanyId == ctx.CompanyId).Select(o => o.AccessionNo).ToListAsync(ct);
				var radNos = await _db.HospitalRadOrders.Where(o => o.CompanyId == ctx.CompanyId).Select(o => o.AccessionNo).ToListAsync(ct);
				var warnings = new List<string>();
				var pending = new List<(HospitalInpatientOrder Record, HospitalLabOrder? Lab, HospitalRadOrder? Rad, HospitalBillLine? Line)>();
				foreach (var s in services.OrderBy(x => ids.IndexOf(x.ID)))
				{
					bool lab = s.Category == HospitalServiceCategories.Lab;
					HospitalLabOrder? lo = null; HospitalRadOrder? ro = null; string acc;
					if (lab)
					{
						acc = HospitalDentalPracticeRules.NextNo(labNos, "LAB-", 6); labNos.Add(acc);
						lo = new HospitalLabOrder
						{
							CompanyId = ctx.CompanyId, AccessionNo = acc, PatientId = adm.PatientId, ServiceId = s.ID, Code = s.Code, Name = s.Name, NameEn = s.NameEn,
							OrderedByEmployeeId = employeeId, Priority = priority, Status = HospitalLabStatus.Ordered, OrderedAt = nowUtc,
						};
						_db.HospitalLabOrders.Add(lo);
					}
					else
					{
						acc = HospitalDentalPracticeRules.NextNo(radNos, "RAD-", 6); radNos.Add(acc);
						exams.TryGetValue(s.ID, out var ex);
						ro = new HospitalRadOrder
						{
							CompanyId = ctx.CompanyId, AccessionNo = acc, PatientId = adm.PatientId, ServiceId = s.ID, Code = s.Code, Name = s.Name, NameEn = s.NameEn,
							Modality = ex?.Modality ?? HospitalRadiologyService.GuessModality(s.Code), Contrast = ex?.Contrast ?? false, OrderedByEmployeeId = employeeId,
							Priority = priority, OrderedAt = nowUtc,
						};
						_db.HospitalRadOrders.Add(ro);
						if (HospitalRadiologyService.ContrastAlert(ro.Contrast, allergies)) warnings.Add(s.Code);
					}
					var line = await _billing.AddServiceChargeAsync(ctx, bill, s.ID, employeeId, nowUtc, ct);
					var rec = new HospitalInpatientOrder
					{
						CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Kind = lab ? HospitalInpatientOrderKind.Lab : HospitalInpatientOrderKind.Imaging,
						ServiceId = s.ID, AccessionNo = acc, Code = s.Code, Name = s.Name, NameEn = s.NameEn, Priority = priority, Note = Clip(input.Note, 300),
						Status = HospitalInpatientOrderStatus.Active, OrderedAt = nowUtc, OrderedByEmployeeId = employeeId,
					};
					pending.Add((rec, lo, ro, line));
				}
				await _db.SaveChangesAsync(ct);   // the orders and their charges, together
				foreach (var (rec, lo, ro, line) in pending)
				{
					rec.LabOrderId = lo?.ID; rec.RadOrderId = ro?.ID; rec.BillLineId = line?.ID;
					_db.Add(rec);
				}
				await _db.SaveChangesAsync(ct);
				return (HospitalResult.Success(pending.Count), new HospitalInpatientOrderOutcome(pending.Count, warnings));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return (HospitalResult.Fail(NoTables), null); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return (HospitalResult.Fail("The change could not be saved"), null); }
		}

		/// Cancels an order nothing has happened to: the order leaves its worklist, its charge the open bill.
		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why it is cancelled");
			try
			{
				var rec = await _db.Set<HospitalInpatientOrder>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (rec == null) return HospitalResult.Fail("not_found");
				if (rec.Status != HospitalInpatientOrderStatus.Active) return HospitalResult.Fail("This order is already cancelled");
				HospitalLabOrder? lo = rec.LabOrderId is int l ? await _db.HospitalLabOrders.FirstOrDefaultAsync(x => x.ID == l && x.CompanyId == ctx.CompanyId, ct) : null;
				HospitalRadOrder? ro = rec.RadOrderId is int r ? await _db.HospitalRadOrders.FirstOrDefaultAsync(x => x.ID == r && x.CompanyId == ctx.CompanyId, ct) : null;
				if (!HospitalInpatientOrderRules.Cancellable(rec.Kind, lo?.Status ?? ro?.Status)) return HospitalResult.Fail("Work has started on this order: it can no longer be cancelled");
				if (rec.BillLineId is int bl)
				{
					var line = await _db.HospitalBillLines.FirstOrDefaultAsync(x => x.ID == bl && x.CompanyId == ctx.CompanyId, ct);
					if (line != null)
					{
						bool open = await _db.HospitalBills.AnyAsync(b => b.ID == line.BillId && b.CompanyId == ctx.CompanyId && b.Status == HospitalBillStatus.Open, ct);
						if (!open) return HospitalResult.Fail("The stay's bill is closed");
						_db.HospitalBillLines.Remove(line);
					}
				}
				if (lo != null) _db.HospitalLabOrders.Remove(lo);
				if (ro != null) _db.HospitalRadOrders.Remove(ro);
				rec.Status = HospitalInpatientOrderStatus.Cancelled; rec.LabOrderId = null; rec.RadOrderId = null; rec.BillLineId = null;
				rec.CancelledAt = nowUtc; rec.CancelledByEmployeeId = employeeId; rec.CancelReason = Clip(reason, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(rec.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// This stay's tests and exams with where each stands now (from the lab's or radiology's own order).
		public async Task<IReadOnlyList<HospitalInpatientOrderRow>> ForAdmissionAsync(BusinessContext ctx, int admissionId, CancellationToken ct = default)
		{
			try
			{
				var recs = await _db.Set<HospitalInpatientOrder>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == admissionId).OrderByDescending(x => x.OrderedAt).ThenByDescending(x => x.ID).ToListAsync(ct);
				var labIds = recs.Where(x => x.LabOrderId != null).Select(x => x.LabOrderId!.Value).ToList();
				var radIds = recs.Where(x => x.RadOrderId != null).Select(x => x.RadOrderId!.Value).ToList();
				var labs = await _db.HospitalLabOrders.AsNoTracking().Where(x => labIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.Status, ct);
				var rads = await _db.HospitalRadOrders.AsNoTracking().Where(x => radIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => new { x.Status, x.Contrast }, ct);
				var allergies = recs.Count == 0 ? null : await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == recs[0].PatientId).Select(p => p.Allergies).FirstOrDefaultAsync(ct);
				return recs.Select(x =>
				{
					string? stage = x.LabOrderId is int l ? labs.GetValueOrDefault(l) : x.RadOrderId is int r && rads.TryGetValue(r, out var rr) ? rr.Status : null;
					bool contrast = x.RadOrderId is int r2 && rads.TryGetValue(r2, out var r3) && HospitalRadiologyService.ContrastAlert(r3.Contrast, allergies);
					return new HospitalInpatientOrderRow(x, stage, x.Status == HospitalInpatientOrderStatus.Active && HospitalInpatientOrderRules.Cancellable(x.Kind, stage), contrast);
				}).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalInpatientOrderRow>(); }
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
	}
}
