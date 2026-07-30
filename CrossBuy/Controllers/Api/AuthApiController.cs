using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/auth")]
	[Produces("application/json")]
	public class AuthApiController : ControllerBase
	{
		private readonly UserManager<Users> _userManager;
		private readonly IEmployeeService _employeeService;
		private readonly ITokenService _tokenService;

		public AuthApiController(
			UserManager<Users> userManager,
			IEmployeeService employeeService,
			ITokenService tokenService)
		{
			_userManager = userManager;
			_employeeService = employeeService;
			_tokenService = tokenService;
		}

		public class LoginRequest
		{
			public string UserName { get; set; } = string.Empty;
			public string Password { get; set; } = string.Empty;
		}

		// POST /api/auth/login
		[HttpPost("login")]
		public async Task<IActionResult> Login([FromBody] LoginRequest model)
		{
			if (model == null || string.IsNullOrWhiteSpace(model.UserName) || string.IsNullOrWhiteSpace(model.Password))
				return BadRequest(new { success = false, message = "اسم المستخدم وكلمة المرور مطلوبان" });

			var user = await _userManager.FindByNameAsync(model.UserName);
			if (user == null || !user.IsActive || !user.IsEndUser)
				return Unauthorized(new { success = false, message = "بيانات الدخول غير صحيحة" });

			var passwordOk = await _userManager.CheckPasswordAsync(user, model.Password);
			if (!passwordOk)
				return Unauthorized(new { success = false, message = "بيانات الدخول غير صحيحة" });

			var employee = await _employeeService.GetEmployeeByUserIdAsync(user.Id);
			if (employee == null)
				return Unauthorized(new { success = false, message = "لا يوجد ملف موظف مرتبط بهذا الحساب" });

			var token = _tokenService.CreateToken(user, employee.ID);

			return Ok(new
			{
				success = true,
				token,
				employee = new
				{
					employee.ID,
					employee.FullName,
					employee.Email,
					employee.ProfileImage
				}
			});
		}

		// GET /api/auth/ping  — quick token check for the mobile app
		[HttpGet("ping")]
		[Microsoft.AspNetCore.Authorization.Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		public IActionResult Ping() => Ok(new { success = true });
	}
}
