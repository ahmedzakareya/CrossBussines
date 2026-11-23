using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalLicenceRow(HospitalPractitioner Doctor, HospitalFace? Face, int? DaysLeft, string State);
	public sealed record HospitalLicencePage(IReadOnlyList<HospitalLicenceRow> Rows, int Total, int Expired, int Expiring, int Missing);

	// ==========================================================================================
	// HOSPITAL H265 — PROFESSIONAL LICENCES ahead of time. A doctor whose licence has lapsed is already refused
	// bookings, theatre and on-call (H5/H29/H62); this shows the lapse COMING: every active practitioner by licence
	// state (expired, within 60 days, no licence on file), and tells the practitioner themself at 60, 30 and 7 days and
	// on the day it lapses — once per stage (the notification's dedup key), when the administrator opens the list.
	// ==========================================================================================
	public static class HospitalLicenceRules
	{
		public const int WarnDays = 60;

		public static string State(string? number, DateTime? expiry, DateTime today)
		{
			if (string.IsNullOrWhiteSpace(number) || expiry == null) return "Missing";
			var d = (expiry.Value.Date - today.Date).TotalDays;
			return d < 0 ? "Expired" : d <= WarnDays ? "Expiring" : "Valid";
		}

		/// The reminder stage of a licence (60, 30, 7 days, or 0 once lapsed); null when no reminder is due.
		public static int? Stage(DateTime? expiry, DateTime today)
		{
			if (expiry == null) return null;
			var d = (expiry.Value.Date - today.Date).TotalDays;
			return d < 0 ? 0 : d <= 7 ? 7 : d <= 30 ? 30 : d <= WarnDays ? 60 : null;
		}

		public static string DedupKey(int practitionerId, DateTime expiry, int stage) =>
			"hosp-licence:" + practitionerId + ":" + expiry.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ":" + stage;
	}

	public sealed class HospitalLicenceService
	{
		public const string NotificationType = "hospital_licence";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly INotificationService? _notify;
		public HospitalLicenceService(CrossDbContext db, IHospitalAccessService access, INotificationService? notify = null) { _db = db; _access = access; _notify = notify; }

		public async Task<HospitalLicencePage?> PageAsync(BusinessContext ctx, string? state, string? q, int page, int size, DateTime todayLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return null;
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).ToListAsync(ct);
			await RemindAsync(ctx, docs, todayLocal, ct);
			var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, docs.Select(d => d.EmployeeId), ct);
			var all = docs.Select(d =>
			{
				int? left = d.LicenseExpiry is DateTime x ? (int)(x.Date - todayLocal.Date).TotalDays : null;
				return new HospitalLicenceRow(d, d.EmployeeId is int e ? faces.GetValueOrDefault(e) : null, left, HospitalLicenceRules.State(d.LicenseNumber, d.LicenseExpiry, todayLocal));
			}).ToList();
			int expired = all.Count(r => r.State == "Expired"), expiring = all.Count(r => r.State == "Expiring"), missing = all.Count(r => r.State == "Missing");
			IEnumerable<HospitalLicenceRow> rows = state is "Expired" or "Expiring" or "Missing" or "Valid" ? all.Where(r => r.State == state) : all.Where(r => r.State != "Valid");
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				rows = rows.Where(r => r.Doctor.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Doctor.NameEn.Contains(t, StringComparison.OrdinalIgnoreCase) || (r.Doctor.LicenseNumber ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
			}
			var list = rows.OrderBy(r => r.State == "Expired" ? 0 : r.State == "Expiring" ? 1 : r.State == "Missing" ? 2 : 3).ThenBy(r => r.DaysLeft ?? int.MaxValue).ThenBy(r => r.Doctor.Name).ToList();
			return new HospitalLicencePage(list.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), list.Count, expired, expiring, missing);
		}

		/// Tells each practitioner (their own account) of a licence at a reminder stage — once per stage.
		private async Task RemindAsync(BusinessContext ctx, List<HospitalPractitioner> docs, DateTime todayLocal, CancellationToken ct)
		{
			if (_notify == null) return;
			foreach (var d in docs.Where(d => d.EmployeeId != null && d.LicenseExpiry != null))
			{
				if (HospitalLicenceRules.Stage(d.LicenseExpiry, todayLocal) is not int stage) continue;
				var key = HospitalLicenceRules.DedupKey(d.ID, d.LicenseExpiry!.Value, stage);
				if (await _db.Notifications.AsNoTracking().AnyAsync(n => n.DedupKey == key, ct)) continue;
				string day = d.LicenseExpiry.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
				await _notify.NotifyAsync(d.EmployeeId!.Value,
					stage == 0 ? "انتهى ترخيصك المهني" : "ترخيصك المهني ينتهي قريبًا", stage == 0 ? "Your professional licence has lapsed" : "Your professional licence expires soon",
					stage == 0 ? "انتهى الترخيص في " + day + "؛ أُوقفت الحجوزات حتى تجديده." : "ينتهي الترخيص في " + day + "؛ جدّده لتستمر الحجوزات.",
					stage == 0 ? "It lapsed on " + day + "; bookings are stopped until it is renewed." : "It expires on " + day + "; renew it to keep your bookings open.",
					NotificationType, d.ID, "/Hospital/Practitioner/" + d.ID, ctx.CompanyId, ctx.EmployeeId,
					priority: stage <= 7 ? "High" : "Normal", category: "hospital", dedupKey: key, icon: "ki-shield-cross");
			}
		}
	}
}
