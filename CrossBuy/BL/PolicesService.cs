using AutoMapper;
using AutoMapper.QueryableExtensions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class PolicesService : IPolicesService
	{
		private readonly CrossDbContext _context;
		private readonly IMapper _mapper;
		private readonly IServiceProvider _serviceProvider;
		private readonly IHttpContextAccessor _httpContextAccessor;


		public PolicesService(CrossDbContext context, IMapper mapper, IServiceProvider serviceProvider, IHttpContextAccessor httpContextAccessor)
		{
			this._context = context;
			_serviceProvider = serviceProvider;
			//_context = context;
			_mapper = mapper;
			_httpContextAccessor = httpContextAccessor;
		}
		public async Task<List<LeaveTypesDto>> GetAllLeaveTypesAsync()
		{
			return await _context.LeaveTypes
			.ProjectTo<LeaveTypesDto>(_mapper.ConfigurationProvider).ToListAsync();

		}

        public async Task<List<PoliciesDto>> GetAllPoliciesAsync()
        {
            var policies = await _context.Policies
                .Include(p => p.LeavePolicies)
                    .ThenInclude(lp => lp.leaveTypes)
                .Include(p => p.salaryPolicies)
                .Include(p => p.AttendancePolicies)
                .ToListAsync();

            var result = policies.Select(p => new PoliciesDto
            {
                ID = p.ID,
                NameAr = p.NameAr,
                NameEn = p.NameEn,
                Notes = p.Notes,

                LeavePolicies = p.LeavePolicies?.Select(lp => new LeavePoliciesDto
                {
                    ID = lp.ID,
                    LeavePolicyTypeID = lp.LeavePolicyTypeID,
                    EntitlementDaysPerYear = lp.EntitlementDaysPerYear,
                    CarryOverLimit = lp.CarryOverLimit,
                    NoticePeriodDays = lp.NoticePeriodDays,
                    RequiresMedicalCertificate = lp.RequiresMedicalCertificate,
                    leaveTypes = lp.leaveTypes == null ? null : new LeaveTypesDto
                    {
                        ID = lp.leaveTypes.ID,
                        NameAr = lp.leaveTypes.NameAr,
                        NameEn = lp.leaveTypes.NameEn,
                        Notes = lp.leaveTypes.Notes,
                    }
                }).ToList(),

                SalaryPolicies = p.salaryPolicies?.Select(s => new SalaryPoliciesDto
                {
                    ID = s.ID,
                    LeavePolicyTypeID = s.LeavePolicyTypeID,
                    Description = s.Description,
                    BaseSalary = s.BaseSalary,
                    HousingAllowance = s.HousingAllowance,
                    TransportationAllowance = s.TransportationAllowance,
                    OtherAllowances = s.OtherAllowances,
                    OvertimeRate = s.OvertimeRate,
                    LatePenaltyPerMinute = s.LatePenaltyPerMinute,
                    AbsencePenaltyPerDay = s.AbsencePenaltyPerDay,
                    SocialInsuranceEmployeeShare = s.SocialInsuranceEmployeeShare,
                    SocialInsuranceCompanyShare = s.SocialInsuranceCompanyShare,
                    TaxRate = s.TaxRate,
                    IsTaxApplicable = s.IsTaxApplicable,
                    PaymentDay = s.PaymentDay,
                    PaymentMethod = s.PaymentMethod,
                }).ToList(),

                AttendancePolicies = p.AttendancePolicies?.Select(a => new AttendancePoliciesDto
                {
                    ID = a.ID,
                    LeavePolicyTypeID = a.LeavePolicyTypeID,

                    AllowedGraceMinutes = a.AllowedGraceMinutes,
                    WarningThresholdCount = a.WarningThresholdCount,
                    DeductionRatePerOccurrence = a.DeductionRatePerOccurrence,
                    UnexcusedAbsenceThreshold = a.UnexcusedAbsenceThreshold,

                    WorkStartTime = a.WorkStartTime,
                    WorkEndTime = a.WorkEndTime,
                    WorkHoursPerDay = a.WorkHoursPerDay,

                    BreakStartTime = a.BreakStartTime,
                    BreakEndTime = a.BreakEndTime,
                    BreakDurationMinutes = a.BreakDurationMinutes,

                    WorkOnSunday = a.WorkOnSunday,
                    WorkOnMonday = a.WorkOnMonday,
                    WorkOnTuesday = a.WorkOnTuesday,
                    WorkOnWednesday = a.WorkOnWednesday,
                    WorkOnThursday = a.WorkOnThursday,
                    WorkOnFriday = a.WorkOnFriday,
                    WorkOnSaturday = a.WorkOnSaturday,

                    AllowPermissions = a.AllowPermissions,

                    MaxPermissionRequestsPerDay = a.MaxPermissionRequestsPerDay,
                    MaxPermissionRequestsPerWeek = a.MaxPermissionRequestsPerWeek,
                    MaxPermissionRequestsPerMonth = a.MaxPermissionRequestsPerMonth,

                    MaxPermissionMinutesPerRequest = a.MaxPermissionMinutesPerRequest,
                    MaxPermissionMinutesPerDay = a.MaxPermissionMinutesPerDay,
                    MaxPermissionMinutesPerWeek = a.MaxPermissionMinutesPerWeek,
                    MaxPermissionMinutesPerMonth = a.MaxPermissionMinutesPerMonth,

                    RequirePermissionApproval = a.RequirePermissionApproval,

                    RejectPermissionIfExceeded = a.RejectPermissionIfExceeded,
                    DeductPermissionIfExceeded = a.DeductPermissionIfExceeded,

                    AllowLateArrivalPermission = a.AllowLateArrivalPermission,
                    AllowEarlyLeavePermission = a.AllowEarlyLeavePermission,
                    AllowDuringWorkPermission = a.AllowDuringWorkPermission,

                    LinkPermissionWithFingerprint = a.LinkPermissionWithFingerprint,

                    PermissionDeductionRatePerMinute = a.PermissionDeductionRatePerMinute

                }).ToList(),

            }).ToList();

            return result;
        }


        public async Task<AttendancePoliciesDto> SaveAttendancePolicyAsync(AttendancePoliciesDto model, CancellationToken ct = default)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var parentPolicy = await _context.Policies
                .FindAsync(new object?[] { model.LeavePolicyTypeID }, ct);

            if (parentPolicy == null)
                throw new KeyNotFoundException("السياسة الرئيسية غير موجودة.");

            AttendancePolicies entity;

            if (model.ID == 0)
            {
                entity = new AttendancePolicies();
                _context.AttendancePolicies.Add(entity);
            }
            else
            {
                entity = await _context.AttendancePolicies
                    .FindAsync(new object?[] { model.ID }, ct);

                if (entity == null)
                    throw new KeyNotFoundException("لائحة الحضور والانصراف غير موجودة.");
            }

            entity.LeavePolicyTypeID = model.LeavePolicyTypeID;

            entity.AllowedGraceMinutes = model.AllowedGraceMinutes;
            entity.WarningThresholdCount = model.WarningThresholdCount;
            entity.DeductionRatePerOccurrence = model.DeductionRatePerOccurrence;
            entity.UnexcusedAbsenceThreshold = model.UnexcusedAbsenceThreshold;

            entity.WorkStartTime = model.WorkStartTime;
            entity.WorkEndTime = model.WorkEndTime;
            entity.WorkHoursPerDay = model.WorkHoursPerDay;

            entity.BreakStartTime = model.BreakStartTime;
            entity.BreakEndTime = model.BreakEndTime;
            entity.BreakDurationMinutes = model.BreakDurationMinutes;

            entity.WorkOnSunday = model.WorkOnSunday;
            entity.WorkOnMonday = model.WorkOnMonday;
            entity.WorkOnTuesday = model.WorkOnTuesday;
            entity.WorkOnWednesday = model.WorkOnWednesday;
            entity.WorkOnThursday = model.WorkOnThursday;
            entity.WorkOnFriday = model.WorkOnFriday;
            entity.WorkOnSaturday = model.WorkOnSaturday;

            entity.AllowPermissions = model.AllowPermissions;

            entity.MaxPermissionRequestsPerDay = model.MaxPermissionRequestsPerDay;
            entity.MaxPermissionRequestsPerWeek = model.MaxPermissionRequestsPerWeek;
            entity.MaxPermissionRequestsPerMonth = model.MaxPermissionRequestsPerMonth;

            entity.MaxPermissionMinutesPerRequest = model.MaxPermissionMinutesPerRequest;
            entity.MaxPermissionMinutesPerDay = model.MaxPermissionMinutesPerDay;
            entity.MaxPermissionMinutesPerWeek = model.MaxPermissionMinutesPerWeek;
            entity.MaxPermissionMinutesPerMonth = model.MaxPermissionMinutesPerMonth;

            entity.RequirePermissionApproval = model.RequirePermissionApproval;

            entity.RejectPermissionIfExceeded = model.RejectPermissionIfExceeded;
            entity.DeductPermissionIfExceeded = model.DeductPermissionIfExceeded;

            entity.AllowLateArrivalPermission = model.AllowLateArrivalPermission;
            entity.AllowEarlyLeavePermission = model.AllowEarlyLeavePermission;
            entity.AllowDuringWorkPermission = model.AllowDuringWorkPermission;

            entity.LinkPermissionWithFingerprint = model.LinkPermissionWithFingerprint;
            entity.PermissionDeductionRatePerMinute = model.PermissionDeductionRatePerMinute;

            await _context.SaveChangesAsync(ct);

            model.ID = entity.ID;
            return model;
        }
        public async Task<LeavePoliciesDto> SaveLeavePolicyAsync(LeavePoliciesDto model, CancellationToken ct = default)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var parentPolicy = await _context.Policies
                .FindAsync(new object?[] { model.LeavePolicyTypeID }, ct);

            if (parentPolicy == null)
                throw new KeyNotFoundException("السياسة الرئيسية غير موجودة.");

            LeavePolicies entity;

            if (model.ID == 0)
            {
                // ── Create: تحقق إن النوع مش موجود خالص في نفس السياسة ──
                var exists = await _context.LeavePolicies
                    .AnyAsync(lp => lp.LeavePolicyTypeID == model.LeavePolicyTypeID
                                 && lp.LeaveTypeID == model.LeaveTypeID, ct);

                if (exists)
                    throw new InvalidOperationException("نوع الإجازة هذا مضاف بالفعل لهذه السياسة.");

                entity = new LeavePolicies
                {
                    LeavePolicyTypeID = model.LeavePolicyTypeID,
                    LeaveTypeID = model.LeaveTypeID.GetValueOrDefault(),
                    EntitlementDaysPerYear = model.EntitlementDaysPerYear,
                    CarryOverLimit = model.CarryOverLimit,
                    NoticePeriodDays = model.NoticePeriodDays,
                    RequiresMedicalCertificate = model.RequiresMedicalCertificate
                };

                _context.LeavePolicies.Add(entity);
            }
            else
            {
                // ── Update: تحقق إن النوع مش موجود في سجل تاني غير السجل الحالي ──
                var exists = await _context.LeavePolicies
                    .AnyAsync(lp => lp.LeavePolicyTypeID == model.LeavePolicyTypeID
                                 && lp.LeaveTypeID == model.LeaveTypeID
                                 && lp.ID != model.ID, ct);

                if (exists)
                    throw new InvalidOperationException("نوع الإجازة هذا مضاف بالفعل في سجل آخر، لا يمكن التعديل.");

                entity = await _context.LeavePolicies
                    .FindAsync(new object?[] { model.ID }, ct);

                if (entity == null)
                    throw new KeyNotFoundException("لائحة الإجازة غير موجودة.");

                entity.LeavePolicyTypeID = model.LeavePolicyTypeID;
                entity.LeaveTypeID = model.LeaveTypeID.GetValueOrDefault();
                entity.EntitlementDaysPerYear = model.EntitlementDaysPerYear;
                entity.CarryOverLimit = model.CarryOverLimit;
                entity.NoticePeriodDays = model.NoticePeriodDays;
                entity.RequiresMedicalCertificate = model.RequiresMedicalCertificate;
            }

            await _context.SaveChangesAsync(ct);

            return new LeavePoliciesDto
            {
                ID = entity.ID,
                LeavePolicyTypeID = entity.LeavePolicyTypeID,
                LeaveTypeID = entity.LeaveTypeID,
                EntitlementDaysPerYear = entity.EntitlementDaysPerYear,
                CarryOverLimit = entity.CarryOverLimit,
                NoticePeriodDays = entity.NoticePeriodDays,
                RequiresMedicalCertificate = entity.RequiresMedicalCertificate
            };
        }
        public async Task<List<LeaveTypesDto>> GetLeaveTypesAsync(CancellationToken ct = default)
        {
            return await _context.LeaveTypes
                .Select(lt => new LeaveTypesDto
                {
                    ID = lt.ID,
                    NameAr = lt.NameAr,
                    NameEn = lt.NameEn,
                    Notes = lt.Notes
                })
                .ToListAsync(ct);
        }
        public async Task<LeaveTypesDto> GetLeaveTypeByIDAsync(int ID)
		{
			var leaveType = await _context.LeaveTypes
				.Where(x => x.ID == ID)
				.ProjectTo<LeaveTypesDto>(_mapper.ConfigurationProvider)
				.FirstOrDefaultAsync();

			if (leaveType == null)
				throw new Exception("Leave type not found.");

			return leaveType;
		}


		public async Task<LeaveTypesDto> SaveLeaveType(LeaveTypesDto model)
		{
			LeaveTypes entity;

			if (model.ID == 0)
			{
				entity = new LeaveTypes();
				_context.LeaveTypes.Add(entity);
			}
			else
			{
				entity = await _context.LeaveTypes.FindAsync(model.ID);

				if (entity == null)
					throw new Exception("Leave Type not found.");
			}

			entity.NameAr = model.NameAr;
			entity.NameEn = model.NameEn;
			entity.Notes = model.Notes;

			await _context.SaveChangesAsync();

			return new LeaveTypesDto
			{
				ID = entity.ID,
				NameAr = entity.NameAr,
				NameEn = entity.NameEn,
				Notes = entity.Notes
			};
		}


        // Service/Repository layer
        public async Task<PoliciesDto> SavePolicyAsync(PoliciesDto model, CancellationToken ct = default)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            // Trim basic fields
            var nameAr = model.NameAr?.Trim();
            var nameEn = model.NameEn?.Trim();
            var notes = model.Notes?.Trim();

            // Optional: basic guard
            if (string.IsNullOrWhiteSpace(nameAr) || string.IsNullOrWhiteSpace(nameEn))
                throw new InvalidOperationException("Arabic and English names are required.");

            Policies entity;

            if (model.ID == 0)
            {
                // Create
                entity = new Policies
                {
                    NameAr = nameAr,
                    NameEn = nameEn,
                    Notes = notes
                    // Collections (LeavePolicies / AttendancePolicies / salaryPolicies) will be handled in dedicated methods later
                };

                _context.Policies.Add(entity);
            }
            else
            {
                // Update
                entity = await _context.Policies.FindAsync(new object?[] { model.ID }, ct);
                if (entity == null)
                    throw new KeyNotFoundException("Policy not found.");

                entity.NameAr = nameAr;
                entity.NameEn = nameEn;
                entity.Notes = notes;
                // Collections update is out of scope for now
            }

            await _context.SaveChangesAsync(ct);

            // Map back to DTO
            return new CrossBuy.ViewModel.PoliciesDto
            {
                ID = entity.ID,
                NameAr = entity.NameAr,
                NameEn = entity.NameEn,
                Notes = entity.Notes

                // LeavePolicies / AttendancePolicies / SalaryPolicies mapping can be added later when needed
            };
        }

        // ── GET ──
        public async Task<SalaryPoliciesDto?> GetSalaryPolicyAsync(int id, CancellationToken ct = default)
        {
            var entity = await _context.SalaryPolicies.FindAsync(new object?[] { id }, ct);
            if (entity == null) return null;

            return new SalaryPoliciesDto
            {
                ID = entity.ID,
                LeavePolicyTypeID = entity.LeavePolicyTypeID,
                Description = entity.Description,
                BaseSalary = entity.BaseSalary,
                HousingAllowance = entity.HousingAllowance,
                TransportationAllowance = entity.TransportationAllowance,
                OtherAllowances = entity.OtherAllowances,
                OvertimeRate = entity.OvertimeRate,
                LatePenaltyPerMinute = entity.LatePenaltyPerMinute,
                AbsencePenaltyPerDay = entity.AbsencePenaltyPerDay,
                SocialInsuranceEmployeeShare = entity.SocialInsuranceEmployeeShare,
                SocialInsuranceCompanyShare = entity.SocialInsuranceCompanyShare,
                TaxRate = entity.TaxRate,
                IsTaxApplicable = entity.IsTaxApplicable,
                PaymentDay = entity.PaymentDay,
                PaymentMethod = entity.PaymentMethod
            };
        }

        // ── SAVE (Create / Update) ──

        public async Task<SalaryPoliciesDto> SaveSalaryPolicyAsync(SalaryPoliciesDto model, CancellationToken ct = default)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            // تأكد إن الـ Policy الرئيسية موجودة
            var parentPolicy = await _context.Policies.FindAsync(new object?[] { model.LeavePolicyTypeID }, ct);
            if (parentPolicy == null)
                throw new KeyNotFoundException("Parent policy not found.");

            SalaryPolicies entity;

            if (model.ID == 0)
            {
                // ── Create ──
                entity = new SalaryPolicies
                {
                    LeavePolicyTypeID = model.LeavePolicyTypeID,
                    Description = model.Description?.Trim(),
                    BaseSalary = model.BaseSalary,
                    HousingAllowance = model.HousingAllowance,
                    TransportationAllowance = model.TransportationAllowance,
                    OtherAllowances = model.OtherAllowances,
                    OvertimeRate = model.OvertimeRate,
                    LatePenaltyPerMinute = model.LatePenaltyPerMinute,
                    AbsencePenaltyPerDay = model.AbsencePenaltyPerDay,
                    SocialInsuranceEmployeeShare = model.SocialInsuranceEmployeeShare,
                    SocialInsuranceCompanyShare = model.SocialInsuranceCompanyShare,
                    TaxRate = model.TaxRate,
                    IsTaxApplicable = model.IsTaxApplicable,
                    PaymentDay = model.PaymentDay,
                    PaymentMethod = model.PaymentMethod?.Trim() ?? string.Empty
                };

                _context.SalaryPolicies.Add(entity);
            }
            else
            {
                // ── Update ──
                entity = await _context.SalaryPolicies.FindAsync(new object?[] { model.ID }, ct);
                if (entity == null)
                    throw new KeyNotFoundException("Salary policy not found.");

                entity.LeavePolicyTypeID = model.LeavePolicyTypeID;
                entity.Description = model.Description?.Trim();
                entity.BaseSalary = model.BaseSalary;
                entity.HousingAllowance = model.HousingAllowance;
                entity.TransportationAllowance = model.TransportationAllowance;
                entity.OtherAllowances = model.OtherAllowances;
                entity.OvertimeRate = model.OvertimeRate;
                entity.LatePenaltyPerMinute = model.LatePenaltyPerMinute;
                entity.AbsencePenaltyPerDay = model.AbsencePenaltyPerDay;
                entity.SocialInsuranceEmployeeShare = model.SocialInsuranceEmployeeShare;
                entity.SocialInsuranceCompanyShare = model.SocialInsuranceCompanyShare;
                entity.TaxRate = model.TaxRate;
                entity.IsTaxApplicable = model.IsTaxApplicable;
                entity.PaymentDay = model.PaymentDay;
                entity.PaymentMethod = model.PaymentMethod?.Trim() ?? string.Empty;
            }

            await _context.SaveChangesAsync(ct);

            // Map back to DTO
            return new SalaryPoliciesDto
            {
                ID = entity.ID,
                LeavePolicyTypeID = entity.LeavePolicyTypeID,
                Description = entity.Description,
                BaseSalary = entity.BaseSalary,
                HousingAllowance = entity.HousingAllowance,
                TransportationAllowance = entity.TransportationAllowance,
                OtherAllowances = entity.OtherAllowances,
                OvertimeRate = entity.OvertimeRate,
                LatePenaltyPerMinute = entity.LatePenaltyPerMinute,
                AbsencePenaltyPerDay = entity.AbsencePenaltyPerDay,
                SocialInsuranceEmployeeShare = entity.SocialInsuranceEmployeeShare,
                SocialInsuranceCompanyShare = entity.SocialInsuranceCompanyShare,
                TaxRate = entity.TaxRate,
                IsTaxApplicable = entity.IsTaxApplicable,
                PaymentDay = entity.PaymentDay,
                PaymentMethod = entity.PaymentMethod
            };
        }

        public async Task<PoliciesDto?> GetPolicyAsync(int id, CancellationToken ct = default)
        {
            var entity = await _context.Policies
                .Include(p => p.LeavePolicies)
                    .ThenInclude(lp => lp.leaveTypes)
                .Include(p => p.salaryPolicies)
                .Include(p => p.AttendancePolicies)
                .FirstOrDefaultAsync(p => p.ID == id, ct);

            if (entity == null) return null;

            return new PoliciesDto
            {
                ID       = entity.ID,
                NameAr   = entity.NameAr,
                NameEn   = entity.NameEn,
                Notes    = entity.Notes,
                LeavePolicies = entity.LeavePolicies?.Select(lp => new LeavePoliciesDto
                {
                    ID                       = lp.ID,
                    LeavePolicyTypeID        = lp.LeavePolicyTypeID,
                    LeaveTypeID              = lp.LeaveTypeID,
                    EntitlementDaysPerYear   = lp.EntitlementDaysPerYear,
                    CarryOverLimit           = lp.CarryOverLimit,
                    NoticePeriodDays         = lp.NoticePeriodDays,
                    RequiresMedicalCertificate = lp.RequiresMedicalCertificate,
                    leaveTypes = lp.leaveTypes != null ? new LeaveTypesDto
                    {
                        ID     = lp.leaveTypes.ID,
                        NameAr = lp.leaveTypes.NameAr,
                        NameEn = lp.leaveTypes.NameEn,
                        Notes  = lp.leaveTypes.Notes
                    } : null
                }).ToList(),
                SalaryPolicies = entity.salaryPolicies?.Select(sp => new SalaryPoliciesDto
                {
                    ID                            = sp.ID,
                    LeavePolicyTypeID             = sp.LeavePolicyTypeID,
                    Description                   = sp.Description,
                    BaseSalary                    = sp.BaseSalary,
                    HousingAllowance              = sp.HousingAllowance,
                    TransportationAllowance       = sp.TransportationAllowance,
                    OtherAllowances               = sp.OtherAllowances,
                    OvertimeRate                  = sp.OvertimeRate,
                    LatePenaltyPerMinute          = sp.LatePenaltyPerMinute,
                    AbsencePenaltyPerDay          = sp.AbsencePenaltyPerDay,
                    SocialInsuranceEmployeeShare  = sp.SocialInsuranceEmployeeShare,
                    SocialInsuranceCompanyShare   = sp.SocialInsuranceCompanyShare,
                    TaxRate                       = sp.TaxRate,
                    IsTaxApplicable               = sp.IsTaxApplicable,
                    PaymentDay                    = sp.PaymentDay,
                    PaymentMethod                 = sp.PaymentMethod
                }).ToList(),
                AttendancePolicies = entity.AttendancePolicies?.Select(ap => new AttendancePoliciesDto
                {
                    ID                              = ap.ID,
                    LeavePolicyTypeID               = ap.LeavePolicyTypeID,
                    AllowedGraceMinutes             = ap.AllowedGraceMinutes,
                    WarningThresholdCount           = ap.WarningThresholdCount,
                    DeductionRatePerOccurrence      = ap.DeductionRatePerOccurrence,
                    UnexcusedAbsenceThreshold       = ap.UnexcusedAbsenceThreshold,
                    WorkStartTime                   = ap.WorkStartTime,
                    WorkEndTime                     = ap.WorkEndTime,
                    WorkHoursPerDay                 = ap.WorkHoursPerDay,
                    BreakStartTime                  = ap.BreakStartTime,
                    BreakEndTime                    = ap.BreakEndTime,
                    BreakDurationMinutes            = ap.BreakDurationMinutes,
                    WorkOnSunday                    = ap.WorkOnSunday,
                    WorkOnMonday                    = ap.WorkOnMonday,
                    WorkOnTuesday                   = ap.WorkOnTuesday,
                    WorkOnWednesday                 = ap.WorkOnWednesday,
                    WorkOnThursday                  = ap.WorkOnThursday,
                    WorkOnFriday                    = ap.WorkOnFriday,
                    WorkOnSaturday                  = ap.WorkOnSaturday,
                    AllowPermissions                = ap.AllowPermissions,
                    MaxPermissionRequestsPerDay     = ap.MaxPermissionRequestsPerDay,
                    MaxPermissionRequestsPerWeek    = ap.MaxPermissionRequestsPerWeek,
                    MaxPermissionRequestsPerMonth   = ap.MaxPermissionRequestsPerMonth,
                    MaxPermissionMinutesPerRequest  = ap.MaxPermissionMinutesPerRequest,
                    MaxPermissionMinutesPerDay      = ap.MaxPermissionMinutesPerDay,
                    MaxPermissionMinutesPerWeek     = ap.MaxPermissionMinutesPerWeek,
                    MaxPermissionMinutesPerMonth    = ap.MaxPermissionMinutesPerMonth,
                    RequirePermissionApproval       = ap.RequirePermissionApproval,
                    RejectPermissionIfExceeded      = ap.RejectPermissionIfExceeded,
                    DeductPermissionIfExceeded      = ap.DeductPermissionIfExceeded,
                    AllowLateArrivalPermission      = ap.AllowLateArrivalPermission,
                    AllowEarlyLeavePermission       = ap.AllowEarlyLeavePermission,
                    AllowDuringWorkPermission       = ap.AllowDuringWorkPermission,
                    LinkPermissionWithFingerprint   = ap.LinkPermissionWithFingerprint,
                    PermissionDeductionRatePerMinute = ap.PermissionDeductionRatePerMinute
                }).ToList()
            };
        }


    }
}
