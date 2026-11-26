using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using I = CrossBuy.BL.CrmIdentity.CrmInboxRules;
using Rules = CrossBuy.BL.CrmIdentity.CrmDuplicateRules;

namespace CrossBuy.BL.CrmIdentity
{
	public sealed record ChannelView(CrmChannel Channel, string? PhoneNumberId, string? DisplayPhone, string? Templates, string? Greeting, bool HasToken, bool HasAppSecret, int Open);
	public sealed record ConversationRow(CrmConversation C, string ChannelKind, string ChannelName, string? PartyName, string? AccountName, PersonRef? Owner);
	public sealed record ThreadMessage(long Id, string Direction, string Body, string Delivery, string? Error, DateTime At, PersonRef? By);
	public sealed record ThreadView(ConversationRow Row, IReadOnlyList<ThreadMessage> Messages, string? ContactName, string? LeadName, string? DealTitle, string? TicketSubject,
		string? ConsentState, bool WindowOpen, IReadOnlyList<string> Templates, decimal? AccountSales, int? AccountHealth);
	public sealed record ChatMessageOut(long Id, string Direction, string Body, DateTime At);
	public sealed record InboxCounts(int All, int Mine, int Unassigned, int Unread, IReadOnlyDictionary<string, int> ByChannel);

	public interface ICrmInboxService
	{
		// ---- company side ----
		Task<List<ChannelView>> ChannelsAsync(int cid);
		Task<(bool ok, string? error, int id)> SaveChannelAsync(int cid, int id, string kind, string name, bool isActive, string? greeting, string? phoneNumberId, string? displayPhone,
			string? templates, string? accessToken, string? appSecret);
		Task<(List<ConversationRow> rows, InboxCounts counts)> InboxAsync(int cid, HashSet<int>? visible, int? me, string? view, string? q);
		Task<ThreadView?> ThreadAsync(int cid, HashSet<int>? visible, int id, bool markRead);
		Task<(bool ok, string? error)> SendAsync(int cid, HashSet<int>? visible, int id, string body, int? me);
		Task<(bool ok, string? error)> SendTemplateAsync(int cid, HashSet<int>? visible, int id, string template, int? me);
		Task<(bool ok, string? error)> NoteAsync(int cid, HashSet<int>? visible, int id, string body, int? me);
		Task<(bool ok, string? error)> AssignAsync(int cid, HashSet<int>? visible, int id, int? owner, int? me);
		Task<(bool ok, string? error)> SetStatusAsync(int cid, HashSet<int>? visible, int id, string status, int? me);
		Task<(bool ok, string? error, int leadId)> CreateLeadAsync(int cid, HashSet<int>? visible, int id, int? me);
		Task<(bool ok, string? error, int ticketId)> CreateTicketAsync(int cid, HashSet<int>? visible, int id, string subject, int? me);
		/// Matches new conversations to the CRM and gives each an owner, inside the company's scope.
		Task<int> LinkPendingAsync(int cid);
		// ---- the outside world: names only a channel key, never a company ----
		Task<CrmChannel?> PublicChannelAsync(string key, string kind);
		Task<(bool ok, string code, string? token)> ChatStartAsync(string key, string? name, string? contact, string? firstMessage, string? page);
		Task<(bool ok, string code)> ChatSendAsync(string key, string token, string body);
		Task<List<ChatMessageOut>?> ChatPollAsync(string key, string token, long after);
		Task<string?> WhatsAppVerifyAsync(string key, string? mode, string? verifyToken, string? challenge);
		Task<(bool ok, int stored)> WhatsAppReceiveAsync(string key, byte[] body, string? signature);
	}

	/// <summary>CRM C4 — the unified inbox: website chat, WhatsApp Business, the company mailbox.</summary>
	/// <remarks>
	/// THE SAME TWO HALVES AS THE WEB FORMS. An outside request (a chat visitor, Meta's webhook) names only a channel's
	/// unguessable key; the company comes from that channel. It writes ONLY inbox rows — a conversation and its messages —
	/// and reads no CRM record. Matching the party to a contact or lead, choosing an owner and notifying them happen in
	/// LinkPendingAsync, inside the company's own scope (the CRM worker, or a member of staff opening the inbox).
	/// CONSENT FROM C1 IS OBEYED: a person who withdrew consent on a channel is not written to on it, whoever presses send.
	/// </remarks>
	public sealed class CrmInboxService : ICrmInboxService
	{
		private readonly CrossDbContext _db;
		private readonly ICrmService _crm;
		private readonly ICommService _mail;
		private readonly INotificationService _notify;
		private readonly IHttpClientFactory _http;
		private readonly IDataProtector _protector;
		public CrmInboxService(CrossDbContext db, ICrmService crm, ICommService mail, INotificationService notify, IHttpClientFactory http, IDataProtectionProvider dp)
		{ _db = db; _crm = crm; _mail = mail; _notify = notify; _http = http; _protector = dp.CreateProtector("CrossBuy.Crm.Channels.v1"); }

		const int BodyMax = 4000;
		static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> Hits = new();
		static bool Allow(string k, int max, TimeSpan window)
		{
			var q = Hits.GetOrAdd(k, _ => new ConcurrentQueue<DateTime>()); var now = DateTime.UtcNow;
			while (q.TryPeek(out var t) && now - t > window) q.TryDequeue(out _);
			if (q.Count >= max) return false; q.Enqueue(now); return true;
		}
		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		static string Preview(string s) => s.Length > 190 ? s[..190] + "…" : s;

		sealed record ChannelConfig(string? PhoneNumberId, string? DisplayPhone, string? Templates, string? Greeting);
		sealed record ChannelSecret(string? AccessToken, string? AppSecret);
		static ChannelConfig Config(CrmChannel c) { try { return JsonSerializer.Deserialize<ChannelConfig>(c.ConfigJson ?? "{}") ?? new(null, null, null, null); } catch { return new(null, null, null, null); } }
		ChannelSecret Secret(CrmChannel c) { try { return c.SecretProtected == null ? new(null, null) : JsonSerializer.Deserialize<ChannelSecret>(_protector.Unprotect(c.SecretProtected)) ?? new(null, null); } catch { return new(null, null); } }

		// ---------------- channels ----------------

		public async Task<List<ChannelView>> ChannelsAsync(int cid)
		{
			var list = await _db.Set<CrmChannel>().AsNoTracking().Where(c => c.CompanyId == cid).OrderBy(c => c.Kind).ThenBy(c => c.Name).ToListAsync();
			var open = await _db.Set<CrmConversation>().AsNoTracking().Where(c => c.CompanyId == cid && c.Status != "Closed").GroupBy(c => c.ChannelId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			return list.Select(c => { var cfg = Config(c); var s = Secret(c);
				return new ChannelView(c, cfg.PhoneNumberId, cfg.DisplayPhone, cfg.Templates, cfg.Greeting, !string.IsNullOrEmpty(s.AccessToken), !string.IsNullOrEmpty(s.AppSecret), open.GetValueOrDefault(c.ID)); }).ToList();
		}

		public async Task<(bool ok, string? error, int id)> SaveChannelAsync(int cid, int id, string kind, string name, bool isActive, string? greeting, string? phoneNumberId, string? displayPhone,
			string? templates, string? accessToken, string? appSecret)
		{
			if (kind is not ("WebChat" or "WhatsApp" or "Email")) return (false, "Unknown channel", 0);
			if (string.IsNullOrWhiteSpace(name)) return (false, "The channel needs a name", 0);
			if (kind == "WhatsApp" && string.IsNullOrWhiteSpace(phoneNumberId)) return (false, "Enter the WhatsApp phone number ID from Meta", 0);
			CrmChannel c;
			if (id > 0)
			{
				c = await _db.Set<CrmChannel>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id) ?? null!;
				if (c == null) return (false, "Record not found", 0);
				if (c.Kind != kind) return (false, "A channel's kind cannot change", 0);
			}
			else
			{
				if (kind == "Email" && await _db.Set<CrmChannel>().AnyAsync(x => x.CompanyId == cid && x.Kind == "Email")) return (false, "The company mailbox is already connected", 0);
				c = new CrmChannel { CompanyId = cid, Kind = kind, PublicKey = I.NewToken(12), CreatedAt = DateTime.UtcNow, VerifyToken = kind == "WhatsApp" ? I.NewToken(16) : null };
				_db.Set<CrmChannel>().Add(c);
			}
			c.Name = name.Trim(); c.IsActive = isActive;
			c.ConfigJson = JsonSerializer.Serialize(new ChannelConfig(phoneNumberId?.Trim(), displayPhone?.Trim(), templates?.Trim(), greeting?.Trim()));
			// a blank secret field means "keep the one stored" — a secret is never sent back to the browser to be re-posted
			var old = Secret(c);
			var token = string.IsNullOrWhiteSpace(accessToken) ? old.AccessToken : accessToken.Trim();
			var secret = string.IsNullOrWhiteSpace(appSecret) ? old.AppSecret : appSecret.Trim();
			c.SecretProtected = token == null && secret == null ? null : _protector.Protect(JsonSerializer.Serialize(new ChannelSecret(token, secret)));
			await _db.SaveChangesAsync();
			return (true, null, c.ID);
		}

		// ---------------- the inbox ----------------

		IQueryable<CrmConversation> Visible(int cid, HashSet<int>? visible)
		{
			var q = _db.Set<CrmConversation>().AsNoTracking().Where(c => c.CompanyId == cid);
			// the CRM's owner whitelist: a rep sees their own conversations and the unassigned queue
			if (visible != null) q = q.Where(c => c.OwnerEmployeeId == null || visible.Contains(c.OwnerEmployeeId.Value));
			return q;
		}

		async Task<Dictionary<int, PersonRef>> PeopleAsync(int cid, IEnumerable<int?> ids)
		{
			var set = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
			if (set.Count == 0) return new();
			return (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && set.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync())
				.ToDictionary(e => e.ID, e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage)));
		}

		async Task<List<ConversationRow>> RowsAsync(int cid, List<CrmConversation> list)
		{
			var chIds = list.Select(c => c.ChannelId).Distinct().ToList();
			var channels = await _db.Set<CrmChannel>().AsNoTracking().Where(c => c.CompanyId == cid && chIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID);
			var cIds = list.Where(c => c.ContactId != null).Select(c => c.ContactId!.Value).ToList();
			var contacts = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && cIds.Contains(c.ID)).Select(c => new { c.ID, c.Name, c.NameEn }).ToDictionaryAsync(c => c.ID);
			var lIds = list.Where(c => c.LeadId != null).Select(c => c.LeadId!.Value).ToList();
			var leads = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && lIds.Contains(l.ID)).Select(l => new { l.ID, l.Name, l.NameEn }).ToDictionaryAsync(l => l.ID);
			var aIds = list.Where(c => c.AccountId != null).Select(c => c.AccountId!.Value).ToList();
			var accounts = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && aIds.Contains(a.ID)).Select(a => new { a.ID, a.Name, a.NameEn }).ToDictionaryAsync(a => a.ID);
			var people = await PeopleAsync(cid, list.Select(c => c.OwnerEmployeeId));
			string N(string n, string? en) => !IsAr && !string.IsNullOrWhiteSpace(en) ? en! : n;
			return list.Select(c =>
			{
				var ch = channels[c.ChannelId];
				string? party = c.ContactId is int ci && contacts.TryGetValue(ci, out var ct) ? N(ct.Name, ct.NameEn)
					: c.LeadId is int li && leads.TryGetValue(li, out var ld) ? N(ld.Name, ld.NameEn) : c.DisplayName;
				string? acc = c.AccountId is int ai && accounts.TryGetValue(ai, out var ac) ? N(ac.Name, ac.NameEn) : null;
				return new ConversationRow(c, ch.Kind, ch.Name, party, acc, c.OwnerEmployeeId is int o ? people.GetValueOrDefault(o) : null);
			}).ToList();
		}

		public async Task<(List<ConversationRow> rows, InboxCounts counts)> InboxAsync(int cid, HashSet<int>? visible, int? me, string? view, string? q)
		{
			var all = Visible(cid, visible);
			var open = all.Where(c => c.Status != "Closed");
			var byChannel = await (from c in open join ch in _db.Set<CrmChannel>().AsNoTracking() on c.ChannelId equals ch.ID group c by ch.Kind into g select new { g.Key, N = g.Count() })
				.ToDictionaryAsync(x => x.Key, x => x.N);
			var counts = new InboxCounts(await open.CountAsync(), me == null ? 0 : await open.CountAsync(c => c.OwnerEmployeeId == me), await open.CountAsync(c => c.OwnerEmployeeId == null),
				await open.CountAsync(c => c.UnreadCount > 0), byChannel);
			var query = view switch
			{
				"mine" => open.Where(c => c.OwnerEmployeeId == me),
				"unassigned" => open.Where(c => c.OwnerEmployeeId == null),
				"closed" => all.Where(c => c.Status == "Closed"),
				"WhatsApp" or "WebChat" or "Email" => from c in open join ch in _db.Set<CrmChannel>().AsNoTracking() on c.ChannelId equals ch.ID where ch.Kind == view select c,
				_ => open,
			};
			foreach (var t in SearchTerms.Parse(q))
			{
				var s = t;
				query = query.Where(c => (c.DisplayName != null && c.DisplayName.Contains(s)) || (c.Phone != null && c.Phone.Contains(s)) || (c.Email != null && c.Email.Contains(s)) || (c.LastPreview != null && c.LastPreview.Contains(s)));
			}
			var list = await query.OrderByDescending(c => c.LastMessageAt).Take(100).ToListAsync();
			return (await RowsAsync(cid, list), counts);
		}

		async Task<string?> ConsentAsync(int cid, CrmConversation c, string kind)
		{
			var channel = kind switch { "WhatsApp" => "WhatsApp", "Email" => "Email", _ => null };
			if (channel == null) return null;
			var (type, sid) = c.ContactId is int ci ? ("Contact", ci) : c.LeadId is int li ? ("Lead", li) : ("", 0);
			if (sid == 0) return null;
			return await _db.Set<CrmConsent>().AsNoTracking().Where(x => x.CompanyId == cid && x.SubjectType == type && x.SubjectId == sid && x.Channel == channel)
				.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.ID).Select(x => x.State).FirstOrDefaultAsync();
		}

		public async Task<ThreadView?> ThreadAsync(int cid, HashSet<int>? visible, int id, bool markRead)
		{
			var c = await Visible(cid, visible).FirstOrDefaultAsync(x => x.ID == id);
			if (c == null) return null;
			if (markRead && c.UnreadCount > 0) await _db.Set<CrmConversation>().Where(x => x.CompanyId == cid && x.ID == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.UnreadCount, 0));
			var row = (await RowsAsync(cid, new List<CrmConversation> { c }))[0];
			var msgs = await _db.Set<CrmConversationMessage>().AsNoTracking().Where(m => m.CompanyId == cid && m.ConversationId == id).OrderByDescending(m => m.ID).Take(200).ToListAsync();
			msgs.Reverse();
			var people = await PeopleAsync(cid, msgs.Select(m => m.SentByEmployeeId));
			string N(string n, string? en) => !IsAr && !string.IsNullOrWhiteSpace(en) ? en! : n;
			var contact = c.ContactId is int ci ? await _db.CrmContacts.AsNoTracking().Where(x => x.CompanyID == cid && x.ID == ci).Select(x => new { x.Name, x.NameEn }).FirstOrDefaultAsync() : null;
			var lead = c.LeadId is int li ? await _db.Leads.AsNoTracking().Where(x => x.CompanyID == cid && x.ID == li).Select(x => new { x.Name, x.NameEn }).FirstOrDefaultAsync() : null;
			var deal = c.OpportunityId is int oi ? await _db.Opportunities.AsNoTracking().Where(x => x.CompanyID == cid && x.ID == oi).Select(x => x.Title).FirstOrDefaultAsync() : null;
			var ticket = c.TicketId is int ti ? await _db.CrmTickets.AsNoTracking().Where(x => x.CompanyID == cid && x.ID == ti).Select(x => x.Subject).FirstOrDefaultAsync() : null;
			var channel = await _db.Set<CrmChannel>().AsNoTracking().FirstAsync(x => x.CompanyId == cid && x.ID == c.ChannelId);
			decimal? sales = null; int? health = null;
			if (c.AccountId is int ai)
			{
				var cust = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.ID == ai).Select(a => a.CustomerId).FirstOrDefaultAsync();
				if (cust is int cu)
				{
					var since = DateTime.Today.AddMonths(-12);
					sales = await _db.SalesInvoices.AsNoTracking().Where(s => s.CompanyID == cid && s.CustomerId == cu && s.Status == "Posted" && s.InvoiceDate >= since).SumAsync(s => (decimal?)(s.GrandTotalBase ?? s.GrandTotal)) ?? 0m;
				}
			}
			return new ThreadView(row, msgs.Select(m => new ThreadMessage(m.ID, m.Direction, m.Body, m.Delivery, m.Error, m.At, m.SentByEmployeeId is int b ? people.GetValueOrDefault(b) : null)).ToList(),
				contact == null ? null : N(contact.Name, contact.NameEn), lead == null ? null : N(lead.Name, lead.NameEn), deal, ticket,
				await ConsentAsync(cid, c, row.ChannelKind), row.ChannelKind != "WhatsApp" || I.InsideWindow(c.LastInboundAt, DateTime.UtcNow),
				Config(channel).Templates?.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(), sales, health);
		}

		async Task<CrmConversation?> TrackedAsync(int cid, HashSet<int>? visible, int id)
		{
			if (!await Visible(cid, visible).AnyAsync(x => x.ID == id)) return null;
			return await _db.Set<CrmConversation>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id);
		}

		async Task AddAsync(CrmConversation c, string dir, string body, int? by, string delivery, string? externalId = null, string? error = null)
		{
			var now = DateTime.UtcNow;
			_db.Set<CrmConversationMessage>().Add(new CrmConversationMessage { CompanyId = c.CompanyId, ConversationId = c.ID, Direction = dir, Body = body, SentByEmployeeId = by, Delivery = delivery, ExternalId = externalId, Error = error, At = now });
			if (dir != "Note" && dir != "System") { c.LastMessageAt = now; c.LastPreview = Preview(body); }
			await _db.SaveChangesAsync();
		}

		public async Task<(bool ok, string? error)> SendAsync(int cid, HashSet<int>? visible, int id, string body, int? me)
		{
			if (me is not int by) return (false, "You do not have permission to perform this action");
			body = (body ?? "").Trim();
			if (body.Length == 0) return (false, "Write a message");
			if (body.Length > BodyMax) return (false, "The message is too long");
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found");
			var ch = await _db.Set<CrmChannel>().AsNoTracking().FirstAsync(x => x.CompanyId == cid && x.ID == c.ChannelId);
			if (!ch.IsActive) return (false, "This channel is switched off");
			if (await ConsentAsync(cid, c, ch.Kind) == "OptOut") return (false, "This person withdrew consent on this channel. The message was not sent.");
			if (c.Status == "Closed") c.Status = "Open";
			switch (ch.Kind)
			{
				case "WebChat":
					await AddAsync(c, "Out", body, by, "Sent");
					return (true, null);
				case "WhatsApp":
				{
					if (!I.InsideWindow(c.LastInboundAt, DateTime.UtcNow)) return (false, "More than 24 hours since the customer's last message: WhatsApp only allows an approved template now.");
					var (ok, wamid, err) = await WhatsAppSendAsync(ch, c.Phone ?? c.ExternalKey.Replace("wa:", ""), new { type = "text", text = new { body } });
					await AddAsync(c, "Out", body, by, ok ? "Sent" : "Failed", wamid, err);
					return ok ? (true, null) : (false, "WhatsApp did not accept the message: " + err);
				}
				case "Email":
				{
					if (string.IsNullOrWhiteSpace(c.Email)) return (false, "This conversation has no e-mail address");
					var (_, status, err) = await _mail.SendAsync(cid, by, c.Email, null, "Re: " + (c.LastPreview ?? ch.Name), body, null, null, "crm");
					var sent = err == null && !string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);
					await AddAsync(c, "Out", body, by, sent ? "Sent" : "Failed", null, err);
					return sent ? (true, null) : (false, err ?? "The e-mail could not be sent");
				}
			}
			return (false, "Unknown channel");
		}

		public async Task<(bool ok, string? error)> SendTemplateAsync(int cid, HashSet<int>? visible, int id, string template, int? me)
		{
			if (me is not int by) return (false, "You do not have permission to perform this action");
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found");
			var ch = await _db.Set<CrmChannel>().AsNoTracking().FirstAsync(x => x.CompanyId == cid && x.ID == c.ChannelId);
			if (ch.Kind != "WhatsApp") return (false, "Templates are a WhatsApp feature");
			var allowed = Config(ch).Templates?.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
			if (!allowed.Contains(template)) return (false, "Unknown template");
			if (await ConsentAsync(cid, c, "WhatsApp") != "OptIn") return (false, "A template starts a conversation, so it needs recorded WhatsApp consent first.");
			var (ok, wamid, err) = await WhatsAppSendAsync(ch, c.Phone ?? c.ExternalKey.Replace("wa:", ""), new { type = "template", template = new { name = template, language = new { code = IsAr ? "ar" : "en" } } });
			await AddAsync(c, "Out", "[" + template + "]", by, ok ? "Sent" : "Failed", wamid, err);
			return ok ? (true, null) : (false, "WhatsApp did not accept the template: " + err);
		}

		/// CRM C6: a sequence's WhatsApp step. Opens (or reuses) the WhatsApp conversation for that number, ties it to the
		/// person when it is not tied yet, and sends the approved template. The CALLER has already required recorded
		/// WhatsApp consent; the delivery outcome is stored on the conversation exactly as a manual template send.
		public async Task<(bool ok, string? error, int? conversationId)> StartTemplateAsync(int cid, string phone, string? name, int? contactId, int? leadId, string template, int? by)
		{
			if (by is not int author) return (false, "The step has no owner to send it", null);
			var ch = await _db.Set<CrmChannel>().AsNoTracking().Where(x => x.CompanyId == cid && x.Kind == "WhatsApp" && x.IsActive).OrderBy(x => x.ID).FirstOrDefaultAsync();
			if (ch == null) return (false, "No WhatsApp channel is connected", null);
			var allowed = Config(ch).Templates?.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
			if (!allowed.Contains(template)) return (false, "Unknown template", null);
			var c = await PartyAsync(ch, I.PhoneKey(phone), name, phone, null);
			if (c.ContactId == null && c.LeadId == null) { c.ContactId = contactId; c.LeadId = leadId; c.Linked = true; c.OwnerEmployeeId ??= author; }
			var (ok, wamid, err) = await WhatsAppSendAsync(ch, phone, new { type = "template", template = new { name = template, language = new { code = IsAr ? "ar" : "en" } } });
			await AddAsync(c, "Out", "[" + template + "]", author, ok ? "Sent" : "Failed", wamid, err);
			return ok ? (true, null, c.ID) : (false, err, c.ID);
		}

		/// One call to the WhatsApp Cloud API. No token configured = refused here, never a silent "sent".
		async Task<(bool ok, string? wamid, string? error)> WhatsAppSendAsync(CrmChannel ch, string to, object message)
		{
			var cfg = Config(ch); var sec = Secret(ch);
			if (string.IsNullOrEmpty(sec.AccessToken) || string.IsNullOrEmpty(cfg.PhoneNumberId)) return (false, null, "the channel is not connected to Meta yet (no access token)");
			var payload = JsonSerializer.SerializeToElement(message);
			var dict = new Dictionary<string, object> { ["messaging_product"] = "whatsapp", ["to"] = new string(to.Where(char.IsDigit).ToArray()) };
			foreach (var p in payload.EnumerateObject()) dict[p.Name] = p.Value;
			try
			{
				var http = _http.CreateClient("crm-whatsapp");
				http.Timeout = TimeSpan.FromSeconds(15);
				using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/v21.0/{Uri.EscapeDataString(cfg.PhoneNumberId)}/messages") { Content = JsonContent.Create(dict) };
				req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sec.AccessToken);
				using var res = await http.SendAsync(req);
				var text = await res.Content.ReadAsStringAsync();
				if (!res.IsSuccessStatusCode)
				{
					string? msg = null;
					try { using var d = JsonDocument.Parse(text); msg = d.RootElement.GetProperty("error").GetProperty("message").GetString(); } catch { }
					return (false, null, (msg ?? ("HTTP " + (int)res.StatusCode)).Length > 290 ? (msg ?? "")[..290] : (msg ?? ("HTTP " + (int)res.StatusCode)));
				}
				using var doc = JsonDocument.Parse(text);
				return (true, doc.RootElement.GetProperty("messages")[0].GetProperty("id").GetString(), null);
			}
			catch (Exception ex) { return (false, null, ex.Message.Length > 290 ? ex.Message[..290] : ex.Message); }
		}

		public async Task<(bool ok, string? error)> NoteAsync(int cid, HashSet<int>? visible, int id, string body, int? me)
		{
			if (me is not int by) return (false, "You do not have permission to perform this action");
			body = (body ?? "").Trim();
			if (body.Length == 0) return (false, "Write a message");
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found");
			await AddAsync(c, "Note", body.Length > BodyMax ? body[..BodyMax] : body, by, "Internal");
			return (true, null);
		}

		public async Task<(bool ok, string? error)> AssignAsync(int cid, HashSet<int>? visible, int id, int? owner, int? me)
		{
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found");
			if (owner is int o && !await _db.Employee.AsNoTracking().AnyAsync(e => e.EmpCompanyID == cid && e.ID == o && e.IsActive)) return (false, "A chosen person is not an active employee of this company");
			c.OwnerEmployeeId = owner;
			var name = owner is int o2 ? (await PeopleAsync(cid, new int?[] { o2 })).GetValueOrDefault(o2)?.Name : null;
			await AddAsync(c, "System", owner == null ? "→ —" : "→ " + name, null, "Internal");
			if (owner is int o3 && o3 != me)
				await _notify.NotifyAsync(o3, "محادثة أُسندت إليك: " + (c.DisplayName ?? c.Phone ?? c.Email), "Conversation assigned to you: " + (c.DisplayName ?? c.Phone ?? c.Email),
					c.LastPreview, c.LastPreview, NotificationTypes.CrmAssigned, c.ID, url: "/Crm/Inbox?id=" + c.ID, companyId: cid, actorEmployeeId: me);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SetStatusAsync(int cid, HashSet<int>? visible, int id, string status, int? me)
		{
			if (status is not ("Open" or "Pending" or "Closed")) return (false, "Unknown state");
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found");
			c.Status = status;
			await AddAsync(c, "System", "● " + status, null, "Internal");
			return (true, null);
		}

		public async Task<(bool ok, string? error, int leadId)> CreateLeadAsync(int cid, HashSet<int>? visible, int id, int? me)
		{
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found", 0);
			if (c.ContactId != null || c.LeadId != null) return (false, "This conversation is already linked to a person", 0);
			var kind = await _db.Set<CrmChannel>().AsNoTracking().Where(x => x.ID == c.ChannelId).Select(x => x.Kind).FirstAsync();
			var name = string.IsNullOrWhiteSpace(c.DisplayName) ? (c.Phone ?? c.Email ?? "—") : c.DisplayName!;
			var marker = "inbox:" + c.ID;
			var (ok, err) = await _crm.SaveLeadAsync(cid, new Lead { Name = name, Phone = c.Phone, Email = c.Email, Source = kind == "WebChat" ? "Web chat" : kind, Status = "New", Notes = c.LastPreview, OwnerEmployeeId = c.OwnerEmployeeId ?? me }, marker);
			if (!ok) return (false, err, 0);
			var leadId = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.CreatedBy == marker).OrderByDescending(l => l.ID).Select(l => l.ID).FirstAsync();
			c.LeadId = leadId; c.OwnerEmployeeId ??= me;
			await AddAsync(c, "System", "+ Lead #" + leadId, null, "Internal");
			return (true, null, leadId);
		}

		public async Task<(bool ok, string? error, int ticketId)> CreateTicketAsync(int cid, HashSet<int>? visible, int id, string subject, int? me)
		{
			var c = await TrackedAsync(cid, visible, id);
			if (c == null) return (false, "Record not found", 0);
			if (c.TicketId != null) return (false, "This conversation already has a ticket", 0);
			subject = string.IsNullOrWhiteSpace(subject) ? (c.LastPreview ?? "—") : subject.Trim();
			var kind = await _db.Set<CrmChannel>().AsNoTracking().Where(x => x.ID == c.ChannelId).Select(x => x.Kind).FirstAsync();
			var (ok, err, tid) = await _crm.SaveTicketAsync(cid, new CrmTicket { Subject = subject.Length > 200 ? subject[..200] : subject, Description = c.LastPreview, AccountId = c.AccountId, ContactId = c.ContactId,
				Channel = kind == "WebChat" ? "Other" : kind, CallerPhone = c.Phone, Priority = "Normal", Status = "New", OwnerEmployeeId = c.OwnerEmployeeId }, null);
			if (!ok) return (false, err, 0);
			c.TicketId = tid;
			await AddAsync(c, "System", "+ Ticket #" + tid, null, "Internal");
			return (true, null, tid);
		}

		// ---------------- linking, in the company's scope ----------------

		public async Task<int> LinkPendingAsync(int cid)
		{
			var pending = await _db.Set<CrmConversation>().Where(c => c.CompanyId == cid && !c.Linked).OrderBy(c => c.CreatedAt).Take(100).ToListAsync();
			if (pending.Count == 0) return 0;
			var contacts = await (from c in _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid)
								  join a in _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid) on c.AccountId equals a.ID
								  select new { c.ID, c.Phone, c.Email, c.AccountId, a.OwnerEmployeeId }).ToListAsync();
			var leads = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Status != "Converted" && l.Status != "Lost")
				.Select(l => new { l.ID, l.Phone, l.Email, l.OwnerEmployeeId, l.AccountId }).ToListAsync();
			var managers = await _db.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == cid && r.Role == "SalesManager").Select(r => r.EmployeeId).Distinct().ToListAsync();
			foreach (var c in pending)
			{
				var pk = Rules.PhoneKey(c.Phone); var ek = Rules.EmailKey(c.Email);
				var ct = contacts.FirstOrDefault(x => (pk != null && pk == Rules.PhoneKey(x.Phone)) || (ek != null && ek == Rules.EmailKey(x.Email)));
				var ld = ct == null ? leads.FirstOrDefault(x => (pk != null && pk == Rules.PhoneKey(x.Phone)) || (ek != null && ek == Rules.EmailKey(x.Email))) : null;
				if (ct != null) { c.ContactId = ct.ID; c.AccountId = ct.AccountId; c.OwnerEmployeeId ??= ct.OwnerEmployeeId; }
				else if (ld != null) { c.LeadId = ld.ID; c.AccountId = ld.AccountId; c.OwnerEmployeeId ??= ld.OwnerEmployeeId; }
				c.Linked = true;
				await _db.SaveChangesAsync();
				var who = c.DisplayName ?? c.Phone ?? c.Email ?? "—";
				if (c.OwnerEmployeeId is int o)
					await _notify.NotifyAsync(o, "محادثة جديدة: " + who, "New conversation: " + who, c.LastPreview, c.LastPreview, NotificationTypes.CrmAssigned, c.ID,
						url: "/Crm/Inbox?id=" + c.ID, companyId: cid, dedupKey: "crm-conv:" + c.ID);
				else foreach (var m in managers)
					await _notify.NotifyAsync(m, "محادثة بلا مالك: " + who, "Unassigned conversation: " + who, c.LastPreview, c.LastPreview, NotificationTypes.CrmAssigned, c.ID,
						url: "/Crm/Inbox?id=" + c.ID, companyId: cid, dedupKey: "crm-conv:" + c.ID + ":" + m);
			}
			return pending.Count;
		}

		// ---------------- the outside world ----------------

		public Task<CrmChannel?> PublicChannelAsync(string key, string kind) =>
			string.IsNullOrWhiteSpace(key) || key.Length > 32 ? Task.FromResult<CrmChannel?>(null)
			: _db.Set<CrmChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.PublicKey == key && c.Kind == kind && c.IsActive);

		async Task<CrmConversation> PartyAsync(CrmChannel ch, string externalKey, string? name, string? phone, string? email)
		{
			var c = await _db.Set<CrmConversation>().FirstOrDefaultAsync(x => x.ChannelId == ch.ID && x.ExternalKey == externalKey);
			if (c != null) { if (c.Status == "Closed") c.Status = "Open"; if (string.IsNullOrWhiteSpace(c.DisplayName) && !string.IsNullOrWhiteSpace(name)) c.DisplayName = name; return c; }
			c = new CrmConversation { CompanyId = ch.CompanyId, ChannelId = ch.ID, ExternalKey = externalKey, DisplayName = name, Phone = phone, Email = email, Status = "Open", Linked = false,
				LastMessageAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
			_db.Set<CrmConversation>().Add(c);
			await _db.SaveChangesAsync();
			return c;
		}

		public async Task<(bool ok, string code, string? token)> ChatStartAsync(string key, string? name, string? contact, string? firstMessage, string? page)
		{
			var ch = await PublicChannelAsync(key, "WebChat");
			if (ch == null) return (false, "not_found", null);
			name = (name ?? "").Trim(); contact = (contact ?? "").Trim(); firstMessage = (firstMessage ?? "").Trim();
			if (name.Length < 2) return (false, "name", null);
			if (firstMessage.Length == 0) return (false, "message", null);
			if (!Allow("chat-start:" + key, 120, TimeSpan.FromHours(1))) return (false, "busy", null);
			var phone = Rules.PhoneKey(contact) != null ? contact : null;
			var email = phone == null && Rules.EmailKey(contact) != null ? contact : null;
			var token = I.NewToken();
			var c = await PartyAsync(ch, I.VisitorKey(token), name.Length > 150 ? name[..150] : name, phone, email);
			var greeting = Config(ch).Greeting;
			await InboundAsync(c, firstMessage.Length > BodyMax ? firstMessage[..BodyMax] : firstMessage, null, DateTime.UtcNow);
			if (!string.IsNullOrWhiteSpace(page)) await AddAsync(c, "System", "Page: " + (page.Length > 300 ? page[..300] : page), null, "Internal");
			if (!string.IsNullOrWhiteSpace(greeting)) _db.Set<CrmConversationMessage>().Add(new CrmConversationMessage { CompanyId = c.CompanyId, ConversationId = c.ID, Direction = "System", Body = greeting!, Delivery = "Internal", At = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, "ok", token);
		}

		async Task InboundAsync(CrmConversation c, string text, string? externalId, DateTime at)
		{
			_db.Set<CrmConversationMessage>().Add(new CrmConversationMessage { CompanyId = c.CompanyId, ConversationId = c.ID, Direction = "In", Body = text, ExternalId = externalId, Delivery = "Received", At = at });
			c.LastInboundAt = at; c.LastMessageAt = at; c.LastPreview = Preview(text); c.UnreadCount += 1;
			if (c.Status == "Closed") c.Status = "Open";
			await _db.SaveChangesAsync();
		}

		public async Task<(bool ok, string code)> ChatSendAsync(string key, string token, string body)
		{
			var ch = await PublicChannelAsync(key, "WebChat");
			if (ch == null || string.IsNullOrWhiteSpace(token)) return (false, "not_found");
			body = (body ?? "").Trim();
			if (body.Length == 0) return (false, "message");
			if (!Allow("chat:" + token, 30, TimeSpan.FromMinutes(10))) return (false, "busy");
			var vk = I.VisitorKey(token);
			var c = await _db.Set<CrmConversation>().FirstOrDefaultAsync(x => x.ChannelId == ch.ID && x.ExternalKey == vk);
			if (c == null) return (false, "not_found");
			await InboundAsync(c, body.Length > BodyMax ? body[..BodyMax] : body, null, DateTime.UtcNow);
			return (true, "ok");
		}

		/// What the visitor may see: their own messages and the replies — never an internal note, never a system line.
		public async Task<List<ChatMessageOut>?> ChatPollAsync(string key, string token, long after)
		{
			var ch = await PublicChannelAsync(key, "WebChat");
			if (ch == null || string.IsNullOrWhiteSpace(token)) return null;
			var vk = I.VisitorKey(token);
			var cid = await _db.Set<CrmConversation>().AsNoTracking().Where(x => x.ChannelId == ch.ID && x.ExternalKey == vk).Select(x => (int?)x.ID).FirstOrDefaultAsync();
			if (cid == null) return null;
			return await _db.Set<CrmConversationMessage>().AsNoTracking().Where(m => m.ConversationId == cid && m.ID > after && (m.Direction == "In" || m.Direction == "Out"))
				.OrderBy(m => m.ID).Take(100).Select(m => new ChatMessageOut(m.ID, m.Direction, m.Body, m.At)).ToListAsync();
		}

		public async Task<string?> WhatsAppVerifyAsync(string key, string? mode, string? verifyToken, string? challenge)
		{
			var ch = await PublicChannelAsync(key, "WhatsApp");
			return ch != null && mode == "subscribe" && !string.IsNullOrEmpty(ch.VerifyToken) && verifyToken == ch.VerifyToken ? challenge : null;
		}

		public async Task<(bool ok, int stored)> WhatsAppReceiveAsync(string key, byte[] body, string? signature)
		{
			var ch = await PublicChannelAsync(key, "WhatsApp");
			if (ch == null) return (false, 0);
			// nothing is believed without Meta's signature over the exact bytes received
			if (!I.SignatureValid(Secret(ch).AppSecret, body, signature)) return (false, 0);
			var (messages, statuses) = I.ParseWebhook(System.Text.Encoding.UTF8.GetString(body));
			var mine = Config(ch).PhoneNumberId;
			var stored = 0;
			foreach (var m in messages.Where(m => string.IsNullOrEmpty(mine) || m.PhoneNumberId == mine))
			{
				var c = await PartyAsync(ch, I.PhoneKey(m.From), m.Name, "+" + m.From, null);
				if (await _db.Set<CrmConversationMessage>().AnyAsync(x => x.ConversationId == c.ID && x.ExternalId == m.MessageId)) continue;   // Meta retries
				await InboundAsync(c, m.Text.Length > BodyMax ? m.Text[..BodyMax] : m.Text, m.MessageId, m.AtUtc);
				stored++;
			}
			foreach (var s in statuses)
			{
				var next = I.DeliveryFor(s.Status);
				if (next == null) continue;
				var msg = await _db.Set<CrmConversationMessage>().FirstOrDefaultAsync(x => x.CompanyId == ch.CompanyId && x.ExternalId == s.MessageId && x.Direction == "Out");
				if (msg == null || !I.Advances(msg.Delivery, next)) continue;
				msg.Delivery = next; if (s.Error != null) msg.Error = s.Error.Length > 300 ? s.Error[..300] : s.Error;
			}
			await _db.SaveChangesAsync();
			return (true, stored);
		}
	}
}
