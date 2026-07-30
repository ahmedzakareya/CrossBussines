using AutoMapper;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class AdministrativeBodiesCompanyService : IAdministrativeBodiesCompanyService
	{
		private readonly CrossDbContext context;
		private readonly IMapper mapper;

		public AdministrativeBodiesCompanyService(CrossDbContext context, IMapper mapper)
        {
			this.context = context;
			this.mapper = mapper;
		}
        public async Task<List<AdministrativeBodiesCompanyDto>> GetAll()
		{
			var model = await context.AdministrativeBodiesCompanies.ToListAsync();
			var dto = mapper.Map<List<AdministrativeBodiesCompanyDto>>(model);
			return dto;
		}

		public async Task<AdministrativeBodiesCompanyDto> GetById(int? id)
		{
			var model = await context.AdministrativeBodiesCompanies.FindAsync(id);
			var dto = mapper.Map<AdministrativeBodiesCompanyDto>(model);
			return dto;
		}

		public async Task<AdministrativeBodiesCompanyDto> Save(AdministrativeBodiesCompanyDto model)
		{
			AdministrativeBodiesCompany administrativeBodiesCompany;

			if (model.A_ID == 0 || model.A_ID == null)
			{
				administrativeBodiesCompany = mapper.Map<AdministrativeBodiesCompany>(model);
				context.AdministrativeBodiesCompanies.Add(administrativeBodiesCompany);
			}
			else
			{
				administrativeBodiesCompany = await context.AdministrativeBodiesCompanies.FindAsync(model.A_ID);
				if (administrativeBodiesCompany != null)
				{
					mapper.Map(model, administrativeBodiesCompany);
				}
				else
				{
					throw new Exception("JobTitle not found.");
				}
			}

			await context.SaveChangesAsync();

			return mapper.Map<AdministrativeBodiesCompanyDto>(administrativeBodiesCompany);
		}
	}
}
