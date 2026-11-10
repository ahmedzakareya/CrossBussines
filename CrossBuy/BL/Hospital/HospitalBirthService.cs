using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H58 — births (HospitalBirth). The ward (`admit`) registers a birth on the mother's file; a LIVE birth
	// opens the newborn's file through the patient registry's own save (its rules, its MRN), in the same transaction,
	// named after the mother until the family names the child. A wrong registration is voided with a reason (`consult`
	// or `manage`); the newborn's file stays (it may already carry care).
	// ==========================================================================================

	public sealed class HospitalBirthInput
	{
		public int MotherPatientId { get; set; } public DateTime? BornAt { get; set; } public string? Sex { get; set; } public string? Outcome { get; set; }
		public string? DeliveryMode { get; set; } public int? GestationWeeks { get; set; } public int BirthOrder { get; set; } = 1; public int WeightGrams { get; set; }
		public decimal? LengthCm { get; set; } public decimal? HeadCm { get; set; } public int? Apgar1 { get; set; } public int? Apgar5 { get; set; } public string? Note { get; set; }
	}

	public sealed record HospitalBirthRow(HospitalBirth Birth, string Mother, string? MotherEn, string MotherMrn, string? Newborn, string? NewbornEn, string? NewbornMrn, string? Attendant, string? AttendantEn);

	public static class HospitalBirthRules
	{
		/// How long after the fact a birth may still be registered here (older ones are the civil registry's).
		public const int MaxDaysLate = 30;

		public static string? RegisterRefusal(HospitalBirthInput i, string? motherSex, DateTime nowWard)
		{
			if (motherSex != HospitalPatientSex.Female) return "The mother's file must be a woman's";
			if (i.BornAt is not { } at) return "Give the time of birth";
			if (at > nowWard.AddMinutes(5)) return "The time of birth is in the future";
			if (at < nowWard.AddDays(-MaxDaysLate)) return "A birth older than {0} days is registered with the civil registry";
			if (!HospitalPatientSex.IsKnown(i.Sex)) return "Choose the baby's sex";
			if (!HospitalBirthOutcome.All.Contains(i.Outcome ?? "")) return "Choose live birth or stillbirth";
			if (!HospitalDeliveryMode.All.Contains(i.DeliveryMode ?? "")) return "Choose how the baby was delivered";
			if (i.WeightGrams is < 200 or > 7000) return "The weight is 200 to 7000 grams";
			if (i.GestationWeeks is < 20 or > 45) return "The gestation is 20 to 45 weeks";
			if (i.BirthOrder is < 1 or > 6) return "The birth order is 1 to 6";
			if (i.Apgar1 is < 0 or > 10 || i.Apgar5 is < 0 or > 10) return "An Apgar score is 0 to 10";
			if (i.LengthCm is < 15 or > 70 || i.HeadCm is < 15 or > 50) return "A measure is out of range";
			if ((i.Note?.Length ?? 0) > 500) return "A text is too long";
			return null;
		}

		/// The newborn's file is named after the mother until the family names the child.
		public static (string Ar, string En) NewbornName(string motherAr, string? motherEn, int order) =>
			("مولود " + motherAr + (order > 1 ? " (" + order + ")" : ""), "Baby of " + (string.IsNullOrWhiteSpace(motherEn) ? motherAr : motherEn) + (order > 1 ? " (" + order + ")" : ""));
	}

	public sealed class HospitalBirthService
	{
		public const string NoTables = "Births are not installed yet (hospital_h58_births.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPatientService _patients;
		public HospitalBirthService(CrossDbContext db, IHospitalAccessService access, HospitalPatientService patients) { _db = db; _access = access; _patients = patients; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalBirth> Births(BusinessContext ctx) => _db.Set<HospitalBirth>().Where(b => b.CompanyId == ctx.CompanyId);

		public async Task<(IReadOnlyList<HospitalBirthRow> Rows, bool Ready)> ListAsync(BusinessContext ctx, int? patientId = null, CancellationToken ct = default)
		{
			try
			{
				var q = Births(ctx).AsNoTracking();
				if (patientId is int pid) q = q.Where(b => b.MotherPatientId == pid || b.NewbornPatientId == pid);
				var rows = await q.OrderByDescending(b => b.ID).Take(200).ToListAsync(ct);
				var pids = rows.Select(r => r.MotherPatientId).Concat(rows.Where(r => r.NewbornPatientId != null).Select(r => r.NewbornPatientId!.Value)).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
				var dids = rows.Where(r => r.AttendantPractitionerId != null).Select(r => r.AttendantPractitionerId!.Value).Distinct().ToList();
				var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && dids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
				return (rows.Select(r =>
				{
					var m = pats.GetValueOrDefault(r.MotherPatientId); var n = r.NewbornPatientId is int nid ? pats.GetValueOrDefault(nid) : null;
					var d = r.AttendantPractitionerId is int did ? docs.GetValueOrDefault(did) : null;
					return new HospitalBirthRow(r, m?.Name ?? "", m?.NameEn, m?.Mrn ?? "", n?.Name, n?.NameEn, n?.Mrn, d?.Name, d?.NameEn);
				}).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalBirthRow>(), false); }
		}

		/// Registers a birth; a live one opens the newborn's file. One transaction. Returns the birth's id.
		public async Task<HospitalResult> RegisterAsync(BusinessContext ctx, HospitalBirthInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var mother = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.MotherPatientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			if (mother == null) return HospitalResult.Fail("not_found");
			var nowWard = HospitalNursingService.UtcToWard(nowUtc);
			if (HospitalBirthRules.RegisterRefusal(input, mother.Sex, nowWard) is string why)
				return HospitalResult.Fail(why, HospitalBirthRules.MaxDaysLate.ToString());
			var bornUtc = HospitalNursingService.WardToUtc(input.BornAt!.Value);
			var admission = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == mother.ID && a.Status == HospitalAdmissionStatus.Active).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			var attendant = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == me && p.IsActive).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct);
			try
			{
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				int? newborn = null;
				if (input.Outcome == HospitalBirthOutcome.Live)
				{
					var (ar, en) = HospitalBirthRules.NewbornName(mother.Name, mother.NameEn, input.BirthOrder);
					var made = await _patients.SaveAsync(ctx, new HospitalPatientInput
					{
						Name = ar, NameEn = en, Sex = input.Sex!, BirthDate = input.BornAt.Value.Date, NationalityId = mother.NationalityId, Phone = mother.Phone,
						EmergencyName = mother.Name, EmergencyPhone = mother.Phone, PriceListId = mother.PriceListId,
					}, employeeId, nowWard, ct);
					if (!made.Ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return made; }
					newborn = made.Id;
				}
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Births(ctx).Select(b => b.BirthNo).ToListAsync(ct);
					var row = new HospitalBirth
					{
						CompanyId = ctx.CompanyId, BirthNo = HospitalDentalPracticeRules.NextNo(nos, "BR-", 6), MotherPatientId = mother.ID, MotherAdmissionId = admission, NewbornPatientId = newborn,
						BornAt = bornUtc, Sex = input.Sex!, Outcome = input.Outcome!, DeliveryMode = input.DeliveryMode!, GestationWeeks = input.GestationWeeks, BirthOrder = input.BirthOrder,
						WeightGrams = input.WeightGrams, LengthCm = input.LengthCm, HeadCm = input.HeadCm, Apgar1 = input.Apgar1, Apgar5 = input.Apgar5, AttendantPractitionerId = attendant,
						Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), RegisteredAt = nowUtc, RegisteredByEmployeeId = me,
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); await tx.CommitAsync(); return HospitalResult.Success(row.ID); }
					catch (DbUpdateException) { _db.Entry(row).State = EntityState.Detached; }   // the number was taken a moment ago
				}
				await tx.RollbackAsync(); _db.ChangeTracker.Clear();
				return HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> VoidAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write the reason");
			try
			{
				var why = reason.Trim()[..Math.Min(300, reason.Trim().Length)];
				int n = await Births(ctx).Where(b => b.ID == id && b.VoidedAt == null).ExecuteUpdateAsync(u => u.SetProperty(b => b.VoidedAt, nowUtc)
					.SetProperty(b => b.VoidedByEmployeeId, employeeId).SetProperty(b => b.VoidReason, why), ct);
				return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This registration is already void");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return HospitalResult.Fail(NoTables); }
		}
	}
}
