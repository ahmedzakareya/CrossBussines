using CrossBuy.ViewModel;
using System.Threading.Tasks;

namespace CrossBuy.BL
{
	public interface IJobTitles
	{
		Task<List<JobTitleDto>> GetAll();
		Task<JobTitleDto> Save (JobTitleDto jobTitleDto);
		Task<JobTitleDto> GetByID(int? ID);
	}
}
