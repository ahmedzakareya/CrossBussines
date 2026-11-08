using System.Globalization;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR3 — THE LEAVE ENGINE: A REQUEST IS CHECKED AGAINST THE LAW AND THE COMPANY POLICY BEFORE IT EXISTS (2026-11-08).
	//
	// Two sources, each owning what it legitimately decides:
	//   * the COUNTRY RULE PACK (law, a setting per company): minimum service, times per career, religion, and whether
	//     the year's days are available up front or accrue monthly;
	//   * the COMPANY POLICY line (LeavePolicies, the employee's assigned policy): the notice period.
	// Every refusal SAYS WHY, with the figure and where it comes from, so the employee is never told only "no".
	//
	// The engine never invents a rule: a leave type the pack does not recognise, or a pack field left at 0/empty,
	// imposes no condition. The balance itself stays in LeaveDashboardService (the canonical remaining-days answer);
	// this engine only narrows it to what has ACCRUED so far when the pack says "monthly".
	// ==========================================================================================
	public sealed record HrLeaveRefusal(string Ar, string En)
	{
		public string Text => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? Ar : En;
	}

	public sealed class HrLeaveEligibility
	{
		private readonly CrossDbContext _db;
		public HrLeaveEligibility(CrossDbContext db) { _db = db; }

		/// Null when the request may proceed; otherwise the first rule it breaks, explained.
		/// <paramref name="remainingForYear"/> is the canonical balance (entitlement + carry-over − used − encashed).
		public async Task<HrLeaveRefusal?> CheckAsync(int employeeId, int leaveTypeId, DateTime start, int days, int remainingForYear,
			DateTime? today = null, CancellationToken ct = default)
		{
			var now = (today ?? DateTime.Today).Date;
			var emp = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId)
				.Select(e => new { e.EmpCompanyID, e.DateOfJoining, e.Religion }).FirstOrDefaultAsync(ct);
			if (emp == null) return null;
			var type = await _db.LeaveTypes.AsNoTracking().Where(t => t.ID == leaveTypeId).Select(t => new { t.NameAr, t.NameEn }).FirstOrDefaultAsync(ct);
			if (type == null) return null;
			string tAr = type.NameAr ?? "الإجازة", tEn = type.NameEn ?? "this leave";

			// ---- the company policy: notice period ----
			var policyId = await _db.PolicyAssignments.AsNoTracking().Where(p => p.EmployeeID == employeeId).Select(p => (int?)p.LeavePolicyTypeID).FirstOrDefaultAsync(ct);
			if (policyId != null)
			{
				var notice = await _db.LeavePolicies.AsNoTracking().Where(l => l.LeavePolicyTypeID == policyId && l.LeaveTypeID == leaveTypeId)
					.Select(l => (int?)l.NoticePeriodDays).MaxAsync(ct) ?? 0;
				var given = (start.Date - now).Days;
				if (notice > 0 && given < notice)
					return new($"تتطلب لائحة الشركة إخطاراً قبل {notice} يوماً من بدء {tAr}، وطلبك يبدأ بعد {Math.Max(given, 0)} يوماً. اختر تاريخ بدء من {now.AddDays(notice):yyyy-MM-dd} أو بعده.",
						$"The company policy requires {notice} days' notice for {tEn}; your request starts in {Math.Max(given, 0)} day(s). Choose a start date on or after {now.AddDays(notice):yyyy-MM-dd}.");
			}

			// ---- the law: the country rule pack ----
			var pack = (await new HrRulePackService(_db).EffectiveAsync(emp.EmpCompanyID, ct))?.Rules;
			var law = HrLeaveMatch.For(pack, type.NameEn, type.NameAr);
			if (law == null) return null;
			var art = string.IsNullOrWhiteSpace(law.Reference) ? "" : $" ({law.Reference})";

			if (!string.IsNullOrWhiteSpace(law.Religion) && !string.Equals(emp.Religion, law.Religion, StringComparison.OrdinalIgnoreCase))
				return string.IsNullOrWhiteSpace(emp.Religion)
					? new($"{tAr} مقصورة بحسب القانون{art} على ديانة محددة، وديانتك غير مسجلة في ملفك. اطلب من الموارد البشرية تسجيلها.",
						$"By law{art}, {tEn} is limited to one religion, and yours is not recorded in your file. Ask HR to record it.")
					: new($"{tAr} مقصورة بحسب القانون{art} على ديانة غير المسجلة في ملفك.",
						$"By law{art}, {tEn} is limited to a religion other than the one in your file.");

			if (law.MinServiceMonths > 0)
			{
				var eligibleFrom = emp.DateOfJoining.Date.AddDays((double)(law.MinServiceMonths * 30.4375m));
				if (start.Date < eligibleFrom)
					return new($"لا تستحق {tAr} قبل إكمال {N(law.MinServiceMonths)} شهراً من الخدمة{art}. تاريخ تعيينك {emp.DateOfJoining:yyyy-MM-dd}، فتستحقها من {eligibleFrom:yyyy-MM-dd}.",
						$"{tEn} is due only after {N(law.MinServiceMonths)} months of service{art}. You joined on {emp.DateOfJoining:yyyy-MM-dd}, so it is due from {eligibleFrom:yyyy-MM-dd}.");
			}

			if (law.MaxTimesPerService > 0)
			{
				var taken = await _db.LeaveRequests.AsNoTracking().CountAsync(r => r.EmployeeID == employeeId && r.LeaveTypeID == leaveTypeId && (r.Status == 0 || r.Status == 1), ct);
				if (taken >= law.MaxTimesPerService)
					return new(law.MaxTimesPerService == 1
							? $"{tAr} تُمنح مرة واحدة طوال مدة الخدمة{art}، ولديك طلب سابق منها."
							: $"{tAr} تُمنح {law.MaxTimesPerService} مرات طوال مدة الخدمة{art}، وقد استُنفدت ({taken}).",
						law.MaxTimesPerService == 1
							? $"{tEn} is granted once in a career{art}, and you already have a request for it."
							: $"{tEn} is granted {law.MaxTimesPerService} times in a career{art}, and all {taken} are used.");
			}

			if (law.AccruesMonthly && policyId != null)
			{
				// What has accrued by the leave's first day: the year's entitlement ÷ 12 for each COMPLETED month since
				// 1 January (or since joining, for someone who joined this year). The canonical balance counts the whole
				// year's entitlement, so the part not yet accrued is taken off it; carry-over stays usable at once.
				var entitlement = await _db.LeavePolicies.AsNoTracking().Where(l => l.LeavePolicyTypeID == policyId && l.LeaveTypeID == leaveTypeId)
					.Select(l => (int?)l.EntitlementDaysPerYear).MaxAsync(ct) ?? 0;
				var accrued = AccruedDays(entitlement, emp.DateOfJoining, start);
				var available = remainingForYear - (entitlement - accrued);
				if (days > available)
					return new($"{tAr} تُكتسب شهرياً بحسب القانون{art} ({N(entitlement / 12m)} يوم كل شهر). حتى {start:yyyy-MM} اكتسبت ما يتيح {Math.Max(available, 0)} يوماً، وطلبت {days}.",
						$"By law{art}, {tEn} accrues monthly ({N(entitlement / 12m)} days a month). By {start:yyyy-MM} you have accrued enough for {Math.Max(available, 0)} day(s), and you asked for {days}.");
			}

			return null;
		}

		/// The leave types for which the employee's assigned policy requires a medical certificate (LeavePolicies).
		public async Task<HashSet<int>> CertificateTypesAsync(int employeeId, CancellationToken ct = default)
		{
			var policyId = await _db.PolicyAssignments.AsNoTracking().Where(p => p.EmployeeID == employeeId).Select(p => (int?)p.LeavePolicyTypeID).FirstOrDefaultAsync(ct);
			if (policyId == null) return new();
			return (await _db.LeavePolicies.AsNoTracking().Where(l => l.LeavePolicyTypeID == policyId && l.RequiresMedicalCertificate == true)
				.Select(l => l.LeaveTypeID).ToListAsync(ct)).ToHashSet();
		}

		public static readonly HrLeaveRefusal CertificateMissing = new(
			"لائحة الشركة تشترط إرفاق شهادة طبية مع هذا النوع من الإجازة. أرفق الشهادة (PDF أو صورة، حتى 15 ميجابايت) ثم أعد الإرسال.",
			"The company policy requires a medical certificate with this type of leave. Attach it (PDF or image, up to 15 MB) and submit again.");

		/// Whole days of a <paramref name="entitlement"/>-day year accrued by <paramref name="on"/>: entitlement ÷ 12 per
		/// completed month counted from 1 January of that year, or from the joining date when the employee joined that year.
		/// Pure, so the month boundaries are testable.
		public static int AccruedDays(int entitlement, DateTime joined, DateTime on)
		{
			var from = joined.Year == on.Year ? joined.Date : new DateTime(on.Year, 1, 1);
			if (joined.Year > on.Year) return 0;
			var months = (on.Year - from.Year) * 12 + on.Month - from.Month - (on.Day < from.Day ? 1 : 0);
			months = Math.Clamp(months, 0, 12);
			return (int)Math.Floor(entitlement * months / 12m);
		}

		private static string N(decimal v) => v.ToString(v == Math.Floor(v) ? "0" : "0.##", CultureInfo.InvariantCulture);
	}
}
