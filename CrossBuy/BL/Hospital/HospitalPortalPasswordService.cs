using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H46 — the patient portal's "forgot password" (email only; owner rulings 2026-11-05).
	//   · A request names a user name or an email; EVERY request gets the same answer, so the page never tells who has
	//     an account. For each matching account (active, with a sign-in and an email) a link is made — a random token
	//     of which only the SHA-256 is kept — that works once, for 30 minutes; at most 3 a hour per account, and a new
	//     link ends the older ones.
	//   · The email goes STRAIGHT to the patient over the company's mail server and is stored nowhere: not in the
	//     communication outbox, where staff could read the link (owner ruling). The request itself is the reset row.
	//   · Setting the password runs Identity's own reset (its validators, its security stamp); the account's
	//     PasswordChangedAt ends every portal session issued before it, and a notice of the change is emailed.
	//   · The desk (`register`) records a patient's email on their portal account.
	// ==========================================================================================

	/// The portal's mail: sent at once, kept nowhere. Production is SMTP with the outbox's own settings.
	public interface IHospitalPortalMailer { Task<bool> SendAsync(string to, string subject, string html, CancellationToken ct = default); }

	public sealed class SmtpHospitalPortalMailer : IHospitalPortalMailer
	{
		private readonly CommService.SmtpOptions _smtp = new();
		private readonly bool _devCapture;
		private readonly ILogger<SmtpHospitalPortalMailer> _log;

		public SmtpHospitalPortalMailer(IConfiguration cfg, IWebHostEnvironment env, ILogger<SmtpHospitalPortalMailer> log)
		{ cfg.GetSection("Smtp").Bind(_smtp); _devCapture = env.IsDevelopment(); _log = log; }

		public async Task<bool> SendAsync(string to, string subject, string html, CancellationToken ct = default)
		{
			// Development only: a copy in the machine's temp folder, so the flow can be tested without the mailbox.
			if (_devCapture)
				try
				{
					var dir = Path.Combine(Path.GetTempPath(), "crossbuy-portal-mail"); Directory.CreateDirectory(dir);
					await File.WriteAllTextAsync(Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".html"), "<!-- to: " + WebUtility.HtmlEncode(to) + " -->\n" + html, ct);
				}
				catch (IOException) { }
			if (!_smtp.IsConfigured) return false;
			try
			{
				using var msg = new MailMessage { From = new MailAddress(_smtp.From, _smtp.FromName), Subject = subject, Body = html, IsBodyHtml = true, BodyEncoding = System.Text.Encoding.UTF8, SubjectEncoding = System.Text.Encoding.UTF8 };
				msg.To.Add(to);
				using var client = new SmtpClient(_smtp.Host, _smtp.Port)
				{
					EnableSsl = _smtp.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network,
					Credentials = string.IsNullOrWhiteSpace(_smtp.User) ? CredentialCache.DefaultNetworkCredentials : new NetworkCredential(_smtp.User, _smtp.Password),
				};
				await client.SendMailAsync(msg, ct);
				return true;
			}
			catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
			{
				_log.LogWarning(ex, "Portal mail could not be sent");   // never the address, never the body
				return false;
			}
		}
	}

	public static class HospitalPortalPasswordRules
	{
		public const int LinkMinutes = 30;
		public const int PerHour = 3;
		/// H47: an invitation (the first password) lives longer than a reset link.
		public const int InviteHours = 72;
		/// H47: this many wrong sign-ins in a row lock the account for LockMinutes.
		public const int MaxFailures = 5;
		public const int LockMinutes = 15;

		/// A new link's token: 32 random bytes, URL-safe.
		public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		/// What is stored instead of the token.
		public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token ?? "")));

		public static bool Usable(DateTime? usedAt, DateTime expiresAt, DateTime nowUtc) => usedAt == null && expiresAt > nowUtc;

		public static bool LooksLikeEmail(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 256 && System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

		/// Why these two entries cannot be the new password before Identity's own rules are asked, or null.
		public static string? EntryRefusal(string? password, string? again)
		{
			if (string.IsNullOrEmpty(password)) return "Choose a password";
			if (password != again) return "The two passwords are not the same";
			return null;
		}
	}

	public sealed class HospitalPortalPasswordService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly UserManager<Users> _users;
		private readonly IHospitalPortalMailer _mail;

		public HospitalPortalPasswordService(CrossDbContext db, IHospitalAccessService access, UserManager<Users> users, IHospitalPortalMailer mail)
		{ _db = db; _access = access; _users = users; _mail = mail; }

		private IQueryable<HospitalPortalAccount> Accounts => _db.Set<HospitalPortalAccount>();
		private IQueryable<HospitalPortalReset> Resets => _db.Set<HospitalPortalReset>();

		/// "Forgot password": links for every account the words match, the same silence for none. `resetMail` makes the
		/// email of a token and a user name (the controller knows the absolute URL and the words). Never says whether
		/// anything matched.
		public async Task RequestAsync(string? who, Func<string, string, (string Subject, string Html)> resetMail, DateTime nowUtc, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(who) || who.Length > 256) return;
			var w = who.Trim();
			var ids = new List<int>();
			try
			{
				var user = await _users.FindByNameAsync(w);
				if (user != null && user.IsActive && user.IsEndUser)
					ids.AddRange(await Accounts.Where(a => a.UserId == user.Id).Select(a => a.ID).ToListAsync(ct));
				if (HospitalPortalPasswordRules.LooksLikeEmail(w))
				{
					var lower = w.ToLowerInvariant();
					ids.AddRange(await Accounts.Where(a => a.Email != null && a.Email.ToLower() == lower).Select(a => a.ID).ToListAsync(ct));
				}
				foreach (var id in ids.Distinct())
				{
					var acc = await Accounts.FirstAsync(a => a.ID == id, ct);
					if (acc.Status == HospitalPortalAccountStatus.Disabled || acc.UserId == null || !HospitalPortalPasswordRules.LooksLikeEmail(acc.Email)) continue;
					var signIn = await _users.FindByIdAsync(acc.UserId);
					if (signIn == null || !signIn.IsActive) continue;
					if (await Resets.CountAsync(r => r.AccountId == acc.ID && r.CreatedAt > nowUtc.AddHours(-1), ct) >= HospitalPortalPasswordRules.PerHour) continue;
					// a new link ends the older ones
					foreach (var old in await Resets.Where(r => r.AccountId == acc.ID && r.UsedAt == null && r.ExpiresAt > nowUtc).ToListAsync(ct)) old.ExpiresAt = nowUtc;
					var token = HospitalPortalPasswordRules.NewToken();
					_db.Add(new HospitalPortalReset { CompanyId = acc.CompanyId, AccountId = acc.ID, TokenHash = HospitalPortalPasswordRules.Hash(token), Email = acc.Email!.Trim(),
						CreatedAt = nowUtc, ExpiresAt = nowUtc.AddMinutes(HospitalPortalPasswordRules.LinkMinutes) });
					await _db.SaveChangesAsync(ct);
					var (subject, html) = resetMail(token, signIn.UserName ?? "");
					await _mail.SendAsync(acc.Email!.Trim(), subject, html, ct);
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); }   // tables not created yet: the same silence
		}

		/// The reset a link's token names, while it still works.
		public async Task<HospitalPortalReset?> LinkAsync(string? token, DateTime nowUtc, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
			try
			{
				var hash = HospitalPortalPasswordRules.Hash(token);
				var r = await Resets.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
				return r != null && HospitalPortalPasswordRules.Usable(r.UsedAt, r.ExpiresAt, nowUtc) ? r : null;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return null; }
		}

		/// Sets the new password through a working link. "expired" when the link no longer works.
		public async Task<HospitalResult> ResetAsync(string? token, string? password, string? again, Func<string, (string Subject, string Html)> changedMail, DateTime nowUtc, CancellationToken ct = default)
		{
			var link = await LinkAsync(token, nowUtc, ct);
			if (link == null) return HospitalResult.Fail("expired");
			if (HospitalPortalPasswordRules.EntryRefusal(password, again) is string why) return HospitalResult.Fail(why);
			var acc = await Accounts.FirstOrDefaultAsync(a => a.ID == link.AccountId && a.Status != HospitalPortalAccountStatus.Disabled, ct);
			var user = acc?.UserId == null ? null : await _users.FindByIdAsync(acc.UserId);
			if (acc == null || user == null || !user.IsActive) return HospitalResult.Fail("expired");
			// Identity's own reset: its password rules, a new security stamp
			var made = await _users.ResetPasswordAsync(user, await _users.GeneratePasswordResetTokenAsync(user), password!);
			if (!made.Succeeded)
				return HospitalResult.Fail(made.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)) ? "Choose a stronger password: at least 6 characters with a capital letter, a small letter, a digit and a symbol" : "The password could not be set");
			var row = await Resets.FirstAsync(x => x.ID == link.ID, ct);
			row.UsedAt = nowUtc;
			acc.PasswordChangedAt = nowUtc;
			await _db.SaveChangesAsync(ct);
			if (HospitalPortalPasswordRules.LooksLikeEmail(acc.Email)) { var (subject, html) = changedMail(user.UserName ?? ""); await _mail.SendAsync(acc.Email!.Trim(), subject, html, ct); }
			return HospitalResult.Success(acc.ID);
		}

		/// The desk (`register`) records the address a patient's portal emails go to.
		public async Task<HospitalResult> SetEmailAsync(BusinessContext ctx, int accountId, string? email, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var e = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
			if (e != null && !HospitalPortalPasswordRules.LooksLikeEmail(e)) return HospitalResult.Fail("This is not an email address");
			try
			{
				var acc = await Accounts.FirstOrDefaultAsync(a => a.ID == accountId && a.CompanyId == ctx.CompanyId, ct);
				if (acc == null) return HospitalResult.Fail("not_found");
				acc.Email = e;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(acc.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("The portal password tables are not created yet (hospital_h46_portal_password.sql)"); }
		}
	}
}
