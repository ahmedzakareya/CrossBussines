using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // The FOURTH outbox consumer, and the whole reason the orchestration layer is event-driven.
    //
    // It honours the same contract the other three do:
    //   * it reads COMMITTED events only, in the dispatcher's own scope, after the business
    //     transaction closed, and never touches that transaction;
    //   * it THROWS on failure, so only ITS dispatch row is marked Failed and only it retries. A
    //     consumer that caught everything would turn a lost task into a silent success;
    //   * it is IDEMPOTENT, because a row can be redelivered after a stale claim or a retry.
    //
    // It holds no policy of its own. Deciding is the rules' job and writing is the service's; this
    // is the wire between the outbox and them, which is precisely why it is the only place tasks are
    // created from events and no controller gains a task call.
    // =============================================================================================
    public sealed class TaskOrchestrationConsumer : IBusinessEventConsumer
    {
        private readonly ITaskOrchestrationService _orchestration;
        private readonly ILogger<TaskOrchestrationConsumer>? _log;

        public TaskOrchestrationConsumer(
            ITaskOrchestrationService orchestration,
            ILogger<TaskOrchestrationConsumer>? log = null)
        { _orchestration = orchestration; _log = log; }

        public string Consumer => BusinessEventConsumers.TaskOrchestration;

        public async Task HandleAsync(BusinessEventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            var result = await _orchestration.ProjectAsync(envelope, cancellationToken);

            // Ignore is the common answer and a successful one: most business events are not work.
            // It is logged at Debug rather than counted as anything, so the dispatch row is marked
            // Done and never retried.
            if (result.Action == TaskProjectionAction.Ignore)
            {
                _log?.LogDebug("Task orchestration ignored {EventType} ({Reason})",
                    envelope.EventType, result.Reason);
                return;
            }

            _log?.LogInformation(
                "Task orchestration {Action} task {TaskId} from {EventType} in company {Company}{Already}",
                result.Action, result.TaskId, envelope.EventType, envelope.Context.CompanyId,
                result.AlreadyProjected ? " (already projected)" : "");
        }
    }
}
