using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using J = CrossBuy.BL.CrmIdentity.CrmJourneyRules;
using S = CrossBuy.BL.CrmIdentity.CrmSequenceRules;

namespace CrossBuy.BL.CrmIdentity
{
	// CRM C7 — dynamic segments and marketing journeys.
	//
	// A SEGMENT is a rule over contacts that reads the CRM, sales and consent together — the account's industry and owner,
	// posted invoices, deals, recorded consent. The CRM worker refreshes it (hourly, and at once on save or on demand) and
	// keeps WHEN each person entered and left; that entry is what starts a journey.
	// A JOURNEY is a path of steps — e-mail, WhatsApp template, wait, a yes/no condition, a task for the account's owner —
	// run for every person who enters its segment after it is switched on (or everyone already in it, if chosen).
	// MARKETING NEEDS CONSENT: unlike a rep's follow-up sequence (C6), a journey sends e-mail or WhatsApp only to someone
	// with RECORDED consent on that channel; never-asked is skipped and written down. Every e-mail carries a one-click
	// unsubscribe link that records the withdrawal in the person's consent history, and a withdrawal ends the journey.

	public sealed record SegmentRow(CrmSegment Seg, List<J.Rule> Rules, int Journeys);
	public sealed record SegmentPreview(int Count, List<(int id, string name, string? account)> Sample);
	public sealed record JourneyRow(CrmJourney J, string Segment, int Entered, int Active, int Finished, int Exited);
	public sealed record NodeStat(int Here, int Passed, int Skipped, int Yes, int No);
	public sealed record JourneyFile(CrmJourney J, CrmSegment Segment, List<J.Node> Nodes, Dictionary<string, NodeStat> Stats, Dictionary<string, int> Counts,
		decimal AttributedSales, int Buyers, int ConsentSkips);
	public sealed record JourneyMemberRow(CrmJourneyMember M, string Name, string? Account, J.Node? Node, CrmJourneyEvent? Last);

	public interface ICrmJourneyService
	{
		Task<(List<SegmentRow> rows, int total)> SegmentsAsync(int cid, string? q, int page, int pageSize);
		Task<CrmSegment?> SegmentAsync(int cid, int id);
		Task<SegmentPreview> PreviewAsync(int cid, IReadOnlyList<J.Rule> rules, bool matchAll);
		Task<(bool ok, string? error, int id)> SaveSegmentAsync(int cid, int? me, int id, string name, string? nameEn, bool matchAll, IReadOnlyList<J.Rule> rules);
		Task<int> RefreshAsync(int cid, int segmentId);
		Task<List<(string value, int n)>> IndustriesAsync(int cid);
		Task<(List<JourneyRow> rows, int total)> JourneysAsync(int cid, string? q, int page, int pageSize);
		Task<JourneyFile?> JourneyAsync(int cid, int id);
		Task<(bool ok, string? error, int id)> SaveJourneyAsync(int cid, int? me, int id, string name, string? nameEn, int segmentId, bool includeExisting, bool exitOnLeave, List<J.Node> nodes);
		Task<(bool ok, string? error)> SetStatusAsync(int cid, int id, string status, string origin);
		Task<(List<JourneyMemberRow> rows, int total)> MembersAsync(int cid, int journeyId, string? status, string? q, int page, int pageSize);
		Task<List<CrmJourneyEvent>> MemberEventsAsync(int cid, int memberId);
		Task<(int refreshed, int entered, int ran)> RunAsync(int cid, CancellationToken ct = default);
		// public
		Task<(string? name, bool already)?> UnsubscribeInfoAsync(string token);
		Task<bool> UnsubscribeAsync(string token);
	}

	public sealed class CrmJourneyService : ICrmJourneyService
	{
		private readonly CrossDbContext _db;
		private readonly ICrmService _crm;
		private readonly ICommService _mail;
		private readonly IServiceProvider _sp;
		private readonly IDataProtector _unsub;
		public CrmJourneyService(CrossDbContext db, ICrmService crm, ICommService mail, IServiceProvider sp, IDataProtectionProvider dp)
		{ _db = db; _crm = crm; _mail = mail; _sp = sp; _unsub = dp.CreateProtector("CrossBuy.Crm.Unsubscribe.v1"); }

		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		static string N(string n, string? en) => !IsAr && !string.IsNullOrWhiteSpace(en) ? en! : n;
		static readonly JsonSerializerOptions Js = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
		public static List<J.Rule> ReadRules(string json) { try { return JsonSerializer.Deserialize<List<J.Rule>>(json, Js) ?? new(); } catch (JsonException) { return new(); } }
		public static List<J.Node> ReadNodes(string json) { try { return JsonSerializer.Deserialize<List<J.Node>>(json, Js) ?? new(); } catch (JsonException) { return new(); } }

		// ------------------------------------------------------------------ the facts every rule reads
		async Task<List<J.Facts>> FactsAsync(int cid, IReadOnlyCollection<int>? onlyContacts = null)
		{
			var cq = _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid);
			if (onlyContacts != null) cq = cq.Where(c => onlyContacts.Contains(c.ID));
			var contacts = await cq.Select(c => new { c.ID, c.AccountId, c.Email, c.Phone, c.IsPrimary }).ToListAsync();
			var accIds = contacts.Select(c => c.AccountId).Distinct().ToList();
			var accounts = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID)).Select(a => new { a.ID, a.Industry, a.OwnerEmployeeId, a.CustomerId }).ToDictionaryAsync(a => a.ID);
			var custIds = accounts.Values.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value).Distinct().ToList();
			var since = DateTime.Today.AddMonths(-12);
			var inv = await _db.SalesInvoices.AsNoTracking().Where(s => s.CompanyID == cid && s.Status == "Posted" && custIds.Contains(s.CustomerId))
				.GroupBy(s => s.CustomerId).Select(g => new { g.Key, Last = g.Max(s => s.InvoiceDate), S12 = g.Where(s => s.InvoiceDate >= since).Sum(s => (decimal?)(s.GrandTotalBase ?? s.GrandTotal)) ?? 0m })
				.ToDictionaryAsync(x => x.Key);
			var deals = await _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.AccountId != null && accIds.Contains(o.AccountId.Value))
				.GroupBy(o => o.AccountId!.Value).Select(g => new { g.Key, Open = g.Any(o => o.Stage != "Won" && o.Stage != "Lost"), Won = g.Where(o => o.Stage == "Won").Max(o => o.ExpectedCloseDate) })
				.ToDictionaryAsync(x => x.Key);
			var ids = contacts.Select(c => c.ID).ToList();
			var consent = (await _db.Set<CrmConsent>().AsNoTracking().Where(c => c.CompanyId == cid && c.SubjectType == "Contact" && ids.Contains(c.SubjectId) && (c.Channel == "WhatsApp" || c.Channel == "Email"))
				.Select(c => new { c.SubjectId, c.Channel, c.State, c.RecordedAt, c.ID }).ToListAsync())
				.GroupBy(c => (c.SubjectId, c.Channel)).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.ID).First().State);
			return contacts.Select(c =>
			{
				var a = accounts.GetValueOrDefault(c.AccountId);
				var i = a?.CustomerId is int cu ? inv.GetValueOrDefault(cu) : null;
				var d = deals.GetValueOrDefault(c.AccountId);
				return new J.Facts(c.ID, c.AccountId, a?.Industry, a?.OwnerEmployeeId, a?.CustomerId != null, i?.Last, i?.S12 ?? 0, d?.Open ?? false, d?.Won,
					consent.GetValueOrDefault((c.ID, "WhatsApp")), consent.GetValueOrDefault((c.ID, "Email")), !string.IsNullOrWhiteSpace(c.Email), !string.IsNullOrWhiteSpace(c.Phone), c.IsPrimary);
			}).ToList();
		}

		// ------------------------------------------------------------------ segments
		public async Task<(List<SegmentRow> rows, int total)> SegmentsAsync(int cid, string? q, int page, int pageSize)
		{
			var query = _db.Set<CrmSegment>().AsNoTracking().Where(s => s.CompanyId == cid);
			foreach (var t in SearchTerms.Parse(q)) { var s = t; query = query.Where(x => x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s))); }
			var total = await query.CountAsync();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var list = await query.OrderByDescending(s => s.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			var ids = list.Select(s => s.ID).ToList();
			var js = await _db.Set<CrmJourney>().AsNoTracking().Where(j => j.CompanyId == cid && ids.Contains(j.SegmentId)).GroupBy(j => j.SegmentId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			return (list.Select(s => new SegmentRow(s, ReadRules(s.RulesJson), js.GetValueOrDefault(s.ID))).ToList(), total);
		}

		public Task<CrmSegment?> SegmentAsync(int cid, int id) => _db.Set<CrmSegment>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == cid && s.ID == id);

		public async Task<List<(string value, int n)>> IndustriesAsync(int cid)
			=> (await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.Industry != null && a.Industry != "").GroupBy(a => a.Industry!.Trim()).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
				.OrderByDescending(x => x.N).Select(x => (x.Key, x.N)).ToList();

		async Task<List<int>> MatchingAsync(int cid, IReadOnlyList<J.Rule> rules, bool matchAll)
			=> (await FactsAsync(cid)).Where(f => J.Matches(rules, matchAll, f, DateTime.Today)).Select(f => f.ContactId).ToList();

		public async Task<SegmentPreview> PreviewAsync(int cid, IReadOnlyList<J.Rule> rules, bool matchAll)
		{
			if (J.RulesRefusal(rules) != null) return new SegmentPreview(0, new());
			var ids = await MatchingAsync(cid, rules, matchAll);
			var sample = ids.Take(8).ToList();
			var people = await (from c in _db.CrmContacts.AsNoTracking() where c.CompanyID == cid && sample.Contains(c.ID)
								join a in _db.CrmAccounts.AsNoTracking() on c.AccountId equals a.ID into aa from a in aa.DefaultIfEmpty()
								select new { c.ID, c.Name, c.NameEn, A = a == null ? null : a.Name, AEn = a == null ? null : a.NameEn }).ToListAsync();
			return new SegmentPreview(ids.Count, people.Select(p => (p.ID, N(p.Name, p.NameEn), p.A == null ? null : N(p.A, p.AEn))).ToList());
		}

		public async Task<(bool ok, string? error, int id)> SaveSegmentAsync(int cid, int? me, int id, string name, string? nameEn, bool matchAll, IReadOnlyList<J.Rule> rules)
		{
			name = (name ?? "").Trim();
			if (name.Length < 2) return (false, "The segment needs a name", 0);
			var refusal = J.RulesRefusal(rules);
			if (refusal != null) return (false, refusal, 0);
			CrmSegment s;
			if (id > 0) s = await _db.Set<CrmSegment>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id) ?? throw new InvalidOperationException("Not found");
			else { s = new CrmSegment { CompanyId = cid, CreatedByEmployeeId = me, CreatedAt = DateTime.UtcNow }; _db.Set<CrmSegment>().Add(s); }
			s.Name = name; s.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(); s.MatchAll = matchAll;
			s.RulesJson = JsonSerializer.Serialize(rules.Select(r => new J.Rule(r.Field, (r.Value ?? "").Trim())), Js);
			await _db.SaveChangesAsync();
			await RefreshAsync(cid, s.ID);
			return (true, null, s.ID);
		}

		/// Recomputes one segment: whoever matches now and is not in enters; whoever is in and no longer matches leaves.
		public async Task<int> RefreshAsync(int cid, int segmentId)
		{
			var s = await _db.Set<CrmSegment>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == segmentId);
			if (s == null) return 0;
			var now = DateTime.UtcNow;
			var matching = (await MatchingAsync(cid, ReadRules(s.RulesJson), s.MatchAll)).ToHashSet();
			var current = await _db.Set<CrmSegmentMember>().Where(m => m.CompanyId == cid && m.SegmentId == segmentId && m.LeftAt == null).ToListAsync();
			var inNow = current.Select(m => m.ContactId).ToHashSet();
			foreach (var m in current.Where(m => !matching.Contains(m.ContactId))) m.LeftAt = now;
			foreach (var c in matching.Where(c => !inNow.Contains(c)))
				_db.Set<CrmSegmentMember>().Add(new CrmSegmentMember { CompanyId = cid, SegmentId = segmentId, ContactId = c, EnteredAt = now });
			s.MemberCount = matching.Count; s.RefreshedAt = now;
			await _db.SaveChangesAsync();
			return matching.Count;
		}

		// ------------------------------------------------------------------ journeys
		public async Task<(List<JourneyRow> rows, int total)> JourneysAsync(int cid, string? q, int page, int pageSize)
		{
			var query = _db.Set<CrmJourney>().AsNoTracking().Where(j => j.CompanyId == cid);
			foreach (var t in SearchTerms.Parse(q)) { var s = t; query = query.Where(x => x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s))); }
			var total = await query.CountAsync();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var list = await query.OrderByDescending(j => j.Status == "Active").ThenByDescending(j => j.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			var ids = list.Select(j => j.ID).ToList(); var segIds = list.Select(j => j.SegmentId).Distinct().ToList();
			var segs = await _db.Set<CrmSegment>().AsNoTracking().Where(s => s.CompanyId == cid && segIds.Contains(s.ID)).ToDictionaryAsync(s => s.ID, s => N(s.Name, s.NameEn));
			var m = await _db.Set<CrmJourneyMember>().AsNoTracking().Where(x => x.CompanyId == cid && ids.Contains(x.JourneyId)).GroupBy(x => new { x.JourneyId, x.Status }).Select(g => new { g.Key.JourneyId, g.Key.Status, N = g.Count() }).ToListAsync();
			int C(int j, string? st) => m.Where(x => x.JourneyId == j && (st == null || x.Status == st)).Sum(x => x.N);
			return (list.Select(j => new JourneyRow(j, segs.GetValueOrDefault(j.SegmentId) ?? "—", C(j.ID, null), C(j.ID, "Active"), C(j.ID, "Finished"), C(j.ID, "Exited"))).ToList(), total);
		}

		public async Task<JourneyFile?> JourneyAsync(int cid, int id)
		{
			var j = await _db.Set<CrmJourney>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id);
			if (j == null) return null;
			var seg = await _db.Set<CrmSegment>().AsNoTracking().FirstAsync(s => s.CompanyId == cid && s.ID == j.SegmentId);
			var nodes = ReadNodes(j.NodesJson);
			var members = await _db.Set<CrmJourneyMember>().AsNoTracking().Where(x => x.CompanyId == cid && x.JourneyId == id).ToListAsync();
			var mids = members.Select(x => x.ID).ToList();
			var evs = await _db.Set<CrmJourneyEvent>().AsNoTracking().Where(e => e.CompanyId == cid && mids.Contains(e.MemberId) && e.NodeId != null)
				.GroupBy(e => new { e.NodeId, e.Outcome }).Select(g => new { g.Key.NodeId, g.Key.Outcome, N = g.Count() }).ToListAsync();
			int E(string n, params string[] o) => evs.Where(e => e.NodeId == n && o.Contains(e.Outcome)).Sum(e => e.N);
			var stats = new Dictionary<string, NodeStat>();
			void Walk(List<J.Node> list) { foreach (var n in list) { stats[n.Id] = new NodeStat(members.Count(m => m.Status == "Active" && m.NodeId == n.Id), E(n.Id, "Queued", "Sent", "Task", "Waiting"), E(n.Id, "Skipped", "Failed"), E(n.Id, "Yes"), E(n.Id, "No")); Walk(n.Yes); Walk(n.No); } }
			Walk(nodes);
			var counts = members.GroupBy(m => m.ExitReason ?? m.Status).ToDictionary(g => g.Key, g => g.Count());
			counts["Entered"] = members.Count;
			var consentSkips = await _db.Set<CrmJourneyEvent>().AsNoTracking().CountAsync(e => e.CompanyId == cid && mids.Contains(e.MemberId) && e.Outcome == "Skipped" && e.Detail != null && e.Detail.Contains("consent"));
			// what members' accounts bought in the 30 days after their entry — each account counted once, from its first entry
			var firstEntry = members.Where(m => m.AccountId != null).GroupBy(m => m.AccountId!.Value).ToDictionary(g => g.Key, g => g.Min(m => m.EnteredAt));
			var accIds = firstEntry.Keys.ToList();
			var cust = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID) && a.CustomerId != null).ToDictionaryAsync(a => a.CustomerId!.Value, a => a.ID);
			var custIds = cust.Keys.ToList();
			var earliest = firstEntry.Count == 0 ? DateTime.Today : firstEntry.Values.Min().Date;
			var invs = await _db.SalesInvoices.AsNoTracking().Where(s => s.CompanyID == cid && s.Status == "Posted" && custIds.Contains(s.CustomerId) && s.InvoiceDate >= earliest)
				.Select(s => new { s.CustomerId, s.InvoiceDate, A = s.GrandTotalBase ?? s.GrandTotal }).ToListAsync();
			var hits = invs.Where(i => firstEntry.TryGetValue(cust[i.CustomerId], out var at) && i.InvoiceDate.Date >= at.ToLocalTime().Date && i.InvoiceDate.Date <= at.ToLocalTime().Date.AddDays(30)).ToList();
			return new JourneyFile(j, seg, nodes, stats, counts, hits.Sum(i => i.A), hits.Select(i => i.CustomerId).Distinct().Count(), consentSkips);
		}

		async Task<HashSet<string>> ApprovedTemplatesAsync(int cid)
			=> (await _db.Set<CrmChannel>().AsNoTracking().Where(c => c.CompanyId == cid && c.Kind == "WhatsApp" && c.IsActive).Select(c => c.ConfigJson).ToListAsync())
				.SelectMany(j => (JsonDocument.Parse(string.IsNullOrWhiteSpace(j) ? "{}" : j).RootElement.TryGetProperty("Templates", out var t) ? t.GetString() ?? "" : "")
					.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToHashSet();

		public async Task<(bool ok, string? error, int id)> SaveJourneyAsync(int cid, int? me, int id, string name, string? nameEn, int segmentId, bool includeExisting, bool exitOnLeave, List<J.Node> nodes)
		{
			name = (name ?? "").Trim();
			if (name.Length < 2) return (false, "The journey needs a name", 0);
			if (!await _db.Set<CrmSegment>().AnyAsync(s => s.CompanyId == cid && s.ID == segmentId)) return (false, "Choose the segment that starts it", 0);
			J.AssignIds(nodes);
			var refusal = J.TreeRefusal(nodes);
			if (refusal != null) return (false, refusal, 0);
			var all = new List<J.Node>(); void Collect(List<J.Node> l) { foreach (var n in l) { all.Add(n); Collect(n.Yes); Collect(n.No); } } Collect(nodes);
			if (all.Any(n => n.Type == "whatsapp"))
			{
				var ok = await ApprovedTemplatesAsync(cid);
				if (all.Any(n => n.Type == "whatsapp" && !ok.Contains(n.Template!.Trim()))) return (false, "A WhatsApp step needs an approved template", 0);
			}
			CrmJourney j;
			if (id > 0)
			{
				j = await _db.Set<CrmJourney>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id) ?? throw new InvalidOperationException("Not found");
				if (j.SegmentId != segmentId && await _db.Set<CrmJourneyMember>().AnyAsync(m => m.CompanyId == cid && m.JourneyId == id)) return (false, "People have already entered, so the segment cannot change", 0);
			}
			else { j = new CrmJourney { CompanyId = cid, CreatedByEmployeeId = me, CreatedAt = DateTime.UtcNow, Status = "Draft" }; _db.Set<CrmJourney>().Add(j); }
			j.Name = name; j.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(); j.SegmentId = segmentId; j.IncludeExisting = includeExisting; j.ExitOnLeave = exitOnLeave;
			j.NodesJson = JsonSerializer.Serialize(nodes, Js);
			await _db.SaveChangesAsync();
			return (true, null, j.ID);
		}

		public async Task<(bool ok, string? error)> SetStatusAsync(int cid, int id, string status, string origin)
		{
			if (status is not ("Active" or "Paused")) return (false, "Unknown state");
			var j = await _db.Set<CrmJourney>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id);
			if (j == null) return (false, "Record not found");
			if (status == "Active" && J.TreeRefusal(ReadNodes(j.NodesJson)) is string r) return (false, r);
			j.Status = status; j.ActivatedAt ??= status == "Active" ? DateTime.UtcNow : null;
			if (origin.Length <= 200) j.PublicOrigin = origin;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(List<JourneyMemberRow> rows, int total)> MembersAsync(int cid, int journeyId, string? status, string? q, int page, int pageSize)
		{
			var j = await _db.Set<CrmJourney>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == journeyId);
			if (j == null) return (new(), 0);
			var nodes = ReadNodes(j.NodesJson);
			var query = _db.Set<CrmJourneyMember>().AsNoTracking().Where(m => m.CompanyId == cid && m.JourneyId == journeyId);
			if (!string.IsNullOrEmpty(status)) query = status is "Active" or "Finished" or "Exited" ? query.Where(m => m.Status == status) : query.Where(m => m.ExitReason == status);
			var all = await query.OrderByDescending(m => m.Status == "Active").ThenByDescending(m => m.ID).ToListAsync();
			var cids = all.Select(m => m.ContactId).ToList();
			var people = await (from c in _db.CrmContacts.AsNoTracking() where c.CompanyID == cid && cids.Contains(c.ID)
								join a in _db.CrmAccounts.AsNoTracking() on c.AccountId equals a.ID into aa from a in aa.DefaultIfEmpty()
								select new { c.ID, c.Name, c.NameEn, A = a == null ? null : a.Name, AEn = a == null ? null : a.NameEn }).ToDictionaryAsync(x => x.ID);
			IEnumerable<CrmJourneyMember> list = all;
			foreach (var t in SearchTerms.Parse(q)) { var s = t; list = list.Where(m => people.TryGetValue(m.ContactId, out var p) && (N(p.Name, p.NameEn).Contains(s, StringComparison.OrdinalIgnoreCase) || (p.A ?? "").Contains(s, StringComparison.OrdinalIgnoreCase))); }
			var filtered = list.ToList();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
			var mids = slice.Select(m => m.ID).ToList();
			var last = (await _db.Set<CrmJourneyEvent>().AsNoTracking().Where(e => e.CompanyId == cid && mids.Contains(e.MemberId)).ToListAsync()).GroupBy(e => e.MemberId).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.ID).First());
			return (slice.Select(m => { var p = people.GetValueOrDefault(m.ContactId); return new JourneyMemberRow(m, p == null ? "#" + m.ContactId : N(p.Name, p.NameEn), p?.A == null ? null : N(p.A, p.AEn), J.Find(nodes, m.NodeId), last.GetValueOrDefault(m.ID)); }).ToList(), filtered.Count);
		}

		public Task<List<CrmJourneyEvent>> MemberEventsAsync(int cid, int memberId)
			=> _db.Set<CrmJourneyEvent>().AsNoTracking().Where(e => e.CompanyId == cid && e.MemberId == memberId).OrderBy(e => e.ID).ToListAsync();

		// ------------------------------------------------------------------ the worker's pass
		public async Task<(int refreshed, int entered, int ran)> RunAsync(int cid, CancellationToken ct = default)
		{
			// 1) segments older than an hour are recomputed
			var stale = await _db.Set<CrmSegment>().AsNoTracking().Where(s => s.CompanyId == cid && (s.RefreshedAt == null || s.RefreshedAt < DateTime.UtcNow.AddHours(-1))).Select(s => s.ID).ToListAsync(ct);
			foreach (var s in stale) await RefreshAsync(cid, s);
			var journeys = await _db.Set<CrmJourney>().AsNoTracking().Where(j => j.CompanyId == cid && j.Status == "Active").ToListAsync(ct);
			int entered = 0, ran = 0;
			foreach (var j in journeys)
			{
				// 2) everyone who entered the segment since the journey was switched on (or everyone in it, if chosen) enters
				var known = (await _db.Set<CrmJourneyMember>().AsNoTracking().Where(m => m.CompanyId == cid && m.JourneyId == j.ID).Select(m => m.ContactId).ToListAsync(ct)).ToHashSet();
				var nodes = ReadNodes(j.NodesJson);
				var newcomers = await _db.Set<CrmSegmentMember>().AsNoTracking().Where(m => m.CompanyId == cid && m.SegmentId == j.SegmentId && m.LeftAt == null && (j.IncludeExisting || m.EnteredAt >= j.ActivatedAt))
					.Select(m => m.ContactId).ToListAsync(ct);
				var accounts = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && newcomers.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.AccountId, ct);
				foreach (var c in newcomers.Where(c => !known.Contains(c)))
				{
					var m = new CrmJourneyMember { CompanyId = cid, JourneyId = j.ID, ContactId = c, AccountId = accounts.TryGetValue(c, out var a) ? a : null, EnteredAt = DateTime.UtcNow, Status = "Active", NodeId = J.First(nodes) };
					_db.Set<CrmJourneyMember>().Add(m);
					try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { _db.Entry(m).State = EntityState.Detached; continue; }
					_db.Set<CrmJourneyEvent>().Add(new CrmJourneyEvent { CompanyId = cid, MemberId = m.ID, At = DateTime.UtcNow, Outcome = "Entered" });
					await _db.SaveChangesAsync(ct);
					entered++;
				}
				// 3) walk the active members as far as they can go now
				// ALL active members, not only those due a step: a withdrawal or a leave must end the journey even mid-wait
				var active = await _db.Set<CrmJourneyMember>().Where(m => m.CompanyId == cid && m.JourneyId == j.ID && m.Status == "Active").ToListAsync(ct);
				foreach (var m in active)
				{
					ct.ThrowIfCancellationRequested();
					try { ran += await AdvanceAsync(cid, j, nodes, m); }
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						_db.ChangeTracker.Clear();
						_sp.GetService<ILogger<CrmJourneyService>>()?.LogError(ex, "CRM journey step failed for member {Id}", m.ID);
					}
				}
			}
			return (stale.Count, entered, ran);
		}

		async Task<string?> ConsentAsync(int cid, int contactId, string channel)
			=> await _db.Set<CrmConsent>().AsNoTracking().Where(c => c.CompanyId == cid && c.SubjectType == "Contact" && c.SubjectId == contactId && c.Channel == channel)
				.OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.ID).Select(c => c.State).FirstOrDefaultAsync();

		async Task EndAsync(CrmJourneyMember m, string status, string? reason)
		{
			m.Status = status; m.ExitReason = reason; m.NodeId = null; m.NextAt = null; m.FinishedAt = DateTime.UtcNow;
			_db.Set<CrmJourneyEvent>().Add(new CrmJourneyEvent { CompanyId = m.CompanyId, MemberId = m.ID, At = DateTime.UtcNow, Outcome = status == "Finished" ? "Finished" : "Exited", Detail = reason });
			await _db.SaveChangesAsync();
		}

		async Task<bool> ClaimAsync(CrmJourneyMember m, string nodeId, string outcome, string? detail = null, int? refId = null)
		{
			var e = new CrmJourneyEvent { CompanyId = m.CompanyId, MemberId = m.ID, NodeId = nodeId, At = DateTime.UtcNow, Outcome = outcome, Detail = detail?.Length > 300 ? detail[..300] : detail, RefId = refId };
			_db.Set<CrmJourneyEvent>().Add(e);
			try { await _db.SaveChangesAsync(); return true; }
			catch (DbUpdateException) { _db.Entry(e).State = EntityState.Detached; return false; }
		}

		/// Runs nodes for one member until they wait, finish or leave. Each node is claimed by its event row first, so a
		/// node never runs twice for the same person.
		async Task<int> AdvanceAsync(int cid, CrmJourney j, List<J.Node> nodes, CrmJourneyMember m)
		{
			// exits first: a withdrawn consent ends the journey; so does leaving the segment, when the journey says so
			if (await _db.Set<CrmConsent>().AsNoTracking().AnyAsync(c => c.CompanyId == cid && c.SubjectType == "Contact" && c.SubjectId == m.ContactId && c.State == "OptOut" && c.RecordedAt > m.EnteredAt))
			{ await EndAsync(m, "Exited", "OptOut"); return 0; }
			var contact = await _db.CrmContacts.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == cid && c.ID == m.ContactId);
			if (contact == null) { await EndAsync(m, "Exited", "Removed"); return 0; }
			if (j.ExitOnLeave && !await _db.Set<CrmSegmentMember>().AsNoTracking().AnyAsync(s => s.CompanyId == cid && s.SegmentId == j.SegmentId && s.ContactId == m.ContactId && s.LeftAt == null))
			{ await EndAsync(m, "Exited", "LeftSegment"); return 0; }
			var account = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.ID == contact.AccountId).Select(a => new { a.Name, a.NameEn, a.OwnerEmployeeId, a.CustomerId }).FirstOrDefaultAsync();
			var owner = account?.OwnerEmployeeId ?? j.CreatedByEmployeeId;
			var ran = 0;
			for (var guard = 0; guard < 12 && m.NodeId != null; guard++)
			{
				var node = J.Find(nodes, m.NodeId);
				if (node == null) break;
				string? next;
				switch (node.Type)
				{
					case "wait":
						if (m.NextAt == null)
						{
							if (!await ClaimAsync(m, node.Id, "Waiting", node.Days.ToString())) return ran;
							m.NextAt = DateTime.UtcNow.AddDays(node.Days); await _db.SaveChangesAsync(); return ran + 1;
						}
						if (m.NextAt > DateTime.UtcNow) return ran;
						m.NextAt = null; next = J.After(nodes, node.Id); break;
					case "branch":
						var yes = await ConditionAsync(cid, j, m, contact, account?.CustomerId, node.Cond!);
						if (!await ClaimAsync(m, node.Id, yes ? "Yes" : "No")) return ran;
						next = J.Enter(nodes, node.Id, yes); break;
					default:
						var (outcome, detail, refId) = await RunNodeAsync(cid, j, m, contact, account == null ? null : N(account.Name, account.NameEn), owner, node);
						if (!await ClaimAsync(m, node.Id, outcome, detail, refId)) return ran;
						next = J.After(nodes, node.Id); break;
				}
				ran++;
				m.NodeId = next; await _db.SaveChangesAsync();
			}
			if (m.NodeId == null || J.Find(nodes, m.NodeId) == null) await EndAsync(m, "Finished", null);
			return ran;
		}

		async Task<bool> ConditionAsync(int cid, CrmJourney j, CrmJourneyMember m, CrmContact c, int? customerId, string cond)
		{
			switch (cond)
			{
				case "bought":
					if (customerId is not int cu) return false;
					var since = m.EnteredAt.ToLocalTime().Date;
					return await _db.SalesInvoices.AsNoTracking().AnyAsync(s => s.CompanyID == cid && s.CustomerId == cu && s.Status == "Posted" && s.InvoiceDate >= since);
				case "replied":
					var convs = _db.Set<CrmConversation>().AsNoTracking().Where(x => x.CompanyId == cid && x.ContactId == c.ID).Select(x => x.ID);
					return await _db.Set<CrmConversationMessage>().AsNoTracking().AnyAsync(x => x.CompanyId == cid && convs.Contains(x.ConversationId) && x.Direction == "In" && x.At > m.EnteredAt);
				case "inSegment":
					return await _db.Set<CrmSegmentMember>().AsNoTracking().AnyAsync(s => s.CompanyId == cid && s.SegmentId == j.SegmentId && s.ContactId == c.ID && s.LeftAt == null);
				case "waOptIn": return await ConsentAsync(cid, c.ID, "WhatsApp") == "OptIn";
				case "emailOptIn": return await ConsentAsync(cid, c.ID, "Email") == "OptIn";
				default: return false;
			}
		}

		string? UnsubscribeUrl(CrmJourney j, int contactId)
			=> j.PublicOrigin == null ? null : j.PublicOrigin + "/store/unsub/" + _unsub.Protect(j.CompanyId + ":" + contactId);

		async Task<(string outcome, string? detail, int? refId)> RunNodeAsync(int cid, CrmJourney j, CrmJourneyMember m, CrmContact c, string? company, int? owner, J.Node node)
		{
			var name = N(c.Name, c.NameEn);
			var rep = owner is int o ? await _db.Employee.AsNoTracking().Where(e => e.ID == o && e.EmpCompanyID == cid).Select(e => IsAr || string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn).FirstOrDefaultAsync() : null;
			var values = new Dictionary<string, string?> { ["name"] = name, ["first"] = S.FirstName(name), ["rep"] = rep, ["company"] = company, ["booking"] = null };
			switch (node.Type)
			{
				case "email":
					if (string.IsNullOrWhiteSpace(c.Email)) return ("Skipped", "no e-mail address", null);
					// marketing: recorded consent only
					if (await ConsentAsync(cid, c.ID, "Email") != "OptIn") return ("Skipped", "no recorded e-mail consent", null);
					var unsub = UnsubscribeUrl(j, c.ID);
					if (unsub == null) return ("Failed", "the journey has no public address for the unsubscribe link", null);
					var body = S.Render(node.Body, values) + "\n\n—\nلإيقاف هذه الرسائل / To stop these e-mails: " + unsub;
					var (mid, _, err) = await _mail.QueueAsync(cid, owner ?? 0, c.Email!, null, S.Render(node.Subject, values), body, null, null, "crm-journey");
					return err == null ? ("Queued", c.Email, mid) : ("Failed", err, mid);
				case "whatsapp":
					if (string.IsNullOrWhiteSpace(c.Phone)) return ("Skipped", "no mobile number", null);
					if (await ConsentAsync(cid, c.ID, "WhatsApp") != "OptIn") return ("Skipped", "no recorded WhatsApp consent", null);
					var inbox = ActivatorUtilities.CreateInstance<CrmInboxService>(_sp);
					var (ok, werr, conv) = await inbox.StartTemplateAsync(cid, c.Phone!, name, c.ID, null, node.Template!, owner);
					return ok ? ("Sent", node.Template, conv) : ("Failed", werr, conv);
				default:   // task for the account's owner
					var marker = "journey:" + m.ID + ":" + node.Id;
					var (aok, aerr) = await _crm.SaveActivityAsync(cid, new Activity
					{
						Type = "Call", Subject = S.Render(node.Subject, values), Notes = S.Render(node.Body, values), DueDate = DateTime.Now, OwnerEmployeeId = owner,
						ReminderAt = DateTime.UtcNow, EntityType = "Contact", EntityId = c.ID,
					}, marker);
					if (!aok) return ("Failed", aerr, null);
					var aid = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.CreatedBy == marker).OrderByDescending(a => a.ID).Select(a => (int?)a.ID).FirstOrDefaultAsync();
					return ("Task", S.Render(node.Subject, values), aid);
			}
		}

		// ------------------------------------------------------------------ unsubscribe (public)
		(int cid, int contactId)? Read(string token)
		{
			try
			{
				var parts = _unsub.Unprotect(token).Split(':');
				return parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var k) ? (c, k) : null;
			}
			catch (System.Security.Cryptography.CryptographicException) { return null; }
		}

		public async Task<(string? name, bool already)?> UnsubscribeInfoAsync(string token)
		{
			if (Read(token) is not (int cid, int contactId)) return null;
			var c = await _db.CrmContacts.AsNoTracking().Where(x => x.CompanyID == cid && x.ID == contactId).Select(x => new { x.Name }).FirstOrDefaultAsync();
			if (c == null) return null;
			return (S.FirstName(c.Name), await ConsentAsync(cid, contactId, "Email") == "OptOut");
		}

		/// Records "OptOut, e-mail, Unsubscribe" in the person's consent history. It can only ever REMOVE permission.
		public async Task<bool> UnsubscribeAsync(string token)
		{
			if (Read(token) is not (int cid, int contactId)) return false;
			if (!await _db.CrmContacts.AsNoTracking().AnyAsync(x => x.CompanyID == cid && x.ID == contactId)) return false;
			if (await ConsentAsync(cid, contactId, "Email") == "OptOut") return true;
			_db.Set<CrmConsent>().Add(new CrmConsent { CompanyId = cid, SubjectType = "Contact", SubjectId = contactId, Channel = "Email", State = "OptOut", Source = "Unsubscribe", RecordedAt = DateTime.UtcNow, Note = "e-mail unsubscribe link" });
			await _db.SaveChangesAsync();
			return true;
		}
	}
}
