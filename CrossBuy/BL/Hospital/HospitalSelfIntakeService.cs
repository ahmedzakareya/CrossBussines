using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H91 — the self-intake: what the tablet submits, and the desk's reconciliation. The match is a hybrid of
	// a deterministic tier (the identity number with the date of birth) and a weighted score over the name's parts
	// (folded, order-free: an Arabic four-part name is written in any order and with any hamza), the date of birth, the
	// mobile and the sex — shown to STAFF only, who confirm against the document before linking.
	// ==========================================================================================
	public sealed class HospitalIntakeSubmitInput
	{
		public string? Language { get; set; }
		public List<string>? RedFlags { get; set; }
		public string? VisitType { get; set; }
		public string? IdType { get; set; }
		public string? IdNumber { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public DateTime? BirthDate { get; set; }
		public string? Sex { get; set; }
		public string? Phone { get; set; }
		public string? Reason { get; set; }
		public string? Allergies { get; set; }
		public string? Medications { get; set; }
		public bool? Pregnant { get; set; }
		public List<string>? Infection { get; set; }
		public string? EmergencyName { get; set; }
		public string? EmergencyPhone { get; set; }
		public bool Consent { get; set; }
	}

	public sealed class HospitalIntakeDecisionInput
	{
		/// Link: the existing file. New file: null.
		public int? PatientId { get; set; }
		/// The desk checked two identifiers against the identity document.
		public bool Verified { get; set; }
		/// New file: the English name the desk completed (the tablet's is optional).
		public string? NameEn { get; set; }
		/// Link: copy the tablet's mobile onto the file.
		public bool UpdatePhone { get; set; }
		/// New file with a strong candidate standing, or a rejection: why.
		public string? Reason { get; set; }
	}

	public static class HospitalIntakeMethod
	{
		public const string Deterministic = "Deterministic";
		public const string Probabilistic = "Probabilistic";
		public const string None = "None";
	}

	public sealed record HospitalIntakeCandidate(HospitalPatient Patient, decimal Score, string Method, IReadOnlyList<string> Matched);
	public sealed record HospitalIntakeRow(HospitalSelfIntake Intake, string? PatientName, string? PatientMrn, int Minutes);
	public sealed record HospitalIntakePage(HospitalSelfIntake Intake, IReadOnlyList<HospitalIntakeCandidate> Candidates, HospitalPatient? Linked, IReadOnlyDictionary<int, string> People);

	public static class HospitalIntakeRules
	{
		public const string ConsentVersion = "2026-11";
		public const decimal Strong = 70m;
		public const decimal Review = 45m;
		private static readonly HashSet<string> Particles = new(StringComparer.Ordinal) { "بن", "ابن", "بنت", "bin", "ibn", "bint", "abu", "ابو", "el", "al" };

		/// The name's parts, folded like H57's duplicate finder (hamza, alef maqsura, taa marbuta, tashkeel) and without
		/// the article and the "son of" particles — so "أحمد محمد علي" and "احمد علي محمد" share three parts.
		public static IReadOnlySet<string> Tokens(string? name)
		{
			var outp = new HashSet<string>(StringComparer.Ordinal);
			foreach (var raw in (name ?? "").Split(new[] { ' ', '-', '.', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var t = HospitalPatientMergeRules.Normalise(raw);
				if (t.StartsWith("ال") && t.Length > 4) t = t[2..];
				if (t.StartsWith("al") && t.Length > 4 && !t.StartsWith("ali")) t = t[2..];
				if (t.Length < 2 || Particles.Contains(t)) continue;
				outp.Add(t);
			}
			return outp;
		}

		/// The parts two names share, and how much of the shorter one that is.
		public static (int Common, decimal Share) NameShare(string? a, string? b)
		{
			var ta = Tokens(a); var tb = Tokens(b);
			if (ta.Count == 0 || tb.Count == 0) return (0, 0);
			int c = ta.Intersect(tb).Count();
			return (c, c / (decimal)Math.Min(ta.Count, tb.Count));
		}

		public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
		public static string IdKey(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

		/// The weighted score (0–100) and the parts that agreed. The identity number with the date of birth is a
		/// deterministic match (100); the number alone is strong but still for the desk to confirm.
		public static (decimal Score, string Method, IReadOnlyList<string> Matched) Score(HospitalSelfIntake i, HospitalPatient p)
		{
			var m = new List<string>();
			bool sameDob = i.BirthDate != null && p.BirthDate?.Date == i.BirthDate.Value.Date;
			var idA = IdKey(i.IdNumber); var idB = IdKey(p.NationalId);
			bool sameId = idA.Length >= 5 && idA == idB;
			if (sameId && sameDob) return (100m, HospitalIntakeMethod.Deterministic, new[] { "IdNumber", "BirthDate" });
			decimal s = 0;
			if (sameId) { s += 60; m.Add("IdNumber"); }
			if (sameDob) { s += 25; m.Add("BirthDate"); }
			var pa = Digits(i.Phone); var pb = Digits(p.Phone);
			if (pa.Length >= 8 && pb.Length >= 8 && pa[^8..] == pb[^8..]) { s += 20; m.Add("Phone"); }
			// Arabic against Arabic, English against English — mixed in one set, a file that has both names and an intake
			// that has one would never reach a full share (the defect a live test found: 68.3 for the same person)
			var (common, share) = new[] { NameShare(i.Name, p.Name), NameShare(i.NameEn, p.NameEn) }.OrderByDescending(x => x.Share).ThenByDescending(x => x.Common).First();
			if (common >= 2) { s += Math.Round(35m * share, 1); m.Add("Name"); }
			else if (common == 1) s += 5;
			if (!string.IsNullOrEmpty(i.Sex) && i.Sex != p.Sex) s -= 20;
			s = Math.Clamp(s, 0, 100);
			return (s, s > 0 ? HospitalIntakeMethod.Probabilistic : HospitalIntakeMethod.None, m);
		}

		public static string? SubmitRefusal(HospitalIntakeSubmitInput i, DateTime today)
		{
			if (i.RedFlags != null && i.RedFlags.Any(f => !HospitalIntakeRedFlag.All.Contains(f))) return "Choose from the list";
			if (i.RedFlags?.Count > 0) return null;   // urgent: the patient goes to the desk, nothing else is asked
			if (!HospitalIntakeVisit.All.Contains(i.VisitType ?? "")) return "Choose why you came";
			if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length < 5 || Tokens(i.Name).Count < 2) return "Write your full name";
			if (i.BirthDate is not DateTime b || b.Date > today || b.Date < today.AddYears(-120)) return "Write your date of birth";
			if (!HospitalPatientSex.IsKnown(i.Sex)) return "Choose the sex";
			var ph = Digits(i.Phone);
			if (ph.Length is < 8 or > 15) return "Write your mobile number";
			if (!string.IsNullOrEmpty(i.IdType) && !HospitalIntakeIdType.All.Contains(i.IdType)) return "Choose the identity document";
			if (!string.IsNullOrWhiteSpace(i.IdNumber) && IdKey(i.IdNumber).Length is < 5 or > 20) return "The identity number is 5 to 20 letters or digits";
			if (i.Infection != null && i.Infection.Any(f => !HospitalIntakeInfection.All.Contains(f))) return "Choose from the list";
			if (i.EmergencyPhone != null && Digits(i.EmergencyPhone).Length is > 0 and (< 8 or > 15)) return "Write a valid emergency phone";
			if (!i.Consent) return "Agree to the use of your data to continue";
			return null;
		}

		public static string? DecisionRefusal(HospitalSelfIntake s, HospitalIntakeDecisionInput d, bool link, decimal bestOther)
		{
			if (s.Status != HospitalIntakeStatus.Submitted) return "This intake is already settled";
			if (!d.Verified) return "Check two identifiers against the identity document first";
			if (s.Urgent && s.AlertAckAt == null) return "Acknowledge the danger signs first";
			if (link && d.PatientId == null) return "Choose the file to link";
			if (!link && bestOther >= Strong && string.IsNullOrWhiteSpace(d.Reason)) return "A file very likely already exists: link it, or write why a new file is right";
			return null;
		}
	}

	public sealed class HospitalSelfIntakeService
	{
		public const string NoTables = "The self-intake is not installed yet (hospital_h91_self_intake.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSelfIntakeService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private IQueryable<HospitalSelfIntake> Intakes(BusinessContext ctx) => _db.Set<HospitalSelfIntake>().Where(x => x.CompanyId == ctx.CompanyId);

		public Task<bool> CanKioskAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Book, null, ct);
		public Task<bool> CanDeskAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Register, null, ct);

		/// The tablet's submission. Returns the ticket number (Args[0]) and whether it is urgent (Args[1]).
		public async Task<HospitalResult> SubmitAsync(BusinessContext ctx, HospitalIntakeSubmitInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanKioskAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var today = Today(nowUtc);
			if (HospitalIntakeRules.SubmitRefusal(input, today) is string why) return HospitalResult.Fail(why);
			bool urgent = input.RedFlags?.Count > 0;
			try
			{
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Intakes(ctx).Select(x => x.IntakeNo).ToListAsync(ct);
					int n = await Intakes(ctx).CountAsync(x => x.TicketDate == today, ct) + 1;
					var row = new HospitalSelfIntake
					{
						CompanyId = ctx.CompanyId, IntakeNo = HospitalDentalPracticeRules.NextNo(nos, "SI-", 6), TicketNo = "R-" + n.ToString("000"), TicketDate = today,
						Status = HospitalIntakeStatus.Submitted, Urgent = urgent, RedFlags = urgent ? string.Join(",", input.RedFlags!.Distinct()) : null,
						Language = input.Language == "en" ? "en" : "ar", VisitType = HospitalIntakeVisit.All.Contains(input.VisitType ?? "") ? input.VisitType! : HospitalIntakeVisit.WalkIn,
						IdType = Clip(input.IdType, 12), IdNumber = Clip(input.IdNumber, 30), Name = Clip(input.Name, 200), NameEn = Clip(input.NameEn, 200), BirthDate = input.BirthDate?.Date,
						Sex = HospitalPatientSex.IsKnown(input.Sex) ? input.Sex : null, Phone = Clip(input.Phone, 20), Reason = Clip(input.Reason, 300), Allergies = Clip(input.Allergies, 300),
						Medications = Clip(input.Medications, 300), Pregnant = input.Sex == HospitalPatientSex.Female ? input.Pregnant : null,
						Infection = input.Infection?.Count > 0 ? string.Join(",", input.Infection.Distinct()) : null, EmergencyName = Clip(input.EmergencyName, 100), EmergencyPhone = Clip(input.EmergencyPhone, 20),
						ConsentVersion = input.Consent ? HospitalIntakeRules.ConsentVersion : null, ConsentAt = input.Consent ? nowUtc : null,
						SubmittedAt = nowUtc, ExpiresAt = HospitalNursingService.WardToUtc(today.AddDays(1)),
					};
					_db.Add(row);
					try
					{
						await _db.SaveChangesAsync(ct);
						return new HospitalResult(true, null, row.ID, new object[] { row.TicketNo, urgent });
					}
					catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
					{
						_db.Entry(row).State = EntityState.Detached;   // two tablets took the same number: take the next
					}
				}
				return HospitalResult.Fail("The change could not be saved");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Intakes nobody settled by the end of their day expire.
		private async Task ExpireAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct)
		{
			var old = await Intakes(ctx).Where(x => x.Status == HospitalIntakeStatus.Submitted && x.ExpiresAt < nowUtc).ToListAsync(ct);
			if (old.Count == 0) return;
			foreach (var x in old) x.Status = HospitalIntakeStatus.Expired;
			await _db.SaveChangesAsync(ct);
		}

		public async Task<(IReadOnlyList<HospitalIntakeRow> Rows, int Total, bool Ready, int Urgent)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				await ExpireAsync(ctx, nowUtc, ct);
				var query = from x in Intakes(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on x.PatientId equals p.ID into pj from p in pj.DefaultIfEmpty()
							select new { x, PName = p == null ? null : p.Name, PMrn = p == null ? null : p.Mrn };
				query = filter switch
				{
					"done" => query.Where(r => r.x.Status == HospitalIntakeStatus.Linked || r.x.Status == HospitalIntakeStatus.NewFile),
					"closed" => query.Where(r => r.x.Status == HospitalIntakeStatus.Rejected || r.x.Status == HospitalIntakeStatus.Expired),
					"urgent" => query.Where(r => r.x.Status == HospitalIntakeStatus.Submitted && r.x.Urgent),
					_ => query.Where(r => r.x.Status == HospitalIntakeStatus.Submitted),
				};
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(r => r.x.TicketNo.Contains(s) || r.x.IntakeNo.Contains(s) || (r.x.Name != null && r.x.Name.Contains(s))
					|| (r.x.NameEn != null && r.x.NameEn.Contains(s)) || (r.x.Phone != null && r.x.Phone.Contains(s)) || (r.x.IdNumber != null && r.x.IdNumber.Contains(s))); }
				int total = await query.CountAsync(ct);
				int urgent = await Intakes(ctx).CountAsync(x => x.Status == HospitalIntakeStatus.Submitted && x.Urgent && x.AlertAckAt == null, ct);
				var list = await query.OrderByDescending(r => r.x.Urgent && r.x.AlertAckAt == null).ThenBy(r => r.x.Status == HospitalIntakeStatus.Submitted ? 0 : 1)
					.ThenBy(r => r.x.Status == HospitalIntakeStatus.Submitted ? r.x.SubmittedAt : DateTime.MaxValue).ThenByDescending(r => r.x.SubmittedAt)
					.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				return (list.Select(r => new HospitalIntakeRow(r.x, r.PName, r.PMrn, (int)Math.Round((nowUtc - r.x.SubmittedAt).TotalMinutes))).ToList(), total, true, urgent);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalIntakeRow>(), 0, false, 0); }
		}

		/// The candidates the desk sees: files sharing the identity number, the date of birth, the mobile's last eight
		/// digits, or two name parts — scored, the strongest first, the weak ones left out.
		public async Task<IReadOnlyList<HospitalIntakeCandidate>> CandidatesAsync(BusinessContext ctx, HospitalSelfIntake s, CancellationToken ct)
		{
			if (s.Name == null && s.IdNumber == null && s.Phone == null) return Array.Empty<HospitalIntakeCandidate>();
			var id = s.IdNumber?.Trim(); var dob = s.BirthDate; var ph = HospitalIntakeRules.Digits(s.Phone); var tail = ph.Length >= 8 ? ph[^8..] : null;
			var first = HospitalIntakeRules.Tokens(s.Name).FirstOrDefault();
			var pool = await _db.HospitalPatients.AsNoTracking()
				.Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && (
					(id != null && p.NationalId == id) || (dob != null && p.BirthDate == dob) || (tail != null && p.Phone != null && p.Phone.EndsWith(tail))
					|| (s.Name != null && first != null && p.Name.Contains(first))))
				.Take(400).ToListAsync(ct);
			return pool.Select(p => { var (sc, m, f) = HospitalIntakeRules.Score(s, p); return new HospitalIntakeCandidate(p, sc, m, f); })
				.Where(c => c.Score >= HospitalIntakeRules.Review).OrderByDescending(c => c.Score).Take(8).ToList();
		}

		public async Task<HospitalIntakePage?> PageAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				await ExpireAsync(ctx, nowUtc, ct);
				var s = await Intakes(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null) return null;
				var cands = s.Status == HospitalIntakeStatus.Submitted ? await CandidatesAsync(ctx, s, ct) : Array.Empty<HospitalIntakeCandidate>();
				var linked = s.PatientId == null ? null : await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == s.PatientId, ct);
				var eids = new[] { s.AlertAckByEmployeeId, s.ReconciledByEmployeeId }.Where(x => x != null).Select(x => x!.Value).ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalIntakePage(s, cands, linked, people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		public async Task<HospitalResult> AcknowledgeAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanDeskAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var s = await Intakes(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				if (!s.Urgent || s.AlertAckAt != null) return HospitalResult.Fail("There is no alert to acknowledge");
				s.AlertAckAt = nowUtc; s.AlertAckByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Links the intake to an existing file: the allergies are added (a lost allergy is a danger, a repeated one is
		/// not), the mobile copied when the desk chose so, and the identity number filled when the file had none.
		public async Task<HospitalResult> LinkAsync(BusinessContext ctx, int id, HospitalIntakeDecisionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanDeskAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var s = await Intakes(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				if (HospitalIntakeRules.DecisionRefusal(s, input, true, 0) is string why) return HospitalResult.Fail(why);
				var p = await _db.HospitalPatients.FirstOrDefaultAsync(x => x.ID == input.PatientId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				var (score, method, _) = HospitalIntakeRules.Score(s, p);
				p.Allergies = HospitalPatientMergeRules.Union(p.Allergies, s.Allergies?.Replace('،', '\n').Replace(',', '\n'));
				if (input.UpdatePhone && s.Phone != null) p.Phone = s.Phone;
				var idKey = HospitalIntakeRules.Digits(s.IdNumber);
				if (string.IsNullOrEmpty(p.NationalId) && idKey.Length is >= 5 and <= 20 && idKey == HospitalIntakeRules.IdKey(s.IdNumber)
					&& !await _db.HospitalPatients.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.NationalId == idKey && x.ID != p.ID, ct)) p.NationalId = idKey;
				p.UpdatedAt = nowUtc; p.UpdatedByEmployeeId = employeeId;
				s.Status = HospitalIntakeStatus.Linked; s.PatientId = p.ID; s.ReconciledAt = nowUtc; s.ReconciledByEmployeeId = employeeId; s.MatchScore = score; s.MatchMethod = method;
				s.DecisionNote = Clip(input.Reason, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Opens a new file from the intake through the registry's own save (its checks: the ID number already owned,
		/// the mobile's shape). Refused while a very likely existing file stands, unless the desk writes why.
		public async Task<HospitalResult> NewFileAsync(BusinessContext ctx, int id, HospitalIntakeDecisionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanDeskAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var s = await Intakes(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				var best = s.Status == HospitalIntakeStatus.Submitted ? (await CandidatesAsync(ctx, s, ct)).Select(c => c.Score).DefaultIfEmpty(0).Max() : 0;
				if (HospitalIntakeRules.DecisionRefusal(s, input, false, best) is string why) return HospitalResult.Fail(why);
				if (s.Name == null || s.BirthDate == null) return HospitalResult.Fail("The intake has no name or date of birth: register the patient at the desk");
				var idDigits = HospitalIntakeRules.Digits(s.IdNumber);
				var saved = await new HospitalPatientService(_db, _access).SaveAsync(ctx, new HospitalPatientInput
				{
					Name = s.Name, NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? (s.NameEn ?? "") : input.NameEn.Trim(), Sex = s.Sex ?? HospitalPatientSex.Male, BirthDate = s.BirthDate,
					NationalId = idDigits.Length is >= 5 and <= 20 && idDigits == HospitalIntakeRules.IdKey(s.IdNumber) ? idDigits : null, Phone = s.Phone,
					Allergies = s.Allergies?.Replace('،', '\n').Replace(',', '\n'), EmergencyName = s.EmergencyName, EmergencyPhone = s.EmergencyPhone, ConsentSigned = s.ConsentAt != null,
				}, employeeId, nowUtc, ct);
				if (!saved.Ok || saved.Id is not int pid) return saved;
				s.Status = HospitalIntakeStatus.NewFile; s.PatientId = pid; s.ReconciledAt = nowUtc; s.ReconciledByEmployeeId = employeeId; s.MatchScore = best; s.MatchMethod = HospitalIntakeMethod.None;
				s.DecisionNote = Clip(input.Reason, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(pid);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RejectAsync(BusinessContext ctx, int id, HospitalIntakeDecisionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanDeskAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(input.Reason)) return HospitalResult.Fail("Write why the intake is rejected");
			try
			{
				var s = await Intakes(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				if (s.Status != HospitalIntakeStatus.Submitted) return HospitalResult.Fail("This intake is already settled");
				if (s.Urgent && s.AlertAckAt == null) return HospitalResult.Fail("Acknowledge the danger signs first");
				s.Status = HospitalIntakeStatus.Rejected; s.ReconciledAt = nowUtc; s.ReconciledByEmployeeId = employeeId; s.DecisionNote = Clip(input.Reason, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}
}
