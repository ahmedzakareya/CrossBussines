using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H209 — notifiable diseases (Kuwait: Ministry of Health, Public Health Department). A diagnosis on a
	// SIGNED visit whose ICD-10 code falls under a disease on the hospital's list becomes a CASE TO REPORT, due within
	// 24 hours (group A) or 7 days (group B) of the diagnosis. It closes when the Ministry's reference is recorded, or is
	// dismissed with its reason (the diagnosis was revised; another facility reported it). An open case past its time
	// is OVERDUE. Nothing is reported by hand from memory: the sweep finds every case on each read of the register.
	//
	// The list is seeded once per company with the Ministry's groups (an administrator may add, regroup or switch off a
	// disease); the duty starts the day the list is set up — visits signed before it are not swept into the register.
	// ==========================================================================================
	public sealed record HospitalDiseaseReportRow(HospitalDiseaseReport Report, string Patient, string PatientEn, string Mrn, HospitalFace? Doctor,
		string Disease, string? DiseaseEn, string Urgency, bool Overdue, string? PatientPhoto = null);

	public sealed class HospitalNotifiableInput
	{
		public string? CodePrefix { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string? Urgency { get; set; }
	}

	public static class HospitalNotifiableRules
	{
		public const int SweepDays = 30;

		/// The Ministry's list the hospital starts with (ICD-10 prefixes; the most specific prefix wins).
		public static readonly IReadOnlyList<(string Prefix, string Ar, string En, string Urgency)> Defaults = new[]
		{
			("A00", "الكوليرا", "Cholera", HospitalNotifiableUrgency.Immediate),
			("A01", "التيفوئيد ونظيرة التيفوئيد", "Typhoid and paratyphoid fever", HospitalNotifiableUrgency.Immediate),
			("A05.1", "التسمم الوشيقي", "Botulism", HospitalNotifiableUrgency.Immediate),
			("A20", "الطاعون", "Plague", HospitalNotifiableUrgency.Immediate),
			("A22", "الجمرة الخبيثة", "Anthrax", HospitalNotifiableUrgency.Immediate),
			("A33", "كزاز الوليد", "Neonatal tetanus", HospitalNotifiableUrgency.Immediate),
			("A36", "الدفتيريا", "Diphtheria", HospitalNotifiableUrgency.Immediate),
			("A39", "عدوى المكورات السحائية", "Meningococcal infection", HospitalNotifiableUrgency.Immediate),
			("A80", "شلل الأطفال", "Poliomyelitis", HospitalNotifiableUrgency.Immediate),
			("A82", "داء الكلب", "Rabies", HospitalNotifiableUrgency.Immediate),
			("A95", "الحمى الصفراء", "Yellow fever", HospitalNotifiableUrgency.Immediate),
			("A98", "الحميات النزفية الفيروسية", "Viral haemorrhagic fever", HospitalNotifiableUrgency.Immediate),
			("B05", "الحصبة", "Measles", HospitalNotifiableUrgency.Immediate),
			("J09", "إنفلونزا الطيور", "Avian influenza", HospitalNotifiableUrgency.Immediate),
			("U07", "كوفيد-19", "COVID-19", HospitalNotifiableUrgency.Immediate),
			("A02", "داء السلمونيلات", "Salmonellosis", HospitalNotifiableUrgency.Weekly),
			("A03", "داء الشيغيلات", "Shigellosis", HospitalNotifiableUrgency.Weekly),
			("A05", "التسمم الغذائي", "Food poisoning", HospitalNotifiableUrgency.Weekly),
			("A15", "السل الرئوي", "Pulmonary tuberculosis", HospitalNotifiableUrgency.Weekly),
			("A16", "السل الرئوي", "Pulmonary tuberculosis", HospitalNotifiableUrgency.Weekly),
			("A17", "سل الجهاز العصبي", "Tuberculosis of the nervous system", HospitalNotifiableUrgency.Weekly),
			("A18", "السل خارج الرئة", "Extrapulmonary tuberculosis", HospitalNotifiableUrgency.Weekly),
			("A19", "السل الدخني", "Miliary tuberculosis", HospitalNotifiableUrgency.Weekly),
			("A23", "داء البروسيلات", "Brucellosis", HospitalNotifiableUrgency.Weekly),
			("A30", "الجذام", "Leprosy", HospitalNotifiableUrgency.Weekly),
			("A35", "الكزاز", "Tetanus", HospitalNotifiableUrgency.Weekly),
			("A37", "السعال الديكي", "Pertussis", HospitalNotifiableUrgency.Weekly),
			("A48.1", "داء الفيالقة", "Legionnaires' disease", HospitalNotifiableUrgency.Weekly),
			("A51", "الزهري المبكر", "Early syphilis", HospitalNotifiableUrgency.Weekly),
			("A54", "السيلان", "Gonorrhoea", HospitalNotifiableUrgency.Weekly),
			("A90", "حمى الضنك", "Dengue fever", HospitalNotifiableUrgency.Weekly),
			("A97", "حمى الضنك", "Dengue", HospitalNotifiableUrgency.Weekly),
			("B01", "جدري الماء", "Chickenpox", HospitalNotifiableUrgency.Weekly),
			("B06", "الحصبة الألمانية", "Rubella", HospitalNotifiableUrgency.Weekly),
			("B15", "التهاب الكبد الفيروسي أ", "Hepatitis A", HospitalNotifiableUrgency.Weekly),
			("B16", "التهاب الكبد الفيروسي ب", "Hepatitis B", HospitalNotifiableUrgency.Weekly),
			("B17.1", "التهاب الكبد الفيروسي ج", "Hepatitis C", HospitalNotifiableUrgency.Weekly),
			("B17.2", "التهاب الكبد الفيروسي هـ", "Hepatitis E", HospitalNotifiableUrgency.Weekly),
			("B20", "فيروس نقص المناعة البشرية", "HIV disease", HospitalNotifiableUrgency.Weekly),
			("B26", "النكاف", "Mumps", HospitalNotifiableUrgency.Weekly),
			("B50", "الملاريا", "Malaria", HospitalNotifiableUrgency.Weekly),
			("B51", "الملاريا", "Malaria", HospitalNotifiableUrgency.Weekly),
			("B54", "الملاريا", "Malaria", HospitalNotifiableUrgency.Weekly),
			("B55", "داء الليشمانيات", "Leishmaniasis", HospitalNotifiableUrgency.Weekly),
		};

		/// A code without its dot, upper case: "a05.1" → "A051".
		public static string Norm(string? code) => (code ?? "").Replace(".", "").Replace(" ", "").Trim().ToUpperInvariant();

		/// The disease a diagnosis code falls under — the most specific prefix wins (A05.1 botulism before A05 food poisoning).
		public static T? Match<T>(string? code, IEnumerable<T> diseases, Func<T, string> prefix) where T : class
		{
			var c = Norm(code);
			if (c.Length == 0) return null;
			return diseases.Where(d => Norm(prefix(d)).Length > 0 && c.StartsWith(Norm(prefix(d)), StringComparison.Ordinal))
				.OrderByDescending(d => Norm(prefix(d)).Length).FirstOrDefault();
		}

		public static DateTime DueAt(string urgency, DateTime diagnosedUtc) =>
			urgency == HospitalNotifiableUrgency.Weekly ? diagnosedUtc.AddDays(7) : diagnosedUtc.AddHours(24);

		public static bool Overdue(HospitalDiseaseReport r, DateTime nowUtc) => r.Status == HospitalDiseaseReportStatus.Due && nowUtc > r.DueAt;

		public static string? CloseRefusal(HospitalDiseaseReport r, string to, string? text)
		{
			if (r.Status != HospitalDiseaseReportStatus.Due) return "This case is closed";
			if (to == HospitalDiseaseReportStatus.Reported)
				return string.IsNullOrWhiteSpace(text) ? "Write the Ministry's reference for this report" : text.Trim().Length > 60 ? "The reference is too long" : null;
			if (to == HospitalDiseaseReportStatus.Dismissed)
				return string.IsNullOrWhiteSpace(text) ? "Write why this is not a case to report" : text.Trim().Length > 500 ? "The text is too long" : null;
			return "not_found";
		}

		public static string? DiseaseRefusal(HospitalNotifiableInput i)
		{
			var p = Norm(i.CodePrefix);
			if (p.Length < 3 || p.Length > 8 || !char.IsLetter(p[0]) || !p.Skip(1).All(char.IsLetterOrDigit)) return "Write the ICD-10 code the disease starts with (e.g. A00 or A05.1)";
			if (string.IsNullOrWhiteSpace(i.Name)) return "Write the disease's name";
			if (i.Name.Trim().Length > 200 || (i.NameEn?.Trim().Length ?? 0) > 200) return "The text is too long";
			return string.IsNullOrEmpty(i.Urgency) || !HospitalNotifiableUrgency.All.Contains(i.Urgency) ? "Choose how fast the Ministry must be told" : null;
		}
	}

	public sealed class HospitalNotifiableService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalNotifiableService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalDiseaseReport> Reports(BusinessContext ctx) => _db.Set<HospitalDiseaseReport>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalNotifiableDisease> Diseases(BusinessContext ctx) => _db.Set<HospitalNotifiableDisease>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		/// The company's list, seeded with the Ministry's on first use.
		public async Task EnsureCatalogueAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			if (await Diseases(ctx).AnyAsync(ct)) return;
			foreach (var d in HospitalNotifiableRules.Defaults)
				_db.Add(new HospitalNotifiableDisease { CompanyId = ctx.CompanyId, CodePrefix = d.Prefix, Name = d.Ar, NameEn = d.En, Urgency = d.Urgency, IsActive = true, CreatedAt = nowUtc });
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); }   // another request seeded it first
		}

		/// Finds every notifiable diagnosis on a visit signed since the list was set up (and within the last 30 days)
		/// that has no case yet, and opens its case. Returns how many were opened.
		public async Task<int> SweepAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var diseases = await Diseases(ctx).AsNoTracking().Where(d => d.IsActive).ToListAsync(ct);
			if (diseases.Count == 0) return 0;
			var floor = await Diseases(ctx).MinAsync(d => d.CreatedAt, ct);
			var window = nowUtc.AddDays(-HospitalNotifiableRules.SweepDays);
			if (floor < window) floor = window;
			var found = await (from e in _db.HospitalEncounters.AsNoTracking()
							   join g in _db.HospitalEncounterDiagnoses.AsNoTracking() on e.ID equals g.EncounterId
							   where e.CompanyId == ctx.CompanyId && g.CompanyId == ctx.CompanyId && e.Status == HospitalEncounterStatus.Signed && e.SignedAt >= floor
							   select new { e.ID, e.PatientId, e.PractitionerId, e.SignedAt, g.Code, g.Name, g.NameEn }).ToListAsync(ct);
			if (found.Count == 0) return 0;
			var encIds = found.Select(f => f.ID).Distinct().ToList();
			var have = (await Reports(ctx).AsNoTracking().Where(r => encIds.Contains(r.EncounterId)).Select(r => new { r.EncounterId, r.DiseaseId }).ToListAsync(ct))
				.Select(x => (x.EncounterId, x.DiseaseId)).ToHashSet();
			var nos = await Reports(ctx).Where(r => r.ReportNo.StartsWith("NTF-")).Select(r => r.ReportNo).ToListAsync(ct);
			int n = 0; foreach (var no in nos) if (int.TryParse(no.AsSpan(4), out var v) && v > n) n = v;
			int opened = 0;
			foreach (var f in found)
			{
				var d = HospitalNotifiableRules.Match(f.Code, diseases, x => x.CodePrefix);
				if (d == null || !have.Add((f.ID, d.ID))) continue;
				var at = f.SignedAt ?? nowUtc;
				_db.Add(new HospitalDiseaseReport
				{
					CompanyId = ctx.CompanyId, ReportNo = "NTF-" + (++n).ToString("000000"), PatientId = f.PatientId, EncounterId = f.ID, DiseaseId = d.ID,
					Code = f.Code, DiagnosisName = f.Name, DiagnosisNameEn = f.NameEn, PractitionerId = f.PractitionerId, DiagnosedAt = at,
					DueAt = HospitalNotifiableRules.DueAt(d.Urgency, at), Status = HospitalDiseaseReportStatus.Due, CreatedAt = nowUtc,
				});
				opened++;
			}
			if (opened == 0) return 0;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return 0; }   // a concurrent read opened them first
			return opened;
		}

		/// Reported (with the Ministry's reference) or dismissed (with the reason) — only the first close lands.
		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, string to, string? text, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Reports(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (HospitalNotifiableRules.CloseRefusal(r, to, text) is string why) return HospitalResult.Fail(why);
			var q = Reports(ctx).Where(x => x.ID == id && x.Status == HospitalDiseaseReportStatus.Due);
			int n = to == HospitalDiseaseReportStatus.Reported
				? await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.MohRef, Clip(text, 60)).SetProperty(x => x.ClosedAt, nowUtc).SetProperty(x => x.ClosedByEmployeeId, employeeId), ct)
				: await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.DismissReason, Clip(text, 500)).SetProperty(x => x.ClosedAt, nowUtc).SetProperty(x => x.ClosedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("Someone else closed this case: open it again");
		}

		public async Task<(IReadOnlyList<HospitalDiseaseReportRow> Rows, int Total, int Due, int Overdue, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? status, bool overdueOnly,
			int? patientId, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalDiseaseReportRow>(), 0, 0, 0, true);
			try
			{
				await EnsureCatalogueAsync(ctx, nowUtc, ct);
				await SweepAsync(ctx, nowUtc, ct);
				var query = from r in Reports(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
							join d in _db.Set<HospitalNotifiableDisease>().AsNoTracking() on r.DiseaseId equals d.ID
							join x0 in _db.HospitalPractitioners.AsNoTracking() on r.PractitionerId equals x0.ID into xx
							from x in xx.DefaultIfEmpty()
							where p.CompanyId == ctx.CompanyId
							select new { r, p, d, x };
				if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.r.Status == status);
				if (patientId is int pid) query = query.Where(x => x.r.PatientId == pid);
				if (overdueOnly) query = query.Where(x => x.r.Status == HospitalDiseaseReportStatus.Due && x.r.DueAt < nowUtc);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var s = q.Trim();
					query = query.Where(x => x.r.ReportNo.Contains(s) || x.r.Code.Contains(s) || x.p.Name.Contains(s) || x.p.NameEn.Contains(s) || x.p.Mrn.Contains(s)
						|| x.d.Name.Contains(s) || (x.d.NameEn != null && x.d.NameEn.Contains(s)) || (x.r.MohRef != null && x.r.MohRef.Contains(s)));
				}
				int total = await query.CountAsync(ct);
				var due = Reports(ctx).Where(r => r.Status == HospitalDiseaseReportStatus.Due);
				int dueN = await due.CountAsync(ct), lateN = await due.CountAsync(r => r.DueAt < nowUtc, ct);
				page = Math.Max(1, page);
				var rows = await query.OrderByDescending(x => x.r.Status == HospitalDiseaseReportStatus.Due).ThenBy(x => x.r.DueAt).ThenByDescending(x => x.r.ID)
					.Skip((page - 1) * size).Take(size).ToListAsync(ct);
				var docs = await HospitalFaces.OfPractitionersAsync(_db, ctx.CompanyId, rows.Select(x => x.r.PractitionerId), ct);
				return (rows.Select(x => new HospitalDiseaseReportRow(x.r, x.p.Name, x.p.NameEn, x.p.Mrn, x.r.PractitionerId is int pr ? docs.GetValueOrDefault(pr) : null, x.d.Name, x.d.NameEn, x.d.Urgency,
					HospitalNotifiableRules.Overdue(x.r, nowUtc)) { PatientPhoto = x.p.PhotoPath }).ToList(), total, dueN, lateN, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalDiseaseReportRow>(), 0, 0, 0, false); }
		}

		// ---- the list (an administrator's) ----

		public async Task<IReadOnlyList<HospitalNotifiableDisease>> DiseasesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return await Diseases(ctx).AsNoTracking().OrderBy(d => d.Urgency).ThenBy(d => d.CodePrefix).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalNotifiableDisease>(); }
		}

		public async Task<HospitalResult> SaveDiseaseAsync(BusinessContext ctx, int? id, HospitalNotifiableInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalNotifiableRules.DiseaseRefusal(input) is string why) return HospitalResult.Fail(why);
			var prefix = input.CodePrefix!.Trim().ToUpperInvariant();
			if (await Diseases(ctx).AnyAsync(d => d.CodePrefix == prefix && d.ID != (id ?? 0), ct)) return HospitalResult.Fail("This code is already on the list");
			HospitalNotifiableDisease? d;
			if (id is int i)
			{
				d = await Diseases(ctx).FirstOrDefaultAsync(x => x.ID == i, ct);
				if (d == null) return HospitalResult.Fail("not_found");
			}
			else { d = new HospitalNotifiableDisease { CompanyId = ctx.CompanyId, IsActive = true, CreatedAt = nowUtc }; _db.Add(d); }
			d.CodePrefix = prefix; d.Name = Clip(input.Name, 200)!; d.NameEn = Clip(input.NameEn, 200); d.Urgency = input.Urgency!;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(d.ID);
		}

		public async Task<HospitalResult> ToggleDiseaseAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var d = await Diseases(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			d.IsActive = !d.IsActive;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(d.ID);
		}
	}
}
