using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel slice 2 (ADR-006) — the SECOND real outbox consumer.
    //
    // It exists to prove per-consumer dispatch independence in production rather than only in tests: it and
    // TimelineProjection read the same committed events, own separate dispatch rows, and fail and retry
    // separately.
    //
    // Contract it must honour:
    //   * it reads COMMITTED events only — it runs in the dispatcher's own scope, after the business
    //     transaction closed, and never touches that transaction;
    //   * it THROWS on failure, so the outbox marks only ITS row Failed and retries only it;
    //   * it is IDEMPOTENT, because a row can be redelivered after a stale claim or a retry;
    //   * it authorizes every recipient before delivering, so nobody is ever linked to a record they cannot
    //     open;
    //   * it persists through the existing NotificationService, which owns the SignalR push and the mute
    //     rules. This consumer sends no email/SMS/push of its own.
    public class NotificationProjectionConsumer : IBusinessEventConsumer
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessEventNotificationMapper _mapper;
        private readonly INotificationService _notifications;
        private readonly IEntityRegistry _registry;
        private readonly IPlatformPermissionProvider _permissions;
        private readonly IBusinessContextFactory _contexts;
        private readonly ICompanyIsolationBypass _bypass;
        private readonly ICompanyScopeHolder _scope;
        private readonly ILogger<NotificationProjectionConsumer> _log;

        public NotificationProjectionConsumer(
            CrossDbContext db,
            IBusinessEventNotificationMapper mapper,
            INotificationService notifications,
            IEntityRegistry registry,
            IPlatformPermissionProvider permissions,
            IBusinessContextFactory contexts,
            ICompanyIsolationBypass bypass,
            ICompanyScopeHolder scope,
            ILogger<NotificationProjectionConsumer> log)
        { _db = db; _mapper = mapper; _notifications = notifications; _registry = registry; _permissions = permissions; _contexts = contexts; _bypass = bypass; _scope = scope; _log = log; }

        public string Consumer => BusinessEventConsumers.NotificationProjection;

        public async Task HandleAsync(BusinessEventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            var commands = _mapper.Map(envelope);
            if (commands.Count == 0) return;   // timeline-only event — a normal, successful outcome

            // Stage 1 Batch B / B3 — the idempotency guard below reads Notifications by (recipient, dedupKey) with
            // NO company predicate, and the recipient may belong to a company other than this scope's. It is the
            // ONLY real idempotency guard (NotificationService's own check is unread-only), so if Batch B's filter
            // hid a row from it, every stale-claim redelivery would send a DUPLICATE notification.
            //
            // Held for the whole handler because both the dedup read and the audience resolution need it, and
            // released with it. The dispatcher already holds PlatformDispatch, so this consumer runs inside it —
            // which is why this is a no-op re-use rather than a nested grant when called from the worker.
            using var projectionScope = _scope.ActiveBypass == null
                ? _bypass.BeginPlatformDispatch(
                    $"Notification projection for {envelope.Entity.Code}/{envelope.Entity.Id} — the idempotency " +
                    "check spans recipient companies.")
                : null;

            // Deep link comes from the registry, never from a hand-built string, so a route change is a
            // one-line fix in EntityRegistry rather than a hunt through notification producers.
            var url = _registry.BuildUrl(envelope.Entity.Code, envelope.Entity.Id);

            foreach (var command in commands)
            {
                var recipients = await ResolveRecipientsAsync(command, cancellationToken);
                if (recipients.Count == 0)
                {
                    _log.LogDebug("Event {EventUid}: no recipients for notification '{Type}'", envelope.EventUid, command.Type);
                    continue;
                }

                foreach (var recipientId in recipients)
                {
                    // Deterministic per (event, recipient): the same dispatch redelivered produces the same
                    // key, and the check below is what makes the whole consumer idempotent.
                    var dedupKey = Truncate($"{command.DedupKeyPrefix}:{recipientId}", 120);

                    bool alreadySent = await _db.Notifications.AsNoTracking().AnyAsync(
                        n => n.RecipientEmployeeID == recipientId && n.DedupKey == dedupKey, cancellationToken);
                    if (alreadySent)
                    {
                        // NOTE: this check, not NotificationService's, is the idempotency guarantee — the
                        // service only suppresses UNREAD duplicates, so a retry after the user read the
                        // notification would otherwise send a second one.
                        _log.LogDebug("Event {EventUid}: notification already delivered to {Recipient}", envelope.EventUid, recipientId);
                        continue;
                    }

                    await _notifications.NotifyAsync(
                        recipientEmployeeId: recipientId,
                        titleAr: command.TitleAr, titleEn: command.TitleEn,
                        bodyAr: command.MessageAr, bodyEn: command.MessageEn,
                        type: command.Type,
                        refId: command.EntityId,
                        url: url,
                        companyId: command.CompanyId,
                        actorEmployeeId: command.ActorEmployeeId,
                        priority: command.Priority,
                        category: command.Category,
                        dedupKey: dedupKey,
                        expiresAt: null,
                        icon: null,
                        entityType: command.EntityType,
                        entityId: command.EntityId);
                }
            }
        }

        // Resolves the audience, then filters it by what each candidate may actually open.
        private async Task<List<int>> ResolveRecipientsAsync(NotificationCommand command, CancellationToken cancellationToken)
        {
            List<int> candidates;

            if (command.RecipientEmployeeId is > 0)
            {
                candidates = new List<int> { command.RecipientEmployeeId.Value };
            }
            else if (command.IsRoleTargeted)
            {
                // Same role tables NotificationService.NotifyRoleAsync reads — resolved here instead of
                // calling NotifyRoleAsync because each recipient needs its own dedup key and permission check,
                // which that method cannot express.
                candidates = command.RecipientScope switch
                {
                    "acc" => await _db.AccountingUserRoles.AsNoTracking()
                        .Where(r => r.CompanyID == command.CompanyId && command.RecipientRoles!.Contains(r.Role))
                        .Select(r => r.EmployeeId).Distinct().ToListAsync(cancellationToken),
                    "inv" => await _db.InventoryUserRoles.AsNoTracking()
                        .Where(r => r.CompanyID == command.CompanyId && command.RecipientRoles!.Contains(r.Role))
                        .Select(r => r.EmployeeId).Distinct().ToListAsync(cancellationToken),
                    // An unsupported scope is a mapper bug, not a runtime condition: fail the dispatch row so
                    // it is visible rather than silently dropping every notification for this event.
                    _ => throw new BusinessEventContractException(
                        $"Notification scope '{command.RecipientScope}' is not supported — NotificationService resolves only 'acc' and 'inv'."),
                };
            }
            else
            {
                throw new BusinessEventContractException(
                    $"Notification command for {command.EntityType}/{command.EntityId} targets neither an employee nor a role scope.");
            }

            // Never notify someone about their own action unless the mapper opts in. Mirrors the
            // exceptEmployeeId parameter the existing NotifyRoleAsync already applies.
            if (command.ExcludeActor && command.ActorEmployeeId is > 0)
                candidates.RemoveAll(id => id == command.ActorEmployeeId!.Value);

            if (candidates.Count == 0) return candidates;

            // Permission gate: a notification is a link. Sending one to a user who cannot open the target
            // would hand them a dead end and leak the record's existence.
            //
            // STAGE 1 BATCH A — this loop is now REAL. Until Stage 1 the accounting/inventory adapters resolved
            // the employee from the HTTP session and the company from a `const int CompanyId = 1`, so inside the
            // dispatcher (where there is no session) their answer was not specific to `candidateId`. That
            // limitation is gone: the module services take the BusinessContext, and the context below is built
            // per recipient by IBusinessContextFactory from the Employee row.
            //
            // Three things this now genuinely enforces, none of which it enforced before:
            //   1. two employees holding the SAME module role get different answers when their record access
            //      differs (CRM ownership, inventory warehouse scope);
            //   2. a recipient in ANOTHER company is excluded — the provider checks company isolation before
            //      module permission;
            //   3. an inactive employee or one with no company is excluded rather than authorized.
            int companyMismatch = 0, unresolved = 0, denied = 0;
            var authorized = new List<int>(candidates.Count);

            foreach (var candidateId in candidates)
            {
                // The company being notified about is passed as the explicit substitution for an orphan
                // Employee row (EmpCompanyID = 0) — a data defect, logged by the factory. It is an argument,
                // never an ambient default.
                var context = await _contexts.ForEmployeeAsync(candidateId, command.CompanyId, cancellationToken);
                if (context == null)
                {
                    unresolved++;
                    _log.LogDebug("Recipient {Recipient} has no resolvable business context (missing, inactive, " +
                                  "or no company); excluded.", candidateId);
                    continue;
                }

                // Belt and braces: the provider enforces this too, but excluding here keeps the reason
                // distinguishable in the metrics below. A role row in another company must not pull a
                // recipient into this company's notification.
                if (context.CompanyId != command.CompanyId)
                {
                    companyMismatch++;
                    _log.LogWarning(
                        "Recipient {Recipient} belongs to company {RecipientCompany} but the event is for company " +
                        "{EventCompany}; excluded.", candidateId, context.CompanyId, command.CompanyId);
                    continue;
                }

                var decision = await _permissions.CanAsync(
                    context, command.EntityType, command.EntityId, PlatformActions.View, cancellationToken);
                if (decision.Allowed) { authorized.Add(candidateId); continue; }

                denied++;
                // Reason only — never the payload, the title or the body. A skipped-recipient log must not
                // become a copy of the notification it declined to send.
                _log.LogDebug("Recipient {Recipient} not authorised for {Entity}/{Id}: {Reason}",
                    candidateId, command.EntityType, command.EntityId, decision.Reason);
            }

            // Operational visibility: a consumer that silently drops most of its audience looks identical to
            // one with a small audience. Counts only, at Information, when anything was dropped.
            if (companyMismatch + unresolved + denied > 0)
                _log.LogInformation(
                    "Notification '{Type}' for {Entity}/{Id}: {Authorized} authorised, {Denied} denied, " +
                    "{Unresolved} unresolvable, {CompanyMismatch} cross-company (of {Candidates} candidates).",
                    command.Type, command.EntityType, command.EntityId,
                    authorized.Count, denied, unresolved, companyMismatch, candidates.Count);

            return authorized;
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value.Substring(0, max);
    }
}
