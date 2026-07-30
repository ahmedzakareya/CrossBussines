using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class MatchRunSummary { public int Scanned { get; set; } public int AutoLinked { get; set; } public int Suggested { get; set; } }

	// TM-9-ج: review-screen DTOs (pending multiple-match suggestions grouped per scheduled task)
	public class SuggestionCandidateDto { public int SuggestionId { get; set; } public string EntityType { get; set; } = ""; public int EntityId { get; set; } public string? Label { get; set; } }
	public class SuggestionGroupDto { public int TaskId { get; set; } public string TaskTitle { get; set; } = ""; public string ExpectedType { get; set; } = ""; public string? PartyName { get; set; } public DateTime? DueDate { get; set; } public List<SuggestionCandidateDto> Candidates { get; set; } = new(); }

	public interface ITaskScheduleMatcher
	{
		Task<MatchRunSummary> RunAsync(int companyId);
		Task<List<SuggestionGroupDto>> GetPendingAsync(int companyId);                                 // TM-9-ج: manager review list
		Task<(bool ok, string? error)> ConfirmAsync(int companyId, int taskId, int entityId);          // link the chosen movement + close the rest
		Task<(bool ok, string? error)> DismissAsync(int companyId, int suggestionId);                  // reject one candidate
	}

	// TM-9-ب: periodic matcher for SCHEDULED tasks. Finds open scheduled tasks (IsScheduled && MatchedAt==null) and matches
	// them to EXISTING movements (purchase/sales invoices) by type + party + time window.
	//   • exactly one candidate  → auto-link (fills TM-2 EntityType/EntityId + MatchedAt, task becomes a normal linked task).
	//   • more than one candidate → records suggestions (TaskMatchSuggestion) for a manager to confirm later (TM-9-ج).
	//   • dedup: a matched task drops out (IsScheduled=false); a movement already linked/suggested is never re-used.
	// Operational ONLY — it READS existing movements, NEVER creates one; no GL; no new financial writer; no invoice-code touched.
	public class TaskScheduleMatcher : ITaskScheduleMatcher
	{
		private readonly CrossDbContext _db;
		public TaskScheduleMatcher(CrossDbContext db) { _db = db; }

		public async Task<MatchRunSummary> RunAsync(int companyId)
		{
			var sum = new MatchRunSummary();
			var open = await _db.TaskItems
				.Where(t => t.CompanyId == companyId && t.IsScheduled && t.MatchedAt == null && t.ExpectedEntityType != null && t.ExpectedPartyId != null)
				.ToListAsync();
			if (open.Count == 0) return sum;

			// "taken" movements: already linked to a matched task, or already sitting as an unresolved suggestion (any task)
			var linkedPurch = await _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId && t.EntityType == "PurchaseInvoice" && t.EntityId != null).Select(t => t.EntityId!.Value).ToListAsync();
			var linkedSales = await _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId && t.EntityType == "SalesInvoice" && t.EntityId != null).Select(t => t.EntityId!.Value).ToListAsync();
			var allSugg = await _db.TaskMatchSuggestions.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => new { s.EntityType, s.EntityId, s.TaskId, s.ResolvedAt }).ToListAsync();
			var pending = allSugg.Where(s => s.ResolvedAt == null).ToList();
			var takenPurch = new HashSet<int>(linkedPurch.Concat(pending.Where(s => s.EntityType == "PurchaseInvoice").Select(s => s.EntityId)));
			var takenSales = new HashSet<int>(linkedSales.Concat(pending.Where(s => s.EntityType == "SalesInvoice").Select(s => s.EntityId)));
			var tasksWithSuggestions = new HashSet<int>(pending.Select(s => s.TaskId));
			// a movement already OFFERED to a task (even if later dismissed) is never re-offered to that same task → no dismiss loop
			var offered = new HashSet<string>(allSugg.Select(s => $"{s.TaskId}|{s.EntityType}|{s.EntityId}"));

			foreach (var t in open)
			{
				sum.Scanned++;
				DateTime lo = (t.ExpectedFrom ?? t.CreatedAt).Date;
				DateTime hi = t.ExpectedTo ?? DateTime.MaxValue;
				int party = t.ExpectedPartyId!.Value;
				var taken = t.ExpectedEntityType == "PurchaseInvoice" ? takenPurch : takenSales;

				List<(int Id, string Label)> candidates;
				if (t.ExpectedEntityType == "PurchaseInvoice")
				{
					var raw = await _db.PurchaseInvoices.AsNoTracking()
						.Where(i => i.CompanyID == companyId && i.VendorId == party && i.InvoiceDate >= lo && i.InvoiceDate <= hi)
						.OrderBy(i => i.InvoiceDate).Take(20).Select(i => new { i.ID, i.InvoiceNo }).ToListAsync();
					candidates = raw.Where(x => !taken.Contains(x.ID) && !offered.Contains($"{t.ID}|PurchaseInvoice|{x.ID}")).Select(x => (x.ID, x.InvoiceNo ?? ("#" + x.ID))).ToList();
				}
				else if (t.ExpectedEntityType == "SalesInvoice")
				{
					var raw = await _db.SalesInvoices.AsNoTracking()
						.Where(i => i.CompanyID == companyId && i.CustomerId == party && i.InvoiceDate >= lo && i.InvoiceDate <= hi)
						.OrderBy(i => i.InvoiceDate).Take(20).Select(i => new { i.ID, i.InvoiceNo }).ToListAsync();
					candidates = raw.Where(x => !taken.Contains(x.ID) && !offered.Contains($"{t.ID}|SalesInvoice|{x.ID}")).Select(x => (x.ID, x.InvoiceNo ?? ("#" + x.ID))).ToList();
				}
				else continue;

				if (candidates.Count == 0) continue;

				// single candidate + no pending suggestions on this task → auto-link (TM-2). Otherwise → suggestions.
				if (candidates.Count == 1 && !tasksWithSuggestions.Contains(t.ID))
				{
					var c = candidates[0];
					t.EntityType = t.ExpectedEntityType; t.EntityId = c.Id; t.MatchedAt = DateTime.UtcNow; t.IsScheduled = false;
					taken.Add(c.Id);
					sum.AutoLinked++;
				}
				else
				{
					foreach (var c in candidates)
					{
						_db.TaskMatchSuggestions.Add(new TaskMatchSuggestion { CompanyId = companyId, TaskId = t.ID, EntityType = t.ExpectedEntityType!, EntityId = c.Id, Label = c.Label, CreatedAt = DateTime.UtcNow });
						taken.Add(c.Id);
						sum.Suggested++;
					}
					tasksWithSuggestions.Add(t.ID);
				}
			}
			await _db.SaveChangesAsync();
			return sum;
		}

		// TM-9-ج: pending multiple-match suggestions grouped per scheduled task (for the manager review screen)
		public async Task<List<SuggestionGroupDto>> GetPendingAsync(int companyId)
		{
			var sugg = await _db.TaskMatchSuggestions.AsNoTracking().Where(s => s.CompanyId == companyId && s.ResolvedAt == null).OrderBy(s => s.TaskId).ToListAsync();
			if (sugg.Count == 0) return new();
			var taskIds = sugg.Select(s => s.TaskId).Distinct().ToList();
			var tasks = await _db.TaskItems.AsNoTracking().Where(t => taskIds.Contains(t.ID) && t.CompanyId == companyId && t.IsScheduled && t.MatchedAt == null).ToListAsync();
			var vIds = tasks.Where(t => t.ExpectedPartyType == "Supplier" && t.ExpectedPartyId != null).Select(t => t.ExpectedPartyId!.Value).Distinct().ToList();
			var cIds = tasks.Where(t => t.ExpectedPartyType == "Customer" && t.ExpectedPartyId != null).Select(t => t.ExpectedPartyId!.Value).Distinct().ToList();
			var vNames = vIds.Count == 0 ? new Dictionary<int, string>() : await _db.Vendors.AsNoTracking().Where(v => vIds.Contains(v.ID)).ToDictionaryAsync(v => v.ID, v => v.Name);
			var cNames = cIds.Count == 0 ? new Dictionary<int, string>() : await _db.Customers.AsNoTracking().Where(c => cIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.Name);
			var groups = new List<SuggestionGroupDto>();
			foreach (var t in tasks)
			{
				var cands = sugg.Where(s => s.TaskId == t.ID).Select(s => new SuggestionCandidateDto { SuggestionId = s.ID, EntityType = s.EntityType, EntityId = s.EntityId, Label = s.Label }).ToList();
				if (cands.Count == 0) continue;
				string? party = t.ExpectedPartyType == "Supplier" && t.ExpectedPartyId != null && vNames.TryGetValue(t.ExpectedPartyId.Value, out var vn) ? vn
							  : t.ExpectedPartyType == "Customer" && t.ExpectedPartyId != null && cNames.TryGetValue(t.ExpectedPartyId.Value, out var cn) ? cn : null;
				groups.Add(new SuggestionGroupDto { TaskId = t.ID, TaskTitle = t.Title, ExpectedType = t.ExpectedEntityType ?? "", PartyName = party, DueDate = t.DueDate, Candidates = cands });
			}
			return groups;
		}

		// TM-9-ج: manager confirms ONE candidate → link the task via TM-2 (EntityType/EntityId) + MatchedAt, become a normal
		// linked task, and close (resolve) all remaining suggestions for that task. Operational only — no GL, no new writer.
		public async Task<(bool ok, string? error)> ConfirmAsync(int companyId, int taskId, int entityId)
		{
			var chosen = await _db.TaskMatchSuggestions.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.TaskId == taskId && s.EntityId == entityId && s.ResolvedAt == null);
			if (chosen == null) return (false, "الاقتراح غير موجود أو تم حسمه");
			var t = await _db.TaskItems.FirstOrDefaultAsync(x => x.ID == taskId && x.CompanyId == companyId);
			if (t == null) return (false, "المهمة غير موجودة");
			if (!t.IsScheduled || t.MatchedAt != null) return (false, "المهمة لم تعد مجدولة");
			t.EntityType = chosen.EntityType; t.EntityId = chosen.EntityId; t.MatchedAt = DateTime.UtcNow; t.IsScheduled = false;
			var all = await _db.TaskMatchSuggestions.Where(s => s.CompanyId == companyId && s.TaskId == taskId && s.ResolvedAt == null).ToListAsync();
			foreach (var s in all) s.ResolvedAt = DateTime.UtcNow;   // chosen + the rest → closed
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// TM-9-ج: reject a single candidate (stays resolved so it is never re-offered to this task; task stays scheduled).
		public async Task<(bool ok, string? error)> DismissAsync(int companyId, int suggestionId)
		{
			var s = await _db.TaskMatchSuggestions.FirstOrDefaultAsync(x => x.ID == suggestionId && x.CompanyId == companyId && x.ResolvedAt == null);
			if (s == null) return (false, "الاقتراح غير موجود أو تم حسمه");
			s.ResolvedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
