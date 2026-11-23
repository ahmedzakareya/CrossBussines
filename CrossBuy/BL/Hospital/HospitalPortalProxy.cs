using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalProxyInput { public string? Scope { get; set; } public DateTime? ExpiresAt { get; set; } }

	// ==========================================================================================
	// HOSPITAL H251 — PROXY ACCESS and ONLINE BILL PAYMENT on the patient portal.
	//   · A family link (the account books for a relative) carries a SCOPE — the whole record (results, bills,
	//     medicines, messages, questionnaires) or bookings only — and an END DATE; an ended link gives nothing.
	//   · A bill's balance can be paid online through the provider's checkout address the administrator enters
	//     ({ref}, {amount}, {currency}); without one, bills are paid at the cashier. The payment is confirmed and posted
	//     by the cashier from the provider's statement — the portal never marks a bill paid by itself.
	// ==========================================================================================
	public static class HospitalPortalPayLink
	{
		public static string? Refusal(string? template)
		{
			if (string.IsNullOrWhiteSpace(template)) return null;
			var t = template.Trim();
			if (t.Length > 300) return "The payment link is 300 characters at most";
			if (!t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(t.Replace("{ref}", "x").Replace("{amount}", "1").Replace("{currency}", "KWD"), UriKind.Absolute, out _))
				return "The payment link is the provider's https address";
			return t.Contains("{ref}") && t.Contains("{amount}") ? null : "The payment link needs {ref} and {amount}";
		}

		public static string? Build(string? template, string billNo, decimal due, string currency, int decimals)
		{
			if (string.IsNullOrWhiteSpace(template) || due <= 0) return null;
			return template.Trim().Replace("{ref}", Uri.EscapeDataString(billNo)).Replace("{amount}", Uri.EscapeDataString(due.ToString("F" + decimals, CultureInfo.InvariantCulture)))
				.Replace("{currency}", Uri.EscapeDataString(currency));
		}
	}

	public static class HospitalPortalProxyRules
	{
		public static bool Active(HospitalPortalFamilyLink l, DateTime nowUtc) => l.ExpiresAt == null || l.ExpiresAt > nowUtc;

		public static string? Refusal(string? scope, DateTime? expiresAt, DateTime nowUtc)
		{
			if (!HospitalPortalProxyScope.All.Contains(scope ?? "")) return "Choose what the proxy may see";
			return expiresAt is DateTime e && e <= nowUtc ? "The end date is in the future" : null;
		}
	}

	/// The front desk sets a family link's scope and end date.
	public sealed class HospitalPortalProxyService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalPortalProxyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		public async Task<HospitalResult> SetAsync(BusinessContext ctx, int accountId, int patientId, HospitalProxyInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalPortalProxyRules.Refusal(i.Scope, i.ExpiresAt, nowUtc) is string why) return HospitalResult.Fail(why);
			var l = await _db.Set<HospitalPortalFamilyLink>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.AccountId == accountId && x.PatientId == patientId, ct);
			if (l == null) return HospitalResult.Fail("not_found");
			l.Scope = i.Scope!; l.ExpiresAt = i.ExpiresAt;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(l.ID);
		}
	}
}
