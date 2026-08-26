using System.Collections.Concurrent;
using System.Security.Claims;
using CrossBuy.BL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Hubs
{
    /// Real-time chat hub (Communication Hub P3). Presence + typing + per-conversation rooms.
    /// Accepts the Identity cookie (web) and JWT bearer (mobile), like NotificationsHub.
    [Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
    public class ChatHub : Hub
    {
        private readonly IEmployeeService _employees;
        private readonly ICommunicationAccessService _access;
        private readonly CrossBuy.Models.Context.CrossDbContext _db;

        public ChatHub(IEmployeeService employees, ICommunicationAccessService access,
            CrossBuy.Models.Context.CrossDbContext db)
        { _employees = employees; _access = access; _db = db; }

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

        // ---- conversation rooms are AUTHORIZED (§11) --------------------------------------------------
        //
        // THE DEFECT THIS CLOSES. JoinConversation called Groups.AddToGroupAsync with no check of any kind.
        // [Authorize] on the class proves the caller is *an* employee — that is authentication, and it says
        // nothing about *this* conversation. Any signed-in employee, or any holder of a mobile JWT, could
        // join the room for any conversation id, including another company's, and from that moment received
        // every message broadcast into it live. The REST endpoints were membership-checked; the socket was
        // the way around them.
        //
        // ONE RULE, NOT A SECOND COPY. The check delegates to ICommunicationAccessService, the same service
        // the controller and ChatService use: it verifies the company on the CONVERSATION ROW and then
        // membership, in that order, so a member row belonging to another company's conversation grants
        // nothing.
        //
        // WHY THE CONTEXT IS BUILT HERE. IBusinessContextFactory resolves from the HTTP request, and a hub
        // method invocation on an established WebSocket has no request to resolve — the factory would fail
        // for every join. The identity used instead is the one the connection already authenticated with
        // (the NameIdentifier claim -> Employee row), and the company is read from that employee's record
        // rather than from anything the client sends. The client supplies only the conversation id, which is
        // exactly what is being authorized.
        public async Task JoinConversation(int conversationId)
        {
            if (conversationId <= 0) return;
            if (!await MayJoinAsync(conversationId)) return;   // silent: a refusal must not confirm the id exists
            await Groups.AddToGroupAsync(Context.ConnectionId, ConvGroup(conversationId));
        }

        // Leaving is not gated: removing yourself from a group you are not in is a no-op, and refusing it
        // would only make a client unable to tidy up after a permission change.
        public Task LeaveConversation(int conversationId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConvGroup(conversationId));

        private async Task<bool> MayJoinAsync(int conversationId)
        {
            try
            {
                var empId = Context.Items.TryGetValue("empId", out var v) && v is int cached ? cached : await EmpIdAsync() ?? 0;
                if (empId <= 0) return false;
                var companyId = await _db.Employee.AsNoTracking()
                    .Where(e => e.ID == empId).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
                if (companyId <= 0) return false;
                var ctx = new CrossBuy.Models.Platform.BusinessContext
                {
                    CompanyId = companyId,
                    EmployeeId = empId,
                    UserId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
                };
                return await _access.IsConversationParticipantAsync(ctx, conversationId);
            }
            catch (Exception)
            {
                return false;   // FAIL CLOSED — an error resolving the answer is not permission.
            }
        }

        // typing indicator → broadcast to the room (except caller)
        public async Task Typing(int conversationId)
        {
            if (Context.Items.TryGetValue("empId", out var v) && v is int empId)
                await Clients.OthersInGroup(ConvGroup(conversationId)).SendAsync("typing", new { conversationId, employeeId = empId });
        }
    }
}
