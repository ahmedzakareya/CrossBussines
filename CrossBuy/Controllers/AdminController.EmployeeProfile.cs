using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR — THE EMPLOYEE FILE, in the owner-approved prototype design (design-output/hr-prototype, #/employee/:id).
	//
	// A read-only profile: banner with the photo, then tabs for personal data, documents & expiry, leave, pay and
	// history. Editing stays on the existing EmployeeData wizard, which the banner links to, so no edit path is
	// lost. Everything is read inside the caller's company: the employee row's own company decides, and a foreign
	// id answers exactly like a missing one.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record ProfilePerson(int Id, string Name, string? NameEn, string? Photo, string? Title, string? TitleEn);
		public sealed record ProfileDoc(string Kind, string Type, string? Number, DateTime? Start, DateTime? Expiry, string? Status, bool HasFile);
		public sealed record ProfilePay(int Year, int Month, decimal Gross, decimal Deductions, decimal Net);
		public sealed record ProfileHistoryRow(string Field, string Old, string New, DateTime EffectiveFrom, string? By);

		public sealed record EmployeeProfileModel(
			int Id, string Name, string? NameEn, string? Photo, string? Title, string? TitleEn,
			string? Branch, string? BranchEn, string? Company, string? CompanyEn, string? Nationality, string? NationalityEn,
			string? Email, string? Phone, string? Address, string? Gender, string? MaritalStatus, string? EmploymentType,
			DateTime? DateOfBirth, DateTime? DateOfJoining, bool Active,
			ProfilePerson? Manager, IReadOnlyList<ProfileDoc> Documents, IReadOnlyList<CrossBuy.BL.LeaveBalanceRow> Balances,
			string? PolicyName, string? PolicyNameEn, IReadOnlyList<ProfilePay> Payslips, bool CanSeePay, bool CanEdit);

		[HttpGet]
		public async Task<IActionResult> EmployeeProfile(int id, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeView);
			if (!gate.Ok) return HrDenied(nameof(EmployeesList));

			var e = await Db.Employee.AsNoTracking()
				.Where(x => x.ID == id && x.EmpCompanyID == gate.CompanyId)
				.Select(x => new
				{
					x.ID, x.FullName, x.FullNameEn, x.ProfileImage, x.Email, x.PhoneNumber, x.Address, x.Gender, x.MaritalStatus,
					x.EmploymentType, x.DateOfBirth, x.DateOfJoining, x.IsActive,
					TitleAr = x.JobTitle.TitleAr, TitleEn = x.JobTitle.Title,
					BranchAr = x.Branch.NameAr, BranchEn = x.Branch.Name,
					CompanyAr = x.Company.ComoanyNameAr, CompanyEn = x.Company.CompanyName,
					// CountriesLookup's nationality columns are SWAPPED: NationalityName holds English, NationalityNameEn Arabic.
					NatAr = x.Country.NationalityNameEn, NatEn = x.Country.NationalityName
				})
				.FirstOrDefaultAsync(ct);
			if (e == null) return NotFound();

			var ctx = await BusinessContexts.TryGetCurrentAsync();

			// The direct manager from the org tree, company-intersected by OrgHierarchy itself.
			ProfilePerson? manager = null;
			var org = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IOrgHierarchy)) as CrossBuy.BL.Platform.IOrgHierarchy;
			var mgrId = org == null ? null : await org.DirectManagerAsync(gate.CompanyId, e.ID, ct);
			if (mgrId is int mid)
			{
				var m = await Db.Employee.AsNoTracking().Where(x => x.ID == mid && x.EmpCompanyID == gate.CompanyId)
					.Select(x => new { x.ID, x.FullName, x.FullNameEn, x.ProfileImage, T = x.JobTitle.TitleAr, TE = x.JobTitle.Title }).FirstOrDefaultAsync(ct);
				if (m != null) manager = new ProfilePerson(m.ID, m.FullName, m.FullNameEn, CrossBuy.BL.Platform.EmployeePhotos.Web(m.ProfileImage), m.T, m.TE);
			}

			// Contracts and documents, both company-bounded.
			var docs = new List<ProfileDoc>();
			var contracts = await Db.EmploymentContracts.AsNoTracking().Where(c => c.EmployeeID == e.ID && c.CompanyID == gate.CompanyId).OrderByDescending(c => c.StartDate).ToListAsync(ct);
			docs.AddRange(contracts.Select(c => new ProfileDoc("contract", c.ContractType, null, c.StartDate, c.EndDate, c.Status, !string.IsNullOrEmpty(c.FilePath))));
			var documents = await Db.EmployeeDocuments.AsNoTracking().Where(d => d.EmployeeID == e.ID && d.CompanyID == gate.CompanyId).OrderBy(d => d.ExpiryDate).ToListAsync(ct);
			docs.AddRange(documents.Select(d => new ProfileDoc("document", d.DocType, d.DocNumber, d.IssueDate, d.ExpiryDate, null, !string.IsNullOrEmpty(d.FilePath))));

			// Leave balances from the same service the employee's own dashboard reads.
			var leave = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.ILeaveDashboardService)) as CrossBuy.BL.ILeaveDashboardService;
			var dash = leave == null ? null : await leave.BuildAsync(e.ID);

			// Pay is payroll data: shown only to payroll-view.
			bool canSeePay = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.PayrollView, null);
			var pays = !canSeePay ? new List<ProfilePay>() : (await Db.Payslips.AsNoTracking()
				.Where(p => p.EmployeeID == e.ID && p.CompanyID == gate.CompanyId)
				.OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).Take(6).ToListAsync(ct))
				.Select(p => new ProfilePay(p.Year, p.Month, p.GrossEarnings, p.GrossEarnings - p.Net, p.Net)).ToList();

			bool canEdit = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.EmployeeManage, null);

			// HR4 — the salary in force today (own or the policy's), its effective-dated history, and who may change it.
			if (canSeePay)
			{
				var salaries = new CrossBuy.BL.Hr.HrSalaryService(Db);
				ViewBag.Salary = await salaries.ForAsync(e.ID, DateTime.Today, ct);
				ViewBag.SalaryHistory = await salaries.HistoryAsync(gate.CompanyId, e.ID, ct);
				ViewBag.CanSetSalary = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.PayrollManage, null);
				ViewBag.Currency = (await new CrossBuy.BL.Hr.HrRulePackService(Db).EffectiveAsync(gate.CompanyId, ct))?.Rules.Currency ?? "";
			}

			// HR1 — identity, bank, emergency contact, dependants and the effective-dated history (all company-scoped).
			ViewBag.Record2 = await Db.Employee.AsNoTracking().Where(x => x.ID == e.ID && x.EmpCompanyID == gate.CompanyId)
				.Select(x => new CrossBuy.Models.Context.Admin.Employee { CivilIdNumber = x.CivilIdNumber, PassportNumber = x.PassportNumber, Religion = x.Religion,
					Iban = x.Iban, BankName = x.BankName, EmergencyContactName = x.EmergencyContactName, EmergencyContactPhone = x.EmergencyContactPhone,
					EmergencyContactRelation = x.EmergencyContactRelation }).FirstOrDefaultAsync(ct);
			ViewBag.Dependants = await Db.EmployeeDependants.AsNoTracking().Where(d => d.EmployeeID == e.ID && d.CompanyID == gate.CompanyId)
				.OrderBy(d => d.DateOfBirth).ToListAsync(ct);
			var hist = await Db.EmployeeHistory.AsNoTracking().Where(h => h.EmployeeID == e.ID && h.CompanyID == gate.CompanyId)
				.OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.ID).Take(50).ToListAsync(ct);
			// ids → names, so the history reads "Accountant → Manager", not "3 → 7"
			var ids = hist.SelectMany(h => new[] { h.OldValue, h.NewValue }).Where(v => int.TryParse(v, out _)).Select(int.Parse).Distinct().ToList();
			var titles = await Db.JobTitles.AsNoTracking().Where(t => ids.Contains(t.ID)).ToDictionaryAsync(t => t.ID, t => (t.TitleAr, t.Title), ct);
			var branches = await Db.Branches.AsNoTracking().Where(b => ids.Contains(b.ID)).ToDictionaryAsync(b => b.ID, b => (b.NameAr, b.Name), ct);
			var units = await Db.Hierarchicals.AsNoTracking().Where(h => ids.Contains(h.H_ID)).ToDictionaryAsync(h => h.H_ID, h => (h.H_Name, h.H_NameEn), ct);
			var who = hist.Where(h => h.ChangedByEmployeeId.HasValue).Select(h => h.ChangedByEmployeeId!.Value).Distinct().ToList();
			var whoNames = await Db.Employee.AsNoTracking().Where(x => who.Contains(x.ID) && x.EmpCompanyID == gate.CompanyId)
				.ToDictionaryAsync(x => x.ID, x => CrossBuy.BL.EmployeeNames.Of(x.FullName, x.FullNameEn), ct);
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string Name(string field, string? v)
			{
				if (v == null || !int.TryParse(v, out var id)) return v ?? "—";
				(string? a, string? en) n = field switch
				{
					"JobTitle" when titles.TryGetValue(id, out var t) => t,
					"Branch" when branches.TryGetValue(id, out var b) => b,
					"Department" when units.TryGetValue(id, out var u) => u,
					_ => (null, null),
				};
				return (ar ? (n.a ?? n.en) : (n.en ?? n.a)) ?? v;
			}
			// Onboarding (2026-11-09): each checklist item's follow-up task on this employee.
			ViewBag.Onboarding = await new CrossBuy.BL.Hr.HrOnboardingService(Db).StatusAsync(gate.CompanyId, e.ID, ct);
			ViewBag.History = hist.Select(h => new ProfileHistoryRow(h.Field, Name(h.Field, h.OldValue), Name(h.Field, h.NewValue), h.EffectiveFrom,
				h.ChangedByEmployeeId is int b && whoNames.TryGetValue(b, out var bn) ? bn : null)).ToList();

			return View("EmployeeProfile", new EmployeeProfileModel(
				e.ID, e.FullName, e.FullNameEn, CrossBuy.BL.Platform.EmployeePhotos.Web(e.ProfileImage), e.TitleAr, e.TitleEn,
				e.BranchAr, e.BranchEn, e.CompanyAr, e.CompanyEn, e.NatAr, e.NatEn,
				e.Email, e.PhoneNumber, e.Address, e.Gender, e.MaritalStatus, e.EmploymentType,
				e.DateOfBirth == default ? null : e.DateOfBirth, e.DateOfJoining == default ? null : e.DateOfJoining, e.IsActive,
				manager, docs, dash?.Balances ?? new List<CrossBuy.BL.LeaveBalanceRow>(), dash?.PolicyNameAr, dash?.PolicyNameEn,
				pays, canSeePay, canEdit));
		}

		// HR1 — dependants: added and removed from the employee file (employee-manage on the subject).
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> AddDependant(int employeeId, string fullName, string? fullNameEn, string relation, DateTime? dateOfBirth, string? civilIdNumber, bool isSponsored = false)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(EmployeeProfile), new { id = employeeId });
			bool mine = await Db.Employee.AsNoTracking().AnyAsync(x => x.ID == employeeId && x.EmpCompanyID == gate.CompanyId);
			var allowed = new[] { "Spouse", "Son", "Daughter", "Father", "Mother", "Other" };
			if (!mine || string.IsNullOrWhiteSpace(fullName)) { TempData["HrErr"] = L["The dependant could not be added"].Value; return RedirectToAction(nameof(EmployeeProfile), new { id = employeeId }); }
			Db.EmployeeDependants.Add(new CrossBuy.Models.Context.Admin.EmployeeDependant { CompanyID = gate.CompanyId, EmployeeID = employeeId,
				FullName = fullName.Trim(), FullNameEn = string.IsNullOrWhiteSpace(fullNameEn) ? null : fullNameEn.Trim(),
				Relation = allowed.Contains(relation) ? relation : "Other", DateOfBirth = dateOfBirth,
				CivilIdNumber = string.IsNullOrWhiteSpace(civilIdNumber) ? null : civilIdNumber.Trim(), IsSponsored = isSponsored, CreatedAt = DateTime.UtcNow });
			await Db.SaveChangesAsync();
			return RedirectToAction(nameof(EmployeeProfile), new { id = employeeId });
		}

		// HR4 — set an employee's pay from a date (a raise is a new row; the same date corrects that row). Payroll-manage.
		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SetSalary(int employeeId, DateTime effectiveFrom, decimal basicSalary, decimal housingAllowance,
			decimal transportAllowance, decimal otherAllowances, string? note)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(EmployeeProfile), new { id = employeeId });
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var (ok, errAr, errEn) = await new CrossBuy.BL.Hr.HrSalaryService(Db).SetAsync(gate.CompanyId, employeeId, effectiveFrom,
				basicSalary, housingAllowance, transportAllowance, otherAllowances, note, ctx?.EmployeeId);
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			if (ok) TempData["HrMsg"] = ar ? $"حُفظ الراتب ساريًا من {effectiveFrom:yyyy-MM-dd}" : $"Salary saved, effective {effectiveFrom:yyyy-MM-dd}";
			else TempData["HrErr"] = ar ? errAr : errEn;
			return Redirect(Url.Action(nameof(EmployeeProfile), new { id = employeeId }) + "#emp_pay");
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteDependant(int id, int employeeId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(EmployeeProfile), new { id = employeeId });
			var d = await Db.EmployeeDependants.FirstOrDefaultAsync(x => x.ID == id && x.EmployeeID == employeeId && x.CompanyID == gate.CompanyId);
			if (d != null) { Db.EmployeeDependants.Remove(d); await Db.SaveChangesAsync(); }
			return RedirectToAction(nameof(EmployeeProfile), new { id = employeeId });
		}
	}
}
