using System.Net.Sockets;
using System.Text;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalFeedInput
	{
		public int? Id { get; set; } public string? Code { get; set; } public string? Name { get; set; } public string? Kind { get; set; }
		public string? Host { get; set; } public int? Port { get; set; } public string? ReceivingApp { get; set; } public string? ReceivingFacility { get; set; } public bool Enabled { get; set; }
	}

	public sealed record HospitalFeedRow(HospitalOutboundFeed Feed, int Queued, int Sent, int Errors, DateTime? LastSent);
	public sealed record HospitalOutboundRow(HospitalOutboundMessage Message, string FeedCode, string PatientName, string? PatientNameEn, string? Photo, string? Mrn);
	public sealed record HospitalOutboundPage(bool Ready, IReadOnlyList<HospitalFeedRow> Feeds, IReadOnlyList<HospitalOutboundRow> Messages, int Total);

	// ==========================================================================================
	// HOSPITAL H247 — collecting the hospital's events into each enabled feed's queue (once per event: the key is unique)
	// and sending them over MLLP. Collection runs when the feeds page is opened or "collect" is pressed, and every minute
	// in the background (HospitalOutboundSenderService, owner-approved 2026-11-23), which also sends. The administrator
	// (`manage`) runs it by hand; the background sender runs the same core for each company.
	// ==========================================================================================
	public sealed class HospitalOutboundService
	{
		public const string NoTables = "Outbound feeds are not installed yet (hospital_h247_outbound_feeds.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalOutboundService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		/// Test seam: delivers framed bytes to host:port and returns the reply text. Null = TCP.
		public Func<string, int, byte[], CancellationToken, Task<string>>? Transport { get; set; }

		public async Task<HospitalOutboundPage?> PageAsync(BusinessContext ctx, string? q, string? status, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return null;
			try
			{
				var feeds = await _db.Set<HospitalOutboundFeed>().AsNoTracking().Where(f => f.CompanyId == ctx.CompanyId).OrderBy(f => f.Code).ToListAsync(ct);
				var stats = await _db.Set<HospitalOutboundMessage>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId).GroupBy(m => new { m.FeedId, m.Status })
					.Select(g => new { g.Key.FeedId, g.Key.Status, N = g.Count(), Last = g.Max(x => x.SentAt) }).ToListAsync(ct);
				var rows = feeds.Select(f => new HospitalFeedRow(f, stats.Where(s => s.FeedId == f.ID && s.Status == HospitalFeedStatus.Queued).Sum(s => s.N),
					stats.Where(s => s.FeedId == f.ID && s.Status == HospitalFeedStatus.Sent).Sum(s => s.N), stats.Where(s => s.FeedId == f.ID && s.Status == HospitalFeedStatus.Error).Sum(s => s.N),
					stats.Where(s => s.FeedId == f.ID).Max(s => s.Last))).ToList();
				var query = from m in _db.Set<HospitalOutboundMessage>().AsNoTracking()
							join f in _db.Set<HospitalOutboundFeed>().AsNoTracking() on m.FeedId equals f.ID
							join p in _db.HospitalPatients.AsNoTracking() on m.PatientId equals p.ID
							where m.CompanyId == ctx.CompanyId
							select new { m, f.Code, p.Name, p.NameEn, p.PhotoPath, p.Mrn };
				if (status is HospitalFeedStatus.Queued or HospitalFeedStatus.Sent or HospitalFeedStatus.Error) query = query.Where(x => x.m.Status == status);
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					query = query.Where(x => x.Name.Contains(t) || (x.NameEn ?? "").Contains(t) || x.Mrn.Contains(t) || x.m.MessageType.Contains(t) || x.m.EventKey.Contains(t) || x.Code.Contains(t));
				}
				int total = await query.CountAsync(ct);
				var list = await query.OrderByDescending(x => x.m.CreatedAt).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				return new HospitalOutboundPage(true, rows, list.Select(x => new HospitalOutboundRow(x.m, x.Code, x.Name, x.NameEn, x.PhotoPath, x.Mrn)).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalOutboundPage(false, Array.Empty<HospitalFeedRow>(), Array.Empty<HospitalOutboundRow>(), 0); }
		}

		public async Task<HospitalResult> SaveFeedAsync(BusinessContext ctx, HospitalFeedInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalHl7Out.FeedRefusal(i.Code, i.Name, i.Kind, i.Host, i.Port, i.Enabled) is string why) return HospitalResult.Fail(why);
			var code = i.Code!.Trim().ToUpperInvariant();
			static string? T(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
			try
			{
				if (await _db.Set<HospitalOutboundFeed>().AnyAsync(f => f.CompanyId == ctx.CompanyId && f.Code == code && f.ID != (i.Id ?? 0), ct)) return HospitalResult.Fail("Another feed uses this code");
				HospitalOutboundFeed? f = i.Id is int id ? await _db.Set<HospitalOutboundFeed>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct) : null;
				if (i.Id != null && f == null) return HospitalResult.Fail("not_found");
				if (f == null) _db.Add(f = new HospitalOutboundFeed { CompanyId = ctx.CompanyId, StartFrom = nowUtc });
				if (i.Enabled && !f.Enabled) f.StartFrom = nowUtc;   // switching on starts from now: history is not replayed
				f.Code = code; f.Name = i.Name!.Trim(); f.Kind = i.Kind!; f.Host = T(i.Host, 200); f.Port = i.Port; f.ReceivingApp = T(i.ReceivingApp, 60); f.ReceivingFacility = T(i.ReceivingFacility, 60);
				f.Enabled = i.Enabled; f.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(f.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Queues every new event of every enabled feed. Returns how many messages were queued.
		public Task<int> CollectAsync(BusinessContext ctx, string facility, DateTime nowUtc, CancellationToken ct = default) => CollectForAsync(ctx.CompanyId, facility, nowUtc, ct);

		/// The background sender's entry: the same collection for one company (no person; the worker scope is bound to it).
		public async Task<int> CollectForAsync(int companyId, string facility, DateTime nowUtc, CancellationToken ct = default)
		{
			var feeds = await _db.Set<HospitalOutboundFeed>().AsNoTracking().Where(f => f.CompanyId == companyId && f.Enabled).ToListAsync(ct);
			int added = 0;
			foreach (var f in feeds)
			{
				var have = (await _db.Set<HospitalOutboundMessage>().AsNoTracking().Where(m => m.CompanyId == companyId && m.FeedId == f.ID && m.EventAt >= f.StartFrom)
					.Select(m => m.EventKey).ToListAsync(ct)).ToHashSet();
				var events = f.Kind == HospitalFeedKind.Adt ? await AdtEventsAsync(companyId, f, facility, nowUtc, have, ct) : await OruEventsAsync(companyId, f, facility, nowUtc, have, ct);
				foreach (var m in events.Take(500)) { _db.Add(m); added++; }
			}
			if (added > 0)
				try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { _db.ChangeTracker.Clear(); return 0; }   // a parallel collect queued them first
			return added;
		}

		private sealed record StayInfo(int ID, int AdmissionId, DateTime StartedAt, string Bed, string Room, int? WardId);

		private static string Control(int feedId, string key) => ("F" + feedId + "-" + key).Replace(":", "-")[..Math.Min(30, ("F" + feedId + "-" + key).Length)];

		private async Task<List<HospitalOutboundMessage>> AdtEventsAsync(int companyId, HospitalOutboundFeed f, string facility, DateTime nowUtc, HashSet<string> have, CancellationToken ct)
		{
			var since = f.StartFrom;
			var adms = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == companyId && (a.AdmittedAt >= since || (a.DischargedAt != null && a.DischargedAt >= since))).ToListAsync(ct);
			var ids = adms.Select(a => a.ID).ToList();
			// a transfer of a patient admitted BEFORE the feed was switched on is still an event: take every stay of the
			// admissions that moved since, so the move is never mistaken for the admission's first bed (live, 2026-11-23)
			var moved = await _db.HospitalBedStays.AsNoTracking().Where(s => s.CompanyId == companyId && s.StartedAt >= since).Select(s => s.AdmissionId).Distinct().ToListAsync(ct);
			var all = ids.Union(moved).ToList();
			var stays = await (from s in _db.HospitalBedStays.AsNoTracking()
							   join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							   where s.CompanyId == companyId && all.Contains(s.AdmissionId)
							   select new StayInfo(s.ID, s.AdmissionId, s.StartedAt, b.Code, r.Number, r.WardId)).ToListAsync(ct);
			var moreIds = stays.Select(s => s.AdmissionId).Except(ids).Distinct().ToList();
			if (moreIds.Count > 0) adms.AddRange(await _db.HospitalAdmissions.AsNoTracking().Where(a => moreIds.Contains(a.ID)).ToListAsync(ct));
			var wards = await _db.Set<HospitalWard>().AsNoTracking().Where(w => w.CompanyId == companyId).ToDictionaryAsync(w => w.ID, w => w.Code, ct);
			var pids = adms.Select(a => a.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var list = new List<HospitalOutboundMessage>();
			var now = Ward(nowUtc);
			foreach (var a in adms)
			{
				if (!pats.TryGetValue(a.PatientId, out var p)) continue;
				var mine = stays.Where(s => s.AdmissionId == a.ID).OrderBy(s => s.StartedAt).ToList();
				HospitalHl7Visit V(StayInfo? s) => new("ADM-" + a.ID, s?.WardId is int w ? wards.GetValueOrDefault(w) : null, s?.Room, s?.Bed, null, Ward(a.AdmittedAt), a.DischargedAt is DateTime d ? Ward(d) : null);
				void Add(string trigger, string key, DateTime atUtc, StayInfo? stay)
				{
					if (have.Contains(key) || atUtc < f.StartFrom) return;
					var ctl = Control(f.ID, key);
					list.Add(new HospitalOutboundMessage
					{
						CompanyId = companyId, FeedId = f.ID, EventKey = key, MessageType = "ADT^" + trigger, PatientId = p.ID, ControlId = ctl, EventAt = atUtc, CreatedAt = nowUtc,
						Raw = HospitalHl7Out.Adt(trigger, ctl, p, V(stay), f.ReceivingApp, f.ReceivingFacility, facility, Ward(atUtc), now),
					});
				}
				Add("A01", "A01:adm:" + a.ID, a.AdmittedAt, mine.FirstOrDefault());
				foreach (var s in mine.Skip(1)) Add("A02", "A02:stay:" + s.ID, s.StartedAt, s);
				if (a.DischargedAt is DateTime dis) Add("A03", "A03:adm:" + a.ID, dis, mine.LastOrDefault());
			}
			return list;
		}

		private async Task<List<HospitalOutboundMessage>> OruEventsAsync(int companyId, HospitalOutboundFeed f, string facility, DateTime nowUtc, HashSet<string> have, CancellationToken ct)
		{
			var orders = await _db.Set<HospitalLabOrder>().AsNoTracking().Where(o => o.CompanyId == companyId && o.VerifiedAt != null && o.VerifiedAt >= f.StartFrom).ToListAsync(ct);
			orders = orders.Where(o => !have.Contains("R01:lab:" + o.ID)).ToList();
			var ids = orders.Select(o => o.ID).ToList();
			var results = await _db.Set<HospitalLabResult>().AsNoTracking().Where(r => r.CompanyId == companyId && ids.Contains(r.LabOrderId)).OrderBy(r => r.SortOrder).ToListAsync(ct);
			var analytes = await _db.Set<HospitalLabAnalyte>().AsNoTracking().Where(a => results.Select(r => r.AnalyteId).Contains(a.ID)).ToDictionaryAsync(a => a.ID, ct);
			var pids = orders.Select(o => o.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var now = Ward(nowUtc);
			return orders.Where(o => pats.ContainsKey(o.PatientId)).Select(o =>
			{
				var key = "R01:lab:" + o.ID; var ctl = Control(f.ID, key);
				var res = results.Where(r => r.LabOrderId == o.ID).Select(r => new HospitalHl7Result(analytes.GetValueOrDefault(r.AnalyteId)?.Code ?? r.AnalyteId.ToString(), string.IsNullOrWhiteSpace(r.NameEn) ? r.Name : r.NameEn,
					r.Value is decimal v ? Math.Round(v, r.Decimals).ToString(System.Globalization.CultureInfo.InvariantCulture) : r.TextValue, r.Value != null ? "NM" : "ST", r.Unit, HospitalHl7Out.Range(r.RefLow, r.RefHigh), HospitalHl7Out.Flag(r.Flag), analytes.GetValueOrDefault(r.AnalyteId)?.Loinc)).ToList();
				return new HospitalOutboundMessage
				{
					CompanyId = companyId, FeedId = f.ID, EventKey = key, MessageType = "ORU^R01", PatientId = o.PatientId, ControlId = ctl, EventAt = o.VerifiedAt!.Value, CreatedAt = nowUtc,
					Raw = HospitalHl7Out.Oru(ctl, pats[o.PatientId], o.AccessionNo, o.Code, string.IsNullOrWhiteSpace(o.NameEn) ? o.Name : o.NameEn, o.CollectedAt is DateTime c ? Ward(c) : null, Ward(o.VerifiedAt!.Value), res, f.ReceivingApp, f.ReceivingFacility, facility, now),
				};
			}).ToList();
		}

		/// Sends the feed's queued and failed messages (oldest first, at most 50) and records each acknowledgement.
		public async Task<HospitalResult> SendAsync(BusinessContext ctx, int feedId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			return await SendForAsync(ctx.CompanyId, feedId, nowUtc, ct);
		}

		/// The background sender's entry: the same send for one company's feed.
		public async Task<HospitalResult> SendForAsync(int companyId, int feedId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var f = await _db.Set<HospitalOutboundFeed>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == feedId && x.CompanyId == companyId, ct);
				if (f == null) return HospitalResult.Fail("not_found");
				if (!f.Enabled || f.Host == null || f.Port == null) return HospitalResult.Fail("The feed is off");
				var queue = await _db.Set<HospitalOutboundMessage>().Where(m => m.CompanyId == companyId && m.FeedId == f.ID && m.Status != HospitalFeedStatus.Sent).OrderBy(m => m.EventAt).Take(50).ToListAsync(ct);
				int sent = 0;
				foreach (var m in queue)
				{
					m.Attempts++;
					try
					{
						var reply = Transport != null ? await Transport(f.Host, f.Port.Value, HospitalHl7Out.Frame(m.Raw), ct) : await TcpAsync(f.Host, f.Port.Value, HospitalHl7Out.Frame(m.Raw), ct);
						var ack = HospitalHl7Out.AckCode(reply);
						m.AckCode = ack;
						if (ack is "AA" or "CA") { m.Status = HospitalFeedStatus.Sent; m.SentAt = nowUtc; m.LastError = null; sent++; }
						else { m.Status = HospitalFeedStatus.Error; m.LastError = ack == null ? "No acknowledgement" : "The receiver answered " + ack; }
					}
					catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException)
					{
						m.Status = HospitalFeedStatus.Error; m.LastError = ex.Message[..Math.Min(300, ex.Message.Length)];
						await _db.SaveChangesAsync(ct);
						return HospitalResult.Fail("The receiver did not answer: {0}", m.LastError);
					}
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(sent);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The enabled feeds that have somewhere to send (the background sender's list for one company).
		public async Task<List<int>> SendableFeedsAsync(int companyId, CancellationToken ct = default) =>
			await _db.Set<HospitalOutboundFeed>().AsNoTracking().Where(f => f.CompanyId == companyId && f.Enabled && f.Host != null && f.Port != null).Select(f => f.ID).ToListAsync(ct);

		private static async Task<string> TcpAsync(string host, int port, byte[] frame, CancellationToken ct)
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(15));
			using var tcp = new TcpClient();
			await tcp.ConnectAsync(host, port, cts.Token);
			var s = tcp.GetStream();
			await s.WriteAsync(frame, cts.Token);
			var buf = new byte[8192]; var sb = new StringBuilder();
			while (true)
			{
				int n = await s.ReadAsync(buf, cts.Token);
				if (n <= 0) break;
				sb.Append(Encoding.UTF8.GetString(buf, 0, n));
				if (sb.ToString().Contains('\u001c')) break;
			}
			return sb.ToString();
		}
	}
}
