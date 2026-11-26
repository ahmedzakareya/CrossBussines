using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using Rules = CrossBuy.BL.CrmIdentity.CrmDuplicateRules;
using Assign = CrossBuy.BL.CrmIdentity.CrmAssignmentRules;

namespace CrossBuy.BL.CrmIdentity
{
	public sealed record WebFormRow(CrmWebForm Form, int Total, int Last30, int Pending, int Converted, DateTime? LastAt);
	public sealed record SubmissionRow(CrmWebSubmission S, string FormName, string? OwnerName, bool OwnerHasPhoto);
	public sealed record TerritoryRow(CrmTerritory T, IReadOnlyList<PersonRef> Members, int LeadsLast30);
	public sealed record SimulationResult(Assign.Decision Decision, string? OwnerName, bool OwnerHasPhoto, string? AccountName);
	public sealed record ProcessResult(int Converted, int Linked, int Failed, int Unassigned);
	public sealed record PublicFormFields(string? Name, string? Company, string? Phone, string? Email, string? Area, string? Need, string? Message, bool Consent, string? Honeypot, string? PageUrl);

	public interface ICrmCaptureService
	{
		Task<List<WebFormRow>> FormsAsync(int cid);
		Task<CrmWebForm?> FormAsync(int cid, int id);
		Task<(bool ok, string? error, int id)> SaveFormAsync(int cid, CrmWebForm dto, int? employeeId);
		Task<(List<SubmissionRow> rows, int total)> SubmissionsAsync(int cid, int? formId, string? status, string? q, int page, int pageSize);
		Task<List<TerritoryRow>> TerritoriesAsync(int cid);
		Task<(bool ok, string? error)> SaveTerritoryAsync(int cid, CrmTerritory dto, int[] memberIds);
		Task<(bool ok, string? error)> DeleteTerritoryAsync(int cid, int id);
		Task<SimulationResult> SimulateAsync(int cid, string? area, string? segment, string? source, string? phone, string? email, string? company);
		/// Turns every pending submission of ONE company into a lead, inside that company's scope. Called by the CRM worker and the staff screens.
		Task<ProcessResult> ProcessPendingAsync(int cid, int max = 50);
		// ---- the anonymous side: names only a form key, never a company ----
		Task<CrmWebForm?> PublicFormAsync(string key);
		Task<(bool ok, string code)> SubmitAsync(string key, PublicFormFields f, string? visitorKey);
	}

	/// <summary>CRM C2 — web forms, the intake, and assignment by territory.</summary>
	/// <remarks>
	/// TWO HALVES THAT NEVER MEET IN ONE REQUEST. The anonymous visitor writes ONE row to CrmWebSubmissions and nothing
	/// else: no lead, no consent, no notification. The company of that row comes from the form found by its key —
	/// the request itself names no company, so no visitor can write into a company of their choosing. Everything that
	/// touches the CRM proper (duplicate check, lead, consent, assignment, first-contact task, notifications) happens
	/// in ProcessPendingAsync, which runs in the company's own scope: the CRM worker every five minutes, or at once
	/// when a member of staff opens the forms screen.
	/// </remarks>
	public sealed class CrmCaptureService : ICrmCaptureService
	{
		private readonly CrossDbContext _db;
		private readonly ICrmService _crm;
		private readonly INotificationService _notify;
		public CrmCaptureService(CrossDbContext db, ICrmService crm, INotificationService notify) { _db = db; _crm = crm; _notify = notify; }

		public const int NameMax = 150, MessageMax = 1000;
		// a visitor may send 5 forms in 10 minutes; a form takes at most 300 an hour. In memory on purpose: a restart
		// forgetting the counts costs a few extra rows, never a lost lead.
		const int PerVisitor = 5, PerForm = 300;
		static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> Hits = new();

		static bool Allow(string key, int max, TimeSpan window)
		{
			var q = Hits.GetOrAdd(key, _ => new ConcurrentQueue<DateTime>());
			var now = DateTime.UtcNow;
			while (q.TryPeek(out var t) && now - t > window) q.TryDequeue(out _);
			if (q.Count >= max) return false;
			q.Enqueue(now);
			return true;
		}

		static string NewKey()
		{
			const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
			var bytes = RandomNumberGenerator.GetBytes(24);
			var sb = new StringBuilder(24);
			foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
			return sb.ToString();
		}

		static string? Clip(string? s, int max) { if (string.IsNullOrWhiteSpace(s)) return null; s = s.Trim(); return s.Length > max ? s[..max] : s; }

		// ---------------- forms ----------------

		public async Task<List<WebFormRow>> FormsAsync(int cid)
		{
			var forms = await _db.Set<CrmWebForm>().AsNoTracking().Where(f => f.CompanyId == cid).OrderByDescending(f => f.IsActive).ThenBy(f => f.Name).ToListAsync();
			var since = DateTime.UtcNow.AddDays(-30);
			var stats = await _db.Set<CrmWebSubmission>().AsNoTracking().Where(s => s.CompanyId == cid && s.Status != "Spam").GroupBy(s => s.FormId)
				.Select(g => new { g.Key, Total = g.Count(), Last30 = g.Count(s => s.ReceivedAt >= since), Pending = g.Count(s => s.Status == "Pending"),
					Converted = g.Count(s => s.Status == "Converted" || s.Status == "Linked"), LastAt = g.Max(s => (DateTime?)s.ReceivedAt) })
				.ToDictionaryAsync(x => x.Key);
			return forms.Select(f => stats.TryGetValue(f.ID, out var s)
				? new WebFormRow(f, s.Total, s.Last30, s.Pending, s.Converted, s.LastAt)
				: new WebFormRow(f, 0, 0, 0, 0, null)).ToList();
		}

		public Task<CrmWebForm?> FormAsync(int cid, int id) =>
			_db.Set<CrmWebForm>().AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == cid && f.ID == id);

		public async Task<(bool ok, string? error, int id)> SaveFormAsync(int cid, CrmWebForm dto, int? employeeId)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "The form needs a name", 0);
			if (string.IsNullOrWhiteSpace(dto.Title)) return (false, "The form needs a title the visitor sees", 0);
			if (string.IsNullOrWhiteSpace(dto.ConsentText)) return (false, "The consent sentence is required", 0);
			if (string.IsNullOrWhiteSpace(dto.ThankYou)) return (false, "The thank-you message is required", 0);
			if (dto.AskArea && Assign.Items(dto.AreaOptions).Count == 0) return (false, "List the areas the visitor can choose from", 0);
			CrmWebForm f;
			if (dto.ID > 0)
			{
				var existing = await _db.Set<CrmWebForm>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == dto.ID);
				if (existing == null) return (false, "Record not found", 0);
				f = existing;
			}
			else
			{
				f = new CrmWebForm { CompanyId = cid, PublicKey = NewKey(), CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.Set<CrmWebForm>().Add(f);
			}
			f.Name = dto.Name.Trim(); f.Title = dto.Title.Trim(); f.Source = string.IsNullOrWhiteSpace(dto.Source) ? "Website" : dto.Source.Trim();
			f.Segment = Clip(dto.Segment, 60); f.AskCompany = dto.AskCompany; f.AskEmail = dto.AskEmail; f.AskArea = dto.AskArea;
			f.AreaOptions = Clip(dto.AreaOptions, 400); f.NeedOptions = Clip(dto.NeedOptions, 400);
			f.ConsentText = dto.ConsentText.Trim(); f.ThankYou = dto.ThankYou.Trim(); f.AllowedSites = Clip(dto.AllowedSites, 400); f.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			return (true, null, f.ID);
		}

		public async Task<(List<SubmissionRow> rows, int total)> SubmissionsAsync(int cid, int? formId, string? status, string? q, int page, int pageSize)
		{
			page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
			var query = from s in _db.Set<CrmWebSubmission>().AsNoTracking().Where(s => s.CompanyId == cid)
						join f in _db.Set<CrmWebForm>().AsNoTracking() on s.FormId equals f.ID
						select new { s, f.Name };
			if (formId is int fid) query = query.Where(x => x.s.FormId == fid);
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.s.Status == status);
			foreach (var t in SearchTerms.Parse(q))
			{
				var term = t;
				query = query.Where(x => x.s.Name.Contains(term) || (x.s.Company != null && x.s.Company.Contains(term)) || (x.s.Phone != null && x.s.Phone.Contains(term)) || (x.s.Email != null && x.s.Email.Contains(term)));
			}
			var total = await query.CountAsync();
			var rows = await query.OrderByDescending(x => x.s.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			var owners = rows.Select(r => r.s.OwnerEmployeeId).Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && owners.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToDictionaryAsync(e => e.ID);
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			return (rows.Select(r =>
			{
				var p = r.s.OwnerEmployeeId is int o && people.TryGetValue(o, out var e) ? e : null;
				return new SubmissionRow(r.s, r.Name, p == null ? null : (!isAr && !string.IsNullOrWhiteSpace(p.FullNameEn) ? p.FullNameEn : p.FullName), p != null && !string.IsNullOrWhiteSpace(p.ProfileImage));
			}).ToList(), total);
		}

		// ---------------- territories ----------------

		async Task<List<Assign.Territory>> RuleTerritoriesAsync(int cid)
		{
			var ts = await _db.Set<CrmTerritory>().AsNoTracking().Where(t => t.CompanyId == cid).ToListAsync();
			var members = await _db.Set<CrmTerritoryMember>().AsNoTracking().Where(m => m.CompanyId == cid).ToListAsync();
			var ids = members.Select(m => m.EmployeeId).Distinct().ToList();
			// an employee who left (inactive) is never handed a lead
			var active = (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && ids.Contains(e.ID) && e.IsActive).Select(e => e.ID).ToListAsync()).ToHashSet();
			return ts.Select(t => new Assign.Territory(t.ID, t.Name, t.Areas, t.Segments, t.Sources, t.Priority, t.IsActive,
				members.Where(m => m.TerritoryId == t.ID).Select(m => new Assign.Member(m.EmployeeId, m.LastAssignedAt, active.Contains(m.EmployeeId))).ToList())).ToList();
		}

		public async Task<List<TerritoryRow>> TerritoriesAsync(int cid)
		{
			var ts = await _db.Set<CrmTerritory>().AsNoTracking().Where(t => t.CompanyId == cid).OrderBy(t => t.Priority).ThenBy(t => t.Name).ToListAsync();
			var members = await _db.Set<CrmTerritoryMember>().AsNoTracking().Where(m => m.CompanyId == cid).ToListAsync();
			var ids = members.Select(m => m.EmployeeId).Distinct().ToList();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var people = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && ids.Contains(e.ID))
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToDictionaryAsync(e => e.ID, e => new PersonRef(e.ID, (!isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage)));
			var since = DateTime.UtcNow.AddDays(-30);
			var counts = await _db.Set<CrmWebSubmission>().AsNoTracking().Where(s => s.CompanyId == cid && s.ProcessedAt >= since && s.AssignReason != null && s.AssignReason.StartsWith("T:"))
				.GroupBy(s => s.AssignReason!).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			return ts.Select(t => new TerritoryRow(t, members.Where(m => m.TerritoryId == t.ID).Select(m => people.GetValueOrDefault(m.EmployeeId)).Where(p => p != null).Cast<PersonRef>().ToList(),
				counts.GetValueOrDefault("T:" + t.ID))).ToList();
		}

		public async Task<(bool ok, string? error)> SaveTerritoryAsync(int cid, CrmTerritory dto, int[] memberIds)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "The territory needs a name");
			memberIds = (memberIds ?? Array.Empty<int>()).Distinct().ToArray();
			if (memberIds.Length == 0) return (false, "Add at least one person to the territory");
			// members must be this company's active employees
			var valid = await _db.Employee.AsNoTracking().CountAsync(e => e.EmpCompanyID == cid && e.IsActive && memberIds.Contains(e.ID));
			if (valid != memberIds.Length) return (false, "A chosen person is not an active employee of this company");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			CrmTerritory t;
			if (dto.ID > 0)
			{
				var existing = await _db.Set<CrmTerritory>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == dto.ID);
				if (existing == null) return (false, "Record not found");
				t = existing;
			}
			else { t = new CrmTerritory { CompanyId = cid, CreatedAt = DateTime.UtcNow }; _db.Set<CrmTerritory>().Add(t); }
			t.Name = dto.Name.Trim(); t.NameEn = Clip(dto.NameEn, 100);
			t.Areas = Clip(dto.Areas, 400); t.Segments = Clip(dto.Segments, 200); t.Sources = Clip(dto.Sources, 200);
			t.Priority = Math.Clamp(dto.Priority, 1, 9999); t.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			var current = await _db.Set<CrmTerritoryMember>().Where(m => m.CompanyId == cid && m.TerritoryId == t.ID).ToListAsync();
			_db.Set<CrmTerritoryMember>().RemoveRange(current.Where(m => !memberIds.Contains(m.EmployeeId)));
			foreach (var e in memberIds.Where(e => current.All(m => m.EmployeeId != e)))
				_db.Set<CrmTerritoryMember>().Add(new CrmTerritoryMember { CompanyId = cid, TerritoryId = t.ID, EmployeeId = e });
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteTerritoryAsync(int cid, int id)
		{
			var t = await _db.Set<CrmTerritory>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id);
			if (t == null) return (false, "Record not found");
			_db.Set<CrmTerritory>().Remove(t);   // members cascade
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---------------- deciding ----------------

		async Task<(int? accountId, int? accountOwner, string? accountName)> AccountForAsync(int cid, string? company, string? phone, string? email)
		{
			var probe = new Rules.Candidate(-1, company ?? "", null, phone, email);
			var accounts = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && a.IsActive)
				.Select(a => new { a.ID, a.Name, a.NameEn, a.Phone, a.Email, a.OwnerEmployeeId }).ToListAsync();
			var best = accounts.Select(a => new { a, m = Rules.Evidence(probe, new Rules.Candidate(a.ID, a.Name, a.NameEn, a.Phone, a.Email)) })
				.Where(x => x.m != null && (x.m.Score >= Rules.Threshold || Rules.SharesIdentifier(x.m)))
				.OrderByDescending(x => x.m!.Score).FirstOrDefault();
			// contacts belong to accounts too: the person's own mobile or e-mail is the strongest link of all
			if (best == null && (Rules.PhoneKey(phone) != null || Rules.EmailKey(email) != null))
			{
				var contacts = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid).Select(c => new { c.AccountId, c.Phone, c.Email }).ToListAsync();
				var hit = contacts.FirstOrDefault(c => (Rules.PhoneKey(phone) is { } p && p == Rules.PhoneKey(c.Phone)) || (Rules.EmailKey(email) is { } e && e == Rules.EmailKey(c.Email)));
				if (hit != null && accounts.FirstOrDefault(a => a.ID == hit.AccountId) is { } acc) return (acc.ID, acc.OwnerEmployeeId, acc.Name);
			}
			return best == null ? (null, null, null) : (best.a.ID, best.a.OwnerEmployeeId, best.a.Name);
		}

		public async Task<SimulationResult> SimulateAsync(int cid, string? area, string? segment, string? source, string? phone, string? email, string? company)
		{
			var (accId, accOwner, accName) = await AccountForAsync(cid, company, phone, email);
			var d = Assign.Decide(new Assign.LeadFacts(area, segment, source, accOwner, accId), await RuleTerritoriesAsync(cid));
			if (d.OwnerEmployeeId is not int o) return new SimulationResult(d, null, false, accName);
			var e = await _db.Employee.AsNoTracking().Where(x => x.EmpCompanyID == cid && x.ID == o).Select(x => new { x.FullName, x.FullNameEn, x.ProfileImage }).FirstOrDefaultAsync();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			return new SimulationResult(d, e == null ? null : (!isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName), e != null && !string.IsNullOrWhiteSpace(e.ProfileImage), accName);
		}

		// ---------------- the intake, inside the company's scope ----------------

		public async Task<ProcessResult> ProcessPendingAsync(int cid, int max = 50)
		{
			int converted = 0, linked = 0, failed = 0, unassigned = 0;
			var pending = await _db.Set<CrmWebSubmission>().Where(s => s.CompanyId == cid && s.Status == "Pending").OrderBy(s => s.ReceivedAt).Take(max).ToListAsync();
			if (pending.Count == 0) return new ProcessResult(0, 0, 0, 0);
			var forms = await _db.Set<CrmWebForm>().AsNoTracking().Where(f => f.CompanyId == cid).ToDictionaryAsync(f => f.ID);
			var managers = await _db.CrmUserRoles.AsNoTracking().Where(r => r.CompanyID == cid && r.Role == "SalesManager").Select(r => r.EmployeeId).Distinct().ToListAsync();
			foreach (var s in pending)
			{
				try
				{
					var outcome = await ProcessOneAsync(cid, s, forms[s.FormId], managers);
					if (outcome == "Linked") linked++; else converted++;
					if (s.OwnerEmployeeId == null) unassigned++;
				}
				catch (Exception ex)
				{
					_db.ChangeTracker.Clear();
					await _db.Set<CrmWebSubmission>().Where(x => x.ID == s.ID && x.CompanyId == cid).ExecuteUpdateAsync(u => u
						.SetProperty(x => x.Status, "Failed").SetProperty(x => x.ProcessedAt, DateTime.UtcNow)
						.SetProperty(x => x.Error, ex.Message.Length > 300 ? ex.Message.Substring(0, 300) : ex.Message));
					failed++;
				}
			}
			return new ProcessResult(converted, linked, failed, unassigned);
		}

		async Task<string> ProcessOneAsync(int cid, CrmWebSubmission s, CrmWebForm form, List<int> managers)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var now = DateTime.UtcNow;
			// 1. an open lead with the same mobile or e-mail is the same enquiry again: never a second lead
			var open = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Status != "Converted" && l.Status != "Lost")
				.Select(l => new { l.ID, l.Phone, l.Email, l.OwnerEmployeeId }).ToListAsync();
			var same = open.FirstOrDefault(l => (Rules.PhoneKey(s.Phone) is { } p && p == Rules.PhoneKey(l.Phone)) || (Rules.EmailKey(s.Email) is { } e && e == Rules.EmailKey(l.Email)));
			if (same != null)
			{
				_db.Activities.Add(new Activity
				{
					CompanyID = cid, Type = "Task", Subject = "طلب جديد من نموذج «" + form.Name + "»: " + (s.Need ?? s.Message ?? s.Name),
					SubjectEn = "New request from the \"" + form.Name + "\" form: " + (s.Need ?? s.Message ?? s.Name),
					DueDate = DateTime.Now.AddHours(1), LeadId = same.ID, EntityType = "Lead", EntityId = same.ID, OwnerEmployeeId = same.OwnerEmployeeId,
					Notes = s.Message, CreatedAt = now, CreatedBy = "web-form",
				});
				s.Status = "Linked"; s.LeadId = same.ID; s.OwnerEmployeeId = same.OwnerEmployeeId; s.AssignReason = "L:" + same.ID; s.ProcessedAt = now;
				await _db.SaveChangesAsync();
				await RecordConsentAsync(cid, s, form, same.ID, now);
				await tx.CommitAsync();
				if (same.OwnerEmployeeId is int o) await NotifyOwnerAsync(cid, o, same.ID, s, form, again: true);
				return "Linked";
			}

			// 2. who takes it
			var (accId, accOwner, _) = await AccountForAsync(cid, s.Company, s.Phone, s.Email);
			var decision = Assign.Decide(new Assign.LeadFacts(s.Area, form.Segment, form.Source, accOwner, accId), await RuleTerritoriesAsync(cid));

			// 3. the lead, through the CRM's own save (scoring, events, automation all run as for a typed-in lead)
			var notes = string.Join("\n", new[] {
				s.Need == null ? null : "الطلب / Need: " + s.Need, s.Area == null ? null : "المنطقة / Area: " + s.Area,
				s.Message, s.PageUrl == null ? null : "Page: " + s.PageUrl }.Where(x => !string.IsNullOrWhiteSpace(x)));
			var (ok, err) = await _crm.SaveLeadAsync(cid, new Lead
			{
				Name = s.Name, Company = s.Company, Phone = s.Phone, Email = s.Email, Source = form.Source, Segment = form.Segment,
				Status = "New", Notes = notes, OwnerEmployeeId = decision.OwnerEmployeeId,
			}, "web-form");
			if (!ok) throw new InvalidOperationException(err ?? "The lead could not be saved");
			// SaveLeadAsync does not hand back the id; the lead is the newest one carrying this submission's own identifiers
			var leadId = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Name == s.Name && l.Phone == s.Phone && l.Email == s.Email && l.CreatedBy == "web-form")
				.OrderByDescending(l => l.ID).Select(l => l.ID).FirstAsync();
			// the save falls back to its own routing when no owner is given; an unmatched lead must stay UNASSIGNED
			await _db.Leads.Where(l => l.CompanyID == cid && l.ID == leadId).ExecuteUpdateAsync(u => u
				.SetProperty(l => l.OwnerEmployeeId, decision.OwnerEmployeeId).SetProperty(l => l.AccountId, accId));

			if (decision.TerritoryId is int tid && decision.OwnerEmployeeId is int owner)
				await _db.Set<CrmTerritoryMember>().Where(m => m.CompanyId == cid && m.TerritoryId == tid && m.EmployeeId == owner)
					.ExecuteUpdateAsync(u => u.SetProperty(m => m.LastAssignedAt, now));

			// 4. the first-contact task, due within the hour; the CRM reminder worker nudges the owner at 45 minutes.
			//    DueDate is wall-clock like every typed-in activity (the screens print it as stored); ReminderAt is UTC,
			//    because CrmReminderHostedService compares it with DateTime.UtcNow.
			if (decision.OwnerEmployeeId is int ow)
				_db.Activities.Add(new Activity
				{
					CompanyID = cid, Type = "Call", Subject = "أول تواصل: " + s.Name + " (نموذج «" + form.Name + "»)", SubjectEn = "First contact: " + s.Name + " (\"" + form.Name + "\" form)",
					DueDate = DateTime.Now.AddHours(1), ReminderAt = now.AddMinutes(45), LeadId = leadId, EntityType = "Lead", EntityId = leadId,
					OwnerEmployeeId = ow, Notes = notes, CreatedAt = now, CreatedBy = "web-form",
				});
			s.Status = "Converted"; s.LeadId = leadId; s.AccountId = accId; s.OwnerEmployeeId = decision.OwnerEmployeeId; s.ProcessedAt = now;
			s.AssignReason = decision.Reason switch
			{
				Assign.Why.AccountOwner => "A:" + accId,
				Assign.Why.Territory => "T:" + decision.TerritoryId,
				Assign.Why.TerritoryHasNobodyAvailable => "U:busy",
				_ => "U:none",
			};
			await _db.SaveChangesAsync();
			await RecordConsentAsync(cid, s, form, leadId, now);
			await tx.CommitAsync();

			if (decision.OwnerEmployeeId is int owner2) await NotifyOwnerAsync(cid, owner2, leadId, s, form, again: false);
			else foreach (var m in managers)
				await _notify.NotifyAsync(m, "عميل محتمل بلا مالك: " + s.Name, "Unassigned lead: " + s.Name,
					"لم تطابقه أي منطقة بيعية — نموذج «" + form.Name + "»", "No territory matched it — \"" + form.Name + "\" form",
					NotificationTypes.CrmAssigned, leadId, url: "/Crm/Leads?q=" + Uri.EscapeDataString(s.Name), companyId: cid, dedupKey: "crm-unassigned:" + leadId);
			return "Converted";
		}

		Task NotifyOwnerAsync(int cid, int owner, int leadId, CrmWebSubmission s, CrmWebForm form, bool again) =>
			_notify.NotifyAsync(owner,
				(again ? "طلب جديد من عميل محتمل لديك: " : "عميل محتمل جديد لك: ") + s.Name,
				(again ? "New request from your lead: " : "New lead for you: ") + s.Name,
				"من نموذج «" + form.Name + "» — تواصل خلال ساعة", "From the \"" + form.Name + "\" form — reach out within the hour",
				NotificationTypes.CrmAssigned, leadId, url: "/Crm/Leads?q=" + Uri.EscapeDataString(s.Name), companyId: cid,
				dedupKey: "crm-webform:" + s.ID);

		/// The visitor ticked the box: that IS a written consent, with the exact sentence they saw frozen in the note.
		/// Unticked records nothing — "never asked" stays the truth.
		async Task RecordConsentAsync(int cid, CrmWebSubmission s, CrmWebForm form, int leadId, DateTime now)
		{
			if (!s.ConsentGiven) return;
			var note = Clip("«" + (s.ConsentText ?? form.ConsentText) + "» — " + form.Name, 300);
			var channels = new List<string>();
			if (Rules.PhoneKey(s.Phone) != null) { channels.Add("WhatsApp"); channels.Add("Sms"); }
			if (Rules.EmailKey(s.Email) != null) channels.Add("Email");
			foreach (var ch in channels)
				_db.Set<CrmConsent>().Add(new CrmConsent { CompanyId = cid, SubjectType = "Lead", SubjectId = leadId, Channel = ch, State = "OptIn", Source = "WebForm", Note = note, RecordedAt = now });
			await _db.SaveChangesAsync();
		}

		// ---------------- the anonymous side ----------------

		public Task<CrmWebForm?> PublicFormAsync(string key) =>
			string.IsNullOrWhiteSpace(key) || key.Length > 32 ? Task.FromResult<CrmWebForm?>(null)
			: _db.Set<CrmWebForm>().AsNoTracking().FirstOrDefaultAsync(f => f.PublicKey == key && f.IsActive);

		/// Validates and stores ONE row. Codes: ok | not_found | name | reach | email | phone | busy. A honeypot hit is
		/// answered "ok" and stored as Spam, so a robot learns nothing.
		public async Task<(bool ok, string code)> SubmitAsync(string key, PublicFormFields f, string? visitorKey)
		{
			var form = await PublicFormAsync(key);
			if (form == null) return (false, "not_found");
			var name = Clip(f.Name, NameMax);
			var phone = Clip(f.Phone, 40);
			var email = Clip(form.AskEmail ? f.Email : null, 150);
			if (name == null || name.Length < 2) return (false, "name");
			if (phone == null && email == null) return (false, "reach");
			if (email != null && Rules.EmailKey(email) == null) return (false, "email");
			if (phone != null && Rules.PhoneKey(phone) == null) return (false, "phone");
			var bot = !string.IsNullOrEmpty(f.Honeypot);
			if (!bot && (!Allow("v:" + key + ":" + (visitorKey ?? "-"), PerVisitor, TimeSpan.FromMinutes(10)) || !Allow("f:" + key, PerForm, TimeSpan.FromHours(1))))
				return (false, "busy");
			var areas = Assign.Items(form.AreaOptions);
			var area = form.AskArea ? Clip(f.Area, 60) : null;
			if (area != null && !areas.Contains(Rules.Fold(area))) area = null;   // only the offered areas reach routing
			var now = DateTime.UtcNow;
			_db.Set<CrmWebSubmission>().Add(new CrmWebSubmission
			{
				CompanyId = form.CompanyId, FormId = form.ID, ReceivedAt = now, Name = name, Company = form.AskCompany ? Clip(f.Company, 150) : null,
				Phone = phone, Email = email, Area = area, Need = Clip(f.Need, 100), Message = Clip(f.Message, MessageMax),
				ConsentGiven = f.Consent, ConsentText = f.Consent ? form.ConsentText : null, PageUrl = Clip(f.PageUrl, 400), VisitorKey = visitorKey,
				Status = bot ? "Spam" : "Pending", ProcessedAt = bot ? now : null,
			});
			await _db.SaveChangesAsync();
			return (true, "ok");
		}
	}
}
