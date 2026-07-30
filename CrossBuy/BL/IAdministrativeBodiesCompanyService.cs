using CrossBuy.ViewModel;

namespace CrossBuy.BL
{
	public interface IAdministrativeBodiesCompanyService
	{
		
		Task<List<AdministrativeBodiesCompanyDto>> GetAll();
		Task<AdministrativeBodiesCompanyDto> GetById(int? id);
		Task<AdministrativeBodiesCompanyDto> Save(AdministrativeBodiesCompanyDto entity);

	}
}
