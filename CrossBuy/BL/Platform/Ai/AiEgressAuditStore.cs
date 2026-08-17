using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.6, Phases 4 and 5. THE DURABLE AUDIT STORE.
    //
    // Uses EF Core against CrossDbContext, matching AiProjectionStore exactly — same kernel, same
    // persistence mechanism. Parameterisation comes from EF; there is no hand-built SQL to get wrong.
    //
    // WRITES ONE TABLE AND NOTHING ELSE. AiEgressAudits only: no business table, no GL, no stock. It
    // does not violate the two-writers rule.
    public interface IAiEgressAuditStore
    {
        /// Persists one audit row. Returns false on failure rather than throwing — the caller decides
        /// what a lost audit means, and that decision is made once, in AiEgressAuditSink.
        Task<bool> WriteAsync(AiEgressAuditRecord record, CancellationToken ct = default);
    }

    public sealed class SqlAiEgressAuditStore : IAiEgressAuditStore
    {
        private readonly CrossDbContext _db;
        private readonly ILogger<SqlAiEgressAuditStore> _log;

        public SqlAiEgressAuditStore(CrossDbContext db, ILogger<SqlAiEgressAuditStore> log)
        {
            _db = db;
            _log = log;
        }

        public async Task<bool> WriteAsync(AiEgressAuditRecord r, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(r);

            // An audit row that cannot say whose data left is not an audit row. The database rejects it
            // too (CK_AiEgressAudits_Company); refusing here turns a constraint violation into a clear
            // log line instead of an exception from the data layer.
            if (r.CompanyId <= 0)
            {
                _log.LogError("AI egress audit REFUSED: company unresolved. correlation={Correlation}", r.CorrelationId);
                return false;
            }

            var success = r.Outcome == AiProviderOutcome.Success;

            var row = new AiEgressAudit
            {
                CompanyID = r.CompanyId,
                ProviderId = Cap(r.ProviderId, 64),
                Feature = Cap(r.Feature.ToString(), 64),
                Model = Cap(r.Model, 128),
                Classification = Cap(r.Classification.ToString(), 64),
                DestinationClass = Cap(r.Destination.ToString(), 64),
                GovernanceDecision = Cap(r.GovernanceDecision, 256),
                ApprovalReference = Cap(r.ApprovalReference, 256),
                CorrelationId = Cap(r.CorrelationId, 128),
                OccurredAtUtc = r.OccurredAtUtc,
                DurationMs = (int)Math.Clamp(r.Duration.TotalMilliseconds, 0, int.MaxValue),
                Success = success,
                Outcome = Cap(r.Outcome.ToString(), 32),

                // CK_AiEgressAudits_Failure: a failure needs a category and a success must not carry
                // one. Normalised here so the constraint can never be the thing that discovers a bug.
                FailureCategory = success ? null : Cap(r.FailureCategory ?? "unspecified", 128),

                InputTokens = Math.Max(0, r.InputTokens),
                OutputTokens = Math.Max(0, r.OutputTokens),
                RequestBytes = Math.Max(0, r.RequestBytes),

                // CK_AiEgressAudits_Cost: amount and currency travel together or neither is stored. A
                // cost with no currency is unusable; a currency with no cost implies a zero nobody
                // measured.
                EstimatedCost = r.EstimatedCost.HasValue && !string.IsNullOrWhiteSpace(r.Currency) ? r.EstimatedCost : null,
                CostCurrency = r.EstimatedCost.HasValue && !string.IsNullOrWhiteSpace(r.Currency) ? Cap(r.Currency, 8) : null,

                CreatedAtUtc = DateTime.UtcNow,
            };

            try
            {
                _db.Set<AiEgressAudit>().Add(row);
                await _db.SaveChangesAsync(ct);

                // Detached for the same reason AiProjectionStore detaches: this row is written and never
                // read back in the same unit of work, and leaving it tracked would make an unrelated
                // later SaveChanges in the same scope carry it along.
                _db.Entry(row).State = EntityState.Detached;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception TYPE and message are logged; the record is not re-serialised into the
                // log, because doing so on every failure would turn a database outage into a second
                // copy of the audit in a place with different retention.
                _log.LogError(ex,
                    "AI egress audit PERSISTENCE FAILED. company={Company} provider={Provider} " +
                    "feature={Feature} outcome={Outcome} correlation={Correlation}",
                    r.CompanyId, r.ProviderId, r.Feature, r.Outcome, r.CorrelationId);
                return false;
            }
        }

        /// Truncation is a last-resort guard, not the primary control: every value written here is
        /// already a bounded enum, identifier or machine code. It exists so an oversized value fails as
        /// a shortened string rather than as a data-layer exception on a request path.
        private static string Cap(string? s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
    }

    // ---------------------------------------------------------------------------------------------
    // THE COMPOSITE SINK — Phase 4's "structured logging AND durable database", and Phase 5's failure
    // policy in one place.
    //
    // ORDER IS DELIBERATE: log FIRST, persist SECOND. The log line is the one that cannot fail, so it
    // is written before the thing that can. If the database is down, the evidence still exists
    // somewhere.
    //
    // ---- PHASE 5: WHAT HAPPENS WHEN THE PROVIDER CALL SUCCEEDS BUT THE AUDIT WRITE FAILS ----
    //
    // The chosen behaviour: the provider result is RETURNED to the caller, and the audit failure is
    // logged at ERROR with a distinct, greppable marker. The call is NOT retried and NOT rolled back.
    //
    // Three options were considered and two rejected:
    //
    //   (a) THROW, failing the caller's request.  REJECTED. The money is already spent and the model
    //       has already seen the data — the send happened. Throwing hides a completed egress from the
    //       user while doing nothing to un-send it, and on a paid provider it invites the user to press
    //       the button again, which is a second charge for the same answer.
    //
    //   (b) RETRY the audit write.  Retrying the AUDIT is safe in isolation, but the retry must never
    //       be allowed to become a retry of the CALL. Keeping them in one method is how that mistake
    //       gets made later, so the audit write is deliberately single-attempt here and durability is
    //       raised, if needed, by an outbox — see below.
    //
    //   (c) LOG LOUDLY AND CONTINUE.  CHOSEN. It matches the convention already used for AI
    //       misconfiguration at startup: fail-closed on the SECURITY decision, fail-open on the
    //       OBSERVABILITY one, and never let the observability failure cause a second paid call.
    //
    // THE HONEST LIMITATION: a database outage during an approved call loses that row from the durable
    // audit. The log line survives. If audit completeness ever becomes a hard compliance requirement,
    // the correct fix is the transactional outbox this kernel already runs (BusinessEventDispatch), not
    // a retry loop next to a paid HTTP call. Recorded rather than pretended away.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiEgressAuditSink : IAiEgressAuditSink
    {
        private readonly IAiEgressAuditStore _store;
        private readonly ILogger<AiEgressAuditSink> _log;

        public AiEgressAuditSink(IAiEgressAuditStore store, ILogger<AiEgressAuditSink> log)
        {
            _store = store;
            _log = log;
        }

        public async Task RecordAsync(AiEgressAuditRecord r, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(r);

            // 1. Structured log — payload-free, and the copy that cannot fail.
            _log.LogInformation(
                "AI-EGRESS-AUDIT company={Company} provider={Provider} feature={Feature} model={Model} " +
                "classification={Classification} destination={Destination} decision={Decision} " +
                "outcome={Outcome} correlation={Correlation} at={At:o} ms={Ms} bytes={Bytes} " +
                "inTokens={In} outTokens={Out} cost={Cost} currency={Currency} failure={Failure}",
                r.CompanyId, r.ProviderId, r.Feature, r.Model, r.Classification, r.Destination,
                r.GovernanceDecision, r.Outcome, r.CorrelationId, r.OccurredAtUtc,
                (long)r.Duration.TotalMilliseconds, r.RequestBytes, r.InputTokens, r.OutputTokens,
                r.EstimatedCost, r.Currency, r.FailureCategory);

            // 2. Durable row. Single attempt, and its failure never propagates to the caller.
            bool persisted;
            try
            {
                persisted = await _store.WriteAsync(r, ct);
            }
            catch (OperationCanceledException)
            {
                // The caller's request was cancelled mid-audit. Losing the row is the correct outcome —
                // re-entering the data layer on a cancelled scope would be worse.
                throw;
            }
            catch (Exception ex)
            {
                // The store already catches its own failures; this is the belt for a future store that
                // forgets to. An observability fault must never surface as a request fault.
                _log.LogError(ex, "AI-EGRESS-AUDIT-STORE-THREW correlation={Correlation}", r.CorrelationId);
                persisted = false;
            }

            if (!persisted)
            {
                // A distinct, greppable marker: this is the line an alert should watch for, because it
                // means the durable trail has a hole that the log line above is now the only record of.
                _log.LogError(
                    "AI-EGRESS-AUDIT-NOT-DURABLE company={Company} provider={Provider} feature={Feature} " +
                    "outcome={Outcome} correlation={Correlation}. The call itself is unaffected and was " +
                    "NOT retried; the structured log line above is the only surviving record.",
                    r.CompanyId, r.ProviderId, r.Feature, r.Outcome, r.CorrelationId);
            }
        }
    }
}
