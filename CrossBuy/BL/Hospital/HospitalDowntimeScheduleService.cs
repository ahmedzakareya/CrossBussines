using System.Text.Json;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H224 — the downtime copies made by themselves: for a ward, one Report Studio schedule per shift (by
	// default 06:30, 14:30, 22:30) generating the ward's downtime summary as a PDF and e-mailing it to the addresses
	// the ward gives (the ward's mailbox, the nurse in charge) — a copy that is in hand BEFORE the system goes down.
	// The schedules are the platform's own (owner: the person who sets them; their rights are checked at every run),
	// so they fire when the platform's schedule worker is on (Reporting:ScheduleWorker:Enabled); "send now" runs one
	// at once through the same runner whatever that switch says.
	// ==========================================================================================
	public sealed class HospitalDowntimeScheduleInput
	{
		public string? Emails { get; set; }
		public List<string>? Times { get; set; }
	}

	public sealed record HospitalDowntimeSchedules(int Count, DateTime? NextRunAt, DateTime? LastRunAt, string? LastRunStatus, string? Emails);

	public static class HospitalDowntimeScheduleRules
	{
		public static readonly IReadOnlyList<string> DefaultTimes = new[] { "06:30", "14:30", "22:30" };

		public static (List<string> Emails, List<(int H, int M)> Times, string? Refusal) Read(HospitalDowntimeScheduleInput i)
		{
			var emails = (i.Emails ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (emails.Count == 0) return (emails, new(), "Write the address the copies go to");
			if (emails.Count > 5) return (emails, new(), "At most five addresses");
			if (emails.Any(e => !System.Net.Mail.MailAddress.TryCreate(e, out var a) || a.Address != e)) return (emails, new(), "An address is not a valid e-mail");
			var times = new List<(int, int)>();
			foreach (var t in (i.Times is { Count: > 0 } ? i.Times : DefaultTimes.ToList()))
			{
				var p = t.Split(':');
				if (p.Length != 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m) || h is < 0 or > 23 || m is < 0 or > 59) return (emails, new(), "A time is not hh:mm");
				times.Add((h, m));
			}
			if (times.Count > 4) return (emails, new(), "At most four copies a day");
			return (emails, times.Distinct().OrderBy(x => x).ToList(), null);
		}

		public static string ParamsFor(int wardId) => JsonSerializer.Serialize(new Dictionary<string, string?> { ["WardId"] = wardId.ToString() });
	}

	public sealed class HospitalDowntimeScheduleService
	{
		private readonly CrossDbContext _db;
		private readonly IReportScheduleService _schedules;
		private readonly IReportScheduleRunner _runner;
		private readonly IReportMailSender? _mail;   // H226
		public HospitalDowntimeScheduleService(CrossDbContext db, IReportScheduleService schedules, IReportScheduleRunner runner, IReportMailSender? mail = null) { _db = db; _schedules = schedules; _runner = runner; _mail = mail; }

		private const string Code = HospitalDocumentDatasetCodes.WardDowntime;

		private async Task<List<ReportSchedule>> OfWardAsync(int companyId, int wardId, CancellationToken ct)
		{
			string marker = "\"WardId\":\"" + wardId + "\"";
			return (await _db.ReportSchedules.AsNoTracking().Where(s => s.CompanyID == companyId && s.ReportCode == Code && s.DeletedAt == null).ToListAsync(ct))
				.Where(s => (s.ParametersJson ?? "").Replace(" ", "").Contains(marker)).OrderBy(s => s.AtHour).ThenBy(s => s.AtMinute).ToList();
		}

		public async Task<Dictionary<int, HospitalDowntimeSchedules>> StateAsync(int companyId, IEnumerable<int> wardIds, CancellationToken ct = default)
		{
			var all = await _db.ReportSchedules.AsNoTracking().Where(s => s.CompanyID == companyId && s.ReportCode == Code && s.DeletedAt == null && s.IsActive).ToListAsync(ct);
			var ids = all.Select(s => s.Id).ToList();
			var mails = await _db.ReportScheduleRecipients.AsNoTracking().Where(r => ids.Contains(r.ScheduleId)).Select(r => new { r.ScheduleId, r.Address }).ToListAsync(ct);
			var result = new Dictionary<int, HospitalDowntimeSchedules>();
			foreach (var w in wardIds)
			{
				string marker = "\"WardId\":\"" + w + "\"";
				var mine = all.Where(s => (s.ParametersJson ?? "").Replace(" ", "").Contains(marker)).ToList();
				if (mine.Count == 0) continue;
				var last = mine.Where(s => s.LastRunAt != null).OrderByDescending(s => s.LastRunAt).FirstOrDefault();
				result[w] = new HospitalDowntimeSchedules(mine.Count, mine.Min(s => s.NextRunAt), last?.LastRunAt, last?.LastRunStatus,
					string.Join(", ", mails.Where(m => m.ScheduleId == mine[0].Id).Select(m => m.Address)));
			}
			return result;
		}

		/// The ward's shift copies replaced by these times and addresses.
		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int wardId, HospitalDowntimeScheduleInput input, CancellationToken ct = default)
		{
			var ward = await _db.HospitalWards.AsNoTracking().Where(w => w.ID == wardId && w.CompanyId == ctx.CompanyId).Select(w => new { w.Code, w.Name, w.NameEn }).FirstOrDefaultAsync(ct);
			if (ward == null) return HospitalResult.Fail("not_found");
			var (emails, times, why) = HospitalDowntimeScheduleRules.Read(input);
			if (why != null) return HospitalResult.Fail(why);
			foreach (var old in await OfWardAsync(ctx.CompanyId, wardId, ct)) await _schedules.DeleteAsync(old.Id, ctx, ct);
			foreach (var (h, m) in times)
			{
				var r = await _schedules.SaveAsync(new ReportScheduleInput
				{
					ReportCode = Code, Name = $"نسخة وقت التوقف — {ward.Name} — {h:00}:{m:00}", NameEn = $"Downtime copy — {ward.NameEn ?? ward.Name} — {h:00}:{m:00}",
					Frequency = ReportScheduleFrequency.Daily, AtHour = h, AtMinute = m, Format = ReportOutputFormat.Pdf,
					Parameters = new Dictionary<string, string?> { ["WardId"] = wardId.ToString() },
					Recipients = emails.Select(e => new ReportScheduleRecipientInput { Address = e }).ToList(),
				}, ctx, ct);
				if (!r.Success) return HospitalResult.Fail(r.Diagnostics.FirstOrDefault()?.Message ?? "The schedule could not be saved");
			}
			return HospitalResult.Success(wardId);
		}

		/// One copy generated and sent now (the ward's first schedule, through the platform's runner).
		public async Task<HospitalResult> SendNowAsync(BusinessContext ctx, int wardId, CancellationToken ct = default) =>
			(await SendNowWithCopyAsync(ctx, wardId, ct)).Result;

		/// H226: the run archives the PDF whether or not it is mailed. When nothing was sent because the platform has no mail
		/// transport, the ward is not left with nothing: the archived copy is handed back to download and print (the archive
		/// re-checks the report's permission on retrieval). A transport that exists but failed stays a refusal.
		public async Task<(HospitalResult Result, long? CopyId)> SendNowWithCopyAsync(BusinessContext ctx, int wardId, CancellationToken ct = default)
		{
			var s = (await OfWardAsync(ctx.CompanyId, wardId, ct)).FirstOrDefault(x => x.IsActive);
			if (s == null) return (HospitalResult.Fail("Set the ward's shift copies first"), null);
			var tracked = await _db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == s.Id, ct);
			var o = await _runner.RunAsync(tracked!, ct);
			if (o.Status != ReportRunStatus.Succeeded) return (HospitalResult.Fail(o.Diagnostics.FirstOrDefault()?.Message ?? "The copy could not be generated"), null);
			if (o.DeliveriesSent > 0) return (HospitalResult.Success(wardId), null);
			bool noTransport = _mail is { IsAvailable: false };
			return noTransport && o.ArchiveEntryId is > 0
				? (HospitalResult.Success(wardId), o.ArchiveEntryId)
				: (HospitalResult.Fail("The copy was generated but no e-mail could be sent"), null);
		}
	}
}
