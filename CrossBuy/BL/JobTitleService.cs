using AutoMapper;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Humanizer;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class JobTitleService : IJobTitles
	{
		private readonly CrossDbContext context;
		private readonly IMapper mapper;

		public JobTitleService(CrossDbContext context , IMapper mapper)
        {
			this.context = context;
			this.mapper = mapper;
		}
        public async Task<List<JobTitleDto>> GetAll()
		{
			var jobTitle = await context.JobTitles.ToListAsync();
			var dto = mapper.Map<List<JobTitleDto>>(jobTitle);
			return dto;
		}

		public async Task<JobTitleDto> GetByID(int? ID)
		{
			var jobTitle = await context.JobTitles.FindAsync(ID);
			var dto = mapper.Map<JobTitleDto>(jobTitle);
			return dto;
		}

		public async Task<JobTitleDto> Save(JobTitleDto model)
		{
			JobTitle jobTitle;

			if (model.ID == 0 || model.ID == null)
			{
				jobTitle = mapper.Map<JobTitle>(model);
				context.JobTitles.Add(jobTitle);
			}
			else
			{
				jobTitle = await context.JobTitles.FindAsync(model.ID);
				if (jobTitle != null)
				{
					mapper.Map(model, jobTitle); 
				}
				else
				{
					throw new Exception("JobTitle not found.");
				}
			}

			await context.SaveChangesAsync();

			return mapper.Map<JobTitleDto>(jobTitle);
		}

	}
}
