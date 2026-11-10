using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H48 — the patient portal's EARLIER-SLOT WAITLIST (the prototype's #/portal-waitlist; owner rulings:
	// email only; the offer is taken from the emailed link on the sign-in page, as the password-reset link is).
	//
	//   · A patient who books online may tick "email me if an earlier slot frees up": that booking joins the line of
	//     its doctor, in the order it was booked.
	//   · THE SWEEP (RunAsync) lapses every offer whose time ran out, then gives each free earlier time of a doctor to
	//     the FIRST booking in that doctor's line it suits — one patient at a time (one open offer per time, one per
	//     booking: filtered unique indexes in the slice). The time is held for that patient for OfferMinutes: portal
	//     booking treats it as taken (HeldAsync). A booking never gets again a time it declined or let lapse.
	//   · Taking it books the earlier time and releases the later booking IN ONE TRANSACTION; the released time is
	//     then offered in its turn. Declining, a lapse, or the desk's "pass to the next" sends it down the line.
	//   · There is no background worker (Program.cs is not this tab's): the sweep runs whenever something can free a
	//     time or end an offer — a cancellation (portal or desk), an answer to an offer, the desk's screen, and the
	//     portal's own pages. A lapsed offer therefore passes on at the next of those, not to the second.
	// ==========================================================================================

	public sealed record HospitalWaitlistOfferMail(string PatientName, string DoctorName, DateTime NewStart, DateTime OldStart, int Minutes);

	/// What the emailed link shows: the earlier time against the one the patient holds.
	public sealed record HospitalWaitlistOfferView(string PatientName, string DoctorName, string? DoctorTitle, DateTime NewStart, DateTime OldStart, int MinutesLeft);

	public static class HospitalWaitlistState
	{
		public const string Waiting = "Waiting";
		public const string Offered = "Offered";
		public const string Took = "Took";
		public const string Lapsed = "Lapsed";
		/// The desk ended the last offer and passed it on; the booking is still in line.
		public const string Passed = "Passed";
	}

	public sealed record HospitalWaitlistRow(int BookingId, int? OfferId, int PatientId, string Name, string NameEn, string? Mrn, string? Photo, string Clinic, string ClinicEn,
		string Doctor, string DoctorEn, DateTime CurrentStart, DateTime Since, string State, int Place, int MinutesLeft, DateTime? OfferedStart, double DaysGained);

	public sealed record HospitalWaitlistEvent(DateTime AtUtc, string Kind, string Name, string NameEn);

	public sealed record HospitalWaitlistBoard(IReadOnlyList<HospitalWaitlistRow> Rows, int Waiting, int OpenOffers, int Refilled, int Freed, double AverageDaysGained,
		int OfferMinutes, bool On, string? LastDoctor, string? LastDoctorEn, DateTime? LastSlot, IReadOnlyList<HospitalWaitlistEvent> LastEvents);

	public static class HospitalPortalWaitlistRules
	{
		/// The free times strictly before the waiting appointment, earliest first, without those this booking has
		/// already declined or let lapse.
		public static IReadOnlyList<(DateTime Day, int Minute)> Earlier(IEnumerable<(DateTime Day, int Minute)> free, DateTime currentStart, IReadOnlyCollection<(DateTime Day, int Minute)> passedUp) =>
			free.Where(f => f.Day.Date.AddMinutes(f.Minute) < currentStart && !passedUp.Contains((f.Day.Date, f.Minute)))
				.OrderBy(f => f.Day.Date).ThenBy(f => f.Minute).Select(f => (f.Day.Date, f.Minute)).Distinct().ToList();

		/// Whether an emailed offer can still be answered.
		public static bool Answerable(string status, DateTime expiresAtUtc, DateTime nowUtc) => status == HospitalPortalOfferStatus.Open && expiresAtUtc > nowUtc;

		/// The sweep offers a booking only a time freed after it joined the line: one already free when the patient
		/// booked, they saw and passed over.
		public static bool FreedForThisBooking(DateTime? freedAtUtc, DateTime joinedAtUtc) => freedAtUtc != null && freedAtUtc >= joinedAtUtc;

		public static int MinutesLeft(DateTime expiresAtUtc, DateTime nowUtc) => Math.Max(0, (int)Math.Ceiling((expiresAtUtc - nowUtc).TotalMinutes));

		public static double DaysGained(DateTime oldStart, DateTime newStart) => Math.Round(Math.Max(0, (oldStart - newStart).TotalDays), 1);

		/// Whether two visits of one patient overlap (the earlier time must not clash with another of their visits).
		public static bool Overlaps(int startA, int minutesA, int startB, int minutesB) => startA < startB + minutesB && startB < startA + minutesA;
	}

	public sealed class HospitalPortalWaitlistService
	{
		public delegate (string Subject, string Html) OfferMailer(string token, HospitalWaitlistOfferMail mail);

		private readonly CrossDbContext _db;
		private readonly IHospitalPortalMailer _mail;

		public HospitalPortalWaitlistService(CrossDbContext db, IHospitalPortalMailer mail) { _db = db; _mail = mail; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<HospitalPortalOffer> Offers => _db.Set<HospitalPortalOffer>();

		private async Task<HospitalPortalSettings> SettingsAsync(int companyId, CancellationToken ct) =>
			await _db.Set<HospitalPortalSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct) ?? new HospitalPortalSettings { CompanyId = companyId };

		/// The times held for an open offer (portal booking treats them as taken). A database without H48 holds none.
		public static async Task<List<(int PractitionerId, DateTime Day, int Minute)>> HeldAsync(CrossDbContext db, int companyId, IReadOnlyCollection<int> practitionerIds, DateTime from, DateTime to, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var rows = await db.Set<HospitalPortalOffer>().AsNoTracking()
					.Where(o => o.CompanyId == companyId && o.Status == HospitalPortalOfferStatus.Open && o.ExpiresAt > nowUtc && practitionerIds.Contains(o.PractitionerId) && o.OfferDate >= from.Date && o.OfferDate <= to.Date)
					.Select(o => new { o.PractitionerId, o.OfferDate, o.StartMinute }).ToListAsync(ct);
				return rows.Select(r => (r.PractitionerId, r.OfferDate.Date, r.StartMinute)).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(); }
		}

		// ====================================================================================
		// THE SWEEP
		// ====================================================================================

		/// Lapses what ran out, then offers every earlier time FREED BY A CANCELLATION since a booking joined the line to
		/// the first booking in its doctor's line (a time that was already free when the patient booked, they saw and
		/// passed over). `onlyBookingId` = the desk's "offer a slot": that one booking, the earliest free earlier time,
		/// freed or not. Returns the offers sent.
		public async Task<int> RunAsync(int companyId, OfferMailer mail, DateTime nowLocal, DateTime nowUtc, int? onlyBookingId = null, CancellationToken ct = default)
		{
			try
			{
				await Offers.Where(o => o.CompanyId == companyId && o.Status == HospitalPortalOfferStatus.Open && o.ExpiresAt <= nowUtc)
					.ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, HospitalPortalOfferStatus.Lapsed), ct);
				var s = await SettingsAsync(companyId, ct);
				if (!s.Waitlist) return 0;

				var line = await (from b in _db.Set<HospitalPortalBooking>().AsNoTracking()
								  join a in _db.HospitalAppointments.AsNoTracking() on b.AppointmentId equals a.ID
								  join acc in _db.Set<HospitalPortalAccount>().AsNoTracking() on b.AccountId equals acc.ID
								  where b.CompanyId == companyId && b.Waitlist && b.CancelledAt == null && a.CompanyId == companyId && a.Status == HospitalAppointmentStatus.Booked
									 && a.AppointmentDate >= nowLocal.Date && acc.Status != HospitalPortalAccountStatus.Disabled && acc.Email != null
								  orderby b.BookedAt, b.ID
								  select new { b.ID, b.AccountId, b.PatientId, b.BookedAt, AppointmentId = a.ID, a.PractitionerId, a.AppointmentDate, a.StartMinute, a.DurationMinutes, acc.Email }).ToListAsync(ct);
				line = line.Where(w => w.AppointmentDate.Date.AddMinutes(w.StartMinute) > nowLocal && HospitalPortalPasswordRules.LooksLikeEmail(w.Email)).ToList();
				if (line.Count == 0) return 0;

				var open = await Offers.AsNoTracking().Where(o => o.CompanyId == companyId && o.Status == HospitalPortalOfferStatus.Open)
					.Select(o => new { o.BookingId, o.PractitionerId, o.OfferDate, o.StartMinute }).ToListAsync(ct);
				var busy = open.Select(o => o.BookingId).ToHashSet();
				var held = open.Select(o => (o.PractitionerId, o.OfferDate.Date, o.StartMinute)).ToHashSet();
				var bookingIds = line.Select(w => w.ID).ToList();
				var passedUp = (await Offers.AsNoTracking().Where(o => bookingIds.Contains(o.BookingId) && o.Status != HospitalPortalOfferStatus.Open)
					.Select(o => new { o.BookingId, o.OfferDate, o.StartMinute }).ToListAsync(ct))
					.ToLookup(o => o.BookingId, o => (o.OfferDate.Date, o.StartMinute));

				var docIds = line.Select(w => w.PractitionerId).Distinct().ToList();
				var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == companyId && docIds.Contains(p.ID) && p.IsActive).ToListAsync(ct);
				var from = nowLocal.Date; var to = line.Max(w => w.AppointmentDate.Date);
				var sch = await HospitalScheduleLoader.LoadAsync(_db, companyId, docs, from, to, ct);
				var linePatients = line.Select(w => w.PatientId).Distinct().ToList();
				var live = await _db.HospitalAppointments.AsNoTracking()
					.Where(a => a.CompanyId == companyId && a.AppointmentDate >= from && a.AppointmentDate <= to && a.Status != HospitalAppointmentStatus.Cancelled && (docIds.Contains(a.PractitionerId) || linePatients.Contains(a.PatientId)))
					.Select(a => new { a.ID, a.PractitionerId, a.PatientId, a.AppointmentDate, a.StartMinute, a.DurationMinutes }).ToListAsync(ct);
				var names = await NamesAsync(companyId, line.Select(w => w.PatientId), docIds, ct);
				// when each time of these doctors was last freed by a cancellation (or released by a taken offer)
				var freedAt = (await _db.HospitalAppointments.AsNoTracking()
					.Where(a => a.CompanyId == companyId && a.AppointmentDate >= from && a.AppointmentDate <= to && a.Status == HospitalAppointmentStatus.Cancelled && docIds.Contains(a.PractitionerId) && a.UpdatedAt != null)
					.Select(a => new { a.PractitionerId, a.AppointmentDate, a.StartMinute, a.UpdatedAt }).ToListAsync(ct))
					.GroupBy(a => (a.PractitionerId, a.AppointmentDate.Date, a.StartMinute)).ToDictionary(g => g.Key, g => g.Max(a => a.UpdatedAt));

				int sent = 0;
				foreach (var w in line)
				{
					if (busy.Contains(w.ID) || (onlyBookingId != null && w.ID != onlyBookingId)) continue;
					var doc = docs.FirstOrDefault(d => d.ID == w.PractitionerId);
					if (doc == null) continue;
					var current = w.AppointmentDate.Date.AddMinutes(w.StartMinute);
					var free = new List<(DateTime, int)>();
					for (var d = from; d <= w.AppointmentDate.Date; d = d.AddDays(1))
					{
						var taken = live.Where(a => a.PractitionerId == doc.ID && a.AppointmentDate.Date == d).Select(a => a.StartMinute)
							.Concat(held.Where(h => h.PractitionerId == doc.ID && h.Item2 == d).Select(h => h.StartMinute)).ToList();
						free.AddRange(HospitalPortalPolicy.FreeSlots(sch[doc.ID], d, taken, s, nowLocal)
							.Where(m => onlyBookingId != null || HospitalPortalWaitlistRules.FreedForThisBooking(freedAt.GetValueOrDefault((doc.ID, d, m)), w.BookedAt))
							.Select(m => (d, m)));
					}
					var choice = HospitalPortalWaitlistRules.Earlier(free, current, passedUp[w.ID].ToList())
						.Select(x => ((DateTime Day, int Minute)?)x)
						.FirstOrDefault(x =>
						{
							int dur = sch[doc.ID].DurationAt(x!.Value.Day, x.Value.Minute) ?? doc.SlotMinutes;
							return !live.Any(a => a.PatientId == w.PatientId && a.ID != w.AppointmentId && a.AppointmentDate.Date == x.Value.Day && HospitalPortalWaitlistRules.Overlaps(a.StartMinute, a.DurationMinutes, x.Value.Minute, dur));
						});
					if (choice is not { } c) continue;

					var token = HospitalPortalPasswordRules.NewToken();
					var row = new HospitalPortalOffer
					{
						CompanyId = companyId, BookingId = w.ID, AppointmentId = w.AppointmentId, AccountId = w.AccountId, PatientId = w.PatientId, PractitionerId = doc.ID,
						OfferDate = c.Day, StartMinute = c.Minute, TokenHash = HospitalPortalPasswordRules.Hash(token), Email = w.Email!.Trim(),
						Status = HospitalPortalOfferStatus.Open, SentAt = nowUtc, ExpiresAt = nowUtc.AddMinutes(s.OfferMinutes),
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); }
					catch (DbUpdateException) { _db.ChangeTracker.Clear(); continue; }   // another sweep offered it a moment ago
					busy.Add(w.ID); held.Add((doc.ID, c.Day, c.Minute));
					var (subject, html) = mail(token, new HospitalWaitlistOfferMail(names.Patient(w.PatientId), names.Doctor(doc.ID), c.Day.AddMinutes(c.Minute), current, s.OfferMinutes));
					await _mail.SendAsync(row.Email, subject, html, ct);
					sent++;
				}
				return sent;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return 0; }
		}

		private sealed record Names(Dictionary<int, (string Name, string NameEn)> Patients, Dictionary<int, (string Name, string NameEn)> Doctors)
		{
			public string Patient(int id) => Patients.TryGetValue(id, out var n) ? DisplayName.Of(n.Name, n.NameEn) : "";
			public string Doctor(int id) => Doctors.TryGetValue(id, out var n) ? DisplayName.Of(n.Name, n.NameEn) : "";
		}

		private async Task<Names> NamesAsync(int companyId, IEnumerable<int> patientIds, IEnumerable<int> doctorIds, CancellationToken ct)
		{
			var pids = patientIds.Distinct().ToList(); var dids = doctorIds.Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == companyId && pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn }).ToListAsync(ct);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == companyId && dids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn }).ToListAsync(ct);
			return new Names(pats.ToDictionary(p => p.ID, p => (p.Name, p.NameEn ?? "")), docs.ToDictionary(p => p.ID, p => (p.Name, p.NameEn ?? "")));
		}

		// ====================================================================================
		// THE EMAILED LINK (the token is the credential: only its hash is kept)
		// ====================================================================================

		private async Task<HospitalPortalOffer?> OfferOfAsync(string? token, CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
			var hash = HospitalPortalPasswordRules.Hash(token);
			try { return await Offers.FirstOrDefaultAsync(o => o.TokenHash == hash, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The offer behind a link, or null when it can no longer be answered (taken, declined, lapsed, passed, or the
		/// later booking is no longer booked).
		public async Task<HospitalWaitlistOfferView?> LinkAsync(string? token, DateTime nowUtc, CancellationToken ct = default)
		{
			var o = await OfferOfAsync(token, ct);
			if (o == null || !HospitalPortalWaitlistRules.Answerable(o.Status, o.ExpiresAt, nowUtc)) return null;
			var old = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(a => a.ID == o.AppointmentId && a.CompanyId == o.CompanyId, ct);
			if (old == null || old.Status != HospitalAppointmentStatus.Booked) return null;
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == o.PractitionerId && p.CompanyId == o.CompanyId, ct);
			var names = await NamesAsync(o.CompanyId, new[] { o.PatientId }, new[] { o.PractitionerId }, ct);
			return new HospitalWaitlistOfferView(names.Patient(o.PatientId), names.Doctor(o.PractitionerId), doc == null ? null : DisplayName.Of(doc.Title ?? "", doc.TitleEn ?? ""),
				o.OfferDate.Date.AddMinutes(o.StartMinute), old.AppointmentDate.Date.AddMinutes(old.StartMinute), HospitalPortalWaitlistRules.MinutesLeft(o.ExpiresAt, nowUtc));
		}

		/// Takes the earlier time: books it and releases the later booking, in one transaction; then the released time
		/// goes down the line. "expired" = the link can no longer be answered.
		public async Task<HospitalResult> TakeAsync(string? token, OfferMailer mail, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			var o = await OfferOfAsync(token, ct);
			if (o == null || !HospitalPortalWaitlistRules.Answerable(o.Status, o.ExpiresAt, nowUtc)) return HospitalResult.Fail("expired");
			var old = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(a => a.ID == o.AppointmentId && a.CompanyId == o.CompanyId, ct);
			if (old == null || old.Status != HospitalAppointmentStatus.Booked) return HospitalResult.Fail("expired");
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == o.PractitionerId && p.CompanyId == o.CompanyId, ct);
			if (doc == null) return HospitalResult.Fail("expired");
			var day = o.OfferDate.Date;
			var sch = await HospitalScheduleLoader.LoadAsync(_db, o.CompanyId, new[] { doc }, day, day, ct);
			int duration = sch[doc.ID].DurationAt(day, o.StartMinute) ?? doc.SlotMinutes;
			var mine = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == o.CompanyId && a.PatientId == o.PatientId && a.AppointmentDate == day && a.Status != HospitalAppointmentStatus.Cancelled && a.ID != old.ID)
				.Select(a => new { a.StartMinute, a.DurationMinutes }).ToListAsync(ct);
			if (mine.Any(a => HospitalPortalWaitlistRules.Overlaps(a.StartMinute, a.DurationMinutes, o.StartMinute, duration))) return HospitalResult.Fail("The patient already has an appointment at that time");

			var booking = await _db.Set<HospitalPortalBooking>().AsNoTracking().FirstAsync(b => b.ID == o.BookingId, ct);
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var appt = new HospitalAppointment
			{
				CompanyId = o.CompanyId, PatientId = o.PatientId, PractitionerId = doc.ID, AppointmentDate = day, StartMinute = o.StartMinute, DurationMinutes = duration,
				VisitType = old.VisitType, Status = HospitalAppointmentStatus.Booked, Note = "Portal · earlier slot", CreatedAt = nowUtc,
			};
			_db.HospitalAppointments.Add(appt);
			try
			{
				await _db.SaveChangesAsync(ct);   // the one-booking-per-slot index refuses a time the desk took a moment ago
				int n = await _db.HospitalAppointments.Where(a => a.ID == old.ID && a.Status == HospitalAppointmentStatus.Booked)
					.ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, HospitalAppointmentStatus.Cancelled).SetProperty(a => a.UpdatedAt, nowUtc), ct);
				int m = await Offers.Where(x => x.ID == o.ID && x.Status == HospitalPortalOfferStatus.Open)
					.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalPortalOfferStatus.Taken).SetProperty(x => x.RespondedAt, nowUtc).SetProperty(x => x.NewAppointmentId, appt.ID), ct);
				if (n != 1 || m != 1) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("expired"); }
				await _db.Set<HospitalPortalBooking>().Where(b => b.ID == booking.ID).ExecuteUpdateAsync(u => u.SetProperty(b => b.CancelledAt, nowUtc), ct);
				_db.Add(new HospitalPortalBooking { CompanyId = o.CompanyId, AppointmentId = appt.ID, AccountId = booking.AccountId, PatientId = o.PatientId, Referral = booking.Referral, BookedAt = nowUtc });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
			}
			catch (DbUpdateException) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("That time is no longer free"); }
			await RunAsync(o.CompanyId, mail, nowLocal, nowUtc, null, ct);   // the later time is free now: down the line
			return HospitalResult.Success(appt.ID);
		}

		/// "No, thanks": the patient keeps the booking they hold and stays in line; the time goes to the next.
		public async Task<HospitalResult> DeclineAsync(string? token, OfferMailer mail, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			var o = await OfferOfAsync(token, ct);
			if (o == null || !HospitalPortalWaitlistRules.Answerable(o.Status, o.ExpiresAt, nowUtc)) return HospitalResult.Fail("expired");
			int n = await Offers.Where(x => x.ID == o.ID && x.Status == HospitalPortalOfferStatus.Open)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalPortalOfferStatus.Declined).SetProperty(x => x.RespondedAt, nowUtc), ct);
			if (n != 1) return HospitalResult.Fail("expired");
			await RunAsync(o.CompanyId, mail, nowLocal, nowUtc, null, ct);
			return HospitalResult.Success(o.ID);
		}

		// ====================================================================================
		// THE DESK
		// ====================================================================================

		/// "Offer a slot": the earliest free earlier time, to this one booking, now.
		public async Task<HospitalResult> OfferNowAsync(BusinessContext ctx, int bookingId, OfferMailer mail, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			var b = await _db.Set<HospitalPortalBooking>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == bookingId && x.CompanyId == ctx.CompanyId, ct);
			if (b == null || !b.Waitlist || b.CancelledAt != null) return HospitalResult.Fail("not_found");
			if (await Offers.AnyAsync(o => o.BookingId == bookingId && o.Status == HospitalPortalOfferStatus.Open && o.ExpiresAt > nowUtc, ct)) return HospitalResult.Fail("An offer is already open for this patient");
			if (!(await SettingsAsync(ctx.CompanyId, ct)).Waitlist) return HospitalResult.Fail("The earlier-slot waitlist is turned off in the portal settings");
			int sent = await RunAsync(ctx.CompanyId, mail, nowLocal, nowUtc, bookingId, ct);
			return sent == 1 ? HospitalResult.Success(bookingId) : HospitalResult.Fail("No earlier free time with this doctor right now");
		}

		/// "Pass to the next": ends this offer and sends the time down the line.
		public async Task<HospitalResult> PassAsync(BusinessContext ctx, int offerId, int? employeeId, OfferMailer mail, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			int n = await Offers.Where(o => o.ID == offerId && o.CompanyId == ctx.CompanyId && o.Status == HospitalPortalOfferStatus.Open)
				.ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, HospitalPortalOfferStatus.Passed).SetProperty(o => o.RespondedAt, nowUtc).SetProperty(o => o.PassedByEmployeeId, employeeId), ct);
			if (n != 1) return HospitalResult.Fail("This offer is no longer open");
			await RunAsync(ctx.CompanyId, mail, nowLocal, nowUtc, null, ct);
			return HospitalResult.Success(offerId);
		}

		/// The desk's screen: the line (waiting and offered, in order), this month's taken offers, the figures, and
		/// what happened to the last time that freed up. Null = the H48 tables are not there.
		public async Task<HospitalWaitlistBoard?> BoardAsync(BusinessContext ctx, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				int c = ctx.CompanyId;
				var s = await SettingsAsync(c, ct);
				var monthStart = new DateTime(nowLocal.Year, nowLocal.Month, 1);
				var monthStartUtc = monthStart.ToUniversalTime();
				var line = await (from b in _db.Set<HospitalPortalBooking>().AsNoTracking()
								  join a in _db.HospitalAppointments.AsNoTracking() on b.AppointmentId equals a.ID
								  where b.CompanyId == c && b.Waitlist && b.CancelledAt == null && a.Status == HospitalAppointmentStatus.Booked && a.AppointmentDate >= nowLocal.Date
								  orderby b.BookedAt, b.ID
								  select new { b.ID, b.PatientId, b.BookedAt, a.PractitionerId, a.AppointmentDate, a.StartMinute }).ToListAsync(ct);
				var ids = line.Select(w => w.ID).ToList();
				var offers = await Offers.AsNoTracking().Where(o => o.CompanyId == c && (ids.Contains(o.BookingId) || (o.Status == HospitalPortalOfferStatus.Taken && o.RespondedAt >= monthStartUtc))).ToListAsync(ct);
				var taken = offers.Where(o => o.Status == HospitalPortalOfferStatus.Taken && o.RespondedAt >= monthStartUtc).ToList();
				var oldIds = taken.Select(o => o.AppointmentId).ToList();
				var olds = await _db.HospitalAppointments.AsNoTracking().Where(a => oldIds.Contains(a.ID)).Select(a => new { a.ID, a.AppointmentDate, a.StartMinute }).ToDictionaryAsync(a => a.ID, ct);
				var tookIds = taken.Select(o => o.BookingId).ToList();
				var tookBookings = await _db.Set<HospitalPortalBooking>().AsNoTracking().Where(b => tookIds.Contains(b.ID)).Select(b => new { b.ID, b.BookedAt }).ToDictionaryAsync(b => b.ID, ct);

				var pids = line.Select(w => w.PatientId).Concat(taken.Select(o => o.PatientId)).Distinct().ToList();
				var dids = line.Select(w => w.PractitionerId).Concat(taken.Select(o => o.PractitionerId)).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == c && pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToDictionaryAsync(p => p.ID, ct);
				var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == c && dids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.DepartmentId }).ToDictionaryAsync(p => p.ID, ct);
				var depIds = docs.Values.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value).Distinct().ToList();
				var deps = await _db.HospitalDepartments.AsNoTracking().Where(d => depIds.Contains(d.ID)).Select(d => new { d.ID, d.Name, d.NameEn }).ToDictionaryAsync(d => d.ID, ct);

				HospitalWaitlistRow Row(int bookingId, int? offerId, int pid, int did, DateTime current, DateTime since, string state, int place, int left, DateTime? offered, double gained)
				{
					var p = pats.GetValueOrDefault(pid); var d = docs.GetValueOrDefault(did); var dep = d?.DepartmentId is int x ? deps.GetValueOrDefault(x) : null;
					return new HospitalWaitlistRow(bookingId, offerId, pid, p?.Name ?? "", p?.NameEn ?? "", p?.Mrn, p?.PhotoPath, dep?.Name ?? "", dep?.NameEn ?? "",
						d?.Name ?? "", d?.NameEn ?? "", current, since, state, place, left, offered, gained);
				}

				var rows = new List<HospitalWaitlistRow>();
				foreach (var g in line.GroupBy(w => w.PractitionerId))
				{
					int place = 0;
					foreach (var w in g)
					{
						place++;
						var mine = offers.Where(o => o.BookingId == w.ID).OrderByDescending(o => o.SentAt).ToList();
						var openOffer = mine.FirstOrDefault(o => o.Status == HospitalPortalOfferStatus.Open && o.ExpiresAt > nowUtc);
						var last = mine.FirstOrDefault();
						var state = openOffer != null ? HospitalWaitlistState.Offered
							: last?.Status == HospitalPortalOfferStatus.Passed ? HospitalWaitlistState.Passed
							: last?.Status is HospitalPortalOfferStatus.Lapsed or HospitalPortalOfferStatus.Open ? HospitalWaitlistState.Lapsed
							: HospitalWaitlistState.Waiting;
						rows.Add(Row(w.ID, openOffer?.ID, w.PatientId, w.PractitionerId, w.AppointmentDate.Date.AddMinutes(w.StartMinute), w.BookedAt, state, place,
							openOffer == null ? 0 : HospitalPortalWaitlistRules.MinutesLeft(openOffer.ExpiresAt, nowUtc), openOffer == null ? null : openOffer.OfferDate.Date.AddMinutes(openOffer.StartMinute), 0));
					}
				}
				rows = rows.OrderBy(r => r.Since).ToList();
				var gains = new List<double>();
				foreach (var o in taken.OrderByDescending(o => o.RespondedAt))
				{
					var old = olds.GetValueOrDefault(o.AppointmentId); if (old == null) continue;
					var oldStart = old.AppointmentDate.Date.AddMinutes(old.StartMinute); var newStart = o.OfferDate.Date.AddMinutes(o.StartMinute);
					double gained = HospitalPortalWaitlistRules.DaysGained(oldStart, newStart); gains.Add(gained);
					rows.Add(Row(o.BookingId, o.ID, o.PatientId, o.PractitionerId, oldStart, tookBookings.GetValueOrDefault(o.BookingId)?.BookedAt ?? o.SentAt, HospitalWaitlistState.Took, 0, 0, newStart, gained));
				}
				// every time that freed up this month: a cancellation, or a later booking released by a taken offer
				int freed = await _db.HospitalAppointments.AsNoTracking().CountAsync(a => a.CompanyId == c && a.Status == HospitalAppointmentStatus.Cancelled && a.UpdatedAt >= monthStartUtc, ct);

				// the last time that freed up: every offer of it, in order
				var lastOffer = await Offers.AsNoTracking().Where(o => o.CompanyId == c).OrderByDescending(o => o.SentAt).FirstOrDefaultAsync(ct);
				var events = new List<HospitalWaitlistEvent>(); string? lastDoc = null, lastDocEn = null; DateTime? lastSlot = null;
				if (lastOffer != null)
				{
					var chain = await Offers.AsNoTracking().Where(o => o.CompanyId == c && o.PractitionerId == lastOffer.PractitionerId && o.OfferDate == lastOffer.OfferDate && o.StartMinute == lastOffer.StartMinute)
						.OrderBy(o => o.SentAt).ToListAsync(ct);
					var cpids = chain.Select(o => o.PatientId).Distinct().ToList();
					var cn = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == c && cpids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn }).ToDictionaryAsync(p => p.ID, ct);
					foreach (var o in chain)
					{
						var who = cn.GetValueOrDefault(o.PatientId);
						events.Add(new HospitalWaitlistEvent(o.SentAt, "Sent", who?.Name ?? "", who?.NameEn ?? ""));
						var end = o.Status switch
						{
							HospitalPortalOfferStatus.Taken => "Taken", HospitalPortalOfferStatus.Declined => "Declined", HospitalPortalOfferStatus.Passed => "Passed",
							HospitalPortalOfferStatus.Lapsed => "Lapsed", _ => o.ExpiresAt <= nowUtc ? "Lapsed" : null,
						};
						if (end != null) events.Add(new HospitalWaitlistEvent(o.RespondedAt ?? o.ExpiresAt, end, who?.Name ?? "", who?.NameEn ?? ""));
					}
					var ld = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.ID == lastOffer.PractitionerId).Select(p => new { p.Name, p.NameEn }).FirstOrDefaultAsync(ct);
					lastDoc = ld?.Name; lastDocEn = ld?.NameEn; lastSlot = lastOffer.OfferDate.Date.AddMinutes(lastOffer.StartMinute);
				}
				return new HospitalWaitlistBoard(rows, line.Count, rows.Count(r => r.State == HospitalWaitlistState.Offered), taken.Count, freed,
					gains.Count == 0 ? 0 : Math.Round(gains.Average(), 1), s.OfferMinutes, s.Waitlist, lastDoc, lastDocEn, lastSlot, events);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
