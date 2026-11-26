using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using Rules = CrossBuy.BL.CrmIdentity.CrmDuplicateRules;
using Health = CrossBuy.BL.Platform.Ai.CrmAccountHealthRules;

namespace CrossBuy.BL.CrmIdentity
{
	// ---------------- read models (concrete records: they cross into runtime-compiled views) ----------------
	public sealed record ConsentState(string Channel, string State, string Source, DateTime At, int? ByEmployeeId);
	public sealed record ConsentEntry(int Id, string Channel, string State, string Source, string? Note, DateTime At, int? ByEmployeeId, string? ByName);
	public sealed record ContactRow(int Id, string Name, string? NameEn, string? Title, string? TitleEn, string? Phone, string? Email, bool IsPrimary,
		int AccountId, string AccountName, string? AccountNameEn, IReadOnlyDictionary<string, ConsentState> Consent);
	public sealed record PersonRef(int EmployeeId, string Name, bool HasPhoto);
	public sealed record ContactFile(ContactRow Contact, IReadOnlyList<ConsentEntry> History, IReadOnlyList<Activity> Timeline,
		IReadOnlyList<DuplicateHit> PossibleDuplicates, IReadOnlyDictionary<int, PersonRef> People);
	public sealed record DuplicatePair(string EntityType, int LowId, int HighId, string LowName, string HighName, string? LowSub, string? HighSub,
		int Score, IReadOnlyList<Rules.Reason> Reasons);
	public sealed record DataHealthFigures(int AccountPairs, int ContactPairs, int CustomerNameGroups, int ContactsTotal, int ContactsNoReach,
		int ContactsNoConsent, int AccountsNoOwner, int AccountsTotal, int Merged);
	public sealed record MergeField(string Key, string Label, string? Survivor, string? Merged, bool PickMergedByDefault);
	public sealed record MergePreview(string EntityType, int SurvivorId, int MergedId, string SurvivorName, string MergedName,
		IReadOnlyList<MergeField> Fields, IReadOnlyDictionary<string, int> Moves, string? Refusal);
	public sealed record DuplicateHit(string Kind, int Id, string Name, string? Sub, string Url, int Score, IReadOnlyList<Rules.Reason> Reasons);
	public sealed record InvoiceLine(int Id, string? No, DateTime Date, decimal Total, string Status);
	public sealed record TicketLine(int Id, string Subject, string Status, string Priority, DateTime? CreatedAt);
	public sealed record DealLine(int Id, string Title, string Stage, decimal Amount, DateTime? ExpectedClose);
	public sealed record Account360(CrmAccount Account, string? CustomerName, IReadOnlyList<ContactRow> Contacts, IReadOnlyList<DealLine> Deals,
		IReadOnlyList<InvoiceLine> Invoices, IReadOnlyList<TicketLine> Tickets, decimal Sales12m, decimal Outstanding, DateTime? FirstInvoice,
		DateTime? LastInvoice, int OpenTickets, int Health, IReadOnlyList<Health.Insight> Insights, PersonRef? Owner, IReadOnlyList<string> Tags);

	public interface ICrmIdentityService
	{
		Task<(List<ContactRow> rows, int total)> SearchContactsAsync(int cid, HashSet<int>? visible, string? q, string? consent, int page, int pageSize);
		Task<ContactFile?> ContactAsync(int cid, HashSet<int>? visible, int id);
		Task<(bool ok, string? error)> RecordConsentAsync(int cid, HashSet<int>? visible, string subjectType, int subjectId, string channel, string state, string source, string? note, int? employeeId);
		/// The one question every future sender (WhatsApp, e-mail, journeys, sequences) must ask before it sends.
		Task<bool> AllowsAsync(int cid, string subjectType, int subjectId, string channel);
		Task<DataHealthFigures> HealthAsync(int cid, HashSet<int>? visible);
		Task<List<DuplicatePair>> PairsAsync(int cid, HashSet<int>? visible, string entityType);
		Task<MergePreview?> PreviewAsync(int cid, HashSet<int>? visible, string entityType, int survivorId, int mergedId, bool isAr);
		Task<(bool ok, string? error)> MergeAsync(int cid, HashSet<int>? visible, string entityType, int survivorId, int mergedId, IReadOnlyCollection<string> takeFromMerged, int? employeeId);
		Task<(bool ok, string? error)> DismissAsync(int cid, HashSet<int>? visible, string entityType, int a, int b, int? employeeId);
		Task<List<DuplicateHit>> CheckAsync(int cid, HashSet<int>? visible, string kind, int? selfId, string? name, string? phone, string? email);
		Task<int?> SurvivorOfAsync(int cid, string entityType, int mergedId);
		Task<Account360?> Account360Async(int cid, HashSet<int>? visible, int id);
	}

	/// <summary>CRM C1 — one customer record: consent, duplicates, merge, the 360 file.</summary>
	/// <remarks>
	/// VISIBILITY IS THE CALLER'S, PASSED IN. The owner whitelist comes from ICrmAccessService exactly as the
	/// account grid applies it (null = unrestricted; an account with no owner is visible to all). A contact is
	/// visible when its account is. Every read and every write here checks it, so an id typed into a URL can
	/// never open, merge or change a record the grid would not have shown.
	/// </remarks>
	public sealed class CrmIdentityService : ICrmIdentityService
	{
		private readonly CrossDbContext _db;
		private readonly IReceivableService _ar;
		public CrmIdentityService(CrossDbContext db, IReceivableService ar) { _db = db; _ar = ar; }

		static readonly string[] Subjects = { "Contact", "Lead" };

		IQueryable<CrmAccount> Accounts(int cid, HashSet<int>? visible)
		{
			var q = _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid);
			if (visible != null) q = q.Where(a => visible.Count > 0 && (a.OwnerEmployeeId == null || visible.Contains(a.OwnerEmployeeId.Value)));
			return q;
		}

		IQueryable<CrmContact> Contacts(int cid, HashSet<int>? visible)
		{
			var acc = Accounts(cid, visible).Select(a => a.ID);
			return _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && acc.Contains(c.AccountId));
		}

		// ---------------- consent ----------------

		async Task<Dictionary<int, Dictionary<string, ConsentState>>> StatesAsync(int cid, string type, ICollection<int> ids)
		{
			var result = ids.Distinct().ToDictionary(i => i, _ => new Dictionary<string, ConsentState>());
			if (ids.Count == 0) return result;
			var rows = await _db.Set<CrmConsent>().AsNoTracking()
				.Where(x => x.CompanyId == cid && x.SubjectType == type && ids.Contains(x.SubjectId))
				.OrderBy(x => x.RecordedAt).ThenBy(x => x.ID)
				.Select(x => new { x.SubjectId, x.Channel, x.State, x.Source, x.RecordedAt, x.RecordedByEmployeeId })
				.ToListAsync();
			foreach (var r in rows)   // ascending, so the last write per channel wins
				result[r.SubjectId][r.Channel] = new ConsentState(r.Channel, r.State, r.Source, r.RecordedAt, r.RecordedByEmployeeId);
			return result;
		}

		public async Task<bool> AllowsAsync(int cid, string subjectType, int subjectId, string channel)
		{
			var last = await _db.Set<CrmConsent>().AsNoTracking()
				.Where(x => x.CompanyId == cid && x.SubjectType == subjectType && x.SubjectId == subjectId && x.Channel == channel)
				.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.ID).Select(x => x.State).FirstOrDefaultAsync();
			return last == "OptIn";   // never asked is not consent
		}

		public async Task<(bool ok, string? error)> RecordConsentAsync(int cid, HashSet<int>? visible, string subjectType, int subjectId, string channel, string state, string source, string? note, int? employeeId)
		{
			if (!Subjects.Contains(subjectType)) return (false, "Unknown record type");
			if (!CrmConsentVocabulary.Channels.Contains(channel)) return (false, "Unknown channel");
			if (!CrmConsentVocabulary.States.Contains(state)) return (false, "Unknown consent state");
			if (!CrmConsentVocabulary.StaffSources.Contains(source)) return (false, "Unknown consent source");
			note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			if (note != null && note.Length > 300) return (false, "The note is longer than 300 characters");
			var exists = subjectType == "Contact"
				? await Contacts(cid, visible).AnyAsync(c => c.ID == subjectId)
				: await _db.Leads.AsNoTracking().AnyAsync(l => l.CompanyID == cid && l.ID == subjectId);
			if (!exists) return (false, "Record not found");
			var current = (await StatesAsync(cid, subjectType, new[] { subjectId }))[subjectId];
			if (current.TryGetValue(channel, out var cur) && cur.State == state) return (false, "The consent is already in that state");
			_db.Set<CrmConsent>().Add(new CrmConsent
			{
				CompanyId = cid, SubjectType = subjectType, SubjectId = subjectId, Channel = channel, State = state, Source = source,
				Note = note, RecordedAt = DateTime.UtcNow, RecordedByEmployeeId = employeeId,
			});
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---------------- contacts ----------------

		public async Task<(List<ContactRow> rows, int total)> SearchContactsAsync(int cid, HashSet<int>? visible, string? q, string? consent, int page, int pageSize)
		{
			page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
			var query = from c in Contacts(cid, visible)
						join a in _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid) on c.AccountId equals a.ID
						select new { c, a.Name, a.NameEn };
			var terms = SearchTerms.Parse(q);
			foreach (var t in terms)
			{
				var s = t;
				query = query.Where(x => x.c.Name.Contains(s) || (x.c.NameEn != null && x.c.NameEn.Contains(s)) || (x.c.Phone != null && x.c.Phone.Contains(s))
					|| (x.c.Email != null && x.c.Email.Contains(s)) || x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s)));
			}
			// consent filters work on the latest row per channel, decided in SQL
			var latest = _db.Set<CrmConsent>().AsNoTracking().Where(k => k.CompanyId == cid && k.SubjectType == "Contact")
				.Where(k => !_db.Set<CrmConsent>().Any(n => n.CompanyId == cid && n.SubjectType == "Contact" && n.SubjectId == k.SubjectId && n.Channel == k.Channel
					&& (n.RecordedAt > k.RecordedAt || (n.RecordedAt == k.RecordedAt && n.ID > k.ID))));
			if (consent == "none") query = query.Where(x => !latest.Any(k => k.SubjectId == x.c.ID));
			else if (consent == "optout") query = query.Where(x => latest.Any(k => k.SubjectId == x.c.ID && k.State == "OptOut"));
			else if (consent == "whatsapp") query = query.Where(x => latest.Any(k => k.SubjectId == x.c.ID && k.Channel == "WhatsApp" && k.State == "OptIn"));
			else if (consent == "email") query = query.Where(x => latest.Any(k => k.SubjectId == x.c.ID && k.Channel == "Email" && k.State == "OptIn"));
			else if (consent == "noreach") query = query.Where(x => (x.c.Phone == null || x.c.Phone == "") && (x.c.Email == null || x.c.Email == ""));
			var total = await query.CountAsync();
			var page1 = await query.OrderBy(x => x.Name).ThenByDescending(x => x.c.IsPrimary).ThenBy(x => x.c.Name)
				.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			var states = await StatesAsync(cid, "Contact", page1.Select(x => x.c.ID).ToList());
			return (page1.Select(x => Row(x.c, x.Name, x.NameEn, states[x.c.ID])).ToList(), total);
		}

		static ContactRow Row(CrmContact c, string accName, string? accNameEn, IReadOnlyDictionary<string, ConsentState> consent) =>
			new(c.ID, c.Name, c.NameEn, c.Title, c.TitleEn, c.Phone, c.Email, c.IsPrimary, c.AccountId, accName, accNameEn, consent);

		async Task<Dictionary<int, PersonRef>> PeopleAsync(int cid, IEnumerable<int?> ids)
		{
			var set = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
			if (set.Count == 0) return new();
			var rows = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && set.Contains(e.ID))
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			return rows.ToDictionary(e => e.ID, e => new PersonRef(e.ID, (!isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage)));
		}

		public async Task<ContactFile?> ContactAsync(int cid, HashSet<int>? visible, int id)
		{
			var c = await Contacts(cid, visible).FirstOrDefaultAsync(x => x.ID == id);
			if (c == null) return null;
			var a = await _db.CrmAccounts.AsNoTracking().Where(x => x.CompanyID == cid && x.ID == c.AccountId).Select(x => new { x.Name, x.NameEn }).FirstAsync();
			var states = await StatesAsync(cid, "Contact", new[] { id });
			var history = await _db.Set<CrmConsent>().AsNoTracking().Where(x => x.CompanyId == cid && x.SubjectType == "Contact" && x.SubjectId == id)
				.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.ID).Take(50).ToListAsync();
			var timeline = await _db.Activities.AsNoTracking().Where(x => x.CompanyID == cid && x.EntityType == "Contact" && x.EntityId == id)
				.OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync();
			var people = await PeopleAsync(cid, history.Select(h => h.RecordedByEmployeeId).Concat(timeline.Select(t => t.OwnerEmployeeId)));
			var entries = history.Select(h => new ConsentEntry(h.ID, h.Channel, h.State, h.Source, h.Note, h.RecordedAt, h.RecordedByEmployeeId,
				h.RecordedByEmployeeId is int e && people.TryGetValue(e, out var p) ? p.Name : null)).ToList();
			var dups = (await CheckAsync(cid, visible, "contact", id, c.Name, c.Phone, c.Email)).Where(h => h.Kind == "Contact").ToList();
			return new ContactFile(Row(c, a.Name, a.NameEn, states[id]), entries, timeline, dups, people);
		}

		// ---------------- duplicates ----------------

		async Task<List<Rules.Candidate>> AccountCandidatesAsync(int cid, HashSet<int>? visible)
		{
			var rows = await (from a in Accounts(cid, visible).Where(a => a.IsActive)
							  join cu in _db.Customers.AsNoTracking().Where(c => c.CompanyID == cid) on a.CustomerId equals (int?)cu.ID into cj
							  from cu in cj.DefaultIfEmpty()
							  select new { a.ID, a.Name, a.NameEn, a.Phone, a.Email, Tax = cu == null ? null : cu.TaxRegNo }).ToListAsync();
			return rows.Select(r => new Rules.Candidate(r.ID, r.Name, r.NameEn, r.Phone, r.Email, r.Tax)).ToList();
		}

		async Task<List<Rules.Candidate>> ContactCandidatesAsync(int cid, HashSet<int>? visible)
		{
			var rows = await Contacts(cid, visible).Select(c => new { c.ID, c.Name, c.NameEn, c.Phone, c.Email, c.AccountId }).ToListAsync();
			// a contact's NAME is weak across companies (two "Mohammad Ali"s), so it only counts inside the same account
			return rows.Select(r => new Rules.Candidate(r.ID, r.Name, r.NameEn, r.Phone, r.Email, null, r.AccountId)).ToList();
		}

		async Task<HashSet<(int, int)>> DismissedAsync(int cid, string type) =>
			(await _db.Set<CrmDuplicateDismissal>().AsNoTracking().Where(d => d.CompanyId == cid && d.EntityType == type)
				.Select(d => new { d.LowId, d.HighId }).ToListAsync()).Select(d => (d.LowId, d.HighId)).ToHashSet();

		static IEnumerable<Rules.Match> ContactMatchesOnly(IEnumerable<Rules.Match> ms, IReadOnlyDictionary<int, Rules.Candidate> byId) =>
			// a contact pair needs an identifier, or a name inside one account; a bare similar name across accounts is not offered
			ms.Where(m => m.Reasons.Any(r => r is Rules.Reason.SamePhone or Rules.Reason.SameEmail) || byId[m.LowId].AccountId == byId[m.HighId].AccountId);

		public async Task<List<DuplicatePair>> PairsAsync(int cid, HashSet<int>? visible, string entityType)
		{
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string N(string n, string? en) => !isAr && !string.IsNullOrWhiteSpace(en) ? en! : n;
			if (entityType == "Account")
			{
				var all = await AccountCandidatesAsync(cid, visible);
				var byId = all.ToDictionary(c => c.Id);
				var meta = await Accounts(cid, visible).Select(a => new { a.ID, a.Industry, a.IndustryEn, a.Phone }).ToDictionaryAsync(a => a.ID);
				return Rules.FindPairs(all, await DismissedAsync(cid, "Account")).Take(200).Select(m => new DuplicatePair("Account", m.LowId, m.HighId,
					N(byId[m.LowId].Name, byId[m.LowId].NameEn), N(byId[m.HighId].Name, byId[m.HighId].NameEn),
					N(meta[m.LowId].Industry ?? "", meta[m.LowId].IndustryEn), N(meta[m.HighId].Industry ?? "", meta[m.HighId].IndustryEn), m.Score, m.Reasons)).ToList();
			}
			else
			{
				var all = await ContactCandidatesAsync(cid, visible);
				var byId = all.ToDictionary(c => c.Id);
				var accNames = await Accounts(cid, visible).Select(a => new { a.ID, a.Name, a.NameEn }).ToDictionaryAsync(a => a.ID, a => N(a.Name, a.NameEn));
				string Acc(int? id) => id is int i && accNames.TryGetValue(i, out var n) ? n : "";
				return ContactMatchesOnly(Rules.FindPairs(all, await DismissedAsync(cid, "Contact")), byId).Take(200).Select(m => new DuplicatePair("Contact", m.LowId, m.HighId,
					N(byId[m.LowId].Name, byId[m.LowId].NameEn), N(byId[m.HighId].Name, byId[m.HighId].NameEn),
					Acc(byId[m.LowId].AccountId), Acc(byId[m.HighId].AccountId), m.Score, m.Reasons)).ToList();
			}
		}

		public async Task<DataHealthFigures> HealthAsync(int cid, HashSet<int>? visible)
		{
			var accountPairs = (await PairsAsync(cid, visible, "Account")).Count;
			var contactPairs = (await PairsAsync(cid, visible, "Contact")).Count;
			var names = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == cid).Select(c => c.Name).ToListAsync();
			var customerGroups = names.GroupBy(n => string.Join(' ', Rules.NameTokens(n))).Count(g => g.Key.Length > 0 && g.Count() > 1);
			var contacts = Contacts(cid, visible);
			var contactsTotal = await contacts.CountAsync();
			var noReach = await contacts.CountAsync(c => (c.Phone == null || c.Phone == "") && (c.Email == null || c.Email == ""));
			var withConsent = await _db.Set<CrmConsent>().AsNoTracking().Where(k => k.CompanyId == cid && k.SubjectType == "Contact")
				.Select(k => k.SubjectId).Distinct().Where(id => contacts.Select(c => c.ID).Contains(id)).CountAsync();
			var accounts = Accounts(cid, visible).Where(a => a.IsActive);
			return new DataHealthFigures(accountPairs, contactPairs, customerGroups, contactsTotal, noReach, contactsTotal - withConsent,
				await accounts.CountAsync(a => a.OwnerEmployeeId == null), await accounts.CountAsync(),
				await _db.Set<CrmMerge>().AsNoTracking().CountAsync(m => m.CompanyId == cid));
		}

		public async Task<List<DuplicateHit>> CheckAsync(int cid, HashSet<int>? visible, string kind, int? selfId, string? name, string? phone, string? email)
		{
			var hits = new List<DuplicateHit>();
			if (string.IsNullOrWhiteSpace(name) && Rules.PhoneKey(phone) == null && Rules.EmailKey(email) == null) return hits;
			var probe = new Rules.Candidate(-1, name ?? "", null, phone, email);
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string N(string n, string? en) => !isAr && !string.IsNullOrWhiteSpace(en) ? en! : n;
			// accounts: a lead or a new account whose company already exists is the commonest duplicate of all
			foreach (var a in await AccountCandidatesAsync(cid, visible))
			{
				if (kind == "account" && a.Id == selfId) continue;
				// a warning, not a merge proposal: enough evidence together, OR one identifier already on file
				if (Rules.Evidence(probe, a) is { } m && (m.Score >= Rules.Threshold || Rules.SharesIdentifier(m)))
					hits.Add(new DuplicateHit("Account", a.Id, N(a.Name, a.NameEn), null, "/Crm/Account360/" + a.Id, m.Score, m.Reasons));
			}
			// contacts and leads are people: a name alone is never offered, an identifier is
			var personProbe = probe with { Name = "" };
			// two people can share a name; the account they belong to is what tells them apart in the warning
			var accountNames = await Accounts(cid, visible).Select(a => new { a.ID, a.Name, a.NameEn }).ToDictionaryAsync(a => a.ID, a => N(a.Name, a.NameEn));
			foreach (var c in await ContactCandidatesAsync(cid, visible))
			{
				if (kind == "contact" && c.Id == selfId) continue;
				var m = Rules.Evidence(probe, c);
				if (m != null && Rules.SharesIdentifier(m))
					hits.Add(new DuplicateHit("Contact", c.Id, N(c.Name, c.NameEn), c.AccountId is int ai && accountNames.TryGetValue(ai, out var an) ? an : null, "/Crm/Contact/" + c.Id, m.Score, m.Reasons));
			}
			var leads = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Status != "Converted")
				.Select(l => new { l.ID, l.Name, l.NameEn, l.Company, l.Phone, l.Email }).ToListAsync();
			foreach (var l in leads)
			{
				if (kind == "lead" && l.ID == selfId) continue;
				var m = Rules.Evidence(personProbe, new Rules.Candidate(l.ID, "", null, l.Phone, l.Email));
				if (m != null && Rules.SharesIdentifier(m)) hits.Add(new DuplicateHit("Lead", l.ID, N(l.Name, l.NameEn), l.Company, "/Crm/Leads?q=" + Uri.EscapeDataString(l.Name), m.Score, m.Reasons));
			}
			return hits.OrderByDescending(h => h.Score).Take(8).ToList();
		}

		public async Task<int?> SurvivorOfAsync(int cid, string entityType, int mergedId)
		{
			// follow a chain (A merged into B, later B into C) to the record that is still alive
			int? current = null; var id = mergedId;
			for (var hop = 0; hop < 10; hop++)
			{
				var next = await _db.Set<CrmMerge>().AsNoTracking().Where(m => m.CompanyId == cid && m.EntityType == entityType && m.MergedId == id)
					.Select(m => (int?)m.SurvivorId).FirstOrDefaultAsync();
				if (next == null) break;
				current = id = next.Value;
			}
			return current;
		}

		public async Task<(bool ok, string? error)> DismissAsync(int cid, HashSet<int>? visible, string entityType, int a, int b, int? employeeId)
		{
			if (a == b || (entityType != "Account" && entityType != "Contact")) return (false, "Unknown record type");
			var ok = entityType == "Account"
				? await Accounts(cid, visible).CountAsync(x => x.ID == a || x.ID == b) == 2
				: await Contacts(cid, visible).CountAsync(x => x.ID == a || x.ID == b) == 2;
			if (!ok) return (false, "Record not found");
			var lo = Math.Min(a, b); var hi = Math.Max(a, b);
			if (await _db.Set<CrmDuplicateDismissal>().AnyAsync(d => d.CompanyId == cid && d.EntityType == entityType && d.LowId == lo && d.HighId == hi)) return (true, null);
			_db.Set<CrmDuplicateDismissal>().Add(new CrmDuplicateDismissal { CompanyId = cid, EntityType = entityType, LowId = lo, HighId = hi, DismissedAt = DateTime.UtcNow, DismissedByEmployeeId = employeeId });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---------------- merge ----------------

		static readonly (string key, string ar, string en)[] AccountFields =
		{
			("Name", "الاسم", "Name"), ("NameEn", "الاسم بالإنجليزي", "English name"), ("Industry", "القطاع", "Industry"), ("Phone", "الهاتف", "Phone"),
			("Email", "البريد", "Email"), ("Website", "الموقع", "Website"), ("Address", "العنوان", "Address"), ("Segment", "الشريحة", "Segment"), ("Owner", "المالك", "Owner"),
		};
		static readonly (string key, string ar, string en)[] ContactFields =
		{
			("Name", "الاسم", "Name"), ("NameEn", "الاسم بالإنجليزي", "English name"), ("Title", "المسمى", "Title"), ("Phone", "الجوال", "Mobile"), ("Email", "البريد", "Email"),
		};

		static string? AccountValue(CrmAccount a, string key) => key switch
		{
			"Name" => a.Name, "NameEn" => a.NameEn, "Industry" => a.Industry, "Phone" => a.Phone, "Email" => a.Email, "Website" => a.Website,
			"Address" => a.Address, "Segment" => a.Segment, "Owner" => a.OwnerEmployeeId?.ToString(), _ => null,
		};
		static string? ContactValue(CrmContact c, string key) => key switch
		{
			"Name" => c.Name, "NameEn" => c.NameEn, "Title" => c.Title, "Phone" => c.Phone, "Email" => c.Email, _ => null,
		};

		async Task<Dictionary<string, int>> AccountMovesAsync(int cid, int mergedId) => new()
		{
			["Contacts"] = await _db.CrmContacts.CountAsync(x => x.CompanyID == cid && x.AccountId == mergedId),
			["Deals"] = await _db.Opportunities.CountAsync(x => x.CompanyID == cid && x.AccountId == mergedId),
			["Leads"] = await _db.Leads.CountAsync(x => x.CompanyID == cid && x.AccountId == mergedId),
			["Tickets"] = await _db.CrmTickets.CountAsync(x => x.CompanyID == cid && x.AccountId == mergedId),
			["Calls"] = await _db.CrmCalls.CountAsync(x => x.CompanyID == cid && x.AccountId == mergedId),
			["Activities"] = await _db.Activities.CountAsync(x => x.CompanyID == cid && x.EntityType == "Account" && x.EntityId == mergedId),
			["Campaigns"] = await _db.CampaignMembers.CountAsync(x => x.CompanyID == cid && x.EntityType == "Account" && x.EntityId == mergedId),
			["Lists"] = await _db.CrmListMembers.CountAsync(x => x.CompanyID == cid && x.EntityType == "Account" && x.EntityId == mergedId),
		};

		async Task<Dictionary<string, int>> ContactMovesAsync(int cid, int mergedId) => new()
		{
			["Tickets"] = await _db.CrmTickets.CountAsync(x => x.CompanyID == cid && x.ContactId == mergedId),
			["Calls"] = await _db.CrmCalls.CountAsync(x => x.CompanyID == cid && x.ContactId == mergedId),
			["Activities"] = await _db.Activities.CountAsync(x => x.CompanyID == cid && x.EntityType == "Contact" && x.EntityId == mergedId),
			["Campaigns"] = await _db.CampaignMembers.CountAsync(x => x.CompanyID == cid && x.EntityType == "Contact" && x.EntityId == mergedId),
			["Lists"] = await _db.CrmListMembers.CountAsync(x => x.CompanyID == cid && x.EntityType == "Contact" && x.EntityId == mergedId),
			["Consents"] = await _db.Set<CrmConsent>().CountAsync(x => x.CompanyId == cid && x.SubjectType == "Contact" && x.SubjectId == mergedId),
		};

		/// Why a merge may not happen, or null. Two accounts tied to two DIFFERENT financial customers carry two
		/// ledgers; joining the CRM records would leave one customer's invoices behind a closed account. That is
		/// an accounting decision, so it is refused here and sent there.
		static string? AccountRefusal(CrmAccount s, CrmAccount m) =>
			s.CustomerId != null && m.CustomerId != null && s.CustomerId != m.CustomerId
				? "Both accounts are linked to different financial customers. Merge the customers in Accounting first."
				: null;

		public async Task<MergePreview?> PreviewAsync(int cid, HashSet<int>? visible, string entityType, int survivorId, int mergedId, bool isAr)
		{
			if (survivorId == mergedId) return null;
			if (entityType == "Account")
			{
				var two = await Accounts(cid, visible).Where(a => a.ID == survivorId || a.ID == mergedId).ToListAsync();
				if (two.Count != 2) return null;
				var s = two.First(a => a.ID == survivorId); var m = two.First(a => a.ID == mergedId);
				var owners = await PeopleAsync(cid, new[] { s.OwnerEmployeeId, m.OwnerEmployeeId });
				string? Show(CrmAccount a, string k) => k == "Owner" ? (a.OwnerEmployeeId is int o && owners.TryGetValue(o, out var p) ? p.Name : null) : AccountValue(a, k);
				var fields = AccountFields.Select(f => new MergeField(f.key, isAr ? f.ar : f.en, Show(s, f.key), Show(m, f.key),
					string.IsNullOrWhiteSpace(AccountValue(s, f.key)) && !string.IsNullOrWhiteSpace(AccountValue(m, f.key)))).ToList();
				return new MergePreview("Account", s.ID, m.ID, s.Name, m.Name, fields, await AccountMovesAsync(cid, m.ID), AccountRefusal(s, m));
			}
			if (entityType == "Contact")
			{
				var two = await Contacts(cid, visible).Where(c => c.ID == survivorId || c.ID == mergedId).ToListAsync();
				if (two.Count != 2) return null;
				var s = two.First(c => c.ID == survivorId); var m = two.First(c => c.ID == mergedId);
				var fields = ContactFields.Select(f => new MergeField(f.key, isAr ? f.ar : f.en, ContactValue(s, f.key), ContactValue(m, f.key),
					string.IsNullOrWhiteSpace(ContactValue(s, f.key)) && !string.IsNullOrWhiteSpace(ContactValue(m, f.key)))).ToList();
				return new MergePreview("Contact", s.ID, m.ID, s.Name, m.Name, fields, await ContactMovesAsync(cid, m.ID), null);
			}
			return null;
		}

		public async Task<(bool ok, string? error)> MergeAsync(int cid, HashSet<int>? visible, string entityType, int survivorId, int mergedId, IReadOnlyCollection<string> takeFromMerged, int? employeeId)
		{
			if (survivorId == mergedId) return (false, "Choose two different records");
			if (entityType == "Account") return await MergeAccountsAsync(cid, visible, survivorId, mergedId, takeFromMerged, employeeId);
			if (entityType == "Contact") return await MergeContactsAsync(cid, visible, survivorId, mergedId, takeFromMerged, employeeId);
			return (false, "Unknown record type");
		}

		static bool Take(IReadOnlyCollection<string> pick, string key, string? survivorValue, string? mergedValue) =>
			!string.IsNullOrWhiteSpace(mergedValue) && (pick.Contains(key) || string.IsNullOrWhiteSpace(survivorValue));

		async Task<(bool ok, string? error)> MergeAccountsAsync(int cid, HashSet<int>? visible, int sid, int mid, IReadOnlyCollection<string> pick, int? emp)
		{
			var ids = await Accounts(cid, visible).Where(a => (a.ID == sid || a.ID == mid) && a.IsActive).Select(a => a.ID).ToListAsync();
			if (ids.Count != 2) return (false, "Record not found");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var s = await _db.CrmAccounts.FirstAsync(a => a.CompanyID == cid && a.ID == sid);
			var m = await _db.CrmAccounts.FirstAsync(a => a.CompanyID == cid && a.ID == mid);
			if (AccountRefusal(s, m) is { } refusal) return (false, refusal);
			var moves = await AccountMovesAsync(cid, mid);

			if (Take(pick, "Name", s.Name, m.Name)) s.Name = m.Name;
			if (Take(pick, "NameEn", s.NameEn, m.NameEn)) s.NameEn = m.NameEn;
			if (Take(pick, "Industry", s.Industry, m.Industry)) { s.Industry = m.Industry; s.IndustryEn = m.IndustryEn; }
			if (Take(pick, "Phone", s.Phone, m.Phone)) s.Phone = m.Phone;
			if (Take(pick, "Email", s.Email, m.Email)) s.Email = m.Email;
			if (Take(pick, "Website", s.Website, m.Website)) s.Website = m.Website;
			if (Take(pick, "Address", s.Address, m.Address)) s.Address = m.Address;
			if (Take(pick, "Segment", s.Segment, m.Segment)) s.Segment = m.Segment;
			if (m.OwnerEmployeeId != null && (pick.Contains("Owner") || s.OwnerEmployeeId == null)) s.OwnerEmployeeId = m.OwnerEmployeeId;
			if (!string.IsNullOrWhiteSpace(m.Notes)) s.Notes = string.IsNullOrWhiteSpace(s.Notes) ? m.Notes : s.Notes + "\n— " + m.Name + ": " + m.Notes;
			// one account ↔ one financial customer: the link moves only into an empty seat
			var customer = m.CustomerId;
			m.IsActive = false;
			if (customer != null && s.CustomerId == null) { m.CustomerId = null; await _db.SaveChangesAsync(); s.CustomerId = customer; }
			await _db.SaveChangesAsync();

			await _db.CrmContacts.Where(x => x.CompanyID == cid && x.AccountId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, sid));
			await _db.Opportunities.Where(x => x.CompanyID == cid && x.AccountId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, sid));
			await _db.Leads.Where(x => x.CompanyID == cid && x.AccountId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, sid));
			await _db.CrmTickets.Where(x => x.CompanyID == cid && x.AccountId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, sid));
			await _db.CrmCalls.Where(x => x.CompanyID == cid && x.AccountId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, sid));
			await MovePolymorphicAsync(cid, "Account", mid, sid);
			// tags: the union, never a second copy of the same tag
			var survivorTags = _db.TagAssignments.Where(t => t.CompanyID == cid && t.EntityType == Platform.EntityRegistry.CrmAccount && t.EntityId == sid).Select(t => t.TagId);
			await _db.TagAssignments.Where(t => t.CompanyID == cid && t.EntityType == Platform.EntityRegistry.CrmAccount && t.EntityId == mid && survivorTags.Contains(t.TagId)).ExecuteDeleteAsync();
			await _db.TagAssignments.Where(t => t.CompanyID == cid && t.EntityType == Platform.EntityRegistry.CrmAccount && t.EntityId == mid).ExecuteUpdateAsync(u => u.SetProperty(t => t.EntityId, sid));
			// custom field values: the survivor's own answer wins, the merged record fills only what is empty
			var survivorFields = _db.CrmCustomFieldValues.Where(v => v.CompanyID == cid && v.EntityType == "Account" && v.EntityId == sid && v.Value != null && v.Value != "").Select(v => v.FieldId);
			await _db.CrmCustomFieldValues.Where(v => v.CompanyID == cid && v.EntityType == "Account" && v.EntityId == sid && (v.Value == null || v.Value == "")).ExecuteDeleteAsync();
			await _db.CrmCustomFieldValues.Where(v => v.CompanyID == cid && v.EntityType == "Account" && v.EntityId == mid && survivorFields.Contains(v.FieldId)).ExecuteDeleteAsync();
			await _db.CrmCustomFieldValues.Where(v => v.CompanyID == cid && v.EntityType == "Account" && v.EntityId == mid).ExecuteUpdateAsync(u => u.SetProperty(v => v.EntityId, sid));

			_db.Set<CrmMerge>().Add(new CrmMerge { CompanyId = cid, EntityType = "Account", SurvivorId = sid, MergedId = mid, MergedName = m.Name, MovedJson = JsonSerializer.Serialize(moves), MergedAt = DateTime.UtcNow, MergedByEmployeeId = emp });
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		async Task MovePolymorphicAsync(int cid, string type, int from, int to)
		{
			await _db.Activities.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == from).ExecuteUpdateAsync(u => u.SetProperty(x => x.EntityId, to));
			// memberships: never enrol the survivor twice in one campaign or list
			var inCampaigns = _db.CampaignMembers.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == to).Select(x => x.CampaignId);
			await _db.CampaignMembers.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == from && inCampaigns.Contains(x.CampaignId)).ExecuteDeleteAsync();
			await _db.CampaignMembers.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == from).ExecuteUpdateAsync(u => u.SetProperty(x => x.EntityId, to));
			var inLists = _db.CrmListMembers.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == to).Select(x => x.ListId);
			await _db.CrmListMembers.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == from && inLists.Contains(x.ListId)).ExecuteDeleteAsync();
			await _db.CrmListMembers.Where(x => x.CompanyID == cid && x.EntityType == type && x.EntityId == from).ExecuteUpdateAsync(u => u.SetProperty(x => x.EntityId, to));
		}

		async Task<(bool ok, string? error)> MergeContactsAsync(int cid, HashSet<int>? visible, int sid, int mid, IReadOnlyCollection<string> pick, int? emp)
		{
			if (await Contacts(cid, visible).CountAsync(c => c.ID == sid || c.ID == mid) != 2) return (false, "Record not found");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var s = await _db.CrmContacts.FirstAsync(c => c.CompanyID == cid && c.ID == sid);
			var m = await _db.CrmContacts.FirstAsync(c => c.CompanyID == cid && c.ID == mid);
			var moves = await ContactMovesAsync(cid, mid);
			if (Take(pick, "Name", s.Name, m.Name)) s.Name = m.Name;
			if (Take(pick, "NameEn", s.NameEn, m.NameEn)) s.NameEn = m.NameEn;
			if (Take(pick, "Title", s.Title, m.Title)) { s.Title = m.Title; s.TitleEn = m.TitleEn; }
			if (Take(pick, "Phone", s.Phone, m.Phone)) s.Phone = m.Phone;
			if (Take(pick, "Email", s.Email, m.Email)) s.Email = m.Email;
			if (m.IsPrimary && s.AccountId == m.AccountId) s.IsPrimary = true;
			if (!string.IsNullOrWhiteSpace(m.Notes)) s.Notes = string.IsNullOrWhiteSpace(s.Notes) ? m.Notes : s.Notes + "\n— " + m.Name + ": " + m.Notes;

			await _db.CrmTickets.Where(x => x.CompanyID == cid && x.ContactId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.ContactId, sid));
			await _db.CrmCalls.Where(x => x.CompanyID == cid && x.ContactId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.ContactId, sid));
			await MovePolymorphicAsync(cid, "Contact", mid, sid);
			// consent is about the PERSON, so the whole history moves; the latest decision on each channel stands
			await _db.Set<CrmConsent>().Where(x => x.CompanyId == cid && x.SubjectType == "Contact" && x.SubjectId == mid).ExecuteUpdateAsync(u => u.SetProperty(x => x.SubjectId, sid));
			_db.CrmContacts.Remove(m);
			_db.Set<CrmMerge>().Add(new CrmMerge { CompanyId = cid, EntityType = "Contact", SurvivorId = sid, MergedId = mid, MergedName = m.Name, MovedJson = JsonSerializer.Serialize(moves), MergedAt = DateTime.UtcNow, MergedByEmployeeId = emp });
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		// ---------------- the 360 file ----------------

		public async Task<Account360?> Account360Async(int cid, HashSet<int>? visible, int id)
		{
			var a = await Accounts(cid, visible).FirstOrDefaultAsync(x => x.ID == id);
			if (a == null) return null;
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var contacts = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && c.AccountId == id).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name).ToListAsync();
			var states = await StatesAsync(cid, "Contact", contacts.Select(c => c.ID).ToList());
			var deals = await _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.AccountId == id)
				.OrderByDescending(o => o.CreatedAt).Take(50).Select(o => new DealLine(o.ID, o.Title, o.Stage, o.Amount, o.ExpectedCloseDate)).ToListAsync();
			var tickets = await _db.CrmTickets.AsNoTracking().Where(t => t.CompanyID == cid && (t.AccountId == id || (a.CustomerId != null && t.CustomerId == a.CustomerId)))
				.OrderByDescending(t => t.CreatedAt).Take(50).Select(t => new TicketLine(t.ID, t.Subject, t.Status, t.Priority, t.CreatedAt)).ToListAsync();
			List<InvoiceLine> invoices = new(); decimal sales = 0, outstanding = 0; DateTime? first = null, last = null; string? customerName = null;
			if (a.CustomerId is int cust)
			{
				var cu = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == cid && c.ID == cust).Select(c => new { c.Name, c.NameEn }).FirstOrDefaultAsync();
				customerName = cu == null ? null : (!isAr && !string.IsNullOrWhiteSpace(cu.NameEn) ? cu.NameEn : cu.Name);
				var inv = _db.SalesInvoices.AsNoTracking().Where(s => s.CompanyID == cid && s.CustomerId == cust && s.Status == "Posted");
				var since = DateTime.Today.AddMonths(-12);
				sales = await inv.Where(s => s.InvoiceDate >= since).SumAsync(s => (decimal?)(s.GrandTotalBase ?? s.GrandTotal)) ?? 0m;
				first = await inv.MinAsync(s => (DateTime?)s.InvoiceDate);
				last = await inv.MaxAsync(s => (DateTime?)s.InvoiceDate);
				invoices = await inv.OrderByDescending(s => s.InvoiceDate).Take(30).Select(s => new InvoiceLine(s.ID, s.InvoiceNo, s.InvoiceDate, s.GrandTotal, s.Status)).ToListAsync();
				outstanding = await _ar.CustomerOutstandingAsync(cid, cust);
			}
			// health: the existing account-health rules, on this one account
			var nowUtc = DateTime.UtcNow; var recentFrom = nowUtc.Date.AddDays(-Health.RecentWindowDays); var previousFrom = recentFrom.AddDays(-Health.PreviousWindowDays);
			var oppIds = _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.AccountId == id).Select(o => (int?)o.ID);
			var acts = _db.Activities.AsNoTracking().Where(x => x.CompanyID == cid && ((x.EntityType == "Account" && x.EntityId == id) || oppIds.Contains(x.OpportunityId)));
			var closed = Health.ClosedStagesForExposure; var today = nowUtc.Date;
			var open = _db.Opportunities.AsNoTracking().Where(o => o.CompanyID == cid && o.AccountId == id && !closed.Contains(o.Stage));
			var signal = new Health.Signal
			{
				AccountId = a.ID, Name = a.Name, OwnerEmployeeId = a.OwnerEmployeeId,
				RecentActivityCount = await acts.CountAsync(x => x.CreatedAt >= recentFrom),
				PreviousActivityCount = await acts.CountAsync(x => x.CreatedAt >= previousFrom && x.CreatedAt < recentFrom),
				LastActivityAt = await acts.MaxAsync(x => x.CreatedAt),
				OpenFollowUpCount = await acts.CountAsync(x => !x.Done),
				OpenOpportunityCount = await open.CountAsync(),
				OpenOpportunityValue = await open.SumAsync(o => (decimal?)o.Amount) ?? 0m,
				PastDueOpportunityCount = await open.CountAsync(o => o.ExpectedCloseDate != null && o.ExpectedCloseDate < today),
				PastDueOpportunityValue = await open.Where(o => o.ExpectedCloseDate != null && o.ExpectedCloseDate < today).SumAsync(o => (decimal?)o.Amount) ?? 0m,
			};
			var insights = Health.Evaluate(signal, nowUtc);
			var health = Math.Max(0, 100 - insights.Sum(i => i.Severity switch { Health.Severity.High => 30, Health.Severity.Medium => 15, _ => 5 }));
			var owner = a.OwnerEmployeeId is int ow ? (await PeopleAsync(cid, new int?[] { ow })).GetValueOrDefault(ow) : null;
			var tagNames = await (from t in _db.TagAssignments.AsNoTracking().Where(t => t.CompanyID == cid && t.EntityType == Platform.EntityRegistry.CrmAccount && t.EntityId == id)
								  join g in _db.Set<CrossBuy.Models.Context.Platform.Tag>().AsNoTracking() on t.TagId equals g.ID
								  select g.Name).ToListAsync();
			return new Account360(a, customerName, contacts.Select(c => Row(c, a.Name, a.NameEn, states[c.ID])).ToList(), deals, invoices,
				tickets, sales, outstanding, first, last, tickets.Count(t => t.Status is not ("Resolved" or "Closed")), health, insights, owner, tagNames);
		}
	}
}
