using CrossBuy.ViewModel;

namespace CrossBuy.BL
{
	public interface IAdministrativeStructureService
	{
		Task<List<HierarchicalDto>> GataAll();
		Task<List<HierarchicalDto>> GetByCompanyAsync(int companyId);
		Task<HierarchicalDto> GetParentData(int ID);
		Task<HierarchicalDto> Save(HierarchicalDto dto);
	}
}
