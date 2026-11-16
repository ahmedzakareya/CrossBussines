using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H139 — the front desk: one workspace instead of eleven screens (docs/hospital/front-desk-plan.md), built the
	// way the leading front desks are (Epic's Department Appointments Report and patient station, athenaOne's status board
	// and check-in page).
	//   · ONE SEARCH: name in Arabic or Latin, MRN, national ID, phone or today's queue number. Arabic names are folded and
	//     tokenised as the duplicate finder does (H57), Latin spellings are keyed so Mohamed / Muhammad / Mohammed meet. The
	//     candidates are ranked; an exact identifier outranks every name.
	//   · TODAY'S BOARD: every appointment of the day by stage (expected, waiting, with the doctor, done, no-show,
	//     cancelled). Each row shows the wait since arrival and a "late" flag for the expected past their time.
	//   · THE PATIENT CARD: banner, the check-in checklist, today's and the next visits, insurance eligibility, what they
	//     owe. Nothing on the checklist refuses a check-in: care is never held for paperwork. Identity and the payer
	//     decision are flagged for attention; the rest is owed.
	//   · CHECK-IN, NO-SHOW and WALK-IN: through the outpatient service itself (its statuses, its queue number). A walk-in
	//     takes the doctor's next free slot today and arrives at once.
	//   · THE INBOX: the counts of what waits for the desk elsewhere (portal requests, waitlist offers, self-intake,
	//     unidentified patients, pre-authorisations, possible duplicates), each opening its own screen.
	// No appointment or queue rule lives here: the outpatient service books, moves and numbers.
	// ==========================================================================================

	public static class HospitalDeskStage
	{
		public const string Expected = "Expected", Waiting = "Waiting", WithDoctor = "WithDoctor", Done = "Done", NoShow = "NoShow", Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Expected, Waiting, WithDoctor, Done, NoShow, Cancelled };
	}

	public sealed record HospitalDeskCandidate(int PatientId, string Name, string? NameEn, string Mrn, string? Photo, string Sex, int? Age, string? Phone, string? NationalId,
		int Score, string Reason, int? TodayAppointmentId, string? TodayStatus, int? QueueNumber);

	public sealed record HospitalDeskRow(HospitalAppointmentView A, string Doctor, string? DoctorEn, string? DoctorPhoto, string? Department, string? DepartmentEn,
		string Stage, int? WaitMinutes, bool Late, HospitalDeskFlags? Flags = null);

	/// H140: what the desk must see on a row before the patient reaches the counter.
	public sealed record HospitalDeskFlags(string? Allergies, decimal Owed, bool Insured, bool Unverified, string? Payer, string? PayerEn, bool FirstVisit, bool WalkIn);

	public sealed record HospitalDeskStats(int Expected, int Arrived, int WaitingNow, int WithDoctor, int Done, int NoShow, int? AvgWaitMinutes, int? MaxWaitMinutes, int Admitted, int InEmergency,
		int Late = 0, int WalkIns = 0, int ToVerify = 0);

	/// H140: a clinic as the desk sees it: the board card, how full the day is and the next free slot.
	public sealed record HospitalDeskClinic(HospitalClinicCard Card, int Booked, int FreeLeft, int? NextFree);

	/// H140: the desk's own strip: who sits here, the shift, the day's collections and the last receipts.
	public sealed record HospitalDeskReceipt(string ReceiptNo, string Patient, string? PatientEn, decimal Amount, string Method, DateTime At);
	public sealed record HospitalDeskMe(string Name, string? Photo, HospitalShiftSummary? Shift, IReadOnlyList<HospitalDeskReceipt> Receipts, int? LastCalled,
		IReadOnlyDictionary<string, decimal> CollectedToday, int ReceiptsToday);

	/// H140: the time grid: doctors across, the day's times down; a cell is an appointment, a free slot or nothing.
	public sealed record HospitalDeskGrid(IReadOnlyList<HospitalPractitionerView> Doctors, IReadOnlyList<int> Minutes,
		IReadOnlyDictionary<(int Doctor, int Minute), HospitalDeskRow> Taken, IReadOnlySet<(int Doctor, int Minute)> Free, int NowMinute);

	/// H140: a doctor's free slots for a walk-in, from now on.
	public sealed record HospitalDeskSlots(HospitalPractitionerView Doctor, int Waiting, IReadOnlyList<int> Free);

	public sealed record HospitalDeskInbox(int PortalRequests, int WaitlistOffers, int Intake, int Unidentified, int Preauths, int Duplicates)
	{
		public int Total => PortalRequests + WaitlistOffers + Intake + Unidentified + Preauths + Duplicates;
	}

	public sealed record HospitalDeskCheck(string Key, bool Done, bool Blocks, string? Detail);

	public sealed record HospitalDeskCard(HospitalPatient Patient, int? Age, IReadOnlyList<HospitalDeskRow> Today, IReadOnlyList<HospitalAppointmentView> Upcoming,
		IReadOnlyList<(int Id, string Name, string? NameEn, string? Department, string? DepartmentEn)> Doctors,
		string? Payer, string? PolicyNo, bool Insured, bool Ineligible, decimal Owed, int OpenBills, int Consents, bool Admitted, IReadOnlyList<HospitalDeskCheck> Checklist);

	public static class HospitalFrontDeskRules
	{
		/// A Latin name's key: letters only, doubled letters once, no vowel after the first letter — so Mohamed, Muhammad
		/// and Mohammed all read "mhmd", Ahmed and Ahmad "ahmd".
		public static string LatinKey(string token)
		{
			var s = new string(token.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').ToArray());
			if (s.Length == 0) return "";
			var sb = new System.Text.StringBuilder().Append(s[0]);
			for (int i = 1; i < s.Length; i++)
			{
				char c = s[i];
				if ("aeiouy".IndexOf(c) >= 0 || c == sb[^1]) continue;
				sb.Append(c);
			}
			return sb.ToString();
		}

		private static HashSet<string> Keys(string? name)
		{
			var keys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var t in HospitalIntakeRules.Tokens(name)) keys.Add(t.All(c => c < 128) ? LatinKey(t) : t);
			keys.Remove("");
			return keys;
		}

		/// How well a typed name matches a patient's names (0–100): the share of typed parts found among the patient's
		/// parts, Arabic folded, Latin keyed; a typed part may be the start of a longer one ("محم" finds "محمد").
		public static int NameScore(string query, string? name, string? nameEn)
		{
			var q = Keys(query);
			if (q.Count == 0) return 0;
			var p = Keys(name); p.UnionWith(Keys(nameEn));
			int hit = q.Count(t => p.Contains(t) || (t.Length >= 3 && p.Any(x => x.StartsWith(t, StringComparison.Ordinal))));
			return (int)Math.Round(100m * hit / q.Count);
		}

		/// The stage of an appointment on the desk's board.
		public static string Stage(string status) => status switch
		{
			HospitalAppointmentStatus.Arrived => HospitalDeskStage.Waiting,
			HospitalAppointmentStatus.InRoom => HospitalDeskStage.WithDoctor,
			HospitalAppointmentStatus.Done => HospitalDeskStage.Done,
			HospitalAppointmentStatus.NoShow => HospitalDeskStage.NoShow,
			HospitalAppointmentStatus.Cancelled => HospitalDeskStage.Cancelled,
			_ => HospitalDeskStage.Expected,
		};

		/// Minutes waited since arrival: until the doctor started, or until now while still waiting.
		public static int? WaitMinutes(DateTime? arrivedAt, DateTime? startedAt, DateTime nowUtc)
			=> arrivedAt is DateTime a ? Math.Max(0, (int)((startedAt ?? nowUtc) - a).TotalMinutes) : null;

		/// Still expected 15 minutes after the appointment's time.
		public const int LateAfterMinutes = 15;
		public static bool Late(string status, int startMinute, int nowMinute) => status == HospitalAppointmentStatus.Booked && nowMinute > startMinute + LateAfterMinutes;

		/// The walk-in's slot: the first free one from now on (a slot that began within the last 10 minutes still counts).
		public static int? WalkInSlot(IReadOnlyList<int> free, int nowMinute) => free.Where(m => m >= nowMinute - 10).OrderBy(m => m).Select(m => (int?)m).FirstOrDefault();

		/// H140: the slot the desk picked, if it is still free and not past; otherwise none.
		public static int? ChosenSlot(IReadOnlyList<int> free, int nowMinute, int chosen) => free.Contains(chosen) && chosen >= nowMinute - 10 ? chosen : null;

		/// The note a walk-in's appointment carries; the board's walk-in flag reads it.
		public const string WalkInNote = "Walk-in";

		/// H140: the grid's rows — every time a doctor has an appointment or a free slot, in order.
		public static IReadOnlyList<int> GridMinutes(IEnumerable<int> taken, IEnumerable<int> free) => taken.Concat(free).Distinct().OrderBy(m => m).ToList();

		/// H140: how full a doctor's day is, 0-100: booked against booked plus what is still free.
		public static int Fill(int booked, int freeLeft) => booked + freeLeft == 0 ? 0 : (int)Math.Round(100m * booked / (booked + freeLeft));

		/// The check-in checklist. Identity (a file with a national ID or a photo) and the payer decision (insured and
		/// eligible, or self-pay) need attention (Blocks marks them); consent, the photo and an open balance are owed.
		/// None of them refuses the check-in itself: a missing ID never holds care.
		public static IReadOnlyList<HospitalDeskCheck> Checklist(HospitalPatient p, bool insured, bool ineligible, int consents, decimal owed)
		{
			bool idOk = !string.IsNullOrWhiteSpace(p.NationalId) || p.PhotoPath != null;
			return new[]
			{
				new HospitalDeskCheck("identity", idOk, true, p.NationalId),
				new HospitalDeskCheck("payer", !ineligible, true, insured ? null : "self"),
				new HospitalDeskCheck("photo", p.PhotoPath != null, false, null),
				new HospitalDeskCheck("phone", !string.IsNullOrWhiteSpace(p.Phone), false, p.Phone),
				new HospitalDeskCheck("consent", consents > 0 || p.ConsentSigned, false, null),
				new HospitalDeskCheck("balance", owed <= 0, false, owed > 0 ? owed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : null),
			};
		}
	}

	public sealed class HospitalFrontDeskService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalOutpatientService _op;
		private readonly HospitalPortalBookingService _portal;
		private readonly HospitalInsuranceService _insurance;
		private readonly HospitalBillingService _billing;

		public HospitalFrontDeskService(CrossDbContext db, IHospitalAccessService access, HospitalOutpatientService op, HospitalPortalBookingService portal,
			HospitalInsuranceService insurance, HospitalBillingService billing)
		{ _db = db; _access = access; _op = op; _portal = portal; _insurance = insurance; _billing = billing; }

		// ====================================================================================
		// SEARCH
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalDeskCandidate>> SearchAsync(BusinessContext ctx, string? q, DateTime todayLocal, int take = 12, CancellationToken ct = default)
		{
			q = q?.Trim();
			if (string.IsNullOrEmpty(q) || q.Length < 2) return Array.Empty<HospitalDeskCandidate>();
			var digits = HospitalIntakeRules.Digits(q);
			var all = _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId);
			var scored = new Dictionary<int, (int Score, string Reason)>();
			void Keep(int id, int score, string reason) { if (!scored.TryGetValue(id, out var s) || s.Score < score) scored[id] = (score, reason); }
			if (digits.Length >= 3 && digits.Length >= q.Count(char.IsLetterOrDigit) - 4)   // mostly digits: an identifier
			{
				var hits = await all.Where(p => p.Mrn.Contains(digits) || (p.NationalId != null && p.NationalId.Contains(digits)) || (p.Phone != null && p.Phone.Contains(digits)))
					.Select(p => new { p.ID, p.Mrn, p.NationalId, p.Phone }).Take(200).ToListAsync(ct);
				foreach (var h in hits)
				{
					if (HospitalIntakeRules.Digits(h.Mrn) == digits || h.Mrn == q) Keep(h.ID, 100, "mrn");
					else if (h.NationalId == digits) Keep(h.ID, 100, "id");
					else if (h.Phone != null && digits.Length >= 8 && HospitalIntakeRules.Digits(h.Phone).EndsWith(digits[^8..])) Keep(h.ID, 90, "phone");
					else Keep(h.ID, 60, h.Mrn.Contains(digits) ? "mrn" : h.NationalId?.Contains(digits) == true ? "id" : "phone");
				}
				// today's queue number
				if (digits.Length <= 4 && int.TryParse(digits, out var qn))
					foreach (var pid in await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == todayLocal.Date && a.QueueNumber == qn).Select(a => a.PatientId).ToListAsync(ct))
						Keep(pid, 95, "queue");
			}
			else
			{
				var names = await all.Select(p => new { p.ID, p.Name, p.NameEn }).ToListAsync(ct);
				foreach (var n in names)
				{
					int s = HospitalFrontDeskRules.NameScore(q, n.Name, n.NameEn);
					if (s >= 50) Keep(n.ID, s, "name");
				}
			}
			var top = scored.OrderByDescending(x => x.Value.Score).Take(take).Select(x => x.Key).ToList();
			if (top.Count == 0) return Array.Empty<HospitalDeskCandidate>();
			var rows = await all.Where(p => top.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var today = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == todayLocal.Date && top.Contains(a.PatientId)
					&& a.Status != HospitalAppointmentStatus.Cancelled).OrderBy(a => a.StartMinute).ToListAsync(ct);
			return top.Where(rows.ContainsKey).Select(id =>
			{
				var p = rows[id]; var a = today.FirstOrDefault(x => x.PatientId == id);
				return new HospitalDeskCandidate(p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Sex, HospitalPatientService.AgeOn(p.BirthDate, todayLocal), p.Phone, p.NationalId,
					scored[id].Score, scored[id].Reason, a?.ID, a?.Status, a?.QueueNumber);
			}).ToList();
		}

		// ====================================================================================
		// THE BOARD
		// ====================================================================================

		private async Task<Dictionary<int, HospitalPractitionerView>> DoctorsAsync(BusinessContext ctx, CancellationToken ct)
			=> (await _op.PractitionersAsync(ctx, ct)).ToDictionary(p => p.Row.ID);

		private static HospitalDeskRow Row(HospitalAppointmentView a, Dictionary<int, HospitalPractitionerView> docs, DateTime nowUtc, int nowMinute)
		{
			var d = docs.GetValueOrDefault(a.PractitionerId);
			return new HospitalDeskRow(a, d?.Row.Name ?? "—", d?.Row.NameEn, d?.PhotoPath, d?.Department?.Name, d?.Department?.NameEn,
				HospitalFrontDeskRules.Stage(a.Status), HospitalFrontDeskRules.WaitMinutes(a.ArrivedAt, a.StartedAt, nowUtc), HospitalFrontDeskRules.Late(a.Status, a.StartMinute, nowMinute));
		}

		public async Task<IReadOnlyList<HospitalDeskRow>> BoardAsync(BusinessContext ctx, DateTime todayLocal, int nowMinute, DateTime nowUtc, CancellationToken ct = default)
		{
			var docs = await DoctorsAsync(ctx, ct);
			return (await _op.DayAsync(ctx, todayLocal, ct)).Select(a => Row(a, docs, nowUtc, nowMinute))
				.OrderBy(r => r.Stage == HospitalDeskStage.Waiting ? 0 : r.Stage == HospitalDeskStage.Expected ? 1 : r.Stage == HospitalDeskStage.WithDoctor ? 2 : 3)
				.ThenByDescending(r => r.WaitMinutes ?? -1).ThenBy(r => r.A.StartMinute).ToList();
		}

		public static HospitalDeskStats Stats(IReadOnlyList<HospitalDeskRow> rows, HospitalDeskCounts? where)
		{
			var live = rows.Where(r => r.Stage != HospitalDeskStage.Cancelled).ToList();
			var waits = live.Where(r => r.WaitMinutes != null).Select(r => r.WaitMinutes!.Value).ToList();
			return new HospitalDeskStats(live.Count, live.Count(r => r.A.ArrivedAt != null), live.Count(r => r.Stage == HospitalDeskStage.Waiting), live.Count(r => r.Stage == HospitalDeskStage.WithDoctor),
				live.Count(r => r.Stage == HospitalDeskStage.Done), live.Count(r => r.Stage == HospitalDeskStage.NoShow),
				waits.Count == 0 ? null : (int)Math.Round(waits.Average()), waits.Count == 0 ? null : waits.Max(), where?.Admitted ?? 0, where?.InEmergency ?? 0,
				live.Count(r => r.Late), live.Count(r => r.Flags?.WalkIn == true), live.Count(r => r.Flags?.Unverified == true && r.Stage is HospitalDeskStage.Expected or HospitalDeskStage.Waiting));
		}

		// ====================================================================================
		// THE INBOX
		// ====================================================================================

		public async Task<HospitalDeskInbox> InboxAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			async Task<int> Safe(Func<Task<int>> f) { try { return await f(); } catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return 0; } }
			return new HospitalDeskInbox(
				await Safe(() => _db.Set<HospitalPortalRequest>().CountAsync(r => r.CompanyId == cid && r.Status == HospitalPortalRequestStatus.Open, ct)),
				await Safe(() => _db.Set<HospitalPortalOffer>().CountAsync(o => o.CompanyId == cid && o.Status == HospitalPortalOfferStatus.Open && o.ExpiresAt > nowUtc, ct)),
				await Safe(() => _db.Set<HospitalSelfIntake>().CountAsync(i => i.CompanyId == cid && i.Status == HospitalIntakeStatus.Submitted, ct)),
				await Safe(() => _db.Set<HospitalUnidentified>().CountAsync(u => u.CompanyId == cid && u.IdentifiedAt == null && u.MergedIntoPatientId == null, ct)),
				await Safe(() => _db.Set<HospitalPreauth>().CountAsync(p => p.CompanyId == cid && p.Status == HospitalPreauthStatus.Pending, ct)),
				await Safe(async () => (await new HospitalPatientMergeService(_db, _access).CandidatesAsync(ctx, 2000, ct)).Count));
		}

		// ====================================================================================
		// THE PATIENT CARD
		// ====================================================================================

		public async Task<HospitalDeskCard?> CardAsync(BusinessContext ctx, int patientId, DateTime todayLocal, int nowMinute, DateTime nowUtc, CancellationToken ct = default)
		{
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return null;
			var docs = await DoctorsAsync(ctx, ct);
			var visits = await _op.PatientAppointmentsAsync(ctx, p.ID, ct);
			var today = visits.Where(a => a.Date.Date == todayLocal.Date).Select(a => Row(a, docs, nowUtc, nowMinute)).OrderBy(r => r.A.StartMinute).ToList();
			var upcoming = visits.Where(a => a.Date.Date > todayLocal.Date && a.Status == HospitalAppointmentStatus.Booked).OrderBy(a => a.Date).ThenBy(a => a.StartMinute).Take(5).ToList();
			var (policy, payer, why) = await _insurance.EligibilityAsync(ctx, p.ID, todayLocal, null, ct);
			// a policy on file that does not cover today leaves the payer undecided; no policy at all is self-pay
			bool ineligible = policy == null && why != null && await _db.Set<HospitalPatientPolicy>().AnyAsync(x => x.CompanyId == ctx.CompanyId && x.PatientId == p.ID, ct);
			var bills = await _billing.BillsAsync(ctx, null, p.ID, nowUtc, ct);
			var open = bills.Where(b => b.Total - b.Paid > 0).ToList();
			decimal owed = open.Sum(b => b.Total - b.Paid);
			int consents = await _db.Set<HospitalConsent>().CountAsync(c => c.CompanyId == ctx.CompanyId && c.PatientId == p.ID, ct);
			bool admitted = await _db.HospitalAdmissions.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.PatientId == p.ID && a.Status == HospitalAdmissionStatus.Active, ct);
			var doctors = docs.Values.Where(d => d.Row.IsActive).OrderBy(d => d.Department?.Name).ThenBy(d => d.Row.Name)
				.Select(d => (d.Row.ID, d.Row.Name, d.Row.NameEn, d.Department?.Name, d.Department?.NameEn)).ToList();
			bool insured = policy != null;
			return new HospitalDeskCard(p, HospitalPatientService.AgeOn(p.BirthDate, todayLocal), today, upcoming, doctors, payer == null ? null : DisplayName.Of(payer.Name, payer.NameEn), policy?.PolicyNo,
				insured, ineligible, owed, open.Count, consents, admitted, HospitalFrontDeskRules.Checklist(p, insured, ineligible, consents, owed));
		}

		// ====================================================================================
		// THE DESK'S ACTS
		// ====================================================================================

		/// Arrived (the queue number comes with it), no-show or cancelled: the outpatient service's own move.
		public Task<HospitalResult> MoveAsync(BusinessContext ctx, int appointmentId, string to, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
			=> to is HospitalAppointmentStatus.Arrived or HospitalAppointmentStatus.NoShow or HospitalAppointmentStatus.Cancelled
				? _op.MoveAsync(ctx, appointmentId, to, employeeId, nowUtc, ct) : Task.FromResult(HospitalResult.Fail("not_found"));

		/// A walk-in: the doctor's next free slot today, booked and arrived at once.
		/// H140: the desk may pick the slot; a picked slot that is no longer free is refused rather than moved.
		public async Task<HospitalResult> WalkInAsync(BusinessContext ctx, int patientId, int practitionerId, string? visitType, int? employeeId, DateTime nowUtc, DateTime nowLocal, CancellationToken ct = default, int? startMinute = null)
		{
			var free = await _portal.StaffFreeSlotsAsync(ctx, practitionerId, nowLocal.Date, nowLocal, ct);
			int nowMinute = nowLocal.Hour * 60 + nowLocal.Minute;
			var slot = startMinute is int chosen ? HospitalFrontDeskRules.ChosenSlot(free, nowMinute, chosen) : HospitalFrontDeskRules.WalkInSlot(free, nowMinute);
			if (slot == null) return HospitalResult.Fail(startMinute != null ? "This slot is no longer free" : "This doctor has no free slot left today");
			var booked = await _op.BookAsync(ctx, new HospitalBookingInput { PatientId = patientId, PractitionerId = practitionerId, Date = nowLocal.Date, StartMinute = slot.Value,
				VisitType = HospitalVisitTypes.All.Contains(visitType ?? "") ? visitType! : HospitalVisitTypes.New, Note = HospitalFrontDeskRules.WalkInNote }, employeeId, nowLocal.Date, ct);
			if (!booked.Ok) return booked;
			var arrived = await _op.MoveAsync(ctx, booked.Id!.Value, HospitalAppointmentStatus.Arrived, employeeId, nowUtc, ct);
			return arrived.Ok ? HospitalResult.Success(booked.Id) : arrived;
		}

		// ====================================================================================
		// H140 - THE WORKSPACE
		// ====================================================================================

		/// The board's rows with what the desk must see on each: allergy, money owed, the payer and whether today is
		/// covered, a first visit, a walk-in. One query per kind, one eligibility check per insured patient.
		public async Task<IReadOnlyList<HospitalDeskRow>> FlagAsync(BusinessContext ctx, IReadOnlyList<HospitalDeskRow> rows, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			var ids = rows.Select(r => r.A.PatientId).Distinct().ToList();
			if (ids.Count == 0) return rows;
			var allergies = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && ids.Contains(p.ID)).Select(p => new { p.ID, p.Allergies }).ToDictionaryAsync(p => p.ID, p => p.Allergies, ct);
			var withPolicy = await _db.Set<HospitalPatientPolicy>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && ids.Contains(x.PatientId)).Select(x => x.PatientId).Distinct().ToListAsync(ct);
			var cover = new Dictionary<int, (bool Insured, bool Unverified, string? Payer, string? PayerEn)>();
			foreach (var id in withPolicy)
			{
				var (policy, payer, _) = await _insurance.EligibilityAsync(ctx, id, todayLocal, null, ct);
				cover[id] = (policy != null, policy == null, payer?.Name, payer?.NameEn);
			}
			var owed = new Dictionary<int, decimal>();
			var billed = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && ids.Contains(b.PatientId)).Select(b => b.PatientId).Distinct().ToListAsync(ct);
			foreach (var id in billed)
				owed[id] = (await _billing.BillsAsync(ctx, null, id, nowUtc, ct)).Sum(b => Math.Max(0, b.Total - b.Paid));
			return rows.Select(r =>
			{
				var c = cover.GetValueOrDefault(r.A.PatientId);
				var al = allergies.GetValueOrDefault(r.A.PatientId);
				return r with { Flags = new HospitalDeskFlags(string.IsNullOrWhiteSpace(al) ? null : al, owed.GetValueOrDefault(r.A.PatientId),
					c.Insured, c.Unverified, c.Payer, c.PayerEn, r.A.VisitType == HospitalVisitTypes.New, r.A.Note == HospitalFrontDeskRules.WalkInNote) };
			}).ToList();
		}

		/// The clinics right now: the outpatient board's cards with how full each day is and the next free slot.
		public async Task<IReadOnlyList<HospitalDeskClinic>> ClinicsAsync(BusinessContext ctx, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			int nowMinute = nowLocal.Hour * 60 + nowLocal.Minute;
			var cards = await _op.BoardAsync(ctx, nowLocal.Date, nowMinute, nowUtc, ct);
			var day = await _op.DayAsync(ctx, nowLocal.Date, ct);
			var list = new List<HospitalDeskClinic>();
			foreach (var c in cards)
			{
				var free = await _portal.StaffFreeSlotsAsync(ctx, c.Practitioner.Row.ID, nowLocal.Date, nowLocal, ct);
				var ahead = free.Where(m => m >= nowMinute - 10).ToList();
				int booked = day.Count(a => a.PractitionerId == c.Practitioner.Row.ID && a.Status != HospitalAppointmentStatus.Cancelled);
				list.Add(new HospitalDeskClinic(c, booked, ahead.Count, ahead.Count == 0 ? null : ahead.Min()));
			}
			return list.OrderByDescending(x => x.Card.Waiting.Count).ThenBy(x => DisplayName.Of(x.Card.Practitioner.Row.Name, x.Card.Practitioner.Row.NameEn)).ToList();
		}

		/// The time grid of the day for the doctors working today (and any doctor with an appointment today).
		public async Task<HospitalDeskGrid> GridAsync(BusinessContext ctx, IReadOnlyList<HospitalDeskRow> rows, DateTime nowLocal, DateTime nowUtc, int? department, CancellationToken ct = default)
		{
			int nowMinute = nowLocal.Hour * 60 + nowLocal.Minute;
			var cards = await _op.BoardAsync(ctx, nowLocal.Date, nowMinute, nowUtc, ct);
			var all = await _op.PractitionersAsync(ctx, ct);
			var doctors = cards.Select(c => c.Practitioner).Concat(all.Where(p => rows.Any(r => r.A.PractitionerId == p.Row.ID)))
				.Where(p => department == null || p.Row.DepartmentId == department)
				.GroupBy(p => p.Row.ID).Select(g => g.First()).OrderBy(p => DisplayName.Of(p.Row.Name, p.Row.NameEn)).ToList();
			var taken = new Dictionary<(int Doctor, int Minute), HospitalDeskRow>();
			foreach (var r in rows.Where(r => r.Stage != HospitalDeskStage.Cancelled)) taken.TryAdd((r.A.PractitionerId, r.A.StartMinute), r);
			var free = new HashSet<(int Doctor, int Minute)>();
			foreach (var d in doctors)
				foreach (var m in await _portal.StaffFreeSlotsAsync(ctx, d.Row.ID, nowLocal.Date, nowLocal, ct)) free.Add((d.Row.ID, m));
			var ids = doctors.Select(d => d.Row.ID).ToHashSet();
			var minutes = HospitalFrontDeskRules.GridMinutes(taken.Keys.Where(k => ids.Contains(k.Doctor)).Select(k => k.Minute), free.Select(f => f.Minute));
			return new HospitalDeskGrid(doctors, minutes, taken, free, nowMinute);
		}

		/// The doctors working today with their free slots from now on, for a walk-in.
		public async Task<IReadOnlyList<HospitalDeskSlots>> WalkInSlotsAsync(BusinessContext ctx, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			int nowMinute = nowLocal.Hour * 60 + nowLocal.Minute;
			var list = new List<HospitalDeskSlots>();
			foreach (var c in await _op.BoardAsync(ctx, nowLocal.Date, nowMinute, nowUtc, ct))
			{
				var free = (await _portal.StaffFreeSlotsAsync(ctx, c.Practitioner.Row.ID, nowLocal.Date, nowLocal, ct)).Where(m => m >= nowMinute - 10).OrderBy(m => m).ToList();
				list.Add(new HospitalDeskSlots(c.Practitioner, c.Waiting.Count, free));
			}
			return list.OrderBy(s => s.Free.Count == 0 ? 1 : 0).ThenBy(s => s.Free.FirstOrDefault()).ToList();
		}

		/// The desk's strip and drawer: who sits here, their open shift, the day's collections (everyone's, by method),
		/// their last receipts, and the last queue number called into a room.
		public async Task<HospitalDeskMe> MeAsync(BusinessContext ctx, DateTime todayLocal, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			string name = "-"; string? photo = null;
			HospitalShiftSummary? summary = null;
			if (ctx.EmployeeId is int me)
			{
				var e = await _db.Employee.AsNoTracking().Where(x => x.ID == me && x.EmpCompanyID == cid).Select(x => new { x.FullName, x.FullNameEn }).FirstOrDefaultAsync(ct);
				if (e != null) name = DisplayName.Of(e.FullName, e.FullNameEn);
				photo = (await EmployeePhotos.ResolveAsync(_db, ctx, new[] { me }, ct)).GetValueOrDefault(me);
				var shift = await _billing.OpenShiftOfAsync(ctx, me, ct);
				if (shift != null) summary = await _billing.ShiftSummaryAsync(ctx, shift.ID, ct);
			}
			var startUtc = HospitalNursingService.WardToUtc(todayLocal.Date);
			var pays = await _db.HospitalPayments.AsNoTracking().Where(p => p.CompanyId == cid && p.ReceivedAt >= startUtc).ToListAsync(ct);
			var by = HospitalPaymentMethods.All.ToDictionary(x => x, x => pays.Where(p => p.Method == x).Sum(p => p.BaseAmount ?? p.Amount));
			var mine = pays.Where(p => p.CashierEmployeeId == ctx.EmployeeId).OrderByDescending(p => p.ReceivedAt).Take(5).ToList();
			var billIds = mine.Select(p => p.BillId).ToList();
			var who = await (from b in _db.HospitalBills.AsNoTracking()
							 join p in _db.HospitalPatients.AsNoTracking() on b.PatientId equals p.ID
							 where b.CompanyId == cid && billIds.Contains(b.ID)
							 select new { b.ID, p.Name, p.NameEn }).ToDictionaryAsync(x => x.ID, ct);
			var receipts = mine.Select(p => new HospitalDeskReceipt(p.ReceiptNo, who.GetValueOrDefault(p.BillId)?.Name ?? "-", who.GetValueOrDefault(p.BillId)?.NameEn,
				p.BaseAmount ?? p.Amount, p.Method, p.ReceivedAt)).ToList();
			var day = todayLocal.Date;
			int? last = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == cid && a.AppointmentDate == day && a.StartedAt != null && a.QueueNumber != null)
				.OrderByDescending(a => a.StartedAt).Select(a => a.QueueNumber).FirstOrDefaultAsync(ct);
			return new HospitalDeskMe(name, photo, summary, receipts, last, by, pays.Count);
		}

		/// The patient's open bills for the counter's payment window.
		public async Task<IReadOnlyList<HospitalBillRow>> OpenBillsAsync(BusinessContext ctx, int patientId, DateTime nowUtc, CancellationToken ct = default)
			=> (await _billing.BillsAsync(ctx, null, patientId, nowUtc, ct)).Where(b => b.Total - b.Paid > 0).OrderBy(b => b.OpenedAt).ToList();
	}
}
