using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H59 — death certificates (HospitalDeath). A doctor (`consult`) certifies; in the same transaction the
	// patient's file is marked deceased. An open stay is then discharged through the admissions service's own discharge
	// (its bed goes to cleaning) — after the certificate commits, so a discharge that cannot happen never loses the
	// certificate; the result says so. A void (with a reason) clears the mark when no other live certificate remains.
	// ==========================================================================================

	public sealed class HospitalDeathInput
	{
		public int PatientId { get; set; } public DateTime? DiedAt { get; set; } public string? Place { get; set; }
		public string? ImmediateCause { get; set; } public string? DueTo1 { get; set; } public string? DueTo2 { get; set; } public string? UnderlyingCause { get; set; }
		public string? OtherConditions { get; set; } public string? Manner { get; set; } public bool AutopsyRequested { get; set; }
	}

	public sealed record HospitalDeathRow(HospitalDeath Death, string Patient, string? PatientEn, string Mrn, string? Photo, string? Doctor, string? DoctorEn, string? DoctorPhoto = null);

	public static class HospitalDeathRules
	{
		/// How long after the fact the hospital may still certify (older ones are the civil registry's).
		public const int MaxDaysLate = 30;

		public static string? CertifyRefusal(HospitalDeathInput i, DateTime? alreadyDeceased, DateTime nowWard)
		{
			if (alreadyDeceased != null) return "This patient's death is already certified";
			if (i.DiedAt is not { } at) return "Give the time of death";
			if (at > nowWard.AddMinutes(5)) return "The time of death is in the future";
			if (at < nowWard.AddDays(-MaxDaysLate)) return "A death older than {0} days is certified with the civil registry";
			if (!HospitalDeathPlace.All.Contains(i.Place ?? "")) return "Choose where the death happened";
			if (string.IsNullOrWhiteSpace(i.ImmediateCause)) return "Write the immediate cause of death";
			if (!HospitalDeathManner.All.Contains(i.Manner ?? "")) return "Choose the manner of death";
			// a chain has no gaps: (c) only after (b), (d) only after (c)
			if (!string.IsNullOrWhiteSpace(i.DueTo2) && string.IsNullOrWhiteSpace(i.DueTo1)) return "Fill the causes in order, from the immediate cause down";
			if (!string.IsNullOrWhiteSpace(i.UnderlyingCause) && string.IsNullOrWhiteSpace(i.DueTo1) && !string.IsNullOrWhiteSpace(i.DueTo2)) return "Fill the causes in order, from the immediate cause down";
			foreach (var t in new[] { i.ImmediateCause, i.DueTo1, i.DueTo2, i.UnderlyingCause }) if ((t?.Length ?? 0) > 300) return "A text is too long";
			if ((i.OtherConditions?.Length ?? 0) > 500) return "A text is too long";
			return null;
		}

		/// The underlying cause the certificate states: the last cause written in Part I.
		public static string Underlying(HospitalDeath d) =>
			new[] { d.UnderlyingCause, d.DueTo2, d.DueTo1, d.ImmediateCause }.First(x => !string.IsNullOrWhiteSpace(x))!;
	}

	public sealed class HospitalDeathService
	{
		public const string NoTables = "Deaths are not installed yet (hospital_h59_deaths.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalAdmissionService _admissions;
		public HospitalDeathService(CrossDbContext db, IHospitalAccessService access, HospitalAdmissionService admissions) { _db = db; _access = access; _admissions = admissions; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalDeath> Deaths(BusinessContext ctx) => _db.Set<HospitalDeath>().Where(d => d.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		public async Task<(IReadOnlyList<HospitalDeathRow> Rows, bool Ready)> ListAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var rows = await Deaths(ctx).AsNoTracking().OrderByDescending(d => d.ID).Take(200).ToListAsync(ct);
				var pids = rows.Select(r => r.PatientId).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
				var dids = rows.Where(r => r.PractitionerId != null).Select(r => r.PractitionerId!.Value).Distinct().ToList();
				var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && dids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
				// The certifying doctor is shown by face, like the patient: the practitioner's employee record carries the photo.
				var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, docs.Values.Where(x => x.EmployeeId != null).Select(x => x.EmployeeId!.Value), ct);
				return (rows.Select(r =>
				{
					var p = pats.GetValueOrDefault(r.PatientId); var d = r.PractitionerId is int did ? docs.GetValueOrDefault(did) : null;
					return new HospitalDeathRow(r, p?.Name ?? "", p?.NameEn, p?.Mrn ?? "", p?.PhotoPath, d?.Name, d?.NameEn, d?.EmployeeId is int de ? photos.GetValueOrDefault(de) : null);
				}).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalDeathRow>(), false); }
		}

		/// Certifies a death and marks the file; then discharges an open stay. Returns the certificate's id; `Args[0]` =
		/// "discharged" / "stay-open" when there was a stay.
		public async Task<HospitalResult> CertifyAsync(BusinessContext ctx, HospitalDeathInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.FirstOrDefaultAsync(x => x.ID == input.PatientId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			var nowWard = HospitalNursingService.UtcToWard(nowUtc);
			if (HospitalDeathRules.CertifyRefusal(input, p.DeceasedAt, nowWard) is string why) return HospitalResult.Fail(why, HospitalDeathRules.MaxDaysLate.ToString());
			var diedUtc = HospitalNursingService.WardToUtc(input.DiedAt!.Value);
			var admission = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == p.ID && a.Status == HospitalAdmissionStatus.Active).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			var doctor = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.EmployeeId == me && x.IsActive).Select(x => (int?)x.ID).FirstOrDefaultAsync(ct);
			int certId;
			try
			{
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				HospitalDeath? row = null;
				for (int attempt = 0; attempt < 3 && row == null; attempt++)
				{
					var nos = await Deaths(ctx).Select(d => d.DeathNo).ToListAsync(ct);
					var r = new HospitalDeath
					{
						CompanyId = ctx.CompanyId, DeathNo = HospitalDentalPracticeRules.NextNo(nos, "DT-", 6), PatientId = p.ID, AdmissionId = admission, DiedAt = diedUtc, Place = input.Place!,
						ImmediateCause = Clip(input.ImmediateCause, 300)!, DueTo1 = Clip(input.DueTo1, 300), DueTo2 = Clip(input.DueTo2, 300), UnderlyingCause = Clip(input.UnderlyingCause, 300),
						OtherConditions = Clip(input.OtherConditions, 500), Manner = input.Manner!, AutopsyRequested = input.AutopsyRequested, PractitionerId = doctor,
						CertifiedAt = nowUtc, CertifiedByEmployeeId = me,
					};
					_db.Add(r);
					p.DeceasedAt = diedUtc; p.UpdatedAt = nowUtc; p.UpdatedByEmployeeId = me;
					try { await _db.SaveChangesAsync(ct); row = r; }
					catch (DbUpdateException)
					{
						_db.Entry(r).State = EntityState.Detached;
						// another doctor certified this patient a moment ago: the unique live certificate refuses the second
						if (await Deaths(ctx).AnyAsync(d => d.PatientId == p.ID && d.VoidedAt == null, ct)) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("This patient's death is already certified"); }
					}
				}
				if (row == null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
				await tx.CommitAsync();
				certId = row.ID;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			if (admission is int aid)
			{
				var d = await _admissions.DischargeAsync(ctx, aid, employeeId, diedUtc, ct);
				return new HospitalResult(true, null, certId, new object[] { d.Ok ? "discharged" : "stay-open" });
			}
			return HospitalResult.Success(certId);
		}

		public async Task<HospitalResult> VoidAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write the reason");
			var why = Clip(reason, 300);
			try
			{
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				var d = await Deaths(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (d == null) return HospitalResult.Fail("not_found");
				int n = await Deaths(ctx).Where(x => x.ID == id && x.VoidedAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.VoidedAt, nowUtc)
					.SetProperty(x => x.VoidedByEmployeeId, employeeId).SetProperty(x => x.VoidReason, why), ct);
				if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This certificate is already void"); }
				// the file is no longer marked deceased unless another live certificate says so
				if (!await Deaths(ctx).AnyAsync(x => x.PatientId == d.PatientId && x.VoidedAt == null, ct))
					await _db.HospitalPatients.Where(x => x.ID == d.PatientId && x.CompanyId == ctx.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.DeceasedAt, (DateTime?)null), ct);
				await tx.CommitAsync();
				return HospitalResult.Success(id);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return HospitalResult.Fail(NoTables); }
		}
	}
}
