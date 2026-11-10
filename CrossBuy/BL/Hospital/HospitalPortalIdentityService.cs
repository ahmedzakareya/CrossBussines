using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H25 — the patient portal's sign-in.
	//
	//   · The sign-in is an ordinary AspNetUsers user (IsEndUser, IsActive), created through UserManager — the
	//     project's own Identity API — never a hand-written hash. It signs in on the SAME portal scheme as the
	//     client portal (CrossBuy.Portal); what makes it a PATIENT is its HospitalPortalAccount row.
	//   · Two doors: the front desk gives a verified account its sign-in (a user name and a first password the
	//     patient then uses); or a person registers themselves, which makes an UNVERIFIED account (H24: it sends
	//     requests until the desk matches it to a file).
	//   · Every sign-in refusal is the same message, so the door cannot be used to find out who is a patient.
	// ==========================================================================================

	public sealed class HospitalPortalRegisterInput
	{
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string? Phone { get; set; }
		public string? NationalId { get; set; }
		public DateTime? BirthDate { get; set; }
		public string? UserName { get; set; }
		public string? Password { get; set; }
	}

	public sealed class HospitalPortalIdentityService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly UserManager<Users> _users;

		public HospitalPortalIdentityService(CrossDbContext db, IHospitalAccessService access, UserManager<Users> users) { _db = db; _access = access; _users = users; }

		private IQueryable<HospitalPortalAccount> Accounts => _db.Set<HospitalPortalAccount>();

		/// The account a signed-in principal is (by its user id alone), unless disabled.
		public Task<HospitalPortalAccount?> AccountOfAsync(string? userId, CancellationToken ct = default) =>
			string.IsNullOrEmpty(userId) ? Task.FromResult<HospitalPortalAccount?>(null)
				: Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId && a.Status != HospitalPortalAccountStatus.Disabled, ct);

		/// The credential check: an active end user, the right password, and a patient portal account. Null on any failure.
		public async Task<(Users User, HospitalPortalAccount Account)?> CheckAsync(string? userName, string? password, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) return null;
			var user = await _users.FindByNameAsync(userName.Trim());
			if (user == null || !user.IsActive || !user.IsEndUser) return null;
			// H47: five wrong passwords in a row lock the sign-in for 15 minutes (the same one refusal while locked)
			if (await _users.IsLockedOutAsync(user)) return null;
			if (!await _users.CheckPasswordAsync(user, password))
			{
				if (await _users.GetAccessFailedCountAsync(user) + 1 >= HospitalPortalPasswordRules.MaxFailures)
				{
					await _users.SetLockoutEnabledAsync(user, true);
					await _users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(HospitalPortalPasswordRules.LockMinutes));
					await _users.ResetAccessFailedCountAsync(user);
				}
				else await _users.AccessFailedAsync(user);
				return null;
			}
			if (await _users.GetAccessFailedCountAsync(user) > 0) await _users.ResetAccessFailedCountAsync(user);
			var acc = await AccountOfAsync(user.Id, ct);
			if (acc == null) return null;
			// H47: the desk's "last sign-in"
			await Accounts.Where(a => a.ID == acc.ID).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, (DateTime?)DateTime.UtcNow), ct);
			return (user, acc);
		}

		/// The front desk (`register`) gives an account its sign-in: a user name and the first password.
		public async Task<HospitalResult> CreateSignInAsync(BusinessContext ctx, int accountId, string? userName, string? password, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var acc = await Accounts.FirstOrDefaultAsync(a => a.ID == accountId && a.CompanyId == ctx.CompanyId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			if (acc.UserId != null) return HospitalResult.Fail("This account already has its sign-in");
			if (string.IsNullOrWhiteSpace(userName) || userName.Trim().Length < 4) return HospitalResult.Fail("A user name has at least 4 characters");
			var (user, why) = await CreateUserAsync(userName.Trim(), password, acc.Phone);
			if (why != null) return HospitalResult.Fail(why);
			acc.UserId = user!.Id;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); await _users.DeleteAsync(user); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
			return HospitalResult.Success(acc.ID);
		}

		/// Self-registration with a hospital (company) that allows it: an unverified account and its sign-in.
		public async Task<HospitalResult> RegisterAsync(int companyId, HospitalPortalRegisterInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await SelfRegistrationOpenAsync(companyId, ct)) return HospitalResult.Fail("Registration online is not open: please visit the hospital");
			if (string.IsNullOrWhiteSpace(input.Name)) return HospitalResult.Fail("Enter your name");
			if (string.IsNullOrWhiteSpace(input.Phone) || input.Phone.Trim().Length < 6) return HospitalResult.Fail("Enter your phone number");
			if (input.NationalId is string nid && nid.Trim().Length > 0 && !nid.Trim().All(char.IsDigit)) return HospitalResult.Fail("The civil ID is digits only");
			if (string.IsNullOrWhiteSpace(input.UserName) || input.UserName.Trim().Length < 4) return HospitalResult.Fail("A user name has at least 4 characters");
			var (user, why) = await CreateUserAsync(input.UserName.Trim(), input.Password, input.Phone.Trim());
			if (why != null) return HospitalResult.Fail(why);
			var acc = new HospitalPortalAccount
			{
				CompanyId = companyId, UserId = user!.Id, Status = HospitalPortalAccountStatus.Unverified, Name = input.Name.Trim()[..Math.Min(200, input.Name.Trim().Length)],
				NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim()[..Math.Min(200, input.NameEn.Trim().Length)], Phone = input.Phone.Trim()[..Math.Min(40, input.Phone.Trim().Length)],
				NationalId = string.IsNullOrWhiteSpace(input.NationalId) ? null : input.NationalId.Trim()[..Math.Min(20, input.NationalId.Trim().Length)], BirthDate = input.BirthDate?.Date, CreatedAt = nowUtc,
			};
			_db.Add(acc);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); await _users.DeleteAsync(user); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
			return HospitalResult.Success(acc.ID);
		}

		/// A company takes self-registration when it is a hospital (it has outpatient clinics) and its portal rules
		/// allow it (no saved rules = the default, which allows it).
		public async Task<bool> SelfRegistrationOpenAsync(int companyId, CancellationToken ct = default)
		{
			if (companyId <= 0 || !await _db.HospitalDepartments.AnyAsync(d => d.CompanyId == companyId && d.IsActive, ct)) return false;
			try { return await _db.Set<HospitalPortalSettings>().AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => (bool?)s.SelfRegistration).FirstOrDefaultAsync(ct) ?? true; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return true; }
		}

		private async Task<(Users? User, string? Refusal)> CreateUserAsync(string userName, string? password, string? phone)
		{
			if (await _users.FindByNameAsync(userName) != null) return (null, "This user name is taken");
			if (string.IsNullOrEmpty(password)) return (null, "Choose a password");
			var user = new Users { UserName = userName, PhoneNumber = phone, IsEndUser = true, IsActive = true };
			var made = await _users.CreateAsync(user, password);
			if (!made.Succeeded)
				return (null, made.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)) ? "Choose a stronger password: at least 6 characters with a capital letter, a small letter, a digit and a symbol"
					: made.Errors.Any(e => e.Code.Contains("UserName", StringComparison.Ordinal)) ? "This user name cannot be used" : "The sign-in could not be created");
			return (user, null);
		}
	}
}
