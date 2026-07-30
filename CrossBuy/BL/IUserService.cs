using CrossBuy.Models.Context.Admin;

namespace CrossBuy.BL
{
	public interface IUserService
	{
		Task<Users> FindUserByNameAsync(string userName);
	}
}
