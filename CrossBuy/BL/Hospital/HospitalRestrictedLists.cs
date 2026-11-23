using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H267 — RESTRICTED FILES IN POPULATION LISTS. A restricted file (H64: a VIP, a member of staff) opens only
	// to the privileged, the care team or a reviewed break-glass. Lists that sweep many patients — disease registries,
	// the peer-review sample, the operative-note worklist — must not show such a file's name and clinical values to
	// everyone with the list's permission. For a viewer who is not privileged (`manage` or `confidential view`), the
	// restricted files are left out of the list; the privileged see everything. The file itself is unchanged.
	// ==========================================================================================
	public static class HospitalRestrictedLists
	{
		/// The restricted patients this viewer may not see in a list (empty for the privileged, or when not installed).
		public static async Task<HashSet<int>> HiddenAsync(CrossDbContext db, IHospitalAccessService access, BusinessContext ctx, CancellationToken ct = default)
		{
			if (await access.CanAsync(ctx, HospitalActions.Manage, null, ct) || await access.CanAsync(ctx, HospitalActions.ConfidentialView, null, ct)) return new();
			try { return (await db.Set<HospitalRestrictedFile>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LiftedAt == null).Select(r => r.PatientId).ToListAsync(ct)).ToHashSet(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(); }
		}
	}
}
