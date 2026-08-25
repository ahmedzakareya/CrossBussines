using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
    // Stage 1 Batch C — communication authorization.
    //
    // This module is the one case where a real record-level rule ALREADY existed: `ChatService.IsMemberAsync`
    // checks `ConversationMember` before returning a conversation. That rule is canonicalised here, not
    // replaced — `ConversationMember` is untouched, and the service is the single place the rule now lives so
    // SignalR, the controller and any future consumer answer the same way. See ADR-029.
    //
    // The parts that had NO rule and now do: announcements (Company/Branch audience), the email outbox
    // (`CommMessage` has no owner or participant column at all — company + an explicit administrative right is
    // the only honest gate), and group management.
    public interface ICommunicationAccessService
    {
        Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default);

        // Membership is the record rule; exposed so ChatService/hubs ask instead of re-deriving it.
        Task<bool> IsConversationParticipantAsync(
            BusinessContext context, int conversationId, CancellationToken cancellationToken = default);

        Task<AccessScope> ResolveConversationScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default);
    }

    // Six actions. `moderate` is absent — no moderation feature exists. `participant-manage` is folded into
    // `manage-group`, which is what ConversationMember.Role = 'Owner' actually expresses. `confidential-view`
    // is absent — no communication entity carries a visibility tier. `outbox-manage` is ADDED because
    // CommMessage has no participant rule and therefore needs an explicit administrative gate.
    public static class CommunicationActions
    {
        public const string Read = "read";
        public const string Send = "send";
        public const string CreateGroup = "create-group";
        public const string ManageGroup = "manage-group";
        public const string AnnouncementSend = "announcement-send";
        public const string OutboxManage = "outbox-manage";

        public static readonly IReadOnlyCollection<string> All = new[]
        { Read, Send, CreateGroup, ManageGroup, AnnouncementSend, OutboxManage };
    }

    public static class CommunicationRoles
    {
        public const string CommunicationAdministrator = "CommunicationAdministrator"; // groups + announcements
        public const string AnnouncementPublisher = "AnnouncementPublisher";           // announcements only
        public const string OutboxOperator = "OutboxOperator";                         // the email outbox

        public static readonly IReadOnlyList<string> All =
            new[] { CommunicationAdministrator, AnnouncementPublisher, OutboxOperator };
    }

    public sealed class CommunicationAccessService : ModuleAccessServiceBase, ICommunicationAccessService
    {
        private readonly CrossDbContext _db;
        private readonly ILogger<CommunicationAccessService> _log;

        public CommunicationAccessService(
            CrossDbContext db, IPlatformRoleDirectory roles, ILogger<CommunicationAccessService> log)
            : base(roles, log)
        { _db = db; _log = log; }

        public override string Scope => EntityRegistry.ScopeCommunication;
        public override IReadOnlyCollection<string> Actions => CommunicationActions.All;

        protected override async Task<bool> EvaluateAsync(
            BusinessContext context, string action, PermissionTarget? target,
            IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken)
        {
            bool commAdmin = Holds(grants, CommunicationRoles.CommunicationAdministrator);
            bool publisher = Holds(grants, CommunicationRoles.AnnouncementPublisher);
            bool outbox = Holds(grants, CommunicationRoles.OutboxOperator);

            // ---- THE EMAIL OUTBOX: never reachable through ordinary `read` ----
            //
            // CommMessage carries CompanyID, ToAddress, Subject and Body — and no owner, sender or participant
            // column. There is therefore no membership rule to derive, so the only honest gate is company plus
            // an explicit administrative right. It is NOT bootstrap-open: exposing every queued email body to
            // every employee by default would be a new exposure created by the batch meant to close one.
            if (action == CommunicationActions.OutboxManage)
            {
                if (!(commAdmin || outbox))
                {
                    _log.LogInformation(
                        "Communication: 'outbox-manage' denied for employee {Employee} — the email outbox has no " +
                        "participant rule, so it requires an explicit role and is never bootstrap-open.",
                        context.EmployeeId);
                    return false;
                }
                return true;
            }

            // ---- announcements: audience is Company/Branch, publishing is a right ----
            if (action == CommunicationActions.AnnouncementSend)
            {
                if (bootstrapOpen) return true;
                if (!(commAdmin || publisher)) return false;
                // A branch-scoped grant may only publish to its own branch. A company-wide grant (null) may
                // publish anywhere in the company.
                return HoldsInBranch(grants, target?.BranchId,
                    CommunicationRoles.CommunicationAdministrator, CommunicationRoles.AnnouncementPublisher);
            }

            // ---- conversation-scoped actions ----
            if (target?.ConversationId is int conversationId && conversationId > 0)
            {
                var conv = await _db.Conversations.AsNoTracking()
                    .Where(c => c.ID == conversationId)
                    .Select(c => new { c.CompanyID, c.Kind, c.CreatedByEmployeeId })
                    .FirstOrDefaultAsync(cancellationToken);

                // Absent and other-company answer identically.
                if (conv == null || conv.CompanyID != context.CompanyId) return false;

                var membership = await _db.ConversationMembers.AsNoTracking()
                    .Where(m => m.ConversationId == conversationId && m.EmployeeId == context.EmployeeId!.Value)
                    .Select(m => m.Role)
                    .FirstOrDefaultAsync(cancellationToken);

                bool isMember = membership != null;
                bool isOwner = string.Equals(membership, "Owner", StringComparison.Ordinal);

                return action switch
                {
                    // Belonging to the company is NOT enough — for a direct OR a group conversation. This is
                    // the rule ChatService already enforced, now canonical, and it holds even under
                    // bootstrap-open: a private message is not "open by default" for compatibility.
                    CommunicationActions.Read => isMember,
                    CommunicationActions.Send => isMember,
                    // Managing a group needs Owner, or the module's administrative right. A plain member
                    // cannot rename the group or change its membership.
                    CommunicationActions.ManageGroup => isOwner || commAdmin,
                    CommunicationActions.CreateGroup => false,   // not a per-conversation action
                    _ => false,
                };
            }

            // ---- module-level questions (no specific conversation) ----
            return action switch
            {
                // "May I use chat at all" — yes for any employee of the company; the record rule above is what
                // actually protects each conversation.
                CommunicationActions.Read or CommunicationActions.Send => true,
                CommunicationActions.CreateGroup => bootstrapOpen || commAdmin || true,
                CommunicationActions.ManageGroup => commAdmin,
                _ => false,
            };
        }

        public async Task<bool> IsConversationParticipantAsync(
            BusinessContext context, int conversationId, CancellationToken cancellationToken = default)
        {
            if (context?.EmployeeId is not > 0 || context.CompanyId <= 0 || conversationId <= 0) return false;

            // Company is verified on the CONVERSATION row, then membership. Both, in that order — a member row
            // for a conversation belonging to another company must not grant access, which is how a
            // cross-company participant is rejected.
            return await _db.Conversations.AsNoTracking()
                .AnyAsync(c => c.ID == conversationId && c.CompanyID == context.CompanyId, cancellationToken)
                && await _db.ConversationMembers.AsNoTracking()
                    .AnyAsync(m => m.ConversationId == conversationId
                                && m.EmployeeId == context.EmployeeId.Value, cancellationToken);
        }

        public async Task<AccessScope> ResolveConversationScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0) return AccessScope.None();
            if (!CommunicationActions.All.Contains(action, StringComparer.Ordinal)) return AccessScope.None();

            // Conversations are ALWAYS membership-scoped — there is no company-wide conversation read, not even
            // for an administrator, because an administrative right over groups is not a right to read private
            // messages. So the breadth is Own, and the consumer filters by ConversationMember.
            return action is CommunicationActions.Read or CommunicationActions.Send
                ? AccessScope.Own(context.CompanyId)
                : AccessScope.None();
        }
    }
}
