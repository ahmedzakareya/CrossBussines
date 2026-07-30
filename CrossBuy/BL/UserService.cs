using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Identity;

namespace CrossBuy.BL
{
	public class UserService : IUserService
	{
		private readonly UserManager<Users> _userManager;

		public UserService(UserManager<Users> userManager)
		{
			_userManager = userManager;
		}

		public async Task<Users> FindUserByNameAsync(string userName)
		{
			return await _userManager.FindByNameAsync(userName);
		}
	}
}
