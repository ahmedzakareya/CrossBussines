using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H23 — the patient portal's booking rules: read (saved, else the defaults) and saved (`manage`).
	// A database without the tables reads as the defaults and refuses to save, so nothing else is disturbed.
	// ==========================================================================================

	public sealed class HospitalPortalClinicInput
	{
		public string? Mode { get; set; }
		public bool FollowUpOwnDoctor { get; set; }
		public bool ReferralForNew { get; set; }
		public bool Video { get; set; }
		public List<string>? VisitTypes { get; set; }
	}

	public sealed class HospitalPortalDoctorInput
	{
		public bool Online { get; set; } = true;
		public bool NewPatients { get; set; } = true;
	}

	public sealed record HospitalPortalDoctorRow(int PractitionerId, string Name, string NameEn, string? Title, string? TitleEn, int? DepartmentId, string? Photo, bool Online, bool NewPatients);

	public sealed class HospitalPortalRulesService
	{
		public const string NoTables = "The patient portal tables are not created yet (hospital_h23_portal_rules.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalPortalRulesService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> CanManage(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;

		/// The company's general rules, or the defaults (no row yet, or no table yet).
		public async Task<HospitalPortalSettings> SettingsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalPortalSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct) ?? new HospitalPortalSettings { CompanyId = ctx.CompanyId }; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalPortalSettings { CompanyId = ctx.CompanyId }; }
		}

		/// Whether the tables exist (the screen says so when they do not).
		public async Task<bool> ReadyAsync(CancellationToken ct = default)
		{
			try { await _db.Set<HospitalPortalSettings>().AsNoTracking().AnyAsync(ct); return true; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return false; }
		}

		/// Every active outpatient clinic with the rule it books by.
		public async Task<IReadOnlyList<HospitalPortalClinic>> ClinicsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var depts = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Code).ToListAsync(ct);
			Dictionary<int, HospitalPortalClinicRule> saved;
			try { saved = await _db.Set<HospitalPortalClinicRule>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).ToDictionaryAsync(r => r.DepartmentId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { saved = new(); }
			return depts.Select(d =>
			{
				var own = saved.TryGetValue(d.ID, out var r);
				var rule = own ? r! : HospitalPortalPolicy.Default(d.Code);
				return new HospitalPortalClinic(d.ID, d.Code, d.Name, d.NameEn ?? d.Name, rule.Mode, rule.FollowUpOwnDoctor, rule.ReferralForNew, rule.Video, HospitalPortalPolicy.ParseTypes(rule.VisitTypes), own);
			}).ToList();
		}

		/// Every active practitioner with their online flags (both on when never set), and their photo by face.
		public async Task<IReadOnlyList<HospitalPortalDoctorRow>> DoctorsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive).OrderBy(p => p.DepartmentId).ThenBy(p => p.Name).ToListAsync(ct);
			Dictionary<int, HospitalPortalDoctorRule> saved;
			try { saved = await _db.Set<HospitalPortalDoctorRule>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).ToDictionaryAsync(r => r.PractitionerId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { saved = new(); }
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, docs.Where(d => d.EmployeeId != null).Select(d => d.EmployeeId!.Value), ct);
			return docs.Select(d =>
			{
				var r = saved.GetValueOrDefault(d.ID);
				return new HospitalPortalDoctorRow(d.ID, d.Name, d.NameEn, d.Title, d.TitleEn, d.DepartmentId, d.EmployeeId is int e ? photos.GetValueOrDefault(e) : null, r?.Online ?? true, r?.NewPatients ?? true);
			}).ToList();
		}

		public async Task<HospitalResult> SaveSettingsAsync(BusinessContext ctx, HospitalPortalSettings input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalPortalPolicy.SettingsRefusal(input) is string why) return HospitalResult.Fail(why);
			if (HospitalPortalPayLink.Refusal(input.PayLinkTemplate) is string payWhy) return HospitalResult.Fail(payWhy);   // H251
			try
			{
				var row = await _db.Set<HospitalPortalSettings>().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct);
				if (row == null) { row = new HospitalPortalSettings { CompanyId = ctx.CompanyId }; _db.Add(row); }
				row.WindowDays = input.WindowDays; row.LeadHours = input.LeadHours; row.CancelHours = input.CancelHours; row.MaxActive = input.MaxActive; row.OfferMinutes = input.OfferMinutes;
				row.FamilyBooking = input.FamilyBooking; row.Waitlist = input.Waitlist; row.SelfRegistration = input.SelfRegistration; row.ShowFees = input.ShowFees;
				row.InsuranceCheck = input.InsuranceCheck; row.Payment = input.Payment; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				row.PayLinkTemplate = string.IsNullOrWhiteSpace(input.PayLinkTemplate) ? null : input.PayLinkTemplate.Trim();   // H251
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
		}

		public async Task<HospitalResult> SaveClinicAsync(BusinessContext ctx, int departmentId, HospitalPortalClinicInput input, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalDepartments.AnyAsync(d => d.ID == departmentId && d.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var types = HospitalPortalPolicy.ParseTypes(string.Join(",", input.VisitTypes ?? new()));
			if (HospitalPortalPolicy.ClinicRefusal(input.Mode, types, input.Video) is string why) return HospitalResult.Fail(why);
			try
			{
				var row = await _db.Set<HospitalPortalClinicRule>().FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.DepartmentId == departmentId, ct);
				if (row == null) { row = new HospitalPortalClinicRule { CompanyId = ctx.CompanyId, DepartmentId = departmentId }; _db.Add(row); }
				row.Mode = input.Mode!; row.FollowUpOwnDoctor = input.FollowUpOwnDoctor; row.ReferralForNew = input.ReferralForNew; row.Video = input.Video;
				row.VisitTypes = HospitalPortalPolicy.FormatTypes(types);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(departmentId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
		}

		public async Task<HospitalResult> SaveDoctorAsync(BusinessContext ctx, int practitionerId, HospitalPortalDoctorInput input, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPractitioners.AnyAsync(p => p.ID == practitionerId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var row = await _db.Set<HospitalPortalDoctorRule>().FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.PractitionerId == practitionerId, ct);
				if (row == null) { row = new HospitalPortalDoctorRule { CompanyId = ctx.CompanyId, PractitionerId = practitionerId }; _db.Add(row); }
				row.Online = input.Online; row.NewPatients = input.NewPatients;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(practitionerId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
		}
	}
}
