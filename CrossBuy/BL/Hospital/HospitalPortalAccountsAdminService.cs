using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H47 — the desk's patient portal accounts screen (the prototype's #/portal-accounts, approved).
	//   · Staff NEVER see or set a password: opening an account makes the sign-in WITHOUT one and emails an invitation
	//     whose link (72 hours, once) sets it; a forgotten one gets a reset link (H46). Email only, stored nowhere.
	//   · A sign-in locks for 15 minutes after five wrong passwords (H46 identity check); the desk can unlock it, and
	//     can disable an account (it keeps its history) and enable it again.
	//   · The state shown is HospitalPortalAccountRules.State's, from the account, its sign-in and its last link.
	//   · Every action is the front desk's (`register`).
	// ==========================================================================================
	public sealed record HospitalPortalFamilyFace(int PatientId, string Name, string NameEn, string? Photo, string Relation);

	public sealed record HospitalPortalAccountRow(HospitalPortalAccount Account, string? UserName, string State, DateTime? LockedUntil, DateTime? InviteSentAt,
		string? Mrn, string? Photo, IReadOnlyList<HospitalPortalFamilyFace> Family);

	public static class HospitalPortalAccountState
	{
		public const string Active = "Active";
		public const string Unverified = "Unverified";
		public const string NoSignIn = "NoSignIn";
		public const string Invited = "Invited";
		public const string InviteExpired = "InviteExpired";
		public const string Locked = "Locked";
		public const string Disabled = "Disabled";
	}

	public static class HospitalPortalAccountRules
	{
		/// What the desk sees: disabled first, then locked, then where the sign-in stands, then whether the file is matched.
		public static string State(string status, bool hasSignIn, bool hasPassword, DateTime? lockedUntil, DateTime? lastInviteExpires, DateTime nowUtc)
		{
			if (status == HospitalPortalAccountStatus.Disabled) return HospitalPortalAccountState.Disabled;
			if (lockedUntil > nowUtc) return HospitalPortalAccountState.Locked;
			if (!hasSignIn) return HospitalPortalAccountState.NoSignIn;
			if (!hasPassword) return lastInviteExpires > nowUtc ? HospitalPortalAccountState.Invited : HospitalPortalAccountState.InviteExpired;
			return status == HospitalPortalAccountStatus.Unverified ? HospitalPortalAccountState.Unverified : HospitalPortalAccountState.Active;
		}

		/// A user name the desk may give: 4 to 40 letters, digits, dots, dashes or underscores.
		public static string? UserNameRefusal(string? userName) =>
			string.IsNullOrWhiteSpace(userName) || userName.Trim().Length < 4 || userName.Trim().Length > 40 ? "A user name has at least 4 characters"
			: !System.Text.RegularExpressions.Regex.IsMatch(userName.Trim(), @"^[A-Za-z0-9._\-]+$") ? "A user name is letters, digits, dots and dashes" : null;
	}

	public sealed class HospitalPortalAccountsAdminService
	{
		public const string NoTables = "The portal password tables are not created yet (hospital_h46_portal_password.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly UserManager<Users> _users;
		private readonly IHospitalPortalMailer _mail;

		public HospitalPortalAccountsAdminService(CrossDbContext db, IHospitalAccessService access, UserManager<Users> users, IHospitalPortalMailer mail)
		{ _db = db; _access = access; _users = users; _mail = mail; }

		private Task<bool> Can(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Register, null, ct);
		private IQueryable<HospitalPortalAccount> Accounts(BusinessContext ctx) => _db.Set<HospitalPortalAccount>().Where(a => a.CompanyId == ctx.CompanyId);

		public async Task<(IReadOnlyList<HospitalPortalAccountRow> Rows, bool Ready)> ListAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var accs = await Accounts(ctx).AsNoTracking().OrderByDescending(a => a.ID).ToListAsync(ct);
				var uids = accs.Where(a => a.UserId != null).Select(a => a.UserId!).ToList();
				var users = await _db.Users.AsNoTracking().Where(u => uids.Contains(u.Id)).Select(u => new { u.Id, u.UserName, Has = u.PasswordHash != null, u.LockoutEnd }).ToDictionaryAsync(u => u.Id, ct);
				var ids = accs.Select(a => a.ID).ToList();
				var invites = await _db.Set<HospitalPortalReset>().AsNoTracking().Where(r => ids.Contains(r.AccountId))
					.GroupBy(r => r.AccountId).Select(g => new { g.Key, Created = g.Max(r => r.CreatedAt), Expires = g.Max(r => r.ExpiresAt) }).ToDictionaryAsync(x => x.Key, ct);
				var links = await _db.Set<HospitalPortalFamilyLink>().AsNoTracking().Where(l => ids.Contains(l.AccountId)).ToListAsync(ct);
				var pids = accs.Where(a => a.PatientId != null).Select(a => a.PatientId!.Value).Concat(links.Select(l => l.PatientId)).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID) && p.CompanyId == ctx.CompanyId).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToDictionaryAsync(p => p.ID, ct);
				var rows = accs.Select(a =>
				{
					var u = a.UserId != null ? users.GetValueOrDefault(a.UserId) : null; var inv = invites.GetValueOrDefault(a.ID);
					var p = a.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					var fam = links.Where(l => l.AccountId == a.ID && pats.ContainsKey(l.PatientId)).Select(l => { var f = pats[l.PatientId]; return new HospitalPortalFamilyFace(f.ID, f.Name, f.NameEn, f.PhotoPath, l.Relation); }).ToList();
					var state = HospitalPortalAccountRules.State(a.Status, u != null, u?.Has == true, u?.LockoutEnd?.UtcDateTime, inv?.Expires, nowUtc);
					return new HospitalPortalAccountRow(a, u?.UserName, state, u?.LockoutEnd?.UtcDateTime > nowUtc ? u.LockoutEnd!.Value.UtcDateTime : null,
						u != null && !u.Has ? inv?.Created : null, p?.Mrn, p?.PhotoPath, fam);
				}).ToList();
				return (rows, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalPortalAccountRow>(), false); }
		}

		/// Opens (or re-sends) the invitation: the sign-in made WITHOUT a password if there is none, the email on the
		/// account, a 72-hour link. `invite` makes the email of a token and the user name.
		public async Task<HospitalResult> InviteAsync(BusinessContext ctx, int accountId, string? userName, Func<string, string, (string Subject, string Html)> invite, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, ct)) return HospitalResult.Fail("forbidden");
			var acc = await Accounts(ctx).FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			if (acc.Status == HospitalPortalAccountStatus.Disabled) return HospitalResult.Fail("This account is disabled");
			Users? user = acc.UserId == null ? null : await _users.FindByIdAsync(acc.UserId);
			if (user != null && await _users.HasPasswordAsync(user)) return HospitalResult.Fail("This patient already set a password: send a password-reset link instead");
			if (!HospitalPortalPasswordRules.LooksLikeEmail(acc.Email)) return HospitalResult.Fail("Record the patient's email first");
			if (user == null)
			{
				if (HospitalPortalAccountRules.UserNameRefusal(userName) is string why) return HospitalResult.Fail(why);
				if (await _users.FindByNameAsync(userName!.Trim()) != null) return HospitalResult.Fail("This user name is taken");
				user = new Users { UserName = userName.Trim(), PhoneNumber = acc.Phone, IsEndUser = true, IsActive = true };
				var made = await _users.CreateAsync(user);   // no password: the patient sets it from the emailed link
				if (!made.Succeeded) return HospitalResult.Fail("This user name cannot be used");
				acc.UserId = user.Id;
				try { await _db.SaveChangesAsync(ct); }
				catch (DbUpdateException) { _db.ChangeTracker.Clear(); await _users.DeleteAsync(user); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
			}
			return await SendLinkAsync(acc, user.UserName ?? "", TimeSpan.FromHours(HospitalPortalPasswordRules.InviteHours), invite, nowUtc, ct);
		}

		/// The desk sends a password-reset link to the account's email (H46's 30-minute link).
		public async Task<HospitalResult> SendResetAsync(BusinessContext ctx, int accountId, Func<string, string, (string Subject, string Html)> reset, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, ct)) return HospitalResult.Fail("forbidden");
			var acc = await Accounts(ctx).FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			if (acc.Status == HospitalPortalAccountStatus.Disabled) return HospitalResult.Fail("This account is disabled");
			if (!HospitalPortalPasswordRules.LooksLikeEmail(acc.Email)) return HospitalResult.Fail("Record the patient's email first");
			var user = acc.UserId == null ? null : await _users.FindByIdAsync(acc.UserId);
			if (user == null || !await _users.HasPasswordAsync(user)) return HospitalResult.Fail("This patient has no password yet: send the invitation instead");
			return await SendLinkAsync(acc, user.UserName ?? "", TimeSpan.FromMinutes(HospitalPortalPasswordRules.LinkMinutes), reset, nowUtc, ct);
		}

		private async Task<HospitalResult> SendLinkAsync(HospitalPortalAccount acc, string userName, TimeSpan life, Func<string, string, (string Subject, string Html)> mail, DateTime nowUtc, CancellationToken ct)
		{
			if (await _db.Set<HospitalPortalReset>().CountAsync(r => r.AccountId == acc.ID && r.CreatedAt > nowUtc.AddHours(-1), ct) >= HospitalPortalPasswordRules.PerHour)
				return HospitalResult.Fail("Three links were sent this hour: wait before sending another");
			foreach (var old in await _db.Set<HospitalPortalReset>().Where(r => r.AccountId == acc.ID && r.UsedAt == null && r.ExpiresAt > nowUtc).ToListAsync(ct)) old.ExpiresAt = nowUtc;
			var token = HospitalPortalPasswordRules.NewToken();
			_db.Add(new HospitalPortalReset { CompanyId = acc.CompanyId, AccountId = acc.ID, TokenHash = HospitalPortalPasswordRules.Hash(token), Email = acc.Email!.Trim(), CreatedAt = nowUtc, ExpiresAt = nowUtc.Add(life) });
			await _db.SaveChangesAsync(ct);
			var (subject, html) = mail(token, userName);
			return await _mail.SendAsync(acc.Email!.Trim(), subject, html, ct) ? HospitalResult.Success(acc.ID) : HospitalResult.Fail("The email could not be sent: check the mail settings");
		}

		public async Task<HospitalResult> UnlockAsync(BusinessContext ctx, int accountId, CancellationToken ct = default)
		{
			if (!await Can(ctx, ct)) return HospitalResult.Fail("forbidden");
			var acc = await Accounts(ctx).AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId, ct);
			var user = acc?.UserId == null ? null : await _users.FindByIdAsync(acc.UserId);
			if (user == null) return HospitalResult.Fail("not_found");
			await _users.SetLockoutEndDateAsync(user, null);
			await _users.ResetAccessFailedCountAsync(user);
			return HospitalResult.Success(accountId);
		}

		/// Disables (no sign-in, the history kept) or enables again (verified if it has a file, else unverified).
		public async Task<HospitalResult> SetEnabledAsync(BusinessContext ctx, int accountId, bool on, CancellationToken ct = default)
		{
			if (!await Can(ctx, ct)) return HospitalResult.Fail("forbidden");
			var acc = await Accounts(ctx).FirstOrDefaultAsync(a => a.ID == accountId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			acc.Status = !on ? HospitalPortalAccountStatus.Disabled : acc.PatientId != null ? HospitalPortalAccountStatus.Verified : HospitalPortalAccountStatus.Unverified;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(accountId);
		}
	}
}
