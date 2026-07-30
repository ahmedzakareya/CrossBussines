using System.Security.Claims;
using CrossBuy.BL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;

namespace CrossBuy.Hubs
{
	/// RC-3c: real-time layer for the POS floor. Each connection joins its BRANCH group "posbranch-{branchId}"
	/// (derived server-side from the authenticated user's POS access — the client cannot pick a branch).
	/// SignalR is a NOTIFICATION layer only: the server broadcasts AFTER a completed service operation.
	/// It NEVER writes data or GL. Events:
	///   OrderSentToKitchen(orderId) · LineKdsStatusChanged(orderId,lineId,status) · OrderReady(orderId) · OrderPaid(orderId)
	[Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
	public class PosHub : Hub
	{
		private readonly IPosAccessService _access;
		public PosHub(IPosAccessService access) { _access = access; }

		public static string BranchGroup(int branchId) => $"posbranch-{branchId}";

		public override async Task OnConnectedAsync()
		{
			var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
			if (!string.IsNullOrEmpty(userId))
			{
				var acc = await _access.ResolveByUserIdAsync(userId);
				if (acc != null)
					await Groups.AddToGroupAsync(Context.ConnectionId, BranchGroup(acc.BranchId));
			}
			await base.OnConnectedAsync();
		}
	}
}
