using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H119 — EVERY TREATMENT SESSION IS CHARGED. A dialysis session, a chemotherapy cycle given and a
	// physiotherapy session attended carried no charge: the work was done and the bill never heard of it. Each kind is
	// now charged — once — the moment it is completed, from the service the price list holds under its code, onto the
	// stay's bill while the patient is admitted or the patient's own open bill otherwise (the ward's rule, H51). The
	// price list is the owner's: until the code is there the session is saved and plainly marked "not billed — no price",
	// and can be charged later from its own screen. Nothing is ever charged twice.
	// ==========================================================================================
	public static class HospitalSessionServiceCodes
	{
		public const string Dialysis = "HD-SESSION";
		public const string Chemo = "CHEMO-ADMIN";
		public const string Physio = "PT-SESSION";
	}

	public static class HospitalSessionKinds
	{
		public const string Dialysis = "Dialysis";
		public const string Chemo = "Chemo";
		public const string Physio = "Physio";
		public static readonly IReadOnlyList<string> All = new[] { Dialysis, Chemo, Physio };
		public static string Code(string kind) => kind switch
		{
			Dialysis => HospitalSessionServiceCodes.Dialysis, Chemo => HospitalSessionServiceCodes.Chemo, Physio => HospitalSessionServiceCodes.Physio,
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		};
	}

	/// What a session's billing cell shows: the session, whether it can be charged at all (done, given, attended), its
	/// bill line when it has one.
	public sealed record HospitalSessionChargeCell(string Kind, int Id, int? BillLineId, bool Chargeable);

	public static class HospitalSessionCharges
	{
		/// The active service the price list holds under the code (null = no price set yet).
		public static Task<HospitalService?> ServiceAsync(CrossDbContext db, BusinessContext ctx, string code, CancellationToken ct) =>
			db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.Code == code && s.IsActive, ct);

		/// H123: the service a session of this kind is charged as — the one its unit's settings name (chosen from the services
		/// list); a service already listed under the old fixed code still counts. Null = the unit has none yet.
		public static async Task<HospitalService?> ServiceForAsync(CrossDbContext db, BusinessContext ctx, string kind, CancellationToken ct)
		{
			if ((await HospitalUnitSettingsService.RowAsync(db, ctx.CompanyId, kind, ct))?.SessionServiceId is int id)
				return await db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.ID == id && s.CompanyId == ctx.CompanyId && s.IsActive, ct);
			return await ServiceAsync(db, ctx, HospitalSessionKinds.Code(kind), ct);
		}

		/// Charges the session's service (its unit's, H123) to the patient's bill (the stay's while admitted). Null when the unit
		/// has no service yet. The caller saves the line's id on the session, in the same transaction.
		public static async Task<HospitalBillLine?> ChargeAsync(CrossDbContext db, HospitalBillingService billing, BusinessContext ctx, int patientId, string kind, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			var s = await ServiceForAsync(db, ctx, kind, ct);
			if (s == null) return null;
			var local = HospitalNursingService.UtcToWard(nowUtc);
			var bill = await billing.BillForWardChargeAsync(ctx, patientId, employeeId, local, ct);
			var line = await billing.AddServiceChargeAsync(ctx, bill, s.ID, employeeId, local, ct);
			await db.SaveChangesAsync(ct);
			return line;
		}
	}

	public sealed class HospitalSessionChargeService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;
		public HospitalSessionChargeService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing) { _db = db; _access = access; _billing = billing; }

		/// Charges a session completed before its price existed — once: a second press (or a second person) finds it charged.
		public async Task<HospitalResult> ChargeLaterAsync(BusinessContext ctx, string kind, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalSessionKinds.All.Contains(kind)) return HospitalResult.Fail("not_found");
			int cid = ctx.CompanyId;
			int? patient = kind switch
			{
				HospitalSessionKinds.Dialysis => await _db.Set<HospitalDialysisSession>().Where(s => s.ID == id && s.CompanyId == cid && s.Status == HospitalDialysisSessionStatus.Done && s.BillLineId == null).Select(s => (int?)s.PatientId).FirstOrDefaultAsync(ct),
				HospitalSessionKinds.Chemo => await _db.Set<HospitalChemoCycle>().Where(s => s.ID == id && s.CompanyId == cid && s.Status == HospitalChemoCycleStatus.Given && s.BillLineId == null).Select(s => (int?)s.PatientId).FirstOrDefaultAsync(ct),
				_ => await _db.Set<HospitalRehabSession>().Where(s => s.ID == id && s.CompanyId == cid && s.Attendance == HospitalRehabAttendance.Attended && s.BillLineId == null).Select(s => (int?)s.PatientId).FirstOrDefaultAsync(ct),
			};
			if (patient is not int pid) return HospitalResult.Fail("This session is already charged, or is not one that is charged");
			if (await HospitalSessionCharges.ServiceForAsync(_db, ctx, kind, ct) == null) return HospitalResult.Fail("Choose the session's service in the unit's settings");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var line = await HospitalSessionCharges.ChargeAsync(_db, _billing, ctx, pid, kind, employeeId, nowUtc, ct);
			if (line == null) { await tx.RollbackAsync(); return HospitalResult.Fail("Choose the session's service in the unit's settings"); }
			int n = kind switch
			{
				HospitalSessionKinds.Dialysis => await _db.Set<HospitalDialysisSession>().Where(s => s.ID == id && s.CompanyId == cid && s.BillLineId == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.BillLineId, line.ID), ct),
				HospitalSessionKinds.Chemo => await _db.Set<HospitalChemoCycle>().Where(s => s.ID == id && s.CompanyId == cid && s.BillLineId == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.BillLineId, line.ID), ct),
				_ => await _db.Set<HospitalRehabSession>().Where(s => s.ID == id && s.CompanyId == cid && s.BillLineId == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.BillLineId, line.ID), ct),
			};
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This session is already charged, or is not one that is charged"); }
			await tx.CommitAsync();
			return HospitalResult.Success(line.ID);
		}
	}
}
