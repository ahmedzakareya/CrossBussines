using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR CHECKLISTS THAT BECOME WORK (2026-11-09): END-OF-SERVICE CLEARANCE and NEW-JOINER ONBOARDING.
	//
	// One mechanism, two lists. Each item of a checklist (a rule-pack setting edited on /Admin/HrRules) becomes ONE
	// follow-up task linked to the employee when the process is started, so nobody is cleared or onboarded from memory:
	//   * clearance (end-of-service screen): custody returned, access closed, work handed over, advances settled…
	//   * onboarding (employee profile): contract signed, accounts created, equipment issued, induction…
	// Idempotent through TaskAutoLog ("<prefix>:<employee>:<item code>"): starting twice creates nothing the second time,
	// and an item added to the checklist later is created by the next start. Routed by the TaskAutoRule of the kind
	// (DefaultAssigneeEmployeeId; 0 = unassigned, for a manager to route) like the other HR follow-ups.
	// ==========================================================================================
	public sealed record ClearanceStatus(HrClearanceItem Item, int? TaskId, string? TaskStatus)
	{
		public bool Done => TaskStatus == "Done";
	}

	public abstract class HrChecklistService
	{
		public const int DueDays = 7;
		protected readonly CrossDbContext _db;
		protected HrChecklistService(CrossDbContext db) { _db = db; }

		protected abstract string RuleTypeName { get; }
		protected abstract string KeyPrefix { get; }
		protected abstract string TitleAr { get; }
		protected abstract string TitleEn { get; }
		protected abstract IReadOnlyList<HrClearanceItem> DefaultItems { get; }
		protected abstract List<HrClearanceItem> FromPack(HrRules pack);

		public string KeyOf(int employeeId, string code) => $"{KeyPrefix}:{employeeId}:{code}";

		private static string CodeOf(HrClearanceItem i) => string.IsNullOrWhiteSpace(i.Code) ? (i.NameEn ?? i.NameAr ?? "item").Trim().ToLowerInvariant().Replace(' ', '-') : i.Code.Trim();

		public async Task<IReadOnlyList<HrClearanceItem>> ChecklistAsync(int companyId, CancellationToken ct = default)
		{
			var pack = (await new HrRulePackService(_db).EffectiveAsync(companyId, ct))?.Rules;
			var own = pack == null ? null : FromPack(pack);
			return own is { Count: > 0 } ? own : DefaultItems;
		}

		public async Task<List<ClearanceStatus>> StatusAsync(int companyId, int employeeId, CancellationToken ct = default)
		{
			var items = await ChecklistAsync(companyId, ct);
			var keys = items.Select(i => KeyOf(employeeId, CodeOf(i))).ToList();
			var logs = await _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId && keys.Contains(l.RuleKey))
				.Select(l => new { l.RuleKey, l.TaskId }).ToListAsync(ct);
			var taskIds = logs.Select(l => l.TaskId).ToList();
			var tasks = await _db.TaskItems.AsNoTracking().Where(t => taskIds.Contains(t.ID)).Select(t => new { t.ID, t.Status }).ToListAsync(ct);
			return items.Select(i =>
			{
				var log = logs.FirstOrDefault(l => l.RuleKey == KeyOf(employeeId, CodeOf(i)));
				var task = log == null ? null : tasks.FirstOrDefault(t => t.ID == log.TaskId);
				return new ClearanceStatus(i, task?.ID, task?.Status);
			}).ToList();
		}

		/// Creates the tasks not created yet; returns how many it created.
		public async Task<int> StartAsync(int companyId, int employeeId, int? byEmployeeId, CancellationToken ct = default)
		{
			var emp = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId && e.EmpCompanyID == companyId)
				.Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			if (emp == null) return 0;
			var rule = await _db.TaskAutoRules.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == companyId && r.RuleType == RuleTypeName, ct);
			if (rule is { IsActive: false }) return 0;
			var status = await StatusAsync(companyId, employeeId, ct);
			int created = 0;
			foreach (var s in status.Where(x => x.TaskId == null))
			{
				var who = emp.FullName ?? emp.FullNameEn ?? ("#" + employeeId);
				var whoEn = emp.FullNameEn ?? emp.FullName ?? ("#" + employeeId);
				var task = new TaskItem
				{
					CompanyId = companyId,
					Title = $"{TitleAr}: {s.Item.NameAr} — {who}",
					TitleEn = $"{TitleEn}: {s.Item.NameEn} — {whoEn}",
					AssigneeEmployeeId = rule?.DefaultAssigneeEmployeeId ?? 0,
					CreatedByEmployeeId = byEmployeeId ?? 0,
					Priority = "High",
					DueDate = DateTime.Today.AddDays(DueDays),
					Status = "New",
					ActualHours = 0m,
					EntityType = EntityRegistry.Employee,
					EntityId = employeeId,
					CreatedAt = DateTime.UtcNow,
				};
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				_db.TaskItems.Add(task);
				await _db.SaveChangesAsync(ct);
				_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = KeyOf(employeeId, CodeOf(s.Item)), TaskId = task.ID, CreatedAt = DateTime.UtcNow });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				created++;
			}
			return created;
		}
	}

	public sealed class HrClearanceService : HrChecklistService
	{
		public const string RuleType = "HrClearance";
		public HrClearanceService(CrossDbContext db) : base(db) { }

		/// Used only when a company's pack carries no checklist of its own.
		public static readonly IReadOnlyList<HrClearanceItem> Defaults = new List<HrClearanceItem>
		{
			new() { Code = "custody", NameAr = "استلام العهد والأصول", NameEn = "Return custody and assets" },
			new() { Code = "access", NameAr = "إغلاق حسابات الأنظمة والبريد", NameEn = "Close system and email access" },
			new() { Code = "handover", NameAr = "تسليم المهام والملفات", NameEn = "Hand over work and files" },
			new() { Code = "finance", NameAr = "تسوية السلف والعهد المالية", NameEn = "Settle advances and cash custody" },
			new() { Code = "documents", NameAr = "استلام الوثائق وإلغاء الإقامة/التصاريح", NameEn = "Collect documents, cancel residence/permits" },
		};

		public static string Key(int employeeId, string code) => $"HrClearance:{employeeId}:{code}";
		protected override string RuleTypeName => RuleType;
		protected override string KeyPrefix => "HrClearance";
		protected override string TitleAr => "إخلاء طرف";
		protected override string TitleEn => "Clearance";
		protected override IReadOnlyList<HrClearanceItem> DefaultItems => Defaults;
		protected override List<HrClearanceItem> FromPack(HrRules pack) => pack.Clearance;
	}

	public sealed class HrOnboardingService : HrChecklistService
	{
		public const string RuleType = "HrOnboarding";
		public HrOnboardingService(CrossDbContext db) : base(db) { }

		public static readonly IReadOnlyList<HrClearanceItem> Defaults = new List<HrClearanceItem>
		{
			new() { Code = "contract", NameAr = "توقيع العقد ورفعه إلى مدير الملفات", NameEn = "Sign the contract and file it" },
			new() { Code = "documents", NameAr = "استلام الوثائق (البطاقة المدنية، الجواز، المؤهل)", NameEn = "Collect documents (civil ID, passport, qualification)" },
			new() { Code = "accounts", NameAr = "إنشاء حسابات الأنظمة والبريد", NameEn = "Create system and email accounts" },
			new() { Code = "equipment", NameAr = "تسليم العهد والأجهزة", NameEn = "Issue equipment and custody" },
			new() { Code = "induction", NameAr = "التعريف بالشركة واللوائح", NameEn = "Induction to the company and its policies" },
			new() { Code = "insurance", NameAr = "التسجيل في التأمينات الاجتماعية", NameEn = "Register with social insurance" },
		};

		protected override string RuleTypeName => RuleType;
		protected override string KeyPrefix => "HrOnboarding";
		protected override string TitleAr => "تهيئة موظف";
		protected override string TitleEn => "Onboarding";
		protected override IReadOnlyList<HrClearanceItem> DefaultItems => Defaults;
		protected override List<HrClearanceItem> FromPack(HrRules pack) => pack.Onboarding;
	}
}
