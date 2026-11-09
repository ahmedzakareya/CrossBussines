using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// APPROVAL SLA AND ESCALATION (prototype #/workflow: "2-day SLA, escalate to the manager's manager", 2026-11-09).
	//
	// A path may carry SlaDays. A request whose CURRENT step has waited longer than that (since the request was raised,
	// or since the previous level decided) is escalated ONCE per step: the step moves to the waiting approver's own
	// manager by the same rule a leaver's steps move by (HrApprovalHandover.RouteLeave/RouteRequest), and both people
	// are told. Each escalation is recorded in TaskAutoLog ("HrApprovalSla:L|R:<request>:<step>:<approver>", TaskId 0: a
	// marker, no task), so a re-run never escalates the same approver's step twice, and the next approver's clock starts
	// at that escalation — they get the full limit, then the request climbs again if they too let it wait.
	// A step that cannot move (nobody above, the requester, someone who already decided) is still marked and its approver
	// reminded, so it is not re-examined every hour.
	// ==========================================================================================
	public sealed record ApprovalEscalationSummary(int Examined, int Escalated, int Reminded);

	public sealed class HrApprovalEscalation
	{
		public const int MaxPerRun = 200;
		private readonly CrossDbContext _db;
		private readonly ILeaveWorkflowService _workflow;
		private readonly INotificationService _notifications;
		public HrApprovalEscalation(CrossDbContext db, ILeaveWorkflowService workflow, INotificationService notifications)
		{ _db = db; _workflow = workflow; _notifications = notifications; }

		// One marker per (request, step, approver): a step reassigned to a new approver is a new key, so they get their own
		// full limit — counted from the last escalation of the request, not from the day it was raised.
		public static string LeaveKey(int reqId, int stepId, int approverId) => $"HrApprovalSla:L:{reqId}:{stepId}:{approverId}";
		public static string RequestKey(int reqId, int stepId, int approverId) => $"HrApprovalSla:R:{reqId}:{stepId}:{approverId}";

		private async Task<DateTime?> LastEscalationAsync(int companyId, string prefix, CancellationToken ct) =>
			await _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId && l.RuleKey.StartsWith(prefix))
				.OrderByDescending(l => l.CreatedAt).Select(l => (DateTime?)l.CreatedAt).FirstOrDefaultAsync(ct);

		public async Task<ApprovalEscalationSummary> SweepAsync(int companyId, DateTime nowUtc, CancellationToken ct = default)
		{
			var paths = await _db.HrApprovalPaths.AsNoTracking().Where(p => p.CompanyID == companyId && p.IsActive && p.SlaDays > 0).ToListAsync(ct);
			if (paths.Count == 0) return new(0, 0, 0);
			var svc = new HrApprovalPathService(_db);
			int examined = 0, escalated = 0, reminded = 0;
			var companyEmployees = _db.Employee.Where(e => e.EmpCompanyID == companyId).Select(e => e.ID);

			// ---- leave ----
			if (paths.Any(p => p.Kind == HrApprovalKinds.Leave))
			{
				var leaves = await _db.LeaveRequests.Include(r => r.ApprovalSteps)
					.Where(r => r.Status == 0 && r.CurrentLevel > 0 && companyEmployees.Contains(r.EmployeeID)).OrderBy(r => r.ID).Take(MaxPerRun).ToListAsync(ct);
				foreach (var req in leaves)
				{
					var path = await svc.ForAsync(companyId, HrApprovalKinds.Leave, req.LeaveTypeID, ct);
					var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
					if (path is not { SlaDays: > 0 } || step == null) continue;
					var since = req.CurrentLevel <= 1 ? req.CreatedAt : req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel - 1)?.DecisionAt ?? req.CreatedAt;
					if (await LastEscalationAsync(companyId, $"HrApprovalSla:L:{req.ID}:", ct) is DateTime last && (since == null || last > since)) since = last;
					if (since is not DateTime t || nowUtc - t < TimeSpan.FromDays(path.SlaDays)) continue;
					var key = LeaveKey(req.ID, step.ID, step.ApproverEmployeeID);
					if (await _db.TaskAutoLogs.AnyAsync(l => l.CompanyId == companyId && l.RuleKey == key, ct)) continue;
					examined++;
					var waiting = step.ApproverEmployeeID;
					var route = HrApprovalHandover.RouteLeave(_db, req, step, await HrApprovalHandover.NextAboveAsync(_workflow, waiting));
					_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = 0, CreatedAt = nowUtc });
					await _db.SaveChangesAsync(ct);
					var who = await NameAsync(req.EmployeeID, ct);
					if (route != ApprovalRoute.Unrouted && req.CurrentApproverEmployeeID is int now && now != waiting)
					{
						escalated++;
						await _notifications.NotifyAsync(now, "طلب إجازة متأخر صُعِّد إليك", "An overdue leave request was escalated to you",
							$"طلب إجازة {who.Ar} تجاوز مهلة {path.SlaDays} يوم دون قرار، فصُعِّد إليك.",
							$"{who.En}'s leave request passed its {path.SlaDays}-day limit without a decision and was escalated to you.", "leave_submitted", req.ID);
						await _notifications.NotifyAsync(waiting, "صُعِّد طلب إجازة لتأخره", "A leave request was escalated for delay",
							$"طلب إجازة {who.Ar} تجاوز مهلة {path.SlaDays} يوم، فانتقل إلى المستوى الأعلى.",
							$"{who.En}'s leave request passed its {path.SlaDays}-day limit and moved up a level.", "leave_advanced", req.ID);
					}
					else
					{
						reminded++;
						await _notifications.NotifyAsync(waiting, "طلب إجازة متأخر بانتظارك", "A leave request is overdue",
							$"طلب إجازة {who.Ar} تجاوز مهلة {path.SlaDays} يوم وما زال بانتظار قرارك.",
							$"{who.En}'s leave request passed its {path.SlaDays}-day limit and still awaits your decision.", "leave_submitted", req.ID);
					}
				}
			}

			// ---- employee requests (letter, permission, advance, profile change) ----
			var kinds = paths.Where(p => p.Kind != HrApprovalKinds.Leave).Select(p => p.Kind).Distinct().ToList();
			if (kinds.Count > 0)
			{
				var reqs = await _db.EmployeeRequests.Include(r => r.ApprovalSteps)
					.Where(r => r.Status == 0 && r.CurrentLevel > 0 && kinds.Contains(r.RequestType) && companyEmployees.Contains(r.EmployeeID)).OrderBy(r => r.ID).Take(MaxPerRun).ToListAsync(ct);
				foreach (var req in reqs)
				{
					var path = await svc.ForAsync(companyId, req.RequestType, null, ct);
					var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
					if (path is not { SlaDays: > 0 } || step == null) continue;
					var since = req.CurrentLevel <= 1 ? req.CreatedAt : req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel - 1)?.DecisionAt ?? req.CreatedAt;
					if (await LastEscalationAsync(companyId, $"HrApprovalSla:R:{req.ID}:", ct) is DateTime last && (since == null || last > since)) since = last;
					if (since is not DateTime t || nowUtc - t < TimeSpan.FromDays(path.SlaDays)) continue;
					var key = RequestKey(req.ID, step.ID, step.ApproverEmployeeID);
					if (await _db.TaskAutoLogs.AnyAsync(l => l.CompanyId == companyId && l.RuleKey == key, ct)) continue;
					examined++;
					var waiting = step.ApproverEmployeeID;
					var route = HrApprovalHandover.RouteRequest(_db, req, step, await HrApprovalHandover.NextAboveAsync(_workflow, waiting));
					_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = 0, CreatedAt = nowUtc });
					await _db.SaveChangesAsync(ct);
					var who = await NameAsync(req.EmployeeID, ct);
					var kind = HrApprovalKinds.Name(req.RequestType);
					if (route != ApprovalRoute.Unrouted && req.CurrentApproverEmployeeID is int now && now != waiting)
					{
						escalated++;
						await _notifications.NotifyAsync(now, $"طلب {kind.Ar} متأخر صُعِّد إليك", $"An overdue {kind.En.ToLowerInvariant()} request was escalated to you",
							$"طلب {kind.Ar} من {who.Ar} تجاوز مهلة {path.SlaDays} يوم دون قرار، فصُعِّد إليك.",
							$"{who.En}'s {kind.En.ToLowerInvariant()} request passed its {path.SlaDays}-day limit and was escalated to you.", "emp_request_submitted", req.ID);
					}
					else
					{
						reminded++;
						await _notifications.NotifyAsync(waiting, $"طلب {kind.Ar} متأخر بانتظارك", $"A {kind.En.ToLowerInvariant()} request is overdue",
							$"طلب {kind.Ar} من {who.Ar} تجاوز مهلة {path.SlaDays} يوم وما زال بانتظار قرارك.",
							$"{who.En}'s {kind.En.ToLowerInvariant()} request passed its {path.SlaDays}-day limit and still awaits your decision.", "emp_request_submitted", req.ID);
					}
				}
			}
			return new(examined, escalated, reminded);
		}

		private async Task<(string Ar, string En)> NameAsync(int employeeId, CancellationToken ct)
		{
			var e = await _db.Employee.AsNoTracking().Where(x => x.ID == employeeId).Select(x => new { x.FullName, x.FullNameEn }).FirstOrDefaultAsync(ct);
			return (e?.FullName ?? "موظف", e?.FullNameEn ?? e?.FullName ?? "An employee");
		}
	}

	// The hourly half: owns no rule, only WHEN and WHICH companies, through the platform's worker pieces (the same shape as
	// TaskEscalationHostedService: certification suppression, startup delay, the worker gate, D-16's per-tick lease
	// re-check that PARKS rather than returns, and one company's failure contained by WorkerCompanyRunner).
	public sealed class HrApprovalEscalationHostedService : BackgroundService
	{
		private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(6);
		private static readonly TimeSpan Period = TimeSpan.FromHours(1);
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly ILogger<HrApprovalEscalationHostedService> _log;

		public HrApprovalEscalationHostedService(IServiceScopeFactory scopes, IWorkerGate gate, CertificationRuntimeState certification,
			ILogger<HrApprovalEscalationHostedService> log)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

		public bool Suppressed => _certification.BackgroundWritersSuppressed;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (_certification.BackgroundWritersSuppressed)
			{
				_log.LogInformation("HR approval escalation is suppressed: this host is a certification runtime.");
				return;
			}
			try { await Task.Delay(StartupDelay, stoppingToken); } catch (TaskCanceledException) { return; }
			if (!await _gate.WaitUntilAllowedAsync(nameof(HrApprovalEscalationHostedService), stoppingToken)) return;

			using var timer = new PeriodicTimer(Period);
			do
			{
				// D-16: the lease can be lost mid-life. PARK, never return — only cancellation ends the loop.
				if (!await _gate.IsStillAllowedAsync(nameof(HrApprovalEscalationHostedService), stoppingToken))
				{
					if (!await _gate.WaitUntilAllowedAsync(nameof(HrApprovalEscalationHostedService), stoppingToken)) return;
				}
				try
				{
					using var root = _scopes.CreateScope();
					var companyScope = root.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();
					await WorkerCompanyRunner.ForEachCompanyAsync(companyScope, _log, nameof(HrApprovalEscalationHostedService),
						async companyId =>
						{
							using var scope = WorkerScope.ForCompany(_scopes, companyId);
							var sweep = ActivatorUtilities.CreateInstance<HrApprovalEscalation>(scope.ServiceProvider);
							var r = await sweep.SweepAsync(companyId, DateTime.UtcNow, stoppingToken);
							if (r.Escalated > 0 || r.Reminded > 0)
								_log.LogInformation("HR approval escalation, company {CompanyId}: {Escalated} escalated, {Reminded} reminded of {Examined} overdue",
									companyId, r.Escalated, r.Reminded, r.Examined);
						},
						stoppingToken);
				}
				catch (OperationCanceledException) { return; }
				catch (Exception ex) { _log.LogError(ex, "HR approval escalation tick failed; the next tick will retry."); }
			}
			while (await SafeWaitAsync(timer, stoppingToken));
		}

		private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
		{
			try { return await timer.WaitForNextTickAsync(ct); }
			catch (OperationCanceledException) { return false; }
		}
	}
}
