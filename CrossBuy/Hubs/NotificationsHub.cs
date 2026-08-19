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
		// Company-wide group — used for real-time broadcasts that every employee in the tenant should react to
		// (e.g. a company calendar event changing → all open calendars refetch). Visibility is still enforced
		// server-side by the data endpoint, so broadcasting the *signal* company-wide is safe.
		public static string CompanyGroupFor(int companyId) => $"co-{companyId}";

		public override async Task OnConnectedAsync()
		{
			var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
			if (!string.IsNullOrEmpty(userId))
			{
				var emp = await _employeeService.GetEmployeeByUserIdAsync(userId);
				if (emp != null)
				{
					await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(emp.ID));
					var cid = emp.EmpCompanyID.GetValueOrDefault();
					if (cid > 0)
						await Groups.AddToGroupAsync(Context.ConnectionId, CompanyGroupFor(cid));
				}
			}
			await base.OnConnectedAsync();
		}
	}
}
