using System.Security.Claims;
using CrossBuy.BL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;

namespace CrossBuy.Hubs
{
	/// Real-time notification hub. Each connection joins a group named "emp-{employeeId}"
	/// so the server can push notifications to a specific employee across web + mobile.
	/// Accepts both the Identity cookie (web) and JWT bearer (mobile).
	[Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
	public class NotificationsHub : Hub
	{
		private readonly IEmployeeService _employeeService;

		public NotificationsHub(IEmployeeService employeeService)
		{
			_employeeService = employeeService;
		}

		public static string GroupFor(int employeeId) => $"emp-{employeeId}";

		public override async Task OnConnectedAsync()
		{
			var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
			if (!string.IsNullOrEmpty(userId))
			{
				var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
				if (emp != null)
					await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(emp.ID));
			}
			await base.OnConnectedAsync();
		}
	}
}
