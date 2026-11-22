using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H236 — the alerts gone past, read as a whole. What must never happen: an override older than the window
	// counted, another company's counted, or the most overridden alert not on top.
	// ==========================================================================================
	public class HospitalOverrideStatsTests
	{
		[Fact]
		public async Task The_window_counts_this_companys_overrides_by_kind_alert_and_person()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var now = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
			HospitalAlertOverride O(int company, string kind, string detail, int by, int daysAgo) => new() { CompanyId = company, PatientId = 1, SourceId = 1, Kind = kind, Detail = detail, Reason = "r", At = now.AddDays(-daysAgo), ByEmployeeId = by };
			host.Seed.Set<HospitalAlertOverride>().AddRange(
				O(1, HospitalAlertKind.Interaction, "warfarin + aspirin — Major", 7, 1), O(1, HospitalAlertKind.Interaction, "warfarin + aspirin — Major", 7, 5),
				O(1, HospitalAlertKind.Interaction, "warfarin + aspirin — Major", 8, 10), O(1, HospitalAlertKind.Dose, "paracetamol daily 5000 > 4000 mg", 8, 3),
				O(1, HospitalAlertKind.Duplicate, "duplicate Ibuprofen / Naproxen", 7, 200),   // outside the window
				O(2, HospitalAlertKind.Dose, "another company", 9, 1));
			await host.Seed.SaveChangesAsync();
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var st = (await new HospitalDrugSafetyService(host.Db, access).OverrideStatsAsync(ctx, 90, now))!;
			Assert.Equal(4, st.Total);
			Assert.Equal((HospitalAlertKind.Interaction, 3), st.ByKind[0]);
			Assert.Equal(("warfarin + aspirin — Major", 3), st.TopAlerts[0]);
			Assert.Equal(2, st.TopPrescribers.Count);
			Assert.Equal(new[] { 2, 2 }, st.TopPrescribers.Select(p => p.Count).ToArray());
		}
	}
}
