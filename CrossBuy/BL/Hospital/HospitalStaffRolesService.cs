using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using CrossBuy.BL.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H194 — turning the hospital's roles on safely. While no hospital role is granted the module is open to every
	// employee; the FIRST grant closes it for everyone without one. So the roles are given here to the whole team at once:
	//   · the team is read from the hospital's own records — a practitioner's employee, and whoever did the work of a role in
	//     the last 180 days (vital signs or the medication round → nurse; a cash shift → cashier; results entered → lab
	//     technician; verified → lab specialist; a scan → radiographer; a signed report → radiologist; a dispense →
	//     pharmacist; a patient registered → reception); the administrator adds anyone else;
	//   · each person's roles are suggested from that work and corrected by the administrator;
	//   · the grants are made through the platform's own grant writer (only a company or platform administrator may grant;
	//     never on the open mode), ALL IN ONE TRANSACTION — one refusal and none is made, so nobody is locked out halfway.
	// ==========================================================================================
	public sealed record HospitalStaffSignal(string Role, int Count);
	public sealed record HospitalStaffRow(int EmployeeId, string Name, string? NameEn, string? Photo, IReadOnlyList<HospitalStaffSignal> Signals,
		IReadOnlyList<string> Suggested, IReadOnlyList<string> Granted);
	public sealed record HospitalStaffRolesPage(IReadOnlyList<HospitalStaffRow> Rows, bool Open, int Granted);
	public sealed class HospitalStaffGrantInput { public List<HospitalStaffGrantLine> Lines { get; set; } = new(); }
	public sealed class HospitalStaffGrantLine { public int EmployeeId { get; set; } public List<string> Roles { get; set; } = new(); }

	public static class HospitalStaffRoleRules
	{
		public const int LookbackDays = 180;
		public const int ManagerAt = 4;

		/// The roles a person's work suggests: a doctor stays a doctor (the desk and ward work a doctor also does is part
		/// of that role); a radiologist or lab specialist covers the technician's work.
		public static IReadOnlyList<string> Suggest(IEnumerable<HospitalStaffSignal> signals)
		{
			var s = signals.Where(x => x.Count > 0).Select(x => x.Role).ToHashSet();
			// the work of four roles or more is a manager's (or the one who set the hospital up): the administrator, alone
			if (s.Count >= ManagerAt) return new[] { HospitalRoles.HospitalAdministrator };
			if (s.Contains(HospitalRoles.HospitalDoctor)) s.RemoveWhere(r => r is HospitalRoles.HospitalNurse or HospitalRoles.HospitalReception);
			if (s.Contains(HospitalRoles.HospitalLabSpecialist)) s.Remove(HospitalRoles.HospitalLabTechnician);
			if (s.Contains(HospitalRoles.HospitalRadiologist)) s.Remove(HospitalRoles.HospitalRadiographer);
			return HospitalRoles.All.Where(s.Contains).ToList();
		}

		/// What is asked and not held yet; an unknown role refuses the whole request.
		public static (IReadOnlyList<(int Employee, string Role)> ToGrant, string? Refusal) Plan(IEnumerable<HospitalStaffGrantLine> lines, IReadOnlyCollection<(int Employee, string Role)> held)
		{
			var list = new List<(int, string)>();
			foreach (var l in lines)
				foreach (var r in l.Roles.Distinct())
				{
					if (!HospitalRoles.All.Contains(r)) return (Array.Empty<(int, string)>(), "Choose the roles from the hospital's list");
					if (!held.Contains((l.EmployeeId, r))) list.Add((l.EmployeeId, r));
				}
			if (list.Count == 0) return (list, "Nothing new to grant");
			return (list, null);
		}
	}

	public sealed class HospitalStaffRolesService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IPlatformGrantWriter _grants;

		public HospitalStaffRolesService(CrossDbContext db, IHospitalAccessService access, IPlatformGrantWriter grants) { _db = db; _access = access; _grants = grants; }

		private IQueryable<PlatformRoleAssignment> Held(BusinessContext ctx, DateTime nowUtc) =>
			_db.Set<PlatformRoleAssignment>().Where(a => a.CompanyID == ctx.CompanyId && a.Scope == EntityRegistry.ScopeHospital && a.PrincipalType == PlatformPrincipalTypes.Employee
				&& a.IsActive && (a.ValidTo == null || a.ValidTo > nowUtc));

		public async Task<HospitalStaffRolesPage?> PageAsync(BusinessContext ctx, IReadOnlyCollection<int> extra, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return null;
			int cid = ctx.CompanyId; var since = nowUtc.AddDays(-HospitalStaffRoleRules.LookbackDays);
			var signals = new List<(int Emp, string Role, int N)>();
			async Task Add(string role, IQueryable<int?> who) =>
				signals.AddRange((await who.Where(x => x != null).GroupBy(x => x!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => (x.Key, role, x.N)));
			await Add(HospitalRoles.HospitalDoctor, _db.HospitalPractitioners.Where(p => p.CompanyId == cid && p.IsActive).Select(p => p.EmployeeId));
			await Add(HospitalRoles.HospitalNurse, _db.Set<HospitalObservation>().Where(o => o.CompanyId == cid && o.TakenAt >= since).Select(o => o.RecordedByEmployeeId));
			await Add(HospitalRoles.HospitalNurse, _db.Set<HospitalMarEntry>().Where(o => o.CompanyId == cid && o.RecordedAt >= since).Select(o => o.RecordedByEmployeeId));
			await Add(HospitalRoles.HospitalCashier, _db.HospitalCashierShifts.Where(s => s.CompanyId == cid && s.OpenedAt >= since).Select(s => (int?)s.CashierEmployeeId));
			await Add(HospitalRoles.HospitalLabTechnician, _db.Set<HospitalLabOrder>().Where(o => o.CompanyId == cid && o.ResultedAt >= since).Select(o => o.ResultedByEmployeeId));
			await Add(HospitalRoles.HospitalLabSpecialist, _db.Set<HospitalLabOrder>().Where(o => o.CompanyId == cid && o.VerifiedAt >= since).Select(o => o.VerifiedByEmployeeId));
			await Add(HospitalRoles.HospitalRadiographer, _db.HospitalRadOrders.Where(o => o.CompanyId == cid && o.ScanStartedAt >= since).Select(o => o.ScannedByEmployeeId));
			await Add(HospitalRoles.HospitalRadiologist, _db.HospitalRadOrders.Where(o => o.CompanyId == cid && o.SignedAt >= since).Select(o => o.SignedByEmployeeId));
			await Add(HospitalRoles.HospitalPharmacist, _db.HospitalDispenses.Where(d => d.CompanyId == cid && d.DispensedAt >= since).Select(d => d.DispensedByEmployeeId));
			await Add(HospitalRoles.HospitalReception, _db.HospitalPatients.Where(p => p.CompanyId == cid && p.CreatedAt >= since).Select(p => p.CreatedByEmployeeId));
			var held = await Held(ctx, nowUtc).Select(a => new { a.PrincipalId, a.Role }).ToListAsync(ct);

			var ids = signals.Select(s => s.Emp).Concat(held.Select(h => h.PrincipalId)).Concat(extra).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == cid && e.IsActive).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, people.Select(p => p.ID).ToList(), ct);
			var rows = people.Select(p =>
			{
				var sig = signals.Where(s => s.Emp == p.ID).GroupBy(s => s.Role).Select(g => new HospitalStaffSignal(g.Key, g.Sum(x => x.N))).ToList();
				var granted = HospitalRoles.All.Where(r => held.Any(h => h.PrincipalId == p.ID && h.Role == r)).ToList();
				return new HospitalStaffRow(p.ID, p.FullName ?? "", p.FullNameEn, photos.GetValueOrDefault(p.ID), sig, HospitalStaffRoleRules.Suggest(sig), granted);
			}).OrderBy(r => r.Granted.Count > 0 ? 1 : 0).ThenBy(r => r.Suggested.Count == 0 ? 1 : 0).ThenBy(r => r.Name).ToList();
			return new HospitalStaffRolesPage(rows, held.Count == 0, held.Count);
		}

		/// Active employees of the company, by name, for the administrator to add someone the records do not show.
		public async Task<IReadOnlyList<(int Id, string Name, string? NameEn)>> SearchAsync(BusinessContext ctx, string? q, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return Array.Empty<(int, string, string?)>();
			var t = (q ?? "").Trim();
			var rows = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive && (t == "" || e.FullName.Contains(t) || (e.FullNameEn != null && e.FullNameEn.Contains(t))))
				.OrderBy(e => e.FullName).Take(20).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			return rows.Select(r => (r.ID, r.FullName ?? "", r.FullNameEn)).ToList();
		}

		/// Every asked role not yet held, through the platform's grant writer, in ONE transaction.
		public async Task<HospitalResult> GrantAsync(BusinessContext ctx, HospitalStaffGrantInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var held = (await Held(ctx, nowUtc).Select(a => new { a.PrincipalId, a.Role }).ToListAsync(ct)).Select(h => (h.PrincipalId, h.Role)).ToHashSet();
			var (plan, why) = HospitalStaffRoleRules.Plan(input.Lines, held);
			if (why != null) return HospitalResult.Fail(why);
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			foreach (var (emp, role) in plan)
			{
				var r = await _grants.CreateGrantAsync(ctx, new CreatePlatformGrantCommand
				{
					CompanyId = ctx.CompanyId, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp, Role = role,
					Reason = "Hospital staff roles (H194)", IdempotencyKey = "h194:" + ctx.CompanyId + ":" + emp + ":" + role,
				}, ct);
				if (r.Outcome is not (PlatformGrantOutcome.Success or PlatformGrantOutcome.IdempotentReplay))
				{
					await tx.RollbackAsync(); _db.ChangeTracker.Clear();
					return HospitalResult.Fail("Nothing was granted: {0}", r.Message);
				}
			}
			await tx.CommitAsync();
			return HospitalResult.Success(plan.Count);
		}
	}
}
