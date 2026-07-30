using System.Collections.Concurrent;
using System.Security.Claims;
using CrossBuy.BL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;

namespace CrossBuy.Hubs
{
    /// Real-time chat hub (Communication Hub P3). Presence + typing + per-conversation rooms.
    /// Accepts the Identity cookie (web) and JWT bearer (mobile), like NotificationsHub.
    [Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
    public class ChatHub : Hub
    {
        private readonly IEmployeeService _employees;
        public ChatHub(IEmployeeService employees) { _employees = employees; }

        // employeeId → live connection count (presence). Small internal org → broadcast to all.
        private static readonly ConcurrentDictionary<int, int> _online = new();

        public static bool IsOnline(int employeeId) => _online.TryGetValue(employeeId, out var c) && c > 0;
        public static IReadOnlyCollection<int> OnlineIds => _online.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();

        public static string ConvGroup(int conversationId) => $"conv-{conversationId}";
        public static string UserGroup(int employeeId) => $"cuser-{employeeId}";

        private async Task<int?> EmpIdAsync()
        {
            var uid = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(uid)) return null;
            var e = await _employees.GetEmployeeByUserIdAsync(uid);
            return e?.ID;
        }

        public override async Task OnConnectedAsync()
        {
            var empId = await EmpIdAsync();
            if (empId != null)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(empId.Value));
                Context.Items["empId"] = empId.Value;
                var count = _online.AddOrUpdate(empId.Value, 1, (_, c) => c + 1);
                if (count == 1) await Clients.All.SendAsync("presence", new { employeeId = empId.Value, online = true });
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (Context.Items.TryGetValue("empId", out var v) && v is int empId)
            {
                var count = _online.AddOrUpdate(empId, 0, (_, c) => Math.Max(0, c - 1));
                if (count == 0) await Clients.All.SendAsync("presence", new { employeeId = empId, online = false });
            }
            await base.OnDisconnectedAsync(exception);
        }

        // client joins/leaves a conversation room (so it receives that room's realtime messages)
        public Task JoinConversation(int conversationId) => Groups.AddToGroupAsync(Context.ConnectionId, ConvGroup(conversationId));
        public Task LeaveConversation(int conversationId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConvGroup(conversationId));

        // typing indicator → broadcast to the room (except caller)
        public async Task Typing(int conversationId)
        {
            if (Context.Items.TryGetValue("empId", out var v) && v is int empId)
                await Clients.OthersInGroup(ConvGroup(conversationId)).SendAsync("typing", new { conversationId, employeeId = empId });
        }
    }
}
