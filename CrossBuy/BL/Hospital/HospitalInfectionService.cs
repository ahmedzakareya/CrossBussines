using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H76 — infection prevention and control. The ward starts isolation, records the invasive devices it puts in
	// and takes out, and reports a suspected infection; infection control confirms or rules it out. Whether a case is
	// hospital-acquired, and which device it is associated with, follow the NHSN rules from the dates — never a tick.
	// The rates are per 1,000 patient-days and per 1,000 device-days, counted per ward from the bed stays.
	// ==========================================================================================
	public sealed class HospitalIsolationInput
	{
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		public string? Precaution { get; set; }
		public string? Organism { get; set; }
		public string? Reason { get; set; }
	}

	public sealed class HospitalDeviceInput
	{
		public int AdmissionId { get; set; }
		public string? Kind { get; set; }
		public string? Site { get; set; }
		/// Ward-local; empty means now.
		public DateTime? At { get; set; }
	}

	public sealed class HospitalInfectionCaseInput
	{
		public int AdmissionId { get; set; }
		public string? Kind { get; set; }
		public string? Organism { get; set; }
		/// Ward-local date of the event.
		public DateTime? OnsetDate { get; set; }
		public string? Notes { get; set; }
	}

	public sealed class HospitalInfectionDecideInput
	{
		public string? Decision { get; set; }
		public string? Note { get; set; }
	}

	/// What the ward sees beside a patient: the live isolations and the devices in place, each with its day.
	public sealed record HospitalIcDevice(int Id, string Kind, string? Site, DateTime InsertedAt, int Day);
	public sealed record HospitalIcBadge(IReadOnlyList<HospitalIsolation> Isolations, IReadOnlyList<HospitalIcDevice> Devices);

	public sealed record HospitalIcPatientRow(int PatientId, int? AdmissionId, string Name, string NameEn, string Mrn, string? Photo, string? Bed, string? Ward, string? WardEn);
	public sealed record HospitalIcIsolationRow(HospitalIsolation Isolation, HospitalIcPatientRow Patient);
	public sealed record HospitalIcDeviceRow(HospitalIcDevice Device, HospitalIcPatientRow Patient);
	public sealed record HospitalIcCaseRow(HospitalInfectionCase Case, string Name, string NameEn, string Mrn, string? Ward, string? WardEn, string? Device);
	public sealed record HospitalIcPage(bool Ready, IReadOnlyList<HospitalIcIsolationRow> Isolations, IReadOnlyList<HospitalIcDeviceRow> Devices, int Suspected,
		IReadOnlyDictionary<int, string> People);

	/// One ward's month: the days and the hospital-acquired confirmed cases, and the rates they make.
	public sealed record HospitalIcRateRow(int? WardId, string Ward, string WardEn, int PatientDays, int LineDays, int CatheterDays, int VentDays,
		int Hai, int Clabsi, int Cauti, int Vap)
	{
		public double? HaiRate => HospitalInfectionRules.Rate(Hai, PatientDays);
		public double? ClabsiRate => HospitalInfectionRules.Rate(Clabsi, LineDays);
		public double? CautiRate => HospitalInfectionRules.Rate(Cauti, CatheterDays);
		public double? VapRate => HospitalInfectionRules.Rate(Vap, VentDays);
		/// Device utilisation ratio: device-days over patient-days.
		public double? LineUse => PatientDays == 0 ? null : Math.Round((double)LineDays / PatientDays, 2);
		public double? CatheterUse => PatientDays == 0 ? null : Math.Round((double)CatheterDays / PatientDays, 2);
		public double? VentUse => PatientDays == 0 ? null : Math.Round((double)VentDays / PatientDays, 2);
	}

	public static class HospitalInfectionRules
	{
		public static string? IsolationRefusal(HospitalIsolationInput i)
		{
			if (!HospitalPrecaution.All.Contains(i.Precaution ?? "")) return "Choose the precautions";
			if ((i.Organism?.Trim().Length ?? 0) > 100) return "The organism's name is too long";
			if (string.IsNullOrWhiteSpace(i.Organism) && string.IsNullOrWhiteSpace(i.Reason)) return "Write the organism or why the patient is isolated";
			return null;
		}

		/// A device goes in during the stay, not in the future.
		public static string? DeviceRefusal(string? kind, DateTime atUtc, DateTime admittedAtUtc, DateTime nowUtc)
		{
			if (!HospitalDeviceKind.All.Contains(kind ?? "")) return "Choose the device";
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < admittedAtUtc.AddMinutes(-5)) return "The time is before the admission";
			return null;
		}

		public static string? RemoveRefusal(HospitalInvasiveDevice d, DateTime atUtc, DateTime nowUtc)
		{
			if (d.RemovedAt != null) return "This device is already out";
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < d.InsertedAt) return "The device cannot come out before it went in";
			return null;
		}

		/// NHSN: hospital-onset when the event falls on or after the third calendar day of the stay (admission day = day 1).
		public static bool HospitalAcquired(DateTime admittedLocalDate, DateTime onsetDate) => onsetDate.Date >= admittedLocalDate.Date.AddDays(2);

		/// NHSN device association: the device was in place for more than two consecutive calendar days on the date of the
		/// event (day 3 of the device or later), and was still in place that day or the day before.
		public static bool DeviceAssociated(DateTime insertedLocalDate, DateTime? removedLocalDate, DateTime onsetDate) =>
			insertedLocalDate.Date <= onsetDate.Date.AddDays(-2) && (removedLocalDate == null || removedLocalDate.Value.Date >= onsetDate.Date.AddDays(-1));

		public static string? CaseRefusal(string? kind, string? organism, DateTime? onsetDate, DateTime todayLocal, DateTime admittedLocalDate)
		{
			if (!HospitalInfectionKind.All.Contains(kind ?? "")) return "Choose the kind of infection";
			if (kind == HospitalInfectionKind.Mdro && string.IsNullOrWhiteSpace(organism)) return "Write the resistant organism";
			if ((organism?.Trim().Length ?? 0) > 100) return "The organism's name is too long";
			if (onsetDate == null) return "Write the date of the event";
			if (onsetDate.Value.Date > todayLocal.Date) return "The date is in the future";
			if (onsetDate.Value.Date < admittedLocalDate.Date) return "The date is before the admission";
			return null;
		}

		/// Infection control decides once: confirm (a device-associated kind needs its device) or rule out with a reason.
		public static string? DecideRefusal(HospitalInfectionCase c, string? decision, string? note)
		{
			if (c.Status != HospitalInfectionStatus.Suspected) return "This case is already decided";
			if (decision == HospitalInfectionStatus.Confirmed)
			{
				if (HospitalInfectionKind.DeviceOf(c.Kind) != null && c.DeviceId == null) return "No device was in place long enough for this kind of infection";
				return null;
			}
			if (decision == HospitalInfectionStatus.RuledOut) return string.IsNullOrWhiteSpace(note) ? "Write why it is ruled out" : null;
			return "Choose the decision";
		}

		/// The device's day today: the insertion day is day 1.
		public static int DeviceDay(DateTime insertedLocal, DateTime todayLocal) => Math.Max(1, (todayLocal.Date - insertedLocal.Date).Days + 1);

		/// The calendar days of [start, end] (end null = still running, up to `until`) that fall in [from, to].
		public static IEnumerable<DateTime> Days(DateTime startLocal, DateTime? endLocal, DateTime from, DateTime to, DateTime until)
		{
			var a = startLocal.Date > from.Date ? startLocal.Date : from.Date;
			var e = (endLocal ?? until).Date;
			var b = e < to.Date ? e : to.Date;
			for (var d = a; d <= b; d = d.AddDays(1)) yield return d;
		}

		/// Events per 1,000 days, one decimal; no days, no rate.
		public static double? Rate(int events, int days) => days == 0 ? null : Math.Round(events * 1000.0 / days, 1);
	}

	public sealed class HospitalInfectionService
	{
		public const string NoTables = "Infection control is not installed yet (hospital_h76_infection.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalInfectionService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Local(DateTime utc) => HospitalNursingService.UtcToWard(utc);
		private IQueryable<HospitalIsolation> Isolations(BusinessContext ctx) => _db.Set<HospitalIsolation>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalInvasiveDevice> Devices(BusinessContext ctx) => _db.Set<HospitalInvasiveDevice>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalInfectionCase> Cases(BusinessContext ctx) => _db.Set<HospitalInfectionCase>().Where(x => x.CompanyId == ctx.CompanyId);
		private Task<HospitalAdmission?> AdmissionAsync(BusinessContext ctx, int id, CancellationToken ct) =>
			_db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == id && a.CompanyId == ctx.CompanyId, ct);

		private async Task<bool> AnyAsync(BusinessContext ctx, CancellationToken ct, params string[] actions)
		{
			foreach (var a in actions) if (await _access.CanAsync(ctx, a, null, ct)) return true;
			return false;
		}

		/// The live isolations and devices in place of these admissions (the nursing station's badges). Empty when not installed.
		public static async Task<IReadOnlyDictionary<int, HospitalIcBadge>> BadgesAsync(CrossDbContext db, int companyId, IReadOnlyCollection<(int AdmissionId, int PatientId)> who, DateTime nowUtc, CancellationToken ct)
		{
			var result = new Dictionary<int, HospitalIcBadge>();
			if (who.Count == 0) return result;
			var admissionIds = who.Select(w => w.AdmissionId).ToList();
			var patientIds = who.Select(w => w.PatientId).Distinct().ToList();
			try
			{
				var iso = await db.Set<HospitalIsolation>().AsNoTracking().Where(x => x.CompanyId == companyId && x.EndedAt == null && patientIds.Contains(x.PatientId)).OrderBy(x => x.StartedAt).ToListAsync(ct);
				var dev = await db.Set<HospitalInvasiveDevice>().AsNoTracking().Where(x => x.CompanyId == companyId && x.RemovedAt == null && admissionIds.Contains(x.AdmissionId)).OrderBy(x => x.InsertedAt).ToListAsync(ct);
				var today = Local(nowUtc);
				foreach (var w in who)
				{
					var i = iso.Where(x => x.PatientId == w.PatientId).ToList();
					var d = dev.Where(x => x.AdmissionId == w.AdmissionId).Select(x => new HospitalIcDevice(x.ID, x.Kind, x.Site, x.InsertedAt, HospitalInfectionRules.DeviceDay(Local(x.InsertedAt), today))).ToList();
					if (i.Count > 0 || d.Count > 0) result[w.AdmissionId] = new HospitalIcBadge(i, d);
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return result;
		}

		/// The patients under isolation now (the map and the ward board mark their beds). Empty when not installed.
		public static async Task<HashSet<int>> IsolatedPatientsAsync(CrossDbContext db, int companyId, CancellationToken ct)
		{
			try { return (await db.Set<HospitalIsolation>().AsNoTracking().Where(x => x.CompanyId == companyId && x.EndedAt == null).Select(x => x.PatientId).Distinct().ToListAsync(ct)).ToHashSet(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HashSet<int>(); }
		}

		// ---------------------------------------------------------------- isolation

		public async Task<HospitalResult> StartIsolationAsync(BusinessContext ctx, HospitalIsolationInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await AnyAsync(ctx, ct, HospitalActions.Admit, HospitalActions.Consult, HospitalActions.Manage)) return HospitalResult.Fail("forbidden");
			if (HospitalInfectionRules.IsolationRefusal(input) is string why) return HospitalResult.Fail(why);
			int patientId = input.PatientId;
			int? admissionId = null;
			if (input.AdmissionId is int aid)
			{
				var adm = await AdmissionAsync(ctx, aid, ct);
				if (adm == null) return HospitalResult.Fail("not_found");
				patientId = adm.PatientId; admissionId = adm.ID;
			}
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				if (await Isolations(ctx).AnyAsync(x => x.PatientId == patientId && x.Precaution == input.Precaution && x.EndedAt == null, ct))
					return HospitalResult.Fail("The patient is already under these precautions");
				var row = new HospitalIsolation
				{
					CompanyId = ctx.CompanyId, PatientId = patientId, AdmissionId = admissionId, Precaution = input.Precaution!, Organism = Clip(input.Organism, 100),
					Reason = Clip(input.Reason, 300), StartedAt = nowUtc, StartedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Ending precautions is a clinical decision (the doctor's or infection control's), not the ward desk's.
		public async Task<HospitalResult> EndIsolationAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await AnyAsync(ctx, ct, HospitalActions.Consult, HospitalActions.Manage)) return HospitalResult.Fail("forbidden");
			try
			{
				var row = await Isolations(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				if (row.EndedAt != null) return HospitalResult.Fail("These precautions have already ended");
				row.EndedAt = nowUtc; row.EndedByEmployeeId = employeeId; row.EndNote = Clip(note, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------- devices

		public async Task<HospitalResult> InsertDeviceAsync(BusinessContext ctx, HospitalDeviceInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await AnyAsync(ctx, ct, HospitalActions.Admit, HospitalActions.Consult)) return HospitalResult.Fail("forbidden");
			var adm = await AdmissionAsync(ctx, input.AdmissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
			if (HospitalInfectionRules.DeviceRefusal(input.Kind, at, adm.AdmittedAt, nowUtc) is string why) return HospitalResult.Fail(why);
			try
			{
				if (await Devices(ctx).AnyAsync(x => x.AdmissionId == adm.ID && x.Kind == input.Kind && x.RemovedAt == null, ct))
					return HospitalResult.Fail("One is already in place: record its removal first");
				var row = new HospitalInvasiveDevice
				{
					CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Kind = input.Kind!, Site = Clip(input.Site, 100),
					InsertedAt = at, InsertedByEmployeeId = employeeId, CreatedAt = nowUtc,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveDeviceAsync(BusinessContext ctx, int id, DateTime? atLocal, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await AnyAsync(ctx, ct, HospitalActions.Admit, HospitalActions.Consult)) return HospitalResult.Fail("forbidden");
			try
			{
				var row = await Devices(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				var at = atLocal == null ? nowUtc : HospitalNursingService.WardToUtc(atLocal.Value);
				if (HospitalInfectionRules.RemoveRefusal(row, at, nowUtc) is string why) return HospitalResult.Fail(why);
				row.RemovedAt = at; row.RemovedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------- cases

		/// The ward reports a suspected infection. Hospital-acquired, the ward it counts on and the associated device are
		/// worked out here from the dates.
		public async Task<HospitalResult> ReportCaseAsync(BusinessContext ctx, HospitalInfectionCaseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await AnyAsync(ctx, ct, HospitalActions.Admit, HospitalActions.Consult, HospitalActions.Manage)) return HospitalResult.Fail("forbidden");
			var adm = await AdmissionAsync(ctx, input.AdmissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			var admittedLocal = Local(adm.AdmittedAt).Date;
			if (HospitalInfectionRules.CaseRefusal(input.Kind, input.Organism, input.OnsetDate, Local(nowUtc), admittedLocal) is string why) return HospitalResult.Fail(why);
			if (adm.DischargedAt != null && input.OnsetDate!.Value.Date > Local(adm.DischargedAt.Value).Date) return HospitalResult.Fail("The date is after the discharge");
			var onset = input.OnsetDate!.Value.Date;
			try
			{
				var id = await RecordCaseAsync(ctx, adm, input.Kind!, input.Organism, onset, input.Notes, employeeId, nowUtc, ct);
				return HospitalResult.Success(id);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// H230: a verified culture with a multidrug-resistant organism opens a SUSPECTED MDRO case for infection control,
		/// when the patient is admitted (an outpatient's flag stays on the culture). The microbiologist verified the
		/// culture, so no ward permission is asked here; infection control still confirms or rules it out as any case,
		/// and confirming puts the patient under contact precautions. One open case per stay and organism.
		public async Task<int?> OpenLabMdroCaseAsync(BusinessContext ctx, int patientId, string organism, DateTime specimenUtc, string notes, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var adm = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.DischargedAt == null)
					.OrderByDescending(a => a.AdmittedAt).FirstOrDefaultAsync(ct);
				if (adm == null) return null;
				var org = Clip(organism, 100);
				if (await Cases(ctx).AnyAsync(c => c.AdmissionId == adm.ID && c.Kind == HospitalInfectionKind.Mdro && c.Organism == org && c.Status == HospitalInfectionStatus.Suspected, ct)) return null;
				var onset = Local(specimenUtc).Date;
				if (onset < Local(adm.AdmittedAt).Date) onset = Local(adm.AdmittedAt).Date;   // taken at the door: day 1 of the stay
				return await RecordCaseAsync(ctx, adm, HospitalInfectionKind.Mdro, org, onset, notes, employeeId, nowUtc, ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The one place a case row is written: its ward that day, its device when device-associated, its number.
		private async Task<int> RecordCaseAsync(BusinessContext ctx, HospitalAdmission adm, string kind, string? organism, DateTime onset, string? notes, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			var admittedLocal = Local(adm.AdmittedAt).Date;
			{
				// the ward of the bed the patient lay in that day (the last stay to start by the end of it)
				var dayEndUtc = HospitalNursingService.WardToUtc(onset.AddDays(1));
				var wardId = await (from s in _db.HospitalBedStays.AsNoTracking()
									join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
									where s.CompanyId == ctx.CompanyId && s.AdmissionId == adm.ID && s.StartedAt < dayEndUtc
									orderby s.StartedAt descending
									select r.WardId).FirstOrDefaultAsync(ct);
				int? deviceId = null;
				if (HospitalInfectionKind.DeviceOf(kind) is string dk)
				{
					var devices = await Devices(ctx).AsNoTracking().Where(x => x.AdmissionId == adm.ID && x.Kind == dk).OrderByDescending(x => x.InsertedAt).ToListAsync(ct);
					deviceId = devices.FirstOrDefault(d => HospitalInfectionRules.DeviceAssociated(Local(d.InsertedAt), d.RemovedAt == null ? null : Local(d.RemovedAt.Value), onset))?.ID;
				}
				var nos = await Cases(ctx).Select(c => c.CaseNo).ToListAsync(ct);
				var row = new HospitalInfectionCase
				{
					CompanyId = ctx.CompanyId, CaseNo = HospitalDentalPracticeRules.NextNo(nos, "IC-", 6), PatientId = adm.PatientId, AdmissionId = adm.ID, Kind = kind,
					Organism = Clip(organism, 100), OnsetDate = onset, HospitalAcquired = HospitalInfectionRules.HospitalAcquired(admittedLocal, onset),
					WardId = wardId, DeviceId = deviceId, Status = HospitalInfectionStatus.Suspected, Notes = Clip(notes, 1000), ReportedAt = nowUtc, ReportedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return row.ID;
			}
		}

		/// Infection control confirms or rules out. A confirmed MDRO or C. difficile puts the patient under contact
		/// precautions at once, unless they already are.
		public async Task<HospitalResult> DecideCaseAsync(BusinessContext ctx, int id, HospitalInfectionDecideInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var c = await Cases(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (c == null) return HospitalResult.Fail("not_found");
				if (HospitalInfectionRules.DecideRefusal(c, input.Decision, input.Note) is string why) return HospitalResult.Fail(why);
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				c.Status = input.Decision!; c.DecidedAt = nowUtc; c.DecidedByEmployeeId = employeeId; c.DecisionNote = Clip(input.Note, 500);
				if (c.Status == HospitalInfectionStatus.Confirmed && HospitalInfectionKind.NeedsContact(c.Kind)
					&& !await Isolations(ctx).AnyAsync(x => x.PatientId == c.PatientId && x.Precaution == HospitalPrecaution.Contact && x.EndedAt == null, ct))
				{
					_db.Add(new HospitalIsolation
					{
						CompanyId = ctx.CompanyId, PatientId = c.PatientId, AdmissionId = c.AdmissionId, Precaution = HospitalPrecaution.Contact,
						Organism = c.Organism ?? (c.Kind == HospitalInfectionKind.Cdi ? "C. difficile" : null), Reason = c.CaseNo, CaseId = c.ID,
						StartedAt = nowUtc, StartedByEmployeeId = employeeId,
					});
				}
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------- reads

		private async Task<Dictionary<int, HospitalIcPatientRow>> PatientRowsAsync(BusinessContext ctx, IReadOnlyCollection<int> patientIds, CancellationToken ct)
		{
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && patientIds.Contains(p.ID))
				.Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToListAsync(ct);
			// where each one lies now (an open bed stay)
			var beds = await (from s in _db.HospitalBedStays.AsNoTracking()
							  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							  join w in _db.HospitalWards.AsNoTracking() on r.WardId equals w.ID into wj
							  from w in wj.DefaultIfEmpty()
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && patientIds.Contains(s.PatientId)
							  select new { s.PatientId, s.AdmissionId, Bed = b.Code, Ward = w == null ? null : w.Name, WardEn = w == null ? null : w.NameEn }).ToListAsync(ct);
			return pats.ToDictionary(p => p.ID, p =>
			{
				var b = beds.FirstOrDefault(x => x.PatientId == p.ID);
				return new HospitalIcPatientRow(p.ID, b?.AdmissionId, p.Name, p.NameEn, p.Mrn, p.PhotoPath, b?.Bed, b?.Ward, b?.WardEn);
			});
		}

		public async Task<HospitalIcPage> PageAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var iso = await Isolations(ctx).AsNoTracking().Where(x => x.EndedAt == null).OrderBy(x => x.StartedAt).ToListAsync(ct);
				var dev = await (from d in Devices(ctx).AsNoTracking()
								 join a in _db.HospitalAdmissions.AsNoTracking() on d.AdmissionId equals a.ID
								 where d.RemovedAt == null && a.Status == HospitalAdmissionStatus.Active
								 orderby d.InsertedAt
								 select d).ToListAsync(ct);
				int suspected = await Cases(ctx).CountAsync(c => c.Status == HospitalInfectionStatus.Suspected, ct);
				var rows = await PatientRowsAsync(ctx, iso.Select(i => i.PatientId).Concat(dev.Select(d => d.PatientId)).Distinct().ToList(), ct);
				var ids = iso.Select(i => i.StartedByEmployeeId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				var today = Local(nowUtc);
				return new HospitalIcPage(true,
					iso.Where(i => rows.ContainsKey(i.PatientId)).Select(i => new HospitalIcIsolationRow(i, rows[i.PatientId])).ToList(),
					dev.Where(d => rows.ContainsKey(d.PatientId)).Select(d => new HospitalIcDeviceRow(new HospitalIcDevice(d.ID, d.Kind, d.Site, d.InsertedAt, HospitalInfectionRules.DeviceDay(Local(d.InsertedAt), today)), rows[d.PatientId]))
						.OrderByDescending(r => r.Device.Day).ToList(),
					suspected, people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalIcPage(false, Array.Empty<HospitalIcIsolationRow>(), Array.Empty<HospitalIcDeviceRow>(), 0, new Dictionary<int, string>());
			}
		}

		/// The case register, newest event first: searched by case number, patient, file number or organism.
		public async Task<(IReadOnlyList<HospitalIcCaseRow> Rows, int Total)> CasesAsync(BusinessContext ctx, string? q, string? status, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var query = from c in Cases(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
							join w in _db.HospitalWards.AsNoTracking() on c.WardId equals w.ID into wj
							from w in wj.DefaultIfEmpty()
							join d in _db.Set<HospitalInvasiveDevice>().AsNoTracking() on c.DeviceId equals d.ID into dj
							from d in dj.DefaultIfEmpty()
							select new { c, p.Name, p.NameEn, p.Mrn, Ward = w == null ? null : w.Name, WardEn = w == null ? null : w.NameEn, Device = d == null ? null : d.Kind };
				if (HospitalInfectionStatus.All.Contains(status ?? "")) query = query.Where(x => x.c.Status == status);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var s = q.Trim();
					query = query.Where(x => x.c.CaseNo.Contains(s) || x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s)) || x.Mrn.Contains(s) || (x.c.Organism != null && x.c.Organism.Contains(s)));
				}
				int total = await query.CountAsync(ct);
				pageSize = Math.Clamp(pageSize, 5, 100);
				page = Math.Max(1, page);
				var rows = await query.OrderByDescending(x => x.c.OnsetDate).ThenByDescending(x => x.c.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				return (rows.Select(x => new HospitalIcCaseRow(x.c, x.Name, x.NameEn ?? "", x.Mrn, x.Ward, x.WardEn, x.Device)).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalIcCaseRow>(), 0); }
		}

		/// One month per ward: patient-days from the bed stays (a day counts on the ward of the last bed the patient lay in
		/// that day), device-days the same way, and the confirmed hospital-acquired cases by the ward they count on.
		public async Task<IReadOnlyList<HospitalIcRateRow>> RatesAsync(BusinessContext ctx, int year, int month, DateTime nowUtc, CancellationToken ct = default)
		{
			var from = new DateTime(year, month, 1);
			var to = from.AddMonths(1).AddDays(-1);
			var today = Local(nowUtc).Date;
			if (to > today) to = today;
			var fromUtc = HospitalNursingService.WardToUtc(from);
			var toUtc = HospitalNursingService.WardToUtc(to.AddDays(1));
			var wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).Select(w => new { w.ID, w.Name, w.NameEn }).ToListAsync(ct);
			var stays = await (from s in _db.HospitalBedStays.AsNoTracking()
							   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							   where s.CompanyId == ctx.CompanyId && s.StartedAt < toUtc && (s.EndedAt == null || s.EndedAt >= fromUtc)
							   select new { s.AdmissionId, s.StartedAt, s.EndedAt, r.WardId }).ToListAsync(ct);
			// the ward of each admission on each day
			var wardOn = new Dictionary<(int, DateTime), int?>();
			foreach (var s in stays.OrderBy(s => s.StartedAt))
				foreach (var d in HospitalInfectionRules.Days(Local(s.StartedAt), s.EndedAt == null ? null : Local(s.EndedAt.Value), from, to, today))
					wardOn[(s.AdmissionId, d)] = s.WardId;   // later stays overwrite: the last bed of the day
			List<HospitalInvasiveDevice> devices; List<HospitalInfectionCase> cases;
			try
			{
				devices = await Devices(ctx).AsNoTracking().Where(d => d.InsertedAt < toUtc && (d.RemovedAt == null || d.RemovedAt >= fromUtc)).ToListAsync(ct);
				cases = await Cases(ctx).AsNoTracking().Where(c => c.Status == HospitalInfectionStatus.Confirmed && c.HospitalAcquired && c.OnsetDate >= from && c.OnsetDate <= to).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { devices = new(); cases = new(); }
			var acc = new Dictionary<int, int[]>();   // ward (0 = none) → patient, line, catheter, vent days
			int[] Of(int? w) { var k = w ?? 0; if (!acc.TryGetValue(k, out var a)) acc[k] = a = new int[4]; return a; }
			foreach (var kv in wardOn) Of(kv.Value)[0]++;
			foreach (var d in devices)
			{
				int slot = d.Kind == HospitalDeviceKind.CentralLine ? 1 : d.Kind == HospitalDeviceKind.UrinaryCatheter ? 2 : 3;
				foreach (var day in HospitalInfectionRules.Days(Local(d.InsertedAt), d.RemovedAt == null ? null : Local(d.RemovedAt.Value), from, to, today))
					if (wardOn.TryGetValue((d.AdmissionId, day), out var w)) Of(w)[slot]++;   // a device-day counts only while the patient lies on a ward
			}
			var rows = new List<HospitalIcRateRow>();
			foreach (var k in acc.Keys.Union(cases.Select(c => c.WardId ?? 0)).Distinct())
			{
				var a = Of(k == 0 ? null : k);
				var mine = cases.Where(c => (c.WardId ?? 0) == k).ToList();
				var w = wards.FirstOrDefault(x => x.ID == k);
				rows.Add(new HospitalIcRateRow(k == 0 ? null : k, w?.Name ?? "—", w?.NameEn ?? "—", a[0], a[1], a[2], a[3], mine.Count,
					mine.Count(c => c.Kind == HospitalInfectionKind.Clabsi), mine.Count(c => c.Kind == HospitalInfectionKind.Cauti), mine.Count(c => c.Kind == HospitalInfectionKind.Vap)));
			}
			return rows.OrderBy(r => r.WardId == null).ThenBy(r => r.WardEn).ToList();
		}
	}
}
