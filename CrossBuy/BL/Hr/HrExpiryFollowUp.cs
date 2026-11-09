using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR2 — EXPIRING EMPLOYEE DOCUMENTS AND CONTRACTS BECOME WORK (prototype roadmap HR2, 2026-11-08).
	//
	// The same design as the document platform's DocumentExpiryProjection, applied to the HR tables it cannot see
	// (EmployeeDocuments.ExpiryDate, EmploymentContracts.EndDate):
	//   * ONE TaskItem per (instrument, expiry date), linked to the EMPLOYEE, DUE ON THE EXPIRY DATE. Nothing else is
	//     needed for escalation: on that date the task becomes DueToday, then an OverdueTask in Workspace Attention.
	//   * Idempotent through TaskAutoLog ("HrDocExpiry:<kind>:<id>:<yyyy-MM-dd>"): running twice creates nothing the
	//     second time; a renewal (a new expiry date) correctly produces new work.
	//   * Switched by the platform's own TaskAutoRule table (RuleType "HrDocumentExpiry"): absent means ON, inactive
	//     means OFF, and DefaultAssigneeEmployeeId routes the tasks (0 = unassigned, for a manager to route).
	// The task is created LeadDays before the expiry; a lookback bounds the first run so years of history are not
	// turned into a wall of tasks.
	// ==========================================================================================
	public sealed record HrExpiryFollowUpSummary(int Examined, int TasksCreated, int AlreadyTracked);

	public sealed class HrExpiryFollowUp
	{
		public const string RuleType = "HrDocumentExpiry";
		public const int LeadDays = 30;
		public const int LookbackDays = 90;
		public const int MaxPerRun = 200;

		private readonly CrossDbContext _db;
		private readonly IHrDocumentService _docs;
		public HrExpiryFollowUp(CrossDbContext db, IHrDocumentService docs) { _db = db; _docs = docs; }

		private static readonly Dictionary<string, (string Ar, string En)> Names = new(StringComparer.OrdinalIgnoreCase)
		{
			["NationalId"] = ("البطاقة المدنية", "Civil ID"), ["Passport"] = ("جواز السفر", "Passport"), ["Qualification"] = ("المؤهل", "Qualification"),
			["Insurance"] = ("التأمين", "Insurance"), ["WorkPermit"] = ("إذن العمل", "Work permit"), ["Other"] = ("وثيقة", "Document"),
			["Permanent"] = ("العقد", "Contract"), ["FixedTerm"] = ("العقد محدد المدة", "Fixed-term contract"), ["Probation"] = ("عقد التجربة", "Probation contract"), ["PartTime"] = ("عقد الدوام الجزئي", "Part-time contract"),
		};

		public static string Key(string kind, int refId, DateTime expiry) => $"HrDocExpiry:{kind}:{refId}:{expiry:yyyy-MM-dd}";

		public async Task<HrExpiryFollowUpSummary> RunAsync(int companyId, CancellationToken ct = default)
		{
			if (companyId <= 0) return new(0, 0, 0);
			var rule = await _db.TaskAutoRules.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == companyId && r.RuleType == RuleType, ct);
			if (rule is { IsActive: false }) return new(0, 0, 0);

			var today = DateTime.Today;
			var items = (await _docs.ExpiringAsync(companyId, LeadDays))
				.Where(i => i.EmployeeId > 0 && i.Date.Date >= today.AddDays(-LookbackDays))
				.OrderBy(i => i.Date).Take(MaxPerRun).ToList();
			var keys = items.Select(i => Key(i.Kind, i.RefId, i.Date.Date)).ToList();
			var already = (await _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId && keys.Contains(l.RuleKey))
				.Select(l => l.RuleKey).ToListAsync(ct)).ToHashSet();

			int created = 0;
			foreach (var i in items)
			{
				var key = Key(i.Kind, i.RefId, i.Date.Date);
				if (already.Contains(key)) continue;
				var n = Names.TryGetValue(i.Label ?? "", out var nm) ? nm : (Ar: i.Label ?? "وثيقة", En: i.Label ?? "Document");
				var who = i.EmployeeName ?? ("#" + i.EmployeeId);
				var task = new TaskItem
				{
					CompanyId = companyId,
					Title = $"تجديد {n.Ar} — {who}",
					TitleEn = $"Renew {n.En} — {who}",
					Description = null,
					AssigneeEmployeeId = rule?.DefaultAssigneeEmployeeId ?? 0,   // 0 = unassigned, for a manager to route
					CreatedByEmployeeId = 0,
					Priority = i.DaysLeft < 0 ? "High" : "Normal",
					DueDate = i.Date.Date,                                       // due on the expiry: Attention does the escalation
					Status = "New",
					ActualHours = 0m,
					EntityType = EntityRegistry.Employee,
					EntityId = i.EmployeeId,
					CreatedAt = DateTime.UtcNow,
				};
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				_db.TaskItems.Add(task);
				await _db.SaveChangesAsync(ct);
				_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = task.ID, CreatedAt = DateTime.UtcNow });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				already.Add(key);
				created++;
			}
			// Probation (2026-11-09): the end of each employee's probation becomes a review task the same way, due on that day.
			var (pExamined, pCreated) = await ProbationAsync(companyId, rule?.DefaultAssigneeEmployeeId ?? 0, ct);
			return new(items.Count + pExamined, created + pCreated, items.Count - created + pExamined - pCreated);
		}

		public static string ProbationKey(int employeeId, DateTime end) => $"HrProbation:{employeeId}:{end:yyyy-MM-dd}";

		private async Task<(int Examined, int Created)> ProbationAsync(int companyId, int assignee, CancellationToken ct)
		{
			var days = (await new HrRulePackService(_db).EffectiveAsync(companyId, ct))?.Rules.ProbationDays ?? 0;
			if (days <= 0) return (0, 0);
			var today = DateTime.Today;
			// joined recently enough that the probation ends between the lookback and the lead window
			var from = today.AddDays(-LookbackDays - days); var to = today.AddDays(LeadDays - days);
			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive && e.DateOfJoining >= from && e.DateOfJoining <= to)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.DateOfJoining }).ToListAsync(ct);
			var keys = emps.Select(e => ProbationKey(e.ID, e.DateOfJoining.Date.AddDays(days))).ToList();
			var done = (await _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId && keys.Contains(l.RuleKey)).Select(l => l.RuleKey).ToListAsync(ct)).ToHashSet();
			int created = 0;
			foreach (var e in emps)
			{
				var end = e.DateOfJoining.Date.AddDays(days);
				var key = ProbationKey(e.ID, end);
				if (done.Contains(key)) continue;
				var who = e.FullName ?? e.FullNameEn ?? ("#" + e.ID); var whoEn = e.FullNameEn ?? e.FullName ?? ("#" + e.ID);
				var task = new TaskItem
				{
					CompanyId = companyId, Title = $"تقييم فترة التجربة — {who}", TitleEn = $"Probation review — {whoEn}",
					Description = null, AssigneeEmployeeId = assignee, CreatedByEmployeeId = 0, Priority = end < today ? "High" : "Normal",
					DueDate = end, Status = "New", ActualHours = 0m, EntityType = EntityRegistry.Employee, EntityId = e.ID, CreatedAt = DateTime.UtcNow,
				};
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				_db.TaskItems.Add(task);
				await _db.SaveChangesAsync(ct);
				_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = task.ID, CreatedAt = DateTime.UtcNow });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				created++;
			}
			return (emps.Count, created);
		}
	}
}
