using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	/// A person as a screen shows them: the name in both languages and the photo (null = initials on the brand tint).
	public sealed record HospitalFace(string? Name, string? NameEn, string? Photo);

	/// The owner's standing rule: wherever a screen names a member of staff, it shows their photo. The photo is the
	/// employee's profile image — a doctor's is the one of the employee the practitioner record is linked to.
	public static class HospitalFaces
	{
		public static async Task<Dictionary<int, HospitalFace>> OfEmployeesAsync(CrossDbContext db, int companyId, IEnumerable<int?> ids, CancellationToken ct = default)
		{
			var want = ids.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			if (want.Count == 0) return new();
			return (await db.Employee.AsNoTracking().Where(e => want.Contains(e.ID) && e.EmpCompanyID == companyId).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync(ct))
				.ToDictionary(e => e.ID, e => new HospitalFace(e.FullName, e.FullNameEn, CrossBuy.BL.Platform.EmployeePhotos.Web(e.ProfileImage)));
		}

		/// Practitioners by id: their own names, the photo of the employee they are.
		public static async Task<Dictionary<int, HospitalFace>> OfPractitionersAsync(CrossDbContext db, int companyId, IEnumerable<int?> ids, CancellationToken ct = default)
		{
			var want = ids.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			if (want.Count == 0) return new();
			var docs = await db.HospitalPractitioners.AsNoTracking().Where(p => want.Contains(p.ID) && p.CompanyId == companyId).Select(p => new { p.ID, p.Name, p.NameEn, p.EmployeeId }).ToListAsync(ct);
			var emps = await OfEmployeesAsync(db, companyId, docs.Select(d => d.EmployeeId), ct);
			return docs.ToDictionary(d => d.ID, d => new HospitalFace(d.Name, d.NameEn, d.EmployeeId is int e ? emps.GetValueOrDefault(e)?.Photo : null));
		}
	}
}
