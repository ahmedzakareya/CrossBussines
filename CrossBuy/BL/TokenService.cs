using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CrossBuy.Models.Context.Admin;
using Microsoft.IdentityModel.Tokens;

namespace CrossBuy.BL
{
	public class TokenService : ITokenService
	{
		private readonly IConfiguration _config;

		public TokenService(IConfiguration config)
		{
			_config = config;
		}

		public string CreateToken(Users user, int employeeId)
		{
			var jwt = _config.GetSection("Jwt");
			var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
			var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

			var claims = new List<Claim>
			{
				new Claim(JwtRegisteredClaimNames.Sub, user.Id),
				new Claim(ClaimTypes.NameIdentifier, user.Id),
				new Claim("employeeId", employeeId.ToString()),
				new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
				new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
			};

			var hours = int.TryParse(jwt["ExpireHours"], out var h) ? h : 168;

			var token = new JwtSecurityToken(
				issuer: jwt["Issuer"],
				audience: jwt["Audience"],
				claims: claims,
				expires: DateTime.UtcNow.AddHours(hours),
				signingCredentials: creds);

			return new JwtSecurityTokenHandler().WriteToken(token);
		}
	}
}
