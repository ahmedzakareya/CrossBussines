using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H157 — the patient's identity at the counter. A similar name is a reason to CHECK, never a match: the desk
	// is shown the other active files whose names read the same (Arabic folded, Latin keyed — Mohamed / Muhammad), each
	// with its file number and date of birth, so the employee confirms with the patient before taking money, printing or
	// moving anyone. Merging is a separate, privileged act (the duplicates screen, H66).
	// ==========================================================================================
	public static class HospitalIdentityRules
	{
		/// Two names read the same when, in either language, at least two parts of one are found in the other and the
		/// share is 75% or more.
		public static bool Similar(string name, string? nameEn, string otherName, string? otherNameEn)
		{
			static int Parts(string? s) => HospitalIntakeRules.Tokens(s).Count();
			bool Close(string? a, string? b1, string? b2) => !string.IsNullOrWhiteSpace(a) && Parts(a) >= 2 && HospitalFrontDeskRules.NameScore(a, b1, b2) >= 75;
			return Close(name, otherName, otherNameEn) || Close(nameEn, otherName, otherNameEn);
		}
	}

	public sealed record HospitalLookalike(int Id, string Mrn, string Name, string? NameEn, DateTime? BirthDate, string? PhoneTail);

	public sealed class HospitalDeskIdentityService
	{
		private readonly CrossDbContext _db;
		public HospitalDeskIdentityService(CrossDbContext db) { _db = db; }

		/// Other active files whose names read like this patient's (at most `take`).
		public async Task<IReadOnlyList<HospitalLookalike>> LookalikesAsync(BusinessContext ctx, int patientId, int take, CancellationToken ct = default)
		{
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return Array.Empty<HospitalLookalike>();
			// narrow in the database by the first part of each name, then decide by the folded/keyed score
			var firstAr = HospitalIntakeRules.Tokens(p.Name).FirstOrDefault();
			var firstEn = HospitalIntakeRules.Tokens(p.NameEn).FirstOrDefault();
			var enStem = firstEn is { Length: >= 3 } ? firstEn[..3] : null;
			var candidates = await _db.HospitalPatients.AsNoTracking()
				.Where(x => x.CompanyId == ctx.CompanyId && x.ID != p.ID && x.IsActive && x.MergedIntoPatientId == null
					&& ((firstAr != null && x.Name.Contains(firstAr)) || (enStem != null && x.NameEn.Contains(enStem))))
				.OrderByDescending(x => x.ID).Take(300)
				.Select(x => new { x.ID, x.Mrn, x.Name, x.NameEn, x.BirthDate, x.Phone }).ToListAsync(ct);
			return candidates.Where(c => HospitalIdentityRules.Similar(p.Name, p.NameEn, c.Name, c.NameEn)).Take(Math.Clamp(take, 1, 10))
				.Select(c => new HospitalLookalike(c.ID, c.Mrn, c.Name, c.NameEn, c.BirthDate, c.Phone is { Length: >= 4 } ph ? ph[^4..] : null)).ToList();
		}
	}
}
