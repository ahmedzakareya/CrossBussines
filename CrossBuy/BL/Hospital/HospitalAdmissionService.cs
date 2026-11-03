using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL ADMISSION SERVICE — accommodating a patient in a bed, transferring, discharging.
	//
	// The rules a bed must pass before a patient lies in it, enforced HERE (the screen only shows
	// the beds that pass, for speed):
	//   · the bed is Vacant, active, in a room that carries beds, in the caller's company;
	//   · the patient has no open admission;
	//   · a ward for one sex takes only that sex;
	//   · a shared room whose type is "same sex only" takes the sex already in it.
	// The database backs the two a race could break (one open admission per patient, one open
	// stay per bed); a lost race comes back as a refusal, never as two patients in one bed.
	// Every write asks HospitalAccessService for `admit` itself.
	// ==========================================================================================

	public sealed class HospitalAdmitInput
	{
		public int PatientId { get; set; }
		public int BedId { get; set; }
		public string? Diagnosis { get; set; }
	}

	/// The patient lying in a bed, as every plan shows them.
	public sealed record HospitalOccupantView(int PatientId, int AdmissionId, string Mrn, string Name, string NameEn, string Sex,
		DateTime? BirthDate, string? PhotoPath, DateTime AdmittedAt, string? Diagnosis, bool HasAllergy);

	/// A bed the patient may take, and the room it is in.
	public sealed record HospitalBedOption(int BedId, string BedCode, int RoomId, string RoomNumber, string? WardName, string? WardNameEn,
		int? TypeId, string? TypeName, string? TypeNameEn, decimal DailyRate);

	public sealed record HospitalStayView(int AdmissionId, string RoomNumber, string BedCode, DateTime StartedAt, DateTime? EndedAt, string? EndReason, string Status, string? Diagnosis);

	public sealed class HospitalAdmissionService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService? _billing;

		public HospitalAdmissionService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService? billing = null)
		{ _db = db; _access = access; _billing = billing; }

		private Task<bool> CanAdmitAsync(BusinessContext ctx, CancellationToken ct)
			=> _access.CanAsync(ctx, HospitalActions.Admit, null, ct);

		// ====================================================================================
		// READS
		// ====================================================================================

		/// Who lies in which bed, for every open stay in the company: bed id → occupant.
		public async Task<Dictionary<int, HospitalOccupantView>> OccupantsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await (from s in _db.HospitalBedStays.AsNoTracking()
							  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							  join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && a.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
							  select new { s.BedId, View = new HospitalOccupantView(p.ID, a.ID, p.Mrn, p.Name, p.NameEn, p.Sex, p.BirthDate, p.PhotoPath, a.AdmittedAt, a.Diagnosis, p.Allergies != null && p.Allergies != "") })
							 .ToListAsync(ct);
			return rows.ToDictionary(r => r.BedId, r => r.View);
		}

		/// The patient's open admission and the bed it is in, or null.
		public async Task<(HospitalAdmission Admission, HospitalBedStay Stay, string RoomNumber, string BedCode)?> CurrentAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			var row = await (from a in _db.HospitalAdmissions.AsNoTracking()
							 join s in _db.HospitalBedStays.AsNoTracking() on a.ID equals s.AdmissionId
							 join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							 join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							 where a.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.Status == HospitalAdmissionStatus.Active && s.EndedAt == null
							 select new { a, s, r.Number, b.Code }).FirstOrDefaultAsync(ct);
			return row == null ? null : (row.a, row.s, row.Number, row.Code);
		}

		public async Task<IReadOnlyList<HospitalStayView>> StaysAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
			=> await (from s in _db.HospitalBedStays.AsNoTracking()
					  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
					  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
					  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
					  where s.CompanyId == ctx.CompanyId && s.PatientId == patientId
					  orderby s.StartedAt descending, s.ID descending
					  select new HospitalStayView(a.ID, r.Number, b.Code, s.StartedAt, s.EndedAt, s.EndReason, a.Status, a.Diagnosis))
					 .ToListAsync(ct);

		/// Every bed this patient may take now, with its room — the accommodation screen's list.
		public async Task<IReadOnlyList<HospitalBedOption>> SuitableBedsAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			if (patient == null) return Array.Empty<HospitalBedOption>();
			var candidates = await CandidatesAsync(ctx, null, ct);
			var result = new List<HospitalBedOption>();
			foreach (var c in candidates)
				if (await RefusalAsync(ctx, patient, c.Bed, c.Room, c.Ward, c.Type, null, ct) == null)
					result.Add(new HospitalBedOption(c.Bed.ID, c.Bed.Code, c.Room.ID, c.Room.Number, c.Ward?.Name, c.Ward?.NameEn, c.Type?.ID, c.Type?.Name, c.Type?.NameEn, c.Type?.DailyRate ?? 0));
			return result;
		}

		private sealed record Candidate(HospitalBed Bed, HospitalRoom Room, HospitalWard? Ward, HospitalRoomType? Type);

		private async Task<List<Candidate>> CandidatesAsync(BusinessContext ctx, int? bedId, CancellationToken ct)
		{
			var q = from b in _db.HospitalBeds.AsNoTracking()
					join r in _db.HospitalRooms.AsNoTracking() on b.RoomId equals r.ID
					join w0 in _db.HospitalWards.AsNoTracking() on r.WardId equals (int?)w0.ID into ws
					from w in ws.DefaultIfEmpty()
					join t0 in _db.HospitalRoomTypes.AsNoTracking() on r.RoomTypeId equals (int?)t0.ID into ts
					from t in ts.DefaultIfEmpty()
					where b.CompanyId == ctx.CompanyId && r.CompanyId == ctx.CompanyId && b.IsActive && r.IsActive
					select new { b, r, w, t };
			if (bedId is int id) q = q.Where(x => x.b.ID == id);
			var rows = await q.OrderBy(x => x.r.Number).ThenBy(x => x.b.Code).ToListAsync(ct);
			return rows.Select(x => new Candidate(x.b, x.r, x.w, x.t)).ToList();
		}

		/// Why this patient may not take this bed, or null when they may. `movingAdmissionId` is the
		/// admission being transferred, so the patient does not count against their own room.
		private async Task<HospitalResult?> RefusalAsync(BusinessContext ctx, HospitalPatient patient, HospitalBed bed, HospitalRoom room,
			HospitalWard? ward, HospitalRoomType? type, int? movingAdmissionId, CancellationToken ct)
		{
			if (!HospitalRoomKinds.CarriesBeds(room.Kind)) return HospitalResult.Fail("This space carries no beds");
			if (bed.Status != HospitalBedStatus.Vacant) return HospitalResult.Fail("Bed {0} is not vacant", bed.Code);
			if (ward != null && ward.SexPolicy != HospitalSexPolicy.Any && !SameSex(ward.SexPolicy, patient.Sex))
				return HospitalResult.Fail("Ward {0} takes the other sex", ward.Name);
			if (type is { SameSexOnly: true })
			{
				int moving = movingAdmissionId ?? 0;
				var others = await (from s in _db.HospitalBedStays.AsNoTracking()
									join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
									where s.CompanyId == ctx.CompanyId && s.RoomId == room.ID && s.EndedAt == null && s.AdmissionId != moving
									select p.Sex).Distinct().ToListAsync(ct);
				if (others.Any(s => s != patient.Sex)) return HospitalResult.Fail("Room {0} is shared with the other sex", room.Number);
			}
			return null;
		}

		private static bool SameSex(string wardPolicy, string patientSex)
			=> (wardPolicy == HospitalSexPolicy.Male && patientSex == HospitalPatientSex.Male)
			|| (wardPolicy == HospitalSexPolicy.Female && patientSex == HospitalPatientSex.Female);

		// ====================================================================================
		// WRITES
		// ====================================================================================

		public async Task<HospitalResult> AdmitAsync(BusinessContext ctx, HospitalAdmitInput input, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await CanAdmitAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			if (patient == null) return HospitalResult.Fail("not_found");
			if (await _db.HospitalAdmissions.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.PatientId == patient.ID && a.Status == HospitalAdmissionStatus.Active, ct))
				return HospitalResult.Fail("This patient is already admitted");
			var c = (await CandidatesAsync(ctx, input.BedId, ct)).FirstOrDefault();
			if (c == null) return HospitalResult.Fail("not_found");
			var refusal = await RefusalAsync(ctx, patient, c.Bed, c.Room, c.Ward, c.Type, null, ct);
			if (refusal != null) return refusal;

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			var admission = new HospitalAdmission
			{
				CompanyId = ctx.CompanyId, PatientId = patient.ID, Status = HospitalAdmissionStatus.Active, AdmittedAt = now,
				Diagnosis = Clean(input.Diagnosis, 400), CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId,
			};
			_db.HospitalAdmissions.Add(admission);
			if (!await TrySaveAsync(ct)) return HospitalResult.Fail("This patient is already admitted");
			_db.HospitalBedStays.Add(new HospitalBedStay { CompanyId = ctx.CompanyId, AdmissionId = admission.ID, PatientId = patient.ID, BedId = c.Bed.ID, RoomId = c.Room.ID, StartedAt = now, CreatedByEmployeeId = employeeId });
			if (!await SetBedAsync(ctx, c.Bed.ID, HospitalBedStatus.Occupied, employeeId, ct) || !await TrySaveAsync(ct))
				return HospitalResult.Fail("Bed {0} is not vacant", c.Bed.Code);   // lost the race for the bed: nothing commits
			await tx.CommitAsync(ct);
			// The stay's bill opens with it; accommodation is counted from the bed stays as the nights pass.
			if (_billing != null) await _billing.EnsureAdmissionBillAsync(ctx, admission.ID, employeeId, now, ct);
			return HospitalResult.Success(admission.ID);
		}

		public async Task<HospitalResult> TransferAsync(BusinessContext ctx, int admissionId, int bedId, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await CanAdmitAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var admission = await _db.HospitalAdmissions.FirstOrDefaultAsync(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active, ct);
			if (admission == null) return HospitalResult.Fail("not_found");
			var stay = await _db.HospitalBedStays.FirstOrDefaultAsync(s => s.AdmissionId == admission.ID && s.CompanyId == ctx.CompanyId && s.EndedAt == null, ct);
			if (stay == null) return HospitalResult.Fail("not_found");
			if (stay.BedId == bedId) return HospitalResult.Fail("The patient is already in this bed");
			var patient = await _db.HospitalPatients.AsNoTracking().FirstAsync(p => p.ID == admission.PatientId && p.CompanyId == ctx.CompanyId, ct);
			var c = (await CandidatesAsync(ctx, bedId, ct)).FirstOrDefault();
			if (c == null) return HospitalResult.Fail("not_found");
			var refusal = await RefusalAsync(ctx, patient, c.Bed, c.Room, c.Ward, c.Type, admission.ID, ct);
			if (refusal != null) return refusal;

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			stay.EndedAt = now; stay.EndReason = HospitalStayEndReasons.Transfer;
			await SetBedAsync(ctx, stay.BedId, HospitalBedStatus.Cleaning, employeeId, ct);
			if (!await TrySaveAsync(ct)) return HospitalResult.Fail("The change could not be saved");
			_db.HospitalBedStays.Add(new HospitalBedStay { CompanyId = ctx.CompanyId, AdmissionId = admission.ID, PatientId = patient.ID, BedId = c.Bed.ID, RoomId = c.Room.ID, StartedAt = now, CreatedByEmployeeId = employeeId });
			if (!await SetBedAsync(ctx, c.Bed.ID, HospitalBedStatus.Occupied, employeeId, ct) || !await TrySaveAsync(ct))
				return HospitalResult.Fail("Bed {0} is not vacant", c.Bed.Code);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(admission.ID);
		}

		public async Task<HospitalResult> DischargeAsync(BusinessContext ctx, int admissionId, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await CanAdmitAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var admission = await _db.HospitalAdmissions.FirstOrDefaultAsync(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active, ct);
			if (admission == null) return HospitalResult.Fail("not_found");
			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			var stay = await _db.HospitalBedStays.FirstOrDefaultAsync(s => s.AdmissionId == admission.ID && s.CompanyId == ctx.CompanyId && s.EndedAt == null, ct);
			if (stay != null)
			{
				stay.EndedAt = now; stay.EndReason = HospitalStayEndReasons.Discharge;
				await SetBedAsync(ctx, stay.BedId, HospitalBedStatus.Cleaning, employeeId, ct);   // housekeeping turns it back to Vacant
			}
			admission.Status = HospitalAdmissionStatus.Discharged; admission.DischargedAt = now; admission.DischargedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(admission.ID);
		}

		private async Task<bool> SetBedAsync(BusinessContext ctx, int bedId, string status, int? employeeId, CancellationToken ct)
		{
			var bed = await _db.HospitalBeds.FirstOrDefaultAsync(b => b.ID == bedId && b.CompanyId == ctx.CompanyId, ct);
			if (bed == null) return false;
			if (status == HospitalBedStatus.Occupied && bed.Status != HospitalBedStatus.Vacant) return false;
			bed.Status = status; bed.UpdatedAt = DateTime.UtcNow; bed.UpdatedByEmployeeId = employeeId;
			return true;
		}

		private async Task<bool> TrySaveAsync(CancellationToken ct)
		{
			try { await _db.SaveChangesAsync(ct); return true; }
			catch (DbUpdateException) { return false; }
		}

		private static string? Clean(string? s, int max)
		{
			var v = (s ?? "").Trim();
			return v.Length == 0 ? null : v.Length > max ? v[..max] : v;
		}
	}
}

namespace CrossBuy.BL.Hospital
{
	/// The occupant as a room plan draws them (JSON for hospital-plan.js): name, photo or initials, the
	/// link to their file. One shape for the board, the room page, the room types and the designer.
	public static class HospitalPlanPatient
	{
		public static object? Of(IReadOnlyDictionary<int, HospitalOccupantView> occupants, int bedId)
		{
			if (!occupants.TryGetValue(bedId, out var o)) return null;
			var parts = o.NameEn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return new
			{
				id = o.PatientId, admissionId = o.AdmissionId, mrn = o.Mrn, name = DisplayName.Of(o.Name, o.NameEn),
				first = DisplayName.Of(o.Name, o.NameEn).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "",
				initials = ((parts.Length > 0 ? parts[0][..1] : "") + (parts.Length > 1 ? parts[1][..1] : "")).ToUpperInvariant(),
				photo = o.PhotoPath, sex = o.Sex, age = HospitalPatientService.AgeOn(o.BirthDate, DateTime.UtcNow),
				since = o.AdmittedAt.ToString("o"), diagnosis = o.Diagnosis, allergy = o.HasAllergy,
			};
		}
	}
}
