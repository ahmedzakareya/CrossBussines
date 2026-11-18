using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H201 — bedside monitors (HL7 v2 ORU^R01 vital signs). A monitor's numbers are NOT the patient's chart:
	//   · the set is placed on a patient by the BED the monitor names (PV1-3) and/or the FILE NUMBER (PID-3); when both
	//     are sent they must be the same admission, else nothing is placed (a monitor moved to another bed is the
	//     classic wrong-patient error); a bed with nobody in it, a patient not admitted, a set measured in the future,
	//     or a message with no vital sign is refused;
	//   · each value is read by its code — LOINC or the monitors' own IEEE 11073 (MDC) names — in its unit (°F becomes
	//     °C; any other unit, or a value outside what a body can show, is left out and named);
	//   · the set then WAITS for a nurse, who adds what no monitor measures (the level of consciousness, oxygen) and
	//     accepts it — charted through the same path as a set typed at the bedside (NEWS2, and the escalation task), at
	//     the time it was measured — or discards it as an artefact, with the reason. A set older than 12 hours is not
	//     charted: take a new one.
	//   · every message is kept as it came, once: the same device and control id again is a duplicate.
	// ==========================================================================================
	public sealed record HospitalVitalSet(string Device, string MessageId, string? BedCode, string? Mrn, DateTime? TakenAt,
		int? HeartRate, int? SpO2, int? RespRate, int? SystolicBp, int? DiastolicBp, decimal? TempC, IReadOnlyList<string> Left);
	public sealed record HospitalDeviceVitalRow(HospitalDeviceVital Vital, string? Patient, string? PatientEn, string? PatientMrn, string? Bed, string? DecidedBy);

	public sealed class HospitalVitalAcceptInput
	{
		public string? Consciousness { get; set; }
		public bool OnOxygen { get; set; }
		public bool SpO2Scale2 { get; set; }
		public int? PainScore { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalDeviceVitalRules
	{
		public const int MaxAgeHours = 12;
		/// A detail is stored as its key and values (key, a tab, each value), so each screen says it in its reader's language.
		public static string Pack(string key, params object?[] args) => args.Length == 0 ? key : key + "\t" + string.Join("\t", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));
		public static (string Key, string[] Args) Unpack(string packed) { var p = packed.Split('\t'); return (p[0], p[1..]); }
		public const string LeftUnit = "{0}: in {1}";
		public const string LeftRange = "{0}: {1} is out of range";

		private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
		{
			["8867-4"] = "HR", ["MDC_ECG_HEART_RATE"] = "HR", ["147842"] = "HR",
			["MDC_PULS_OXIM_PULS_RATE"] = "PR", ["MDC_PULS_RATE"] = "PR", ["149530"] = "PR",
			["59408-5"] = "SPO2", ["2708-6"] = "SPO2", ["MDC_PULS_OXIM_SAT_O2"] = "SPO2", ["150456"] = "SPO2",
			["9279-1"] = "RR", ["MDC_RESP_RATE"] = "RR", ["MDC_TTHOR_RESP_RATE"] = "RR", ["151562"] = "RR", ["151594"] = "RR",
			["8480-6"] = "SBP", ["MDC_PRESS_BLD_NONINV_SYS"] = "SBP", ["150021"] = "SBP",
			["8462-4"] = "DBP", ["MDC_PRESS_BLD_NONINV_DIA"] = "DBP", ["150022"] = "DBP",
			["8310-5"] = "TEMP", ["8331-1"] = "TEMP", ["MDC_TEMP"] = "TEMP", ["MDC_TEMP_BODY"] = "TEMP", ["150344"] = "TEMP", ["150364"] = "TEMP",
		};

		/// The parameter an OBX names (by its identifier, else its text), or null.
		public static string? Parameter(string code, string text) =>
			Codes.TryGetValue(code.Trim(), out var p) ? p : Codes.TryGetValue(text.Trim(), out var q) ? q : null;

		private static string U(string? u) => (u ?? "").Trim().Replace(" ", "").ToLowerInvariant();
		public static bool PerMinute(string? u) => U(u) is "" or "/min" or "1/min" or "min-1" or "bpm" or "{beats}/min" or "{beat}/min" or "{breaths}/min" or "{breath}/min" or "{rpm}" or "/mn";
		public static bool Percent(string? u) => U(u) is "" or "%" or "{percent}";
		public static bool MmHg(string? u) => U(u) is "" or "mm[hg]" or "mmhg";
		/// °C as is; °F converted; null = a unit we do not read.
		public static decimal? Celsius(decimal v, string? u) => U(u) switch
		{
			"" or "cel" or "c" or "°c" or "degc" => v,
			"[degf]" or "f" or "°f" or "degf" => Math.Round((v - 32m) * 5m / 9m, 1, MidpointRounding.AwayFromZero),
			_ => null,
		};

		/// HL7 TS (yyyyMMddHHmm[ss][±zzzz]) to UTC; without an offset the time is the ward's (the server's local).
		public static DateTime? Time(string? ts)
		{
			if (string.IsNullOrWhiteSpace(ts)) return null;
			ts = ts.Trim();
			string offset = "";
			int sign = Math.Max(ts.IndexOf('+'), ts.IndexOf('-'));
			if (sign > 0) { offset = ts[sign..]; ts = ts[..sign]; }
			int dot = ts.IndexOf('.'); if (dot > 0) ts = ts[..dot];
			string[] fmts = { "yyyyMMddHHmmss", "yyyyMMddHHmm" };
			if (!DateTime.TryParseExact(ts, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
			if (offset.Length == 5 && int.TryParse(offset.AsSpan(1, 2), out var hh) && int.TryParse(offset.AsSpan(3, 2), out var mm))
				return DateTime.SpecifyKind(t - (offset[0] == '-' ? -1 : 1) * new TimeSpan(hh, mm, 0), DateTimeKind.Utc);
			return DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime();
		}

		/// The vital signs in a monitor's message, or why it cannot be read.
		public static (HospitalVitalSet? Set, string? Refusal) Parse(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return (null, "The message is empty");
			var segs = raw.Replace("\r\n", "\r").Replace('\n', '\r').Split('\r', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimStart()).Where(s => s.Length > 0).ToList();
			var msh = segs.FirstOrDefault();
			if (msh == null || !msh.StartsWith("MSH") || msh.Length < 8) return (null, "This is not an HL7 message (no MSH segment)");
			char fs = msh[3], cs = msh[4];
			var m = msh.Split(fs);
			string Msh(int n) => m.Length > n - 1 ? m[n - 1] : "";
			string Comp(string field, int n) { var c = field.Split(cs); return c.Length > n - 1 ? c[n - 1].Trim() : ""; }
			string type = Comp(Msh(9), 1) + "^" + Comp(Msh(9), 2);
			if (!type.StartsWith("ORU^R01", StringComparison.OrdinalIgnoreCase)) return (null, "Only result messages (ORU^R01) are read");
			string device = Comp(Msh(3), 1), control = Msh(10).Trim();
			if (device.Length == 0) return (null, "The message names no sending device (MSH-3)");
			if (control.Length == 0) return (null, "The message has no control id (MSH-10)");
			string? mrn = null, bed = null; DateTime? when = null, obrWhen = null;
			var v = new Dictionary<string, decimal>(); var left = new List<string>();
			foreach (var s in segs.Skip(1))
			{
				var f = s.Split(fs);
				string Fld(int n) => f.Length > n ? f[n] : "";
				switch (f[0])
				{
					case "PID": mrn ??= Comp(Fld(3), 1) is { Length: > 0 } x ? x : null; break;
					case "PV1": { var b = Comp(Fld(3), 3); bed ??= b.Length > 0 ? b : Comp(Fld(3), 1) is { Length: > 0 } y ? y : null; break; }
					case "OBR": obrWhen ??= Time(Comp(Fld(7), 1)); break;
					case "OBX":
					{
						string code = Comp(Fld(3), 1), text = Comp(Fld(3), 2), units = Comp(Fld(6), 1), status = Fld(11).Trim().ToUpperInvariant();
						var p = Parameter(code, text);
						if (p == null || status is "X" or "D" or "W") break;
						if (!decimal.TryParse(Fld(5).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var val)) break;
						when ??= Time(Comp(Fld(14), 1));
						bool unitOk = p switch { "HR" or "PR" or "RR" => PerMinute(units), "SPO2" => Percent(units), "SBP" or "DBP" => MmHg(units), _ => true };
						if (p == "TEMP") { if (Celsius(val, units) is decimal c) val = c; else unitOk = false; }
						if (!unitOk) { left.Add(Pack(LeftUnit, Name(p), units)); break; }
						if (!v.ContainsKey(p)) v[p] = val;
						break;
					}
				}
			}
			// the ECG's rate is the heart rate; the oximeter's pulse only when there is no ECG
			if (!v.ContainsKey("HR") && v.TryGetValue("PR", out var pr)) v["HR"] = pr;
			int? I(string k) => v.TryGetValue(k, out var x) ? (int)Math.Round(x, MidpointRounding.AwayFromZero) : null;
			decimal? T() => v.TryGetValue("TEMP", out var x) ? Math.Round(x, 1, MidpointRounding.AwayFromZero) : null;
			int? hr = I("HR"), spo2 = I("SPO2"), rr = I("RR"), sbp = I("SBP"), dbp = I("DBP"); decimal? temp = T();
			// a value no body can show is an artefact: left out, named (the same ranges as a set typed at the bedside)
			bool Bad(HospitalObservationInput i) => HospitalNews2Rules.Refusal(i) != null;
			if (hr is int h && Bad(new() { HeartRate = h })) { left.Add(Pack(LeftRange, Name("HR"), h)); hr = null; }
			if (spo2 is int o && Bad(new() { SpO2 = o })) { left.Add(Pack(LeftRange, Name("SPO2"), o)); spo2 = null; }
			if (rr is int r && Bad(new() { RespRate = r })) { left.Add(Pack(LeftRange, Name("RR"), r)); rr = null; }
			if (temp is decimal t && Bad(new() { TempC = t })) { left.Add(Pack(LeftRange, Name("TEMP"), t.ToString(CultureInfo.InvariantCulture))); temp = null; }
			if (sbp != null || dbp != null)
			{
				if (sbp == null || dbp == null || Bad(new() { SystolicBp = sbp, DiastolicBp = dbp }))
				{ left.Add(Pack(LeftRange, Name("BP"), (sbp?.ToString() ?? "?") + "/" + (dbp?.ToString() ?? "?"))); sbp = dbp = null; }
			}
			if (hr == null && spo2 == null && rr == null && sbp == null && temp == null) return (null, left.Count > 0 ? "No vital sign in the message could be read" : "The message holds no vital signs");
			return (new HospitalVitalSet(device, control, bed, mrn, when ?? obrWhen ?? Time(Msh(7)), hr, spo2, rr, sbp, dbp, temp, left), null);
		}

		public static string Name(string p) => p switch { "HR" or "PR" => "HR", "SPO2" => "SpO2", "RR" => "RR", "SBP" or "DBP" or "BP" => "BP", _ => "Temp" };

		/// Whom a set belongs to: (bed's admission, file number's admission) must agree when both are named.
		public static string? WhoRefusal(string? bed, string? mrn, int? bedAdmission, bool bedKnown, int? mrnAdmission, bool mrnKnown)
		{
			if (bed == null && mrn == null) return "The message names no bed (PV1-3) and no patient (PID-3)";
			if (bed != null && !bedKnown) return "No such bed: {0}";
			if (bed != null && bedAdmission == null) return "Nobody is in bed {0}";
			if (mrn != null && !mrnKnown) return "No patient has the file number {1}";
			if (mrn != null && mrnAdmission == null) return "The patient {1} is not admitted";
			if (bed != null && mrn != null && bedAdmission != mrnAdmission) return "The patient in bed {0} is not {1}: the monitor may have moved";
			return null;
		}

		/// Why a waiting set cannot be charted now, or null.
		public static string? AcceptRefusal(HospitalDeviceVital v, HospitalVitalAcceptInput i, DateTime nowUtc)
		{
			if (v.Status != HospitalDeviceVitalStatus.Pending) return "This reading was already decided";
			if (string.IsNullOrEmpty(i.Consciousness)) return "Choose the level of consciousness";
			if (v.TakenAt is DateTime t && nowUtc - t > TimeSpan.FromHours(MaxAgeHours)) return "A reading older than 12 hours is not charted: take a new set";
			return null;
		}
	}

	public sealed class HospitalDeviceVitalService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalObservationService _obs;
		public HospitalDeviceVitalService(CrossDbContext db, IHospitalAccessService access, HospitalObservationService obs) { _db = db; _access = access; _obs = obs; }

		private IQueryable<HospitalDeviceVital> Vitals(BusinessContext ctx) => _db.Set<HospitalDeviceVital>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrEmpty(s) ? null : s.Length <= n ? s : s[..n];

		/// One monitor message: kept as it came; placed on a patient and left waiting for a nurse, or refused with why.
		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, string? raw, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(raw)) return HospitalResult.Fail("The message is empty");
			if (raw.Length > 200_000) return HospitalResult.Fail("The message is too large");
			var row = new HospitalDeviceVital { CompanyId = ctx.CompanyId, Raw = raw, ReceivedAt = nowUtc, ReceivedByEmployeeId = employeeId, Device = "?", MessageId = "?" };
			var (set, why) = HospitalDeviceVitalRules.Parse(raw);
			if (set != null)
			{
				row.Device = Clip(set.Device, 60)!; row.MessageId = Clip(set.MessageId, 60)!; row.BedCode = Clip(set.BedCode, 30); row.Mrn = Clip(set.Mrn, 30); row.TakenAt = set.TakenAt;
				row.HeartRate = set.HeartRate; row.SpO2 = set.SpO2; row.RespRate = set.RespRate; row.SystolicBp = set.SystolicBp; row.DiastolicBp = set.DiastolicBp; row.TempC = set.TempC;
				if (await Vitals(ctx).AnyAsync(x => x.Device == row.Device && x.MessageId == row.MessageId && x.Status != HospitalDeviceVitalStatus.Duplicate, ct))
					why = "duplicate";
				else if (set.TakenAt is DateTime t && t > nowUtc.AddMinutes(5)) why = "The reading is dated in the future";
				else
				{
					int? bedAdm = null, mrnAdm = null; bool bedKnown = false, mrnKnown = false;
					if (set.BedCode != null)
					{
						var bed = await _db.HospitalBeds.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.Code == set.BedCode).Select(b => (int?)b.ID).FirstOrDefaultAsync(ct);
						bedKnown = bed != null;
						if (bed != null)
							bedAdm = await (from s in _db.HospitalBedStays.AsNoTracking()
											join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
											where s.CompanyId == ctx.CompanyId && s.BedId == bed && s.EndedAt == null && a.Status == HospitalAdmissionStatus.Active
											select (int?)a.ID).FirstOrDefaultAsync(ct);
					}
					if (set.Mrn != null)
					{
						var pid = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.Mrn == set.Mrn).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct);
						mrnKnown = pid != null;
						if (pid != null)
							mrnAdm = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == pid && a.Status == HospitalAdmissionStatus.Active).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
					}
					var who = HospitalDeviceVitalRules.WhoRefusal(set.BedCode, set.Mrn, bedAdm, bedKnown, mrnAdm, mrnKnown);
					if (who != null) why = HospitalDeviceVitalRules.Pack(who, set.BedCode ?? "", set.Mrn ?? "");
					else
					{
						row.AdmissionId = bedAdm ?? mrnAdm;
						row.PatientId = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.ID == row.AdmissionId).Select(a => (int?)a.PatientId).FirstOrDefaultAsync(ct);
					}
				}
			}
			if (why == "duplicate") { row.Status = HospitalDeviceVitalStatus.Duplicate; row.Detail = null; }
			else if (why != null) { row.Status = HospitalDeviceVitalStatus.Refused; row.Detail = Clip(why, 500); }
			else { row.Status = HospitalDeviceVitalStatus.Pending; row.Detail = set!.Left.Count == 0 ? null : Clip(string.Join("; ", set.Left), 500); }
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// The nurse charts a waiting set — with what no monitor measures — through the bedside path (NEWS2, escalation).
		public async Task<(HospitalResult Result, int? News2, string? Risk, bool Escalated)> AcceptAsync(BusinessContext ctx, int id, HospitalVitalAcceptInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return (HospitalResult.Fail("forbidden"), null, null, false);
			var v = await Vitals(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (v == null || v.AdmissionId == null) return (HospitalResult.Fail("not_found"), null, null, false);
			if (HospitalDeviceVitalRules.AcceptRefusal(v, input ?? new(), nowUtc) is string why) return (HospitalResult.Fail(why), null, null, false);
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// only the first decision lands
			int n = await Vitals(ctx).Where(x => x.ID == id && x.Status == HospitalDeviceVitalStatus.Pending).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, HospitalDeviceVitalStatus.Accepted).SetProperty(x => x.DecidedAt, nowUtc).SetProperty(x => x.DecidedByEmployeeId, employeeId), ct);
			if (n != 1) { await tx.RollbackAsync(); return (HospitalResult.Fail("This reading was already decided"), null, null, false); }
			var obs = new HospitalObservationInput
			{
				AdmissionId = v.AdmissionId.Value, HeartRate = v.HeartRate, SpO2 = v.SpO2, RespRate = v.RespRate, SystolicBp = v.SystolicBp, DiastolicBp = v.DiastolicBp, TempC = v.TempC,
				Consciousness = input!.Consciousness, OnOxygen = input.OnOxygen, SpO2Scale2 = input.SpO2Scale2, PainScore = input.PainScore,
				Note = Clip(string.IsNullOrWhiteSpace(input.Note) ? "HL7 · " + v.Device : input.Note.Trim() + " · HL7 · " + v.Device, 300),
			};
			var r = await _obs.RecordAsync(ctx, obs, employeeId, nowUtc, ct, v.TakenAt);
			if (!r.Result.Ok) { await tx.RollbackAsync(); return r; }
			await Vitals(ctx).Where(x => x.ID == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ObservationId, r.Result.Id), ct);
			await tx.CommitAsync();
			return r;
		}

		/// An artefact — not charted, with the nurse's reason.
		public async Task<HospitalResult> DiscardAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why the reading is not charted");
			string why = Clip(reason.Trim(), 500)!;
			int n = await Vitals(ctx).Where(x => x.ID == id && x.Status == HospitalDeviceVitalStatus.Pending).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, HospitalDeviceVitalStatus.Discarded).SetProperty(x => x.Detail, why)
				.SetProperty(x => x.DecidedAt, nowUtc).SetProperty(x => x.DecidedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : await Vitals(ctx).AnyAsync(x => x.ID == id, ct) ? HospitalResult.Fail("This reading was already decided") : HospitalResult.Fail("not_found");
		}

		public async Task<(IReadOnlyList<HospitalDeviceVitalRow> Rows, int Total, int Waiting, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? status, int page, int size, CancellationToken ct = default)
		{
			try
			{
				var query = from v in Vitals(ctx).AsNoTracking()
							join p0 in _db.HospitalPatients.AsNoTracking() on v.PatientId equals p0.ID into pp
							from p in pp.DefaultIfEmpty()
							join e0 in _db.Employee.AsNoTracking() on v.DecidedByEmployeeId equals e0.ID into ee
							from e in ee.DefaultIfEmpty()
							select new { v, p, e };
				if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.v.Status == status);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					query = query.Where(x => x.v.Device.Contains(t) || (x.v.BedCode != null && x.v.BedCode.Contains(t)) || (x.v.Mrn != null && x.v.Mrn.Contains(t))
						|| (x.p != null && (x.p.Name.Contains(t) || x.p.NameEn.Contains(t) || x.p.Mrn.Contains(t))));
				}
				int total = await query.CountAsync(ct);
				int waiting = await Vitals(ctx).CountAsync(x => x.Status == HospitalDeviceVitalStatus.Pending, ct);
				page = Math.Max(1, page);
				var rows = await query.OrderByDescending(x => x.v.Status == HospitalDeviceVitalStatus.Pending).ThenByDescending(x => x.v.ReceivedAt).ThenByDescending(x => x.v.ID)
					.Skip((page - 1) * size).Take(size).ToListAsync(ct);
				return (rows.Select(x => new HospitalDeviceVitalRow(x.v, x.p?.Name, x.p?.NameEn, x.p?.Mrn, x.v.BedCode, x.e == null ? null : EmployeeNames.Of(x.e))).ToList(), total, waiting, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return (Array.Empty<HospitalDeviceVitalRow>(), 0, 0, false); }
		}
	}
}
