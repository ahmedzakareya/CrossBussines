using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using R = CrossBuy.BL.CrmIdentity.CrmServiceRules;

namespace CrossBuy.BL.CrmIdentity
{
	// CRM C8 — the service desk around the existing tickets.
	//
	// KNOWLEDGE BASE: articles the team writes once; the ticket screen offers the best few for that ticket, and a reply
	// can carry one. Published + public articles are also the portal's help pages.
	// PUBLIC THREAD: a ticket gets a conversation the CUSTOMER sees — our replies and theirs — beside the internal
	// working the ticket already had. A first reply from us stamps the ticket's first response (that is the SLA clock).
	// PORTAL: a portal user opens tickets for their own customer, follows the thread, answers, and rates the result.
	// SURVEY: resolving a ticket creates its one satisfaction survey: the portal shows it, and when the person can be
	// reached by e-mail (and has not withdrawn e-mail consent) a one-click link is queued to them.
	// SLA SWEEP: the breach notice the supervisor used to send by button now also runs in the CRM worker — the dedup key
	// (one notice per ticket per day) already made it safe to repeat.

	public sealed record KbRow(CrmKbArticle A, PersonRef? By);
	public sealed record ThreadEntry(CrmTicketMessage M, string Who, bool Ours, string? ArticleTitle);
	public sealed record ServiceOverview(int Open, int Breached, int Unowned, int? FirstResponseMetPercent, R.Csat Csat30, R.Csat Csat90,
		List<(CrmTicketSurvey s, string subject)> Recent, List<CrmKbArticle> TopArticles, int PortalOpened30);
	public sealed record PortalTicketRow(CrmTicket T, int Replies, bool AwaitingYou, CrmTicketSurvey? Survey);
	public sealed record PortalTicketView(CrmTicket T, List<ThreadEntry> Thread, CrmTicketSurvey? Survey);

	public interface ICrmServiceDeskService
	{
		Task<(List<KbRow> rows, int total)> KbListAsync(int cid, string? q, string? status, int page, int pageSize);
		Task<CrmKbArticle?> KbAsync(int cid, int id);
		Task<(bool ok, string? error, int id)> SaveKbAsync(int cid, int? me, CrmKbArticle a);
		Task<List<CrmKbArticle>> SuggestAsync(int cid, string? text, bool publicOnly = false, int take = 3);
		Task<List<ThreadEntry>> ThreadAsync(int cid, int ticketId);
		Task<(bool ok, string? error)> ReplyAsync(int cid, int? me, int ticketId, string body, int? articleId, string origin);
		Task<CrmTicketSurvey?> SurveyOfAsync(int cid, int ticketId);
		Task<int> SurveyForAsync(int cid, int ticketId, string? origin);
		Task<ServiceOverview> OverviewAsync(int cid);
		Task<(int sent, int unowned)> SweepBreachesAsync(int cid);
		Task<int> RunAsync(int cid);
		// the portal (the caller's own customer only)
		Task<List<PortalTicketRow>> PortalTicketsAsync(PortalContext ctx);
		Task<PortalTicketView?> PortalTicketAsync(PortalContext ctx, int id);
		Task<(bool ok, string code, int id)> PortalOpenAsync(PortalContext ctx, string? subject, string? body, string? category);
		Task<(bool ok, string code)> PortalReplyAsync(PortalContext ctx, int id, string? body);
		Task<(bool ok, string code)> PortalRateAsync(PortalContext ctx, int id, int score, string? comment);
		Task<List<CrmKbArticle>> PortalHelpAsync(PortalContext ctx, string? q);
		Task<CrmKbArticle?> PortalArticleAsync(PortalContext ctx, int id);
		Task<bool> PortalVoteAsync(PortalContext ctx, int id, bool helpful);
		// the e-mailed survey link (anonymous)
		Task<(CrmTicketSurvey s, string subject)?> SurveyByTokenAsync(string token);
		Task<bool> AnswerByTokenAsync(string token, int score, string? comment);
	}

	public sealed class CrmServiceDeskService : ICrmServiceDeskService
	{
		private readonly CrossDbContext _db;
		private readonly ICrmService _crm;
		private readonly ICommService _mail;
		private readonly INotificationService _notify;
		public CrmServiceDeskService(CrossDbContext db, ICrmService crm, ICommService mail, INotificationService notify) { _db = db; _crm = crm; _mail = mail; _notify = notify; }

		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
		static string Clip(string? s, int n) => (s ?? "").Trim() is var t && t.Length > n ? t[..n] : (s ?? "").Trim();

		// ------------------------------------------------------------------ knowledge base
		public async Task<(List<KbRow> rows, int total)> KbListAsync(int cid, string? q, string? status, int page, int pageSize)
		{
			var query = _db.Set<CrmKbArticle>().AsNoTracking().Where(a => a.CompanyId == cid);
			if (!string.IsNullOrEmpty(status)) query = status == "Public" ? query.Where(a => a.IsPublic && a.Status == "Published") : query.Where(a => a.Status == status);
			foreach (var t in SearchTerms.Parse(q)) { var s = t; query = query.Where(a => a.Title.Contains(s) || (a.TitleEn != null && a.TitleEn.Contains(s)) || (a.Keywords != null && a.Keywords.Contains(s)) || (a.Category != null && a.Category.Contains(s)) || a.Body.Contains(s)); }
			var total = await query.CountAsync();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var list = await query.OrderBy(a => a.Status == "Archived").ThenByDescending(a => a.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			var ids = list.Where(a => a.UpdatedByEmployeeId != null).Select(a => a.UpdatedByEmployeeId!.Value).Distinct().ToList();
			var people = (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && ids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync())
				.ToDictionary(e => e.ID, e => new PersonRef(e.ID, (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage)));
			return (list.Select(a => new KbRow(a, a.UpdatedByEmployeeId is int p ? people.GetValueOrDefault(p) : null)).ToList(), total);
		}

		public Task<CrmKbArticle?> KbAsync(int cid, int id) => _db.Set<CrmKbArticle>().AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == cid && a.ID == id);

		public async Task<(bool ok, string? error, int id)> SaveKbAsync(int cid, int? me, CrmKbArticle dto)
		{
			var title = Clip(dto.Title, 200); var body = (dto.Body ?? "").Trim();
			if (title.Length < 3) return (false, "The article needs a title", 0);
			if (body.Length < 10) return (false, "The article needs a body", 0);
			if (dto.Status is not ("Draft" or "Published" or "Archived")) return (false, "Unknown state", 0);
			CrmKbArticle a;
			if (dto.ID > 0) a = await _db.Set<CrmKbArticle>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == dto.ID) ?? throw new InvalidOperationException("Not found");
			else { a = new CrmKbArticle { CompanyId = cid, CreatedAt = DateTime.UtcNow }; _db.Set<CrmKbArticle>().Add(a); }
			a.Title = title; a.TitleEn = string.IsNullOrWhiteSpace(dto.TitleEn) ? null : Clip(dto.TitleEn, 200); a.Body = body; a.BodyEn = string.IsNullOrWhiteSpace(dto.BodyEn) ? null : dto.BodyEn.Trim();
			a.Category = string.IsNullOrWhiteSpace(dto.Category) ? null : Clip(dto.Category, 100); a.Keywords = string.IsNullOrWhiteSpace(dto.Keywords) ? null : Clip(dto.Keywords, 400);
			a.IsPublic = dto.IsPublic; a.Status = dto.Status; a.UpdatedByEmployeeId = me; a.UpdatedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null, a.ID);
		}

		public async Task<List<CrmKbArticle>> SuggestAsync(int cid, string? text, bool publicOnly = false, int take = 3)
		{
			var q = _db.Set<CrmKbArticle>().AsNoTracking().Where(a => a.CompanyId == cid && a.Status == "Published");
			if (publicOnly) q = q.Where(a => a.IsPublic);
			var all = await q.ToListAsync();
			var best = R.Suggest(text, all.Select(a => new R.Article(a.ID, a.Title + " " + a.TitleEn, a.Keywords, a.Body + " " + a.BodyEn)), take);
			return best.Select(b => all.First(a => a.ID == b.id)).ToList();
		}

		// ------------------------------------------------------------------ the public thread
		public async Task<List<ThreadEntry>> ThreadAsync(int cid, int ticketId)
		{
			var msgs = await _db.Set<CrmTicketMessage>().AsNoTracking().Where(m => m.CompanyId == cid && m.TicketId == ticketId).OrderBy(m => m.ID).ToListAsync();
			var emp = msgs.Where(m => m.ByEmployeeId != null).Select(m => m.ByEmployeeId!.Value).Distinct().ToList();
			var pu = msgs.Where(m => m.ByPortalUserId != null).Select(m => m.ByPortalUserId!.Value).Distinct().ToList();
			var art = msgs.Where(m => m.ArticleId != null).Select(m => m.ArticleId!.Value).Distinct().ToList();
			var empNames = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && emp.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => (!IsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "");
			// only when the portal wrote here: the staff screen must not depend on the portal's tables existing
			var puNames = pu.Count == 0 ? new Dictionary<int, string>() : await _db.Set<CrossBuy.Models.Context.Portal.PortalUser>().AsNoTracking().Where(p => p.CompanyID == cid && pu.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ContactName);
			var arts = await _db.Set<CrmKbArticle>().AsNoTracking().Where(a => a.CompanyId == cid && art.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => !IsAr && !string.IsNullOrWhiteSpace(a.TitleEn) ? a.TitleEn! : a.Title);
			return msgs.Select(m => new ThreadEntry(m, m.Direction == "Out" ? empNames.GetValueOrDefault(m.ByEmployeeId ?? 0, "") : m.ByPortalUserId is int p ? puNames.GetValueOrDefault(p, "") : "",
				m.Direction == "Out", m.ArticleId is int a ? arts.GetValueOrDefault(a) : null)).ToList();
		}

		/// Our reply. It is the ticket's first response if none was stamped yet, moves a New ticket to Open, and — when the
		/// ticket came from the portal — tells the customer by e-mail that there is an answer waiting.
		public async Task<(bool ok, string? error)> ReplyAsync(int cid, int? me, int ticketId, string body, int? articleId, string origin)
		{
			if (me is not int by) return (false, "Your login is not linked to an employee");
			body = (body ?? "").Trim();
			var t = await _db.CrmTickets.FirstOrDefaultAsync(x => x.CompanyID == cid && x.ID == ticketId);
			if (t == null) return (false, "Record not found");
			if (body.Length == 0 && articleId == null) return (false, "Write a reply");
			CrmKbArticle? article = null;
			if (articleId is int aid)
			{
				article = await _db.Set<CrmKbArticle>().FirstOrDefaultAsync(a => a.CompanyId == cid && a.ID == aid && a.Status == "Published");
				if (article == null) return (false, "Record not found");
				// an internal article is for the team: it never goes to a customer as it is
				if (!article.IsPublic) return (false, "This article is internal and is not sent to customers");
				article.UsedInTickets++;
				if (body.Length == 0) body = (IsAr || string.IsNullOrWhiteSpace(article.BodyEn) ? article.Body : article.BodyEn!);
			}
			if (body.Length > 4000) return (false, "The message is too long");
			_db.Set<CrmTicketMessage>().Add(new CrmTicketMessage { CompanyId = cid, TicketId = ticketId, Direction = "Out", Body = body, ByEmployeeId = by, ArticleId = article?.ID, At = DateTime.UtcNow });
			t.FirstRespondedAt ??= DateTime.UtcNow;
			if (t.Status == "New") t.Status = "Open";
			await _db.SaveChangesAsync();
			if (t.Channel == "Portal" && t.SourceType == "PortalUser" && t.SourceId is int puid)
			{
				var pu = await _db.Set<CrossBuy.Models.Context.Portal.PortalUser>().AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == cid && p.Id == puid && p.IsActive);
				if (pu?.Email is string to && !string.IsNullOrWhiteSpace(to))
					await _mail.QueueAsync(cid, by, to, null, (IsAr ? "رد على طلبك #" : "A reply to your request #") + t.ID + " — " + t.Subject,
						body + "\n\n—\n" + (IsAr ? "تابع طلبك من بوابة العملاء: " : "Follow your request on the client portal: ") + origin + "/ClientPortal/Support/" + t.ID, null, null, "crm-ticket");
			}
			return (true, null);
		}

		// ------------------------------------------------------------------ survey
		public Task<CrmTicketSurvey?> SurveyOfAsync(int cid, int ticketId) => _db.Set<CrmTicketSurvey>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == cid && s.TicketId == ticketId);

		/// The ticket's one survey, created when it is resolved. Returns 1 when a survey was created now. The e-mail goes to
		/// the portal user who opened it, else the ticket's contact — never to someone who withdrew e-mail consent.
		public async Task<int> SurveyForAsync(int cid, int ticketId, string? origin)
		{
			var t = await _db.CrmTickets.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == cid && x.ID == ticketId);
			if (t == null || t.Status is not ("Resolved" or "Closed")) return 0;
			if (await _db.Set<CrmTicketSurvey>().AnyAsync(s => s.CompanyId == cid && s.TicketId == ticketId)) return 0;
			string? to = null;
			if (t.Channel == "Portal" && t.SourceType == "PortalUser" && t.SourceId is int puid)
				to = await _db.Set<CrossBuy.Models.Context.Portal.PortalUser>().AsNoTracking().Where(p => p.CompanyID == cid && p.Id == puid && p.IsActive).Select(p => p.Email).FirstOrDefaultAsync();
			if (string.IsNullOrWhiteSpace(to) && t.ContactId is int ct)
			{
				var email = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && c.ID == ct).Select(c => c.Email).FirstOrDefaultAsync();
				var optOut = await _db.Set<CrmConsent>().AsNoTracking().Where(c => c.CompanyId == cid && c.SubjectType == "Contact" && c.SubjectId == ct && c.Channel == "Email")
					.OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.ID).Select(c => c.State).FirstOrDefaultAsync() == "OptOut";
				if (!optOut) to = email;
			}
			var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			var s = new CrmTicketSurvey { CompanyId = cid, TicketId = ticketId, TokenHash = Hash(token), SentAt = DateTime.UtcNow, SentTo = origin != null && !string.IsNullOrWhiteSpace(to) ? to : null };
			_db.Set<CrmTicketSurvey>().Add(s);
			try { await _db.SaveChangesAsync(); } catch (DbUpdateException) { return 0; }   // another request made it first
			if (s.SentTo != null)
				await _mail.QueueAsync(cid, t.OwnerEmployeeId ?? 0, s.SentTo, null, (IsAr ? "كيف كانت خدمتنا؟ طلبك #" : "How did we do? Request #") + t.ID,
					(IsAr ? "تم حل طلبك: " : "Your request was resolved: ") + t.Subject + "\n\n" + (IsAr ? "قيّم الخدمة بنقرة واحدة: " : "Rate it with one click: ") + origin + "/store/csat/" + token, null, null, "crm-survey");
			return 1;
		}

		public async Task<(CrmTicketSurvey s, string subject)?> SurveyByTokenAsync(string token)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length != 48) return null;
			var h = Hash(token);
			var s = await _db.Set<CrmTicketSurvey>().AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == h);
			if (s == null) return null;
			var subj = await _db.CrmTickets.AsNoTracking().Where(t => t.CompanyID == s.CompanyId && t.ID == s.TicketId).Select(t => t.Subject).FirstOrDefaultAsync();
			return (s, subj ?? "");
		}

		public async Task<bool> AnswerByTokenAsync(string token, int score, string? comment)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length != 48 || score is < 1 or > 5) return false;
			var h = Hash(token);
			var s = await _db.Set<CrmTicketSurvey>().FirstOrDefaultAsync(x => x.TokenHash == h);
			if (s == null) return false;
			return await AnswerAsync(s, score, comment);
		}

		async Task<bool> AnswerAsync(CrmTicketSurvey s, int score, string? comment)
		{
			if (s.AnsweredAt != null) return true;   // the first answer stands; a second click changes nothing
			s.Score = (byte)score; s.Comment = string.IsNullOrWhiteSpace(comment) ? null : Clip(comment, 1000); s.AnsweredAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			if (score <= 2)
			{
				var t = await _db.CrmTickets.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == s.CompanyId && x.ID == s.TicketId);
				if (t?.OwnerEmployeeId is int o)
					await _notify.NotifyAsync(o, "تقييم منخفض للطلب #" + t.ID + " (" + score + "/5)", "Low rating on request #" + t.ID + " (" + score + "/5)", s.Comment, s.Comment,
						NotificationTypes.CrmTicket, t.ID, url: "/Crm/TicketEditor?id=" + t.ID, companyId: s.CompanyId, priority: "High", dedupKey: "crm-csat-low:" + s.ID);
			}
			return true;
		}

		// ------------------------------------------------------------------ overview + worker
		public async Task<ServiceOverview> OverviewAsync(int cid)
		{
			var now = DateTime.UtcNow;
			var tickets = _db.CrmTickets.AsNoTracking().Where(t => t.CompanyID == cid);
			var open = await tickets.CountAsync(t => t.Status != "Resolved" && t.Status != "Closed");
			var breached = (await _crm.BreachedTicketsAsync(cid));
			var since = now.AddDays(-90);
			var responded = await tickets.Where(t => t.CreatedAt >= since && t.FirstResponseDueAt != null && t.FirstRespondedAt != null).Select(t => new { t.FirstRespondedAt, t.FirstResponseDueAt }).ToListAsync();
			int? met = responded.Count == 0 ? null : (int)Math.Round(responded.Count(r => r.FirstRespondedAt <= r.FirstResponseDueAt) * 100.0 / responded.Count);
			var surveys = await _db.Set<CrmTicketSurvey>().AsNoTracking().Where(s => s.CompanyId == cid && s.AnsweredAt >= since).ToListAsync();
			var recent = surveys.OrderByDescending(s => s.AnsweredAt).Take(8).ToList();
			var rIds = recent.Select(s => s.TicketId).ToList();
			var subjects = await tickets.Where(t => rIds.Contains(t.ID)).ToDictionaryAsync(t => t.ID, t => t.Subject);
			var top = await _db.Set<CrmKbArticle>().AsNoTracking().Where(a => a.CompanyId == cid && a.Status == "Published").OrderByDescending(a => a.UsedInTickets + a.Views).Take(5).ToListAsync();
			var portal30 = await tickets.CountAsync(t => t.Channel == "Portal" && t.CreatedAt >= now.AddDays(-30));
			return new ServiceOverview(open, breached.Count, breached.Count(t => t.OwnerEmployeeId is not > 0), met,
				R.Summarise(surveys.Where(s => s.AnsweredAt >= now.AddDays(-30)).Select(s => (int)s.Score!)), R.Summarise(surveys.Select(s => (int)s.Score!)),
				recent.Select(s => (s, subjects.GetValueOrDefault(s.TicketId, ""))).ToList(), top, portal30);
		}

		/// The breach notice, once per ticket per day. The supervisor's button and the CRM worker both call this.
		public async Task<(int sent, int unowned)> SweepBreachesAsync(int cid)
		{
			var breached = await _crm.BreachedTicketsAsync(cid);
			int sent = 0, unowned = 0;
			foreach (var t in breached)
			{
				if (t.OwnerEmployeeId is not > 0) { unowned++; continue; }
				await _notify.NotifyAsync(t.OwnerEmployeeId.Value,
					titleAr: "تذكرة تجاوزت المدة المحددة", titleEn: "Ticket past its SLA", bodyAr: "#" + t.ID + " — " + t.Subject, bodyEn: "#" + t.ID + " — " + t.Subject,
					type: NotificationTypes.CrmTicket, refId: t.ID, url: "/Crm/TicketEditor?id=" + t.ID, companyId: cid,
					priority: "High", entityType: "Ticket", entityId: t.ID,
					// one notice per ticket per day, however often this runs
					dedupKey: "crm_ticket_breach:" + t.ID + ":" + DateTime.UtcNow.ToString("yyyyMMdd"));
				sent++;
			}
			return (sent, unowned);
		}

		/// The worker's pass: the SLA sweep, and a survey (portal-only — no request, so no link to e-mail) for any ticket
		/// resolved in the last week by a path that did not create one.
		public async Task<int> RunAsync(int cid)
		{
			await SweepBreachesAsync(cid);
			var week = DateTime.UtcNow.AddDays(-7);
			var ids = await _db.CrmTickets.AsNoTracking().Where(t => t.CompanyID == cid && (t.Status == "Resolved" || t.Status == "Closed") && t.ResolvedAt >= week
				&& !_db.Set<CrmTicketSurvey>().Any(s => s.CompanyId == cid && s.TicketId == t.ID)).Select(t => t.ID).Take(100).ToListAsync();
			var n = 0;
			foreach (var id in ids) n += await SurveyForAsync(cid, id, null);
			return n;
		}

		// ------------------------------------------------------------------ the portal
		IQueryable<CrmTicket> Mine(PortalContext ctx) => _db.CrmTickets.AsNoTracking().Where(t => t.CompanyID == ctx.CompanyId && t.CustomerId == ctx.CustomerId);

		public async Task<List<PortalTicketRow>> PortalTicketsAsync(PortalContext ctx)
		{
			var list = await Mine(ctx).OrderByDescending(t => t.Status != "Resolved" && t.Status != "Closed").ThenByDescending(t => t.ID).Take(200).ToListAsync();
			var ids = list.Select(t => t.ID).ToList();
			var msgs = await _db.Set<CrmTicketMessage>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && ids.Contains(m.TicketId)).Select(m => new { m.TicketId, m.Direction, m.ID }).ToListAsync();
			var surveys = await _db.Set<CrmTicketSurvey>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.TicketId)).ToDictionaryAsync(s => s.TicketId);
			return list.Select(t =>
			{
				var mine = msgs.Where(m => m.TicketId == t.ID).OrderBy(m => m.ID).ToList();
				return new PortalTicketRow(t, mine.Count, mine.LastOrDefault()?.Direction == "Out" && t.Status is not ("Resolved" or "Closed"), surveys.GetValueOrDefault(t.ID));
			}).ToList();
		}

		public async Task<PortalTicketView?> PortalTicketAsync(PortalContext ctx, int id)
		{
			var t = await Mine(ctx).FirstOrDefaultAsync(x => x.ID == id);
			if (t == null) return null;
			return new PortalTicketView(t, await ThreadAsync(ctx.CompanyId, id), await SurveyOfAsync(ctx.CompanyId, id));
		}

		static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int n, DateTime since)> Rate = new();
		static bool Allow(int portalUser) { var now = DateTime.UtcNow; var v = Rate.AddOrUpdate(portalUser, _ => (1, now), (_, o) => now - o.since > TimeSpan.FromHours(1) ? (1, now) : (o.n + 1, o.since)); return v.n <= 20; }

		public async Task<(bool ok, string code, int id)> PortalOpenAsync(PortalContext ctx, string? subject, string? body, string? category)
		{
			subject = Clip(subject, 200); body = Clip(body, 4000);
			if (subject.Length < 3) return (false, "subject", 0);
			if (body.Length < 5) return (false, "body", 0);
			if (!Allow(ctx.PortalUserId)) return (false, "busy", 0);
			// the CRM account that IS this customer, when there is one; a ticket without it is still the customer's
			var acc = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && a.CustomerId == ctx.CustomerId).Select(a => (int?)a.ID).FirstOrDefaultAsync();
			var (ok, err, id) = await _crm.SaveTicketAsync(ctx.CompanyId, new CrmTicket
			{
				Subject = subject, Description = body, Category = string.IsNullOrWhiteSpace(category) ? null : Clip(category, 100), Priority = "Normal", Channel = "Portal",
				CustomerId = ctx.CustomerId, AccountId = acc, SourceType = "PortalUser", SourceId = ctx.PortalUserId,
			}, "portal:" + ctx.PortalUserId);
			if (!ok) return (false, "failed", 0);
			_db.Set<CrmTicketMessage>().Add(new CrmTicketMessage { CompanyId = ctx.CompanyId, TicketId = id, Direction = "In", Body = body, ByPortalUserId = ctx.PortalUserId, At = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, "ok", id);
		}

		public async Task<(bool ok, string code)> PortalReplyAsync(PortalContext ctx, int id, string? body)
		{
			body = Clip(body, 4000);
			if (body.Length == 0) return (false, "body");
			if (!Allow(ctx.PortalUserId)) return (false, "busy");
			var t = await _db.CrmTickets.FirstOrDefaultAsync(x => x.CompanyID == ctx.CompanyId && x.CustomerId == ctx.CustomerId && x.ID == id);
			if (t == null) return (false, "not_found");
			_db.Set<CrmTicketMessage>().Add(new CrmTicketMessage { CompanyId = ctx.CompanyId, TicketId = id, Direction = "In", Body = body, ByPortalUserId = ctx.PortalUserId, At = DateTime.UtcNow });
			// the customer answering a resolved request reopens it; waiting on them ends when they answer
			if (t.Status is "Resolved" or "Pending") { t.Status = "Open"; t.ResolvedAt = null; }
			await _db.SaveChangesAsync();
			if (t.OwnerEmployeeId is int o)
				await _notify.NotifyAsync(o, "رد العميل على الطلب #" + t.ID, "The customer replied on request #" + t.ID, body, body, NotificationTypes.CrmTicket, t.ID,
					url: "/Crm/TicketEditor?id=" + t.ID, companyId: ctx.CompanyId, dedupKey: "crm-portal-reply:" + t.ID + ":" + DateTime.UtcNow.Ticks);
			return (true, "ok");
		}

		public async Task<(bool ok, string code)> PortalRateAsync(PortalContext ctx, int id, int score, string? comment)
		{
			if (score is < 1 or > 5) return (false, "score");
			if (!await Mine(ctx).AnyAsync(t => t.ID == id && (t.Status == "Resolved" || t.Status == "Closed"))) return (false, "not_found");
			var s = await _db.Set<CrmTicketSurvey>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.TicketId == id);
			if (s == null)
			{
				_db.Set<CrmTicketSurvey>().Add(s = new CrmTicketSurvey { CompanyId = ctx.CompanyId, TicketId = id, TokenHash = Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(24))), SentAt = DateTime.UtcNow });
				await _db.SaveChangesAsync();
			}
			await AnswerAsync(s, score, comment);
			return (true, "ok");
		}

		public async Task<List<CrmKbArticle>> PortalHelpAsync(PortalContext ctx, string? q)
		{
			var query = _db.Set<CrmKbArticle>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status == "Published" && a.IsPublic);
			if (string.IsNullOrWhiteSpace(q)) return await query.OrderByDescending(a => a.Views).Take(12).ToListAsync();
			return await SuggestAsync(ctx.CompanyId, q, publicOnly: true, take: 12);
		}

		public async Task<CrmKbArticle?> PortalArticleAsync(PortalContext ctx, int id)
		{
			var a = await _db.Set<CrmKbArticle>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ID == id && x.Status == "Published" && x.IsPublic);
			if (a == null) return null;
			a.Views++; await _db.SaveChangesAsync();
			return a;
		}

		public async Task<bool> PortalVoteAsync(PortalContext ctx, int id, bool helpful)
		{
			var a = await _db.Set<CrmKbArticle>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ID == id && x.Status == "Published" && x.IsPublic);
			if (a == null) return false;
			if (helpful) a.Helpful++; else a.NotHelpful++;
			await _db.SaveChangesAsync();
			return true;
		}
	}
}
