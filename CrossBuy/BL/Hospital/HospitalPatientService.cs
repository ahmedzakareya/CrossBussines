using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL PATIENT SERVICE — the one place a patient's file is read, opened and changed.
	//
	//   · Every query names the caller's company (no global filter on these tables).
	//   · Every write asks HospitalAccessService for `register` itself.
	//   · The MRN is generated here, never typed: "MRN-" + the next number in the company,
	//     starting at 100001. Two desks registering at once both try; the unique index lets one
	//     win and the other retries with the next number.
	//   · One file per national ID per company. The duplicate check (SimilarAsync) also looks at
	//     the mobile number and the name, so the desk sees an existing file before opening a
	//     second one for the same person.
	//   · Opening, creating or changing a file writes HospitalPatientAccessLog.
	// ==========================================================================================

	public sealed class HospitalPatientInput
	{
		public int? Id { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Sex { get; set; } = HospitalPatientSex.Male;
		public DateTime? BirthDate { get; set; }
		public string? NationalId { get; set; }
		public int? NationalityId { get; set; }
		public string? Phone { get; set; }
		public string? BloodGroup { get; set; }
		public string? Allergies { get; set; }
		public string? ChronicConditions { get; set; }
		public string? EmergencyName { get; set; }
		public string? EmergencyRelation { get; set; }
		public string? EmergencyPhone { get; set; }
		public bool ConsentSigned { get; set; }
		/// H7: the price list the patient's bills use (null = the main list). Only an approved list is accepted.
		public int? PriceListId { get; set; }
	}

	public sealed record HospitalPatientRow(int Id, string Mrn, string Name, string NameEn, string Sex, DateTime? BirthDate,
		string? NationalId, string? Phone, bool HasAllergy, DateTime CreatedAt, string? PhotoPath = null);

	public sealed record HospitalPatientStats(int Total, int ThisMonth, int PossibleDuplicates, int WithAllergy);

	public sealed record HospitalPatientPage(IReadOnlyList<HospitalPatientRow> Rows, int Total, int Page, int PageSize, HospitalPatientStats Stats);

	/// Why a file looks like the one being typed: the same ID, the same mobile, or the same name.
	public sealed record HospitalSimilarPatient(int Id, string Mrn, string Name, string NameEn, string? NationalId, string? Phone, string Reason);

	public sealed record HospitalPatientAccessView(DateTime At, string Action, int? EmployeeId);

	public sealed class HospitalPatientService
	{
		public const string MrnPrefix = "MRN-";
		public const int FirstMrnNumber = 100001;
		public const int PageSize = 25;
		private static readonly Regex Digits = new(@"^[0-9]{5,20}$", RegexOptions.Compiled);
		private static readonly Regex PhoneShape = new(@"^\+?[0-9 ]{6,20}$", RegexOptions.Compiled);

		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalPatientService(CrossDbContext db, IHospitalAccessService access)
		{ _db = db; _access = access; }

		private Task<bool> CanRegisterAsync(BusinessContext ctx, CancellationToken ct)
			=> _access.CanAsync(ctx, HospitalActions.Register, null, ct);

		private IQueryable<HospitalPatient> Mine(BusinessContext ctx) => _db.HospitalPatients.Where(p => p.CompanyId == ctx.CompanyId && p.IsActive);

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<HospitalPatientPage> ListAsync(BusinessContext ctx, string? query, int page, DateTime now, CancellationToken ct = default)
		{
			var q = Mine(ctx).AsNoTracking();
			var term = (query ?? "").Trim();
			if (term.Length > 0)
				q = q.Where(p => p.Mrn.Contains(term) || p.Name.Contains(term) || p.NameEn.Contains(term)
					|| (p.NationalId != null && p.NationalId.Contains(term)) || (p.Phone != null && p.Phone.Contains(term)));
			int total = await q.CountAsync(ct);
			page = Math.Max(1, page);
			var rows = await q.OrderByDescending(p => p.ID).Skip((page - 1) * PageSize).Take(PageSize)
				.Select(p => new HospitalPatientRow(p.ID, p.Mrn, p.Name, p.NameEn, p.Sex, p.BirthDate, p.NationalId, p.Phone,
					p.Allergies != null && p.Allergies != "", p.CreatedAt, p.PhotoPath))
				.ToListAsync(ct);
			return new HospitalPatientPage(rows, total, page, PageSize, await StatsAsync(ctx, now, ct));
		}

		public async Task<HospitalPatientStats> StatsAsync(BusinessContext ctx, DateTime now, CancellationToken ct = default)
		{
			var all = Mine(ctx).AsNoTracking();
			var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
			int total = await all.CountAsync(ct);
			int thisMonth = await all.CountAsync(p => p.CreatedAt >= monthStart, ct);
			int allergy = await all.CountAsync(p => p.Allergies != null && p.Allergies != "", ct);
			// A possible duplicate: two files with the same English name and the same birth date. The
			// national ID cannot repeat (unique index), so this is what slips through: a file opened
			// without the ID, and a second one with it.
			int dups = (await all.Where(p => p.BirthDate != null)
				.GroupBy(p => new { p.NameEn, p.BirthDate })
				.Select(g => g.Count()).Where(n => n > 1).ToListAsync(ct)).Sum();
			return new HospitalPatientStats(total, thisMonth, dups, allergy);
		}

		public Task<HospitalPatient?> GetAsync(BusinessContext ctx, int id, CancellationToken ct = default)
			=> Mine(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == id, ct);

		/// Files that may be the person being registered. `excludeId` leaves out the file being edited.
		public async Task<IReadOnlyList<HospitalSimilarPatient>> SimilarAsync(BusinessContext ctx, string? nationalId, string? phone,
			string? name, string? nameEn, int? excludeId, CancellationToken ct = default)
		{
			var nid = Clean(nationalId); var tel = CleanPhone(phone); var ar = Clean(name); var en = Clean(nameEn);
			if (nid == null && tel == null && ar == null && en == null) return Array.Empty<HospitalSimilarPatient>();
			int skip = excludeId ?? 0;
			var hits = await Mine(ctx).AsNoTracking()
				.Where(p => p.ID != skip && ((nid != null && p.NationalId == nid) || (tel != null && p.Phone == tel)
					|| (ar != null && p.Name == ar) || (en != null && p.NameEn == en)))
				.OrderBy(p => p.ID).Take(5)
				.ToListAsync(ct);
			return hits.Select(p => new HospitalSimilarPatient(p.ID, p.Mrn, p.Name, p.NameEn, p.NationalId, p.Phone,
				nid != null && p.NationalId == nid ? "national-id" : tel != null && p.Phone == tel ? "phone" : "name")).ToList();
		}

		public async Task<IReadOnlyList<HospitalPatientAccessView>> AccessLogAsync(BusinessContext ctx, int patientId, int take = 50, CancellationToken ct = default)
			=> await _db.HospitalPatientAccessLogs.AsNoTracking()
				.Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId)
				.OrderByDescending(a => a.At).ThenByDescending(a => a.ID).Take(take)
				.Select(a => new HospitalPatientAccessView(a.At, a.Action, a.EmployeeId))
				.ToListAsync(ct);

		/// Opens a file for reading and records that it was opened. Null when the file is not the
		/// caller's company's — absent and foreign look the same.
		public async Task<HospitalPatient?> OpenAsync(BusinessContext ctx, int id, int? employeeId, CancellationToken ct = default)
		{
			var p = await GetAsync(ctx, id, ct);
			if (p == null) return null;
			_db.HospitalPatientAccessLogs.Add(new HospitalPatientAccessLog
			{ CompanyId = ctx.CompanyId, PatientId = p.ID, EmployeeId = employeeId, Action = HospitalPatientAccessActions.Open, At = DateTime.UtcNow });
			await _db.SaveChangesAsync(ct);
			return p;
		}

		// ====================================================================================
		// WRITES
		// ====================================================================================

		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, HospitalPatientInput input, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await CanRegisterAsync(ctx, ct)) return HospitalResult.Fail("forbidden");

			var name = Clean(input.Name); var nameEn = Clean(input.NameEn);
			if (name == null || name.Length > 200) return HospitalResult.Fail("Enter the Arabic name");
			if (nameEn == null || nameEn.Length > 200) return HospitalResult.Fail("Enter the English name");
			if (!HospitalPatientSex.IsKnown(input.Sex)) return HospitalResult.Fail("Choose the sex");
			if (input.BirthDate is DateTime dob && (dob.Date > now.Date || dob.Date < now.Date.AddYears(-130)))
				return HospitalResult.Fail("Enter a real date of birth");

			var nid = Clean(input.NationalId);
			if (nid != null && !Digits.IsMatch(nid)) return HospitalResult.Fail("The ID number must be 5 to 20 digits");
			var phone = CleanPhone(input.Phone);
			if (phone != null && !PhoneShape.IsMatch(phone)) return HospitalResult.Fail("Enter a valid mobile number");
			var ePhone = CleanPhone(input.EmergencyPhone);
			if (ePhone != null && !PhoneShape.IsMatch(ePhone)) return HospitalResult.Fail("Enter a valid mobile number");
			var blood = Clean(input.BloodGroup);
			if (blood != null && !HospitalBloodGroups.IsKnown(blood)) return HospitalResult.Fail("Unknown blood group");
			if (input.NationalityId is int nat && !await _db.CountriesLookup.AnyAsync(c => c.ID == nat, ct))
				return HospitalResult.Fail("Unknown nationality");

			int self = input.Id ?? 0;
			if (nid != null)
			{
				var owner = await _db.HospitalPatients.AsNoTracking()
					.Where(p => p.CompanyId == ctx.CompanyId && p.NationalId == nid && p.ID != self)
					.Select(p => p.Mrn).FirstOrDefaultAsync(ct);
				if (owner != null) return HospitalResult.Fail("This ID number already has a file: {0}", owner);
			}

			if (input.PriceListId is int plid && !await _db.HospitalPriceLists.AnyAsync(l => l.ID == plid && l.CompanyId == ctx.CompanyId && l.Status == HospitalPriceListStatus.Active, ct))
				return HospitalResult.Fail("Choose an approved price list");

			HospitalPatient? row;
			string action;
			if (input.Id is int id)
			{
				row = await _db.HospitalPatients.FirstOrDefaultAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
				action = HospitalPatientAccessActions.Update;
			}
			else
			{
				row = new HospitalPatient { CompanyId = ctx.CompanyId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.HospitalPatients.Add(row);
				action = HospitalPatientAccessActions.Create;
			}

			row.Name = name; row.NameEn = nameEn; row.Sex = input.Sex; row.BirthDate = input.BirthDate?.Date;
			row.NationalId = nid; row.NationalityId = input.NationalityId; row.Phone = phone; row.BloodGroup = blood;
			row.Allergies = Clean(input.Allergies, 1000); row.ChronicConditions = Clean(input.ChronicConditions, 1000);
			row.EmergencyName = Clean(input.EmergencyName, 200); row.EmergencyRelation = Clean(input.EmergencyRelation, 60); row.EmergencyPhone = ePhone;
			row.ConsentSigned = input.ConsentSigned;
			row.PriceListId = input.PriceListId;

			if (action == HospitalPatientAccessActions.Create)
			{
				// Two desks can reach the same next number; the unique index decides, the loser retries.
				for (int attempt = 0; ; attempt++)
				{
					row.Mrn = await NextMrnAsync(ctx, ct);
					try { await _db.SaveChangesAsync(ct); break; }
					catch (DbUpdateException) when (attempt < 3)
					{
						// The ID lost a race too: that is a real refusal, not a number clash to retry.
						if (await NidTakenAsync(ctx, nid, ct)) { _db.Entry(row).State = EntityState.Detached; return HospitalResult.Fail("This ID number already has a file: {0}", nid!); }
					}
				}
			}
			else await _db.SaveChangesAsync(ct);

			_db.HospitalPatientAccessLogs.Add(new HospitalPatientAccessLog
			{ CompanyId = ctx.CompanyId, PatientId = row.ID, EmployeeId = employeeId, Action = action, At = DateTime.UtcNow });
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// H3: attaches a stored photo to the file. The caller stores the bytes first (HospitalPatientPhotoService, on the
		/// document platform);
		/// a refused caller never reaches it, so nothing is written for them.
		public async Task<HospitalResult> SetPhotoAsync(BusinessContext ctx, int id, string photoPath, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanRegisterAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var row = await _db.HospitalPatients.FirstOrDefaultAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			if (row == null) return HospitalResult.Fail("not_found");
			row.PhotoPath = photoPath; row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
			_db.HospitalPatientAccessLogs.Add(new HospitalPatientAccessLog
			{ CompanyId = ctx.CompanyId, PatientId = row.ID, EmployeeId = employeeId, Action = HospitalPatientAccessActions.Update, At = DateTime.UtcNow });
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// The next MRN in the company: one past the highest number issued so far.
		public async Task<string> NextMrnAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var issued = await _db.HospitalPatients.AsNoTracking()
				.Where(p => p.CompanyId == ctx.CompanyId && p.Mrn.StartsWith(MrnPrefix))
				.Select(p => p.Mrn).ToListAsync(ct);
			int max = FirstMrnNumber - 1;
			foreach (var m in issued)
				if (int.TryParse(m.AsSpan(MrnPrefix.Length), out var n) && n > max) max = n;
			return MrnPrefix + (max + 1);
		}

		private Task<bool> NidTakenAsync(BusinessContext ctx, string? nid, CancellationToken ct)
			=> nid == null ? Task.FromResult(false) : _db.HospitalPatients.AnyAsync(p => p.CompanyId == ctx.CompanyId && p.NationalId == nid, ct);

		private static string? Clean(string? s, int max = int.MaxValue)
		{
			var v = (s ?? "").Trim();
			if (v.Length == 0) return null;
			return v.Length > max ? v[..max] : v;
		}

		private static string? CleanPhone(string? s)
		{
			var v = Clean(s);
			return v == null ? null : Regex.Replace(v, @"\s+", " ");
		}

		/// Whole years between the birth date and `today`.
		public static int? AgeOn(DateTime? birth, DateTime today)
		{
			if (birth is not DateTime b) return null;
			int age = today.Year - b.Year;
			if (b.Date > today.Date.AddYears(-age)) age--;
			return Math.Max(0, age);
		}
	}
}
