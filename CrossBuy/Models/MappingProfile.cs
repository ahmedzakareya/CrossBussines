using AutoMapper;
using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Identity;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace CrossBuy.Models
{
	public class MappingProfile :Profile
	{

		public MappingProfile()
		{
			CreateMap<Employee, EmployeeViewModel>();

			CreateMap<LoginViewModel, Users>();


			CreateMap<CompanyDto, Companies>()
			 .ForMember(dest => dest.CompanyImage, opt => opt.Ignore());
			CreateMap<Companies, CompanyDto>()
	.ForMember(dest => dest.CompanyName, opt => opt.MapFrom(src => src.ComoanyNameAr));

			CreateMap<JobTitle, JobTitleDto>();
			CreateMap<JobTitleDto, JobTitle>();

			CreateMap<AdministrativeBodiesCompany, AdministrativeBodiesCompanyDto>();
			CreateMap<AdministrativeBodiesCompanyDto, AdministrativeBodiesCompany>();


			CreateMap<HierarchicalDto, Hierarchical>();
			CreateMap<Hierarchical, HierarchicalDto>();

			CreateMap<LeavePolicies, LeavePoliciesDto>();
			CreateMap<LeavePoliciesDto, LeavePolicies>();

			CreateMap<LeaveTypesDto , LeaveTypes> ();
			CreateMap<LeaveTypes, LeaveTypesDto>();

			CreateMap<PoliciesDto, Policies>();
			CreateMap<Policies, PoliciesDto>();

			CreateMap<SalaryPolicies, SalaryPoliciesDto>();
			CreateMap<SalaryPoliciesDto, SalaryPolicies>();

			CreateMap<AttendancePolicies, AttendancePoliciesDto>();
			CreateMap<AttendancePoliciesDto, AttendancePolicies>();

		}

	}
}
