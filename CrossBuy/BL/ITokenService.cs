using CrossBuy.Models.Context.Admin;

namespace CrossBuy.BL
{
	public interface ITokenService
	{
		// Builds a signed JWT for the given user/employee.
		string CreateToken(Users user, int employeeId);
	}
}
