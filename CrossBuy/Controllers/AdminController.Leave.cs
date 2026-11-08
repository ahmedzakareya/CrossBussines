using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR — LEAVE, in the owner-approved prototype design (design-output/hr-prototype, #/leave).
	//
	// HR's own view of leave across the company, which the system did not have: requests reached HR only one at a
	// time through the approvals inbox. Three parts, all read-only and all inside the caller's company:
	//   * the requests, with WHERE each one is (the current approver of the chain, or the decision);
	//   * the team calendar for a month (weekend and official holidays shaded — they are not counted);
	//   * the leave policies actually assigned to this company's employees, line by line.
	// Approving stays in the approvals inbox; requesting stays in the employee's own portal.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record LeaveRow(int Id, int EmployeeId, string? Name, string? TypeAr, string? TypeEn, DateTime Start, DateTime End, int Days, int Status,
			string? WaitingOnAr, string? WaitingOnEn, int Level, string? DecidedByAr, string? DecidedByEn, DateTime? DecisionAt, long? CertificateDocId = null);
		public sealed record LeavePolicyLine(string? TypeAr, string? TypeEn, int Days, int CarryOver, int Notice, bool Medical, decimal? LawDays = null, string? LawReference = null);
		public sealed record LeavePolicyBlock(int Id, string? NameAr, string? NameEn, int Employees, IReadOnlyList<LeavePolicyLine> Lines);
		public sealed record LeavePageModel(IReadOnlyList<LeaveRow> Requests, DateTime Month, IReadOnlyList<DateTime> Holidays,
			IReadOnlyList<LeavePolicyBlock> Policies, Dictionary<int, string> Photos, int Pending, int OnLeaveToday, int ApprovedThisYear, int DaysThisYear);

		[HttpGet]
		public async Task<IActionResult> Leave(string? month = null, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
			if (!gate.Ok) return HrDenied(nameof(Index));
			int company = gate.CompanyId;
			var today = DateTime.Today;
			var m = DateTime.TryParseExact(month ?? "", "yyyy-MM", null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : new DateTime(today.Year, today.Month, 1);
			var monthEnd = m.AddMonths(1).AddDays(-1);

			var rows = await LoadLeaveRowsAsync(company, m, monthEnd, ct);

			// Official holidays in the shown month (a recurring one is placed in that year).
			var holidays = new List<DateTime>();
			try
			{
				foreach (var h in await holidayService.GetAllAsync(company))
				{
					var d = h.HolidayDate.Date;
					if (h.IsRecurring) { try { d = new DateTime(m.Year, d.Month, d.Day); } catch { continue; } }
					if (d >= m && d <= monthEnd) holidays.Add(d);
				}
			}
			catch { /* holidays are an overlay; the page stands without them */ }

			// Policies assigned to this company's employees (the policy tables carry no company of their own yet).
			var assigned = await Db.PolicyAssignments.AsNoTracking()
				.Where(a => Db.Employee.Any(e => e.ID == a.EmployeeID && e.EmpCompanyID == company))
				.GroupBy(a => a.LeavePolicyTypeID).Select(g => new { PolicyId = g.Key, Count = g.Count() }).ToListAsync(ct);
			var ids = assigned.Select(a => a.PolicyId).ToList();
			var policies = await Db.Policies.AsNoTracking().Where(p => ids.Contains(p.ID)).Select(p => new { p.ID, p.NameAr, p.NameEn }).ToListAsync(ct);
			var lines = await Db.LeavePolicies.AsNoTracking().Where(l => ids.Contains(l.LeavePolicyTypeID))
				.Select(l => new { l.LeavePolicyTypeID, TAr = l.leaveTypes!.NameAr, TEn = l.leaveTypes.NameEn, l.EntitlementDaysPerYear, l.CarryOverLimit, l.NoticePeriodDays, l.RequiresMedicalCertificate })
				.ToListAsync(ct);
			// COMPLIANCE AGAINST THE COUNTRY RULE PACK (2026-11-08): each policy line is matched to the pack's leave type
			// (by code and its synonyms in the type's English name), so a line BELOW the statutory minimum shows.
			var lawPack = (await new CrossBuy.BL.Hr.HrRulePackService(Db).EffectiveAsync(company, ct))?.Rules;
			CrossBuy.BL.Hr.HrLeaveRule? Law(string? nameEn, string? nameAr) => CrossBuy.BL.Hr.HrLeaveMatch.For(lawPack, nameEn, nameAr);
			var blocks = policies.Select(p => new LeavePolicyBlock(p.ID, p.NameAr, p.NameEn, assigned.First(a => a.PolicyId == p.ID).Count,
				lines.Where(l => l.LeavePolicyTypeID == p.ID).Select(l => { var law = Law(l.TEn, l.TAr); return new LeavePolicyLine(l.TAr, l.TEn, l.EntitlementDaysPerYear, l.CarryOverLimit, l.NoticePeriodDays, l.RequiresMedicalCertificate == true, law?.Days, law?.Reference); }).ToList()))
				.OrderByDescending(b => b.Employees).ToList();

			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, rows.Select(r => r.EmployeeId).Distinct(), ct);

			int year = today.Year;
			var model = new LeavePageModel(rows, m, holidays, blocks, photos,
				Pending: rows.Count(r => r.Status == 0),
				OnLeaveToday: rows.Count(r => r.Status == 1 && r.Start.Date <= today && r.End.Date >= today),
				ApprovedThisYear: await Db.LeaveRequests.AsNoTracking().CountAsync(r => r.Employee != null && r.Employee.EmpCompanyID == company && r.Status == 1 && r.StartDate.Year == year, ct),
				DaysThisYear: await Db.LeaveRequests.AsNoTracking().Where(r => r.Employee != null && r.Employee.EmpCompanyID == company && r.Status == 1 && r.StartDate.Year == year).SumAsync(r => (int?)r.Days, ct) ?? 0);
			// The weekend shaded on the calendar is the company's own, from its country rule pack (Fri/Sat when there is none).
			var pack = await new CrossBuy.BL.Hr.HrRulePackService(Db).EffectiveAsync(company, ct);
			ViewBag.Weekend = (pack?.Rules.Weekend ?? new List<string> { "Friday", "Saturday" })
				.Select(d => Enum.TryParse<DayOfWeek>(d, out var w) ? (DayOfWeek?)w : null).Where(w => w.HasValue).Select(w => w!.Value).ToHashSet();
			return View("Leave", model);
		}

		// The company's leave requests as screen rows: everything pending, plus the last 120 days and anything touching
		// [from, to]. ONE query for the page (stats, calendar) and the paged list (LeaveData), so they cannot disagree.
		private async Task<List<LeaveRow>> LoadLeaveRowsAsync(int company, DateTime m, DateTime monthEnd, CancellationToken ct)
		{
			var today = DateTime.Today;
			// Requests in the company: everything pending, plus the last 120 days and anything touching the shown month.
			var since = today.AddDays(-120);
			var raw = await Db.LeaveRequests.AsNoTracking()
				.Where(r => r.Employee != null && r.Employee.EmpCompanyID == company
					&& (r.Status == 0 || r.StartDate >= since || (r.StartDate <= monthEnd && r.EndDate >= m)))
				.OrderByDescending(r => r.Status == 0).ThenByDescending(r => r.StartDate)
				.Select(r => new { r.ID, r.EmployeeID, r.Employee!.FullName, r.Employee.FullNameEn, TAr = r.LeaveType!.NameAr, TEn = r.LeaveType.NameEn,
					r.StartDate, r.EndDate, r.Days, r.Status, r.CurrentApproverEmployeeID, r.CurrentLevel, r.ApproverEmployeeID, r.DecisionAt })
				.Take(300).ToListAsync(ct);

			// The people named in "where it is", resolved in the same company only.
			var who = raw.Select(r => r.CurrentApproverEmployeeID).Concat(raw.Select(r => r.ApproverEmployeeID)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
			var names = await Db.Employee.AsNoTracking().Where(e => who.Contains(e.ID) && e.EmpCompanyID == company)
				.ToDictionaryAsync(e => e.ID, e => (ar: e.FullName, en: e.FullNameEn), ct);
			string? Ar(int? id) => id is int i && names.TryGetValue(i, out var n) ? (n.ar ?? n.en) : null;
			string? En(int? id) => id is int i && names.TryGetValue(i, out var n) ? (n.en ?? n.ar) : null;

			// HR3: the medical certificate attached to each request (File Manager, on the request)
			var certs = await Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<CrossBuy.BL.Hr.HrFileStore>(HttpContext.RequestServices)
				.LeaveCertificatesAsync(company, raw.Select(r => r.ID), ct);

			return raw.Select(r => new LeaveRow(r.ID, r.EmployeeID, CrossBuy.BL.EmployeeNames.Of(r.FullName, r.FullNameEn), r.TAr, r.TEn, r.StartDate, r.EndDate, r.Days, r.Status,
				Ar(r.CurrentApproverEmployeeID), En(r.CurrentApproverEmployeeID), r.CurrentLevel, Ar(r.ApproverEmployeeID), En(r.ApproverEmployeeID), r.DecisionAt,
				certs.TryGetValue(r.ID, out var cd) ? cd : null)).ToList();

		}
	}
}
