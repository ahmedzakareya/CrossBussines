using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalIcuGcsInput { public int AdmissionId { get; set; } public int Eye { get; set; } public int Verbal { get; set; } public int Motor { get; set; } public bool Intubated { get; set; } }
	public sealed class HospitalIcuApacheInput { public int AdmissionId { get; set; } public HospitalApacheInput Values { get; set; } = new(); }
	public sealed class HospitalIcuInfusionInput
	{
		public int AdmissionId { get; set; }
		public string? Drug { get; set; } public string? DoseUnit { get; set; }
		public decimal? ConcPerMl { get; set; } public decimal? WeightKg { get; set; }
		public decimal MinDose { get; set; } public decimal MaxDose { get; set; } public decimal MaxStep { get; set; }
		public decimal Dose { get; set; } public string? Target { get; set; }
	}
	public sealed class HospitalIcuTitrateInput { public decimal Dose { get; set; } public string? Reason { get; set; } }
	public sealed class HospitalIcuDeviceInput { public int AdmissionId { get; set; } public string? Kind { get; set; } public string? Site { get; set; } public DateTime? InsertedAt { get; set; } }
	public sealed class HospitalIcuBundleInput { public List<string>? Items { get; set; } }

	public sealed record HospitalIcuInfusionView(HospitalIcuInfusion Infusion, decimal? RateMlH, IReadOnlyList<HospitalIcuTitration> Titrations);
	public sealed record HospitalIcuDeviceView(HospitalIcuDevice Device, int Days, HospitalIcuBundleCheck? Today);
	public sealed record HospitalIcuCare(bool Ready, IReadOnlyList<HospitalIcuScore> Scores, IReadOnlyList<HospitalIcuInfusionView> Infusions, IReadOnlyList<HospitalIcuDeviceView> Devices)
	{
		public HospitalIcuScore? LatestGcs => Scores.Where(s => s.Kind == HospitalIcuScoreKind.Gcs).MaxBy(s => s.At);
		public HospitalIcuScore? LatestApache => Scores.Where(s => s.Kind == HospitalIcuScoreKind.Apache2).MaxBy(s => s.At);
	}

	// ==========================================================================================
	// HOSPITAL H238 — the ICU patient's scores, infusions and devices. The rules are HospitalIcuCareRules'. Only a patient
	// in an intensive-care bed now (HospitalIcuService.PatientsAsync) is scored, titrated or bundled; the nurse or the
	// doctor (`admit` or `consult`) records. Nothing here leaves the stock or reaches a bill.
	// ==========================================================================================
	public sealed class HospitalIcuCareService
	{
		public const string NoTables = "The intensive-care care tables are not installed yet (hospital_h238_icu_care.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalIcuService _icu;
		public HospitalIcuCareService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; _icu = new HospitalIcuService(db, access); }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private async Task<bool> CanAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
		private async Task<HospitalIcuPatient?> InIcuAsync(BusinessContext ctx, int admissionId, CancellationToken ct) =>
			(await _icu.PatientsAsync(ctx, ct)).FirstOrDefault(p => p.AdmissionId == admissionId);
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		public async Task<HospitalIcuCare> CareAsync(BusinessContext ctx, int admissionId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var scores = await _db.Set<HospitalIcuScore>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.AdmissionId == admissionId).OrderByDescending(s => s.At).Take(20).ToListAsync(ct);
				var inf = await _db.Set<HospitalIcuInfusion>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.AdmissionId == admissionId && x.StoppedAt == null).OrderBy(x => x.StartedAt).ToListAsync(ct);
				var ids = inf.Select(x => x.ID).ToList();
				var tit = await _db.Set<HospitalIcuTitration>().AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId && ids.Contains(t.InfusionId)).OrderByDescending(t => t.At).ToListAsync(ct);
				var dev = await _db.Set<HospitalIcuDevice>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.AdmissionId == admissionId && d.RemovedAt == null).OrderBy(d => d.InsertedAt).ToListAsync(ct);
				var today = Ward(nowUtc).Date;
				var dids = dev.Select(d => d.ID).ToList();
				var checks = await _db.Set<HospitalIcuBundleCheck>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && dids.Contains(c.DeviceId) && c.Day == today).ToListAsync(ct);
				return new HospitalIcuCare(true, scores,
					inf.Select(i => new HospitalIcuInfusionView(i, HospitalIcuCareRules.RateMlH(i.Dose, i.DoseUnit, i.ConcPerMl, i.WeightKg), tit.Where(t => t.InfusionId == i.ID).Take(6).ToList())).ToList(),
					dev.Select(d => new HospitalIcuDeviceView(d, HospitalIcuCareRules.DeviceDays(Ward(d.InsertedAt), null, Ward(nowUtc)), checks.FirstOrDefault(c => c.DeviceId == d.ID))).ToList());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalIcuCare(false, Array.Empty<HospitalIcuScore>(), Array.Empty<HospitalIcuInfusionView>(), Array.Empty<HospitalIcuDeviceView>());
			}
		}

		private async Task<HospitalResult> SaveAsync(object row, CancellationToken ct, Func<int> id)
		{
			try { _db.Add(row); await _db.SaveChangesAsync(ct); return HospitalResult.Success(id()); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> GcsAsync(BusinessContext ctx, HospitalIcuGcsInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await InIcuAsync(ctx, i.AdmissionId, ct);
			if (p == null) return HospitalResult.Fail("This patient is not in an intensive-care bed");
			int verbal = i.Intubated ? 1 : i.Verbal;
			if (HospitalIcuCareRules.GcsRefusal(i.Eye, verbal, i.Motor) is string why) return HospitalResult.Fail(why);
			var row = new HospitalIcuScore
			{
				CompanyId = ctx.CompanyId, AdmissionId = p.AdmissionId, PatientId = p.PatientId, Kind = HospitalIcuScoreKind.Gcs, At = nowUtc, ByEmployeeId = employeeId,
				Total = HospitalIcuCareRules.Gcs(i.Eye, verbal, i.Motor), Detail = $"E={i.Eye};V={(i.Intubated ? "T" : i.Verbal.ToString())};M={i.Motor}",
			};
			return await SaveAsync(row, ct, () => row.ID);
		}

		public async Task<HospitalResult> ApacheAsync(BusinessContext ctx, HospitalIcuApacheInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await InIcuAsync(ctx, i.AdmissionId, ct);
			if (p == null) return HospitalResult.Fail("This patient is not in an intensive-care bed");
			var v = i.Values ?? new HospitalApacheInput();
			v.Age ??= HospitalPatientService.AgeOn(p.BirthDate, nowUtc);
			if (HospitalIcuCareRules.ApacheRefusal(v) is string why) return HospitalResult.Fail(why);
			var r = HospitalIcuCareRules.Apache(v);
			var parts = r.Parts.Concat(new[] { ("Age", r.AgePoints), ("Chronic", r.ChronicPoints) });
			var row = new HospitalIcuScore { CompanyId = ctx.CompanyId, AdmissionId = p.AdmissionId, PatientId = p.PatientId, Kind = HospitalIcuScoreKind.Apache2, At = nowUtc, ByEmployeeId = employeeId, Total = r.Total, Detail = HospitalIcuCareRules.Detail(parts) };
			return await SaveAsync(row, ct, () => row.ID);
		}

		public async Task<HospitalResult> StartInfusionAsync(BusinessContext ctx, HospitalIcuInfusionInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await InIcuAsync(ctx, i.AdmissionId, ct);
			if (p == null) return HospitalResult.Fail("This patient is not in an intensive-care bed");
			if (HospitalIcuCareRules.ProtocolRefusal(i.Drug, i.DoseUnit, i.MinDose, i.MaxDose, i.MaxStep, i.Dose, i.Target) is string why) return HospitalResult.Fail(why);
			if (i.ConcPerMl is <= 0 || i.WeightKg is <= 0 or > 400) return HospitalResult.Fail("A value is outside what can be measured");
			var row = new HospitalIcuInfusion
			{
				CompanyId = ctx.CompanyId, AdmissionId = p.AdmissionId, PatientId = p.PatientId, Drug = i.Drug!.Trim(), DoseUnit = i.DoseUnit!, ConcPerMl = i.ConcPerMl, WeightKg = i.WeightKg,
				MinDose = i.MinDose, MaxDose = i.MaxDose, MaxStep = i.MaxStep, Target = i.Target!.Trim(), Dose = i.Dose, StartedAt = nowUtc, ByEmployeeId = employeeId,
			};
			return await SaveAsync(row, ct, () => row.ID);
		}

		public async Task<HospitalResult> TitrateAsync(BusinessContext ctx, int infusionId, HospitalIcuTitrateInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var inf = await _db.Set<HospitalIcuInfusion>().FirstOrDefaultAsync(x => x.ID == infusionId && x.CompanyId == ctx.CompanyId, ct);
				if (inf == null) return HospitalResult.Fail("not_found");
				if (inf.StoppedAt != null) return HospitalResult.Fail("This infusion is stopped");
				if (HospitalIcuCareRules.TitrateRefusal(inf.Dose, i.Dose, inf.MinDose, inf.MaxDose, inf.MaxStep, i.Reason) is string why) return HospitalResult.Fail(why);
				_db.Add(new HospitalIcuTitration { CompanyId = ctx.CompanyId, InfusionId = inf.ID, At = nowUtc, FromDose = inf.Dose, ToDose = i.Dose, Reason = i.Reason!.Trim()[..Math.Min(200, i.Reason.Trim().Length)], ByEmployeeId = employeeId });
				inf.Dose = i.Dose;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(inf.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> StopInfusionAsync(BusinessContext ctx, int infusionId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var inf = await _db.Set<HospitalIcuInfusion>().FirstOrDefaultAsync(x => x.ID == infusionId && x.CompanyId == ctx.CompanyId, ct);
			if (inf == null) return HospitalResult.Fail("not_found");
			if (inf.StoppedAt != null) return HospitalResult.Fail("This infusion is stopped");
			inf.StoppedAt = nowUtc; await _db.SaveChangesAsync(ct); return HospitalResult.Success(inf.ID);
		}

		public async Task<HospitalResult> InsertDeviceAsync(BusinessContext ctx, HospitalIcuDeviceInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var p = await InIcuAsync(ctx, i.AdmissionId, ct);
			if (p == null) return HospitalResult.Fail("This patient is not in an intensive-care bed");
			var insertedWard = i.InsertedAt ?? Ward(nowUtc);
			if (HospitalIcuCareRules.DeviceRefusal(i.Kind, insertedWard, Ward(nowUtc), i.Site) is string why) return HospitalResult.Fail(why);
			var row = new HospitalIcuDevice
			{
				CompanyId = ctx.CompanyId, AdmissionId = p.AdmissionId, PatientId = p.PatientId, Kind = i.Kind!, Site = string.IsNullOrWhiteSpace(i.Site) ? null : i.Site.Trim(),
				InsertedAt = i.InsertedAt is DateTime w ? HospitalNursingService.WardToUtc(w) : nowUtc, ByEmployeeId = employeeId,
			};
			return await SaveAsync(row, ct, () => row.ID);
		}

		public async Task<HospitalResult> RemoveDeviceAsync(BusinessContext ctx, int deviceId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var d = await _db.Set<HospitalIcuDevice>().FirstOrDefaultAsync(x => x.ID == deviceId && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (d.RemovedAt != null) return HospitalResult.Fail("This device is already removed");
			d.RemovedAt = nowUtc; await _db.SaveChangesAsync(ct); return HospitalResult.Success(d.ID);
		}

		/// Today's bundle check on a device in place; checking again today corrects it.
		public async Task<HospitalResult> CheckBundleAsync(BusinessContext ctx, int deviceId, HospitalIcuBundleInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var d = await _db.Set<HospitalIcuDevice>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == deviceId && x.CompanyId == ctx.CompanyId, ct);
				if (d == null) return HospitalResult.Fail("not_found");
				if (d.RemovedAt != null) return HospitalResult.Fail("This device is already removed");
				var (items, complete) = HospitalIcuCareRules.Check(d.Kind, i.Items);
				var day = Ward(nowUtc).Date;
				var row = await _db.Set<HospitalIcuBundleCheck>().FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.DeviceId == d.ID && c.Day == day, ct);
				if (row == null) _db.Add(row = new HospitalIcuBundleCheck { CompanyId = ctx.CompanyId, DeviceId = d.ID, Day = day });
				row.Items = items; row.Complete = complete; row.At = nowUtc; row.ByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
