using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H92 — the unidentified patient. Registering opens a REAL file through the registry's own save (so it has
	// an MRN and can carry orders, medicines, blood, an admission, a bill) under a temporary name nobody can mistake
	// for a person, and — when asked — the emergency visit on it in the same step. Identifying settles it: the file is
	// renamed to the person (nobody had one), or merged into the person's own file through H57 (everything moves).
	// ==========================================================================================
	public sealed class HospitalUnidentifiedInput
	{
		public string? Sex { get; set; }
		public int EstimatedAge { get; set; }
		public string? Description { get; set; }
		public string? BroughtBy { get; set; }
		public bool MedicoLegal { get; set; }
		public string? PoliceRef { get; set; }
		/// Also open the emergency visit on the new file.
		public bool OpenEmergency { get; set; } = true;
		public string? ArrivalMode { get; set; }
		public string? Complaint { get; set; }
		public int? Esi { get; set; }
	}

	public sealed class HospitalIdentifyInput
	{
		/// Merge into this existing file; null = rename the temporary file to the person.
		public int? ExistingPatientId { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string? Sex { get; set; }
		public DateTime? BirthDate { get; set; }
		public string? NationalId { get; set; }
		public string? Phone { get; set; }
		/// Who identified the patient, and how (a relative with an ID card, the police, a fingerprint…).
		public string? How { get; set; }
	}

	public sealed record HospitalUnidentifiedRow(HospitalUnidentified Row, string Name, string Mrn, int Hours, bool InEmergency);

	public static class HospitalUnidentifiedRules
	{
		public static (string Ar, string En) TempName(string sex, DateTime day, int seq) =>
			($"مجهول {(sex == HospitalPatientSex.Female ? "أنثى" : "ذكر")} {day:yyyy-MM-dd} رقم {seq:00}", $"Unknown {(sex == HospitalPatientSex.Female ? "female" : "male")} {day:yyyy-MM-dd} #{seq:00}");

		/// An estimated age stands as 1 January of the year it gives — the registration convention for an unknown date.
		public static DateTime EstimatedBirth(int age, DateTime today) => new(today.Year - age, 1, 1);

		public static string? RegisterRefusal(HospitalUnidentifiedInput i)
		{
			if (!HospitalPatientSex.IsKnown(i.Sex)) return "Choose the sex";
			if (i.EstimatedAge is < 0 or > 120) return "The estimated age is between 0 and 120";
			if (string.IsNullOrWhiteSpace(i.Description) || i.Description.Trim().Length < 10) return "Describe the patient: build, marks, clothing, where found";
			if (i.OpenEmergency)
			{
				if (!HospitalErArrival.All.Contains(i.ArrivalMode ?? "")) return "Choose how the patient came";
				if (string.IsNullOrWhiteSpace(i.Complaint)) return "Write why the patient is here";
				if (i.Esi is < 1 or > 5) return "The triage level is between 1 and 5";
			}
			return null;
		}

		public static string? IdentifyRefusal(HospitalUnidentified u, HospitalIdentifyInput i, DateTime today)
		{
			if (u.IdentifiedAt != null) return "This patient is already identified";
			if (string.IsNullOrWhiteSpace(i.How)) return "Write who identified the patient, and how";
			if (i.ExistingPatientId != null) return i.ExistingPatientId == u.PatientId ? "Choose the person's own file, not the temporary one" : null;
			if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.NameEn)) return "Write the real name, in Arabic and in English";
			if (i.Name.Contains("مجهول") || i.NameEn.Contains("Unknown", StringComparison.OrdinalIgnoreCase)) return "Write the real name, in Arabic and in English";
			if (!HospitalPatientSex.IsKnown(i.Sex)) return "Choose the sex";
			if (i.BirthDate is DateTime b && (b.Date > today || b.Date < today.AddYears(-130))) return "Enter a real date of birth";
			return null;
		}
	}

	public sealed class HospitalUnidentifiedService
	{
		public const string NoTables = "Unidentified-patient registration is not installed yet (hospital_h92_unidentified.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalUnidentifiedService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private IQueryable<HospitalUnidentified> Rows(BusinessContext ctx) => _db.Set<HospitalUnidentified>().Where(x => x.CompanyId == ctx.CompanyId);

		public Task<bool> CanRegisterAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Register, null, ct);

		/// Opens the file (and the emergency visit). Returns the PATIENT id; Args[0] the emergency visit id or null, Args[1]
		/// the emergency's refusal when the file was made but the visit was not.
		public async Task<HospitalResult> RegisterAsync(BusinessContext ctx, HospitalUnidentifiedInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanRegisterAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalUnidentifiedRules.RegisterRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				var today = Today(nowUtc);
				int seq = (await Rows(ctx).Where(x => x.ArrivedOn == today).Select(x => (int?)x.Seq).MaxAsync(ct) ?? 0) + 1;
				var (ar, en) = HospitalUnidentifiedRules.TempName(input.Sex!, today, seq);
				var saved = await new HospitalPatientService(_db, _access).SaveAsync(ctx, new HospitalPatientInput
				{
					Name = ar, NameEn = en, Sex = input.Sex!, BirthDate = HospitalUnidentifiedRules.EstimatedBirth(input.EstimatedAge, today),
				}, employeeId, nowUtc, ct);
				if (!saved.Ok || saved.Id is not int pid) return saved;
				_db.Add(new HospitalUnidentified
				{
					CompanyId = ctx.CompanyId, PatientId = pid, Seq = seq, ArrivedOn = today, EstimatedAge = input.EstimatedAge, Description = Clip(input.Description, 500)!,
					BroughtBy = Clip(input.BroughtBy, 150), MedicoLegal = input.MedicoLegal, PoliceRef = Clip(input.PoliceRef, 60), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				});
				await _db.SaveChangesAsync(ct);
				if (!input.OpenEmergency) return new HospitalResult(true, null, pid, new object?[] { null, null }!);
				var er = await new HospitalEmergencyService(_db, _access).ArriveAsync(ctx, new HospitalErArrivalInput
				{ PatientId = pid, ArrivalMode = input.ArrivalMode!, Complaint = input.Complaint!.Trim(), Esi = input.Esi }, employeeId, nowUtc, ct);
				return new HospitalResult(true, null, pid, new object?[] { er.Ok ? er.Id : null, er.Ok ? null : er.Error }!);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Settles the identity: the temporary file renamed to the person, or merged into the person's own file.
		public async Task<HospitalResult> IdentifyAsync(BusinessContext ctx, int id, HospitalIdentifyInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanRegisterAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var u = await Rows(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (u == null) return HospitalResult.Fail("not_found");
				if (HospitalUnidentifiedRules.IdentifyRefusal(u, input, Today(nowUtc)) is string why) return HospitalResult.Fail(why);
				int temp = u.PatientId;
				if (input.ExistingPatientId is int keep)
				{
					// H57 does the move: every record follows, the duplicate is closed and points at the survivor
					var merged = await new HospitalPatientMergeService(_db, _access).MergeAsync(ctx, keep, temp, "Unidentified patient identified: " + input.How!.Trim(), employeeId, nowUtc, ct);
					if (!merged.Ok) return merged;
					_db.ChangeTracker.Clear();
					u = await Rows(ctx).FirstAsync(x => x.ID == id, ct);
					u.Resolution = HospitalUnidentifiedResolution.Merged; u.MergedIntoPatientId = keep;
				}
				else
				{
					var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == temp, ct);
					var saved = await new HospitalPatientService(_db, _access).SaveAsync(ctx, new HospitalPatientInput
					{
						Id = p.ID, Name = input.Name!.Trim(), NameEn = input.NameEn!.Trim(), Sex = input.Sex!, BirthDate = input.BirthDate ?? p.BirthDate, NationalId = input.NationalId,
						NationalityId = p.NationalityId, Phone = input.Phone ?? p.Phone, BloodGroup = p.BloodGroup, Allergies = p.Allergies, ChronicConditions = p.ChronicConditions,
						EmergencyName = p.EmergencyName, EmergencyRelation = p.EmergencyRelation, EmergencyPhone = p.EmergencyPhone, ConsentSigned = p.ConsentSigned, PriceListId = p.PriceListId,
					}, employeeId, nowUtc, ct);
					if (!saved.Ok) return saved;
					u.Resolution = HospitalUnidentifiedResolution.Renamed;
				}
				u.IdentifiedAt = nowUtc; u.IdentifiedByEmployeeId = employeeId; u.IdentifiedHow = Clip(input.How, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(input.ExistingPatientId ?? temp);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The temporary identities, the unsettled first (the longest waiting at the top), searched and paged.
		public async Task<(IReadOnlyList<HospitalUnidentifiedRow> Rows, int Total, bool Ready, int Open)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var query = from u in Rows(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on (u.MergedIntoPatientId ?? u.PatientId) equals p.ID
							select new { u, p.Name, p.Mrn };
				query = filter == "done" ? query.Where(x => x.u.IdentifiedAt != null) : query.Where(x => x.u.IdentifiedAt == null);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.Name.Contains(s) || x.Mrn.Contains(s) || x.u.Description.Contains(s) || (x.u.PoliceRef != null && x.u.PoliceRef.Contains(s))); }
				int total = await query.CountAsync(ct);
				int open = await Rows(ctx).CountAsync(x => x.IdentifiedAt == null, ct);
				var list = await (filter == "done" ? query.OrderByDescending(x => x.u.IdentifiedAt) : query.OrderBy(x => x.u.CreatedAt)).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var pids = list.Select(x => x.u.MergedIntoPatientId ?? x.u.PatientId).ToList();
				var inEr = await _db.Set<HospitalErVisit>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && v.PatientId != null && pids.Contains(v.PatientId.Value) && v.Stage != HospitalErStage.Closed)
					.Select(v => v.PatientId!.Value).ToListAsync(ct);
				return (list.Select(x => new HospitalUnidentifiedRow(x.u, x.Name, x.Mrn, (int)Math.Floor(((x.u.IdentifiedAt ?? nowUtc) - x.u.CreatedAt).TotalHours), inEr.Contains(x.u.MergedIntoPatientId ?? x.u.PatientId))).ToList(), total, true, open);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalUnidentifiedRow>(), 0, false, 0); }
		}
	}
}
