using System.Globalization;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR5 — ATTENDANCE FROM A FINGERPRINT DEVICE EXPORT (2026-11-08).
	//
	// The common device export (ZKTeco "attlog" and the CSVs most devices produce) is one PUNCH per line:
	//     <device user id> <separator> <date and time> [<separator> anything else…]
	// separated by a tab, comma or semicolon. The device user id is matched to Employee.AttendanceDeviceId, or — when no
	// employee carries that device id — to the employee number in the system, both within the importing company only.
	//
	// An employee's punches on one day become ONE attendance record: the first punch is the check-in, the last the
	// check-out (a single punch is a check-in only). Each day is recorded through IAttendanceService.RecordAsync, so lateness,
	// early leave, overtime and the day's status come from the same roster/policy arithmetic as every other source.
	// A day already corrected by hand (Source "Manual") is never overwritten — it is reported instead.
	// Re-importing the same file changes nothing: each day is upserted, not added. Night shifts crossing midnight are
	// recorded per calendar day (each day's first/last punch), which the report states.
	// ==========================================================================================
	public sealed record AttendanceImportResult(int Lines, int Punches, int DaysRecorded, int DaysKeptManual, int BadLines,
		List<string> UnknownIds, List<string> Errors, DateTime? From, DateTime? To);

	public sealed class HrAttendanceImport
	{
		public const int MaxLines = 200_000;
		private static readonly string[] Formats =
		{
			"yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy-MM-ddTHH:mm:ss",
			"dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm",
		};

		private readonly CrossDbContext _db;
		private readonly IAttendanceService _attendance;
		public HrAttendanceImport(CrossDbContext db, IAttendanceService attendance) { _db = db; _attendance = attendance; }

		/// Parses one line into (device id, instant); null when it is not a punch (a header, a blank line, garbage).
		public static (string Id, DateTime At)? ParseLine(string line)
		{
			if (string.IsNullOrWhiteSpace(line)) return null;
			var parts = line.Split(new[] { '\t', ',', ';' }, StringSplitOptions.TrimEntries);
			if (parts.Length < 2) return null;
			var id = parts[0].Trim('"', ' ');
			var when = parts[1].Trim('"', ' ');
			// some exports put the date and the time in two columns
			if (parts.Length >= 3 && when.Length <= 10 && TimeSpan.TryParse(parts[2].Trim('"', ' '), CultureInfo.InvariantCulture, out _)) when += " " + parts[2].Trim('"', ' ');
			if (id.Length == 0 || id.Length > 50) return null;
			if (!DateTime.TryParseExact(when, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
			return (id, at);
		}

		public async Task<AttendanceImportResult> ImportAsync(int companyId, Stream content, string? userId, CancellationToken ct = default)
		{
			var punches = new List<(string Id, DateTime At)>();
			int lines = 0, bad = 0;
			using (var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true))
			{
				string? line;
				while ((line = await reader.ReadLineAsync(ct)) != null)
				{
					if (++lines > MaxLines) break;
					var p = ParseLine(line);
					if (p is { } ok) punches.Add(ok); else if (!string.IsNullOrWhiteSpace(line)) bad++;
				}
			}

			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive)
				.Select(e => new { e.ID, e.AttendanceDeviceId }).ToListAsync(ct);
			var byDevice = emps.Where(e => !string.IsNullOrWhiteSpace(e.AttendanceDeviceId)).ToDictionary(e => e.AttendanceDeviceId!.Trim(), e => e.ID, StringComparer.OrdinalIgnoreCase);
			var byId = emps.ToDictionary(e => e.ID.ToString(CultureInfo.InvariantCulture), e => e.ID);
			int? Resolve(string id) => byDevice.TryGetValue(id, out var a) ? a : byId.TryGetValue(id, out var b) ? b : null;

			var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			var days = new Dictionary<(int Emp, DateTime Day), (DateTime First, DateTime Last, int Count)>();
			foreach (var (id, at) in punches)
			{
				if (Resolve(id) is not int emp) { unknown.Add(id); continue; }
				var key = (emp, at.Date);
				days[key] = days.TryGetValue(key, out var v) ? (at < v.First ? at : v.First, at > v.Last ? at : v.Last, v.Count + 1) : (at, at, 1);
			}

			var manual = (await _db.AttendanceRecords.AsNoTracking().Where(r => r.CompanyID == companyId && r.Source == "Manual")
				.Select(r => new { r.EmployeeID, r.WorkDate }).ToListAsync(ct)).Select(r => (r.EmployeeID, r.WorkDate.Date)).ToHashSet();
			int recorded = 0, keptManual = 0;
			var errors = new List<string>();
			foreach (var ((emp, day), v) in days.OrderBy(k => k.Key.Day))
			{
				if (manual.Contains((emp, day))) { keptManual++; continue; }
				var (ok, err, _) = await _attendance.RecordAsync(companyId, emp, day, v.First, v.Count > 1 ? v.Last : null, "Device", null, userId);
				if (ok) recorded++; else if (errors.Count < 20) errors.Add($"#{emp} {day:yyyy-MM-dd}: {err}");
			}
			return new AttendanceImportResult(lines, punches.Count, recorded, keptManual, bad, unknown.Take(50).ToList(), errors,
				days.Count == 0 ? null : days.Keys.Min(k => k.Day), days.Count == 0 ? null : days.Keys.Max(k => k.Day));
		}
	}
}
