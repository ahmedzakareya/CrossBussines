using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // "DOES THIS DATABASE ACTUALLY HAVE THAT TABLE?" — asked once, cached, and shared by every
    // feature in this platform that ships its schema as a separate, optionally-applied slice.
    //
    // WHY THIS IS ONE CLASS AND NOT ONE PER FEATURE. Task links and push tokens have the same
    // problem and nothing else in common: a deploy/sql slice that some deployments have run and
    // others have not, a store that must not be registered against a database without the table, and
    // a configuration flag that used to be asked instead of the database. The CACHING RULE is the
    // part worth getting right exactly once — when to re-probe, what never to remember, what happens
    // when twenty requests ask at the same moment — and a second copy of it is a second place for
    // that rule to be subtly different.
    //
    // THE PROBE ITSELF STAYS PER-FEATURE. Each slice creates differently-named objects, and a probe
    // that took a list of object names as data would turn a compile-time fact into a string nobody
    // checks. ICommTaskLinkSchemaProbe and its siblings each name their own objects as constants;
    // this class only decides how often one of them is believed.
    // =============================================================================================
    public readonly struct CommSchemaAnswer
    {
        public required bool Provisioned { get; init; }

        // Every expected object that was not found, named, so an operator learns what to fix from the
        // check rather than by reading the DDL and comparing by hand.
        public required IReadOnlyList<string> Missing { get; init; }

        // Set when the probe could not determine the answer at all — a provider without system
        // catalogues, a connection that was not up, a query that threw. NOT the same as "checked and
        // absent", and the difference is the whole reason this field exists; see below.
        public string? Inconclusive { get; init; }
    }

    public sealed class CommSchemaProvisioning
    {
        private const int Unknown = 0;
        private const int Provisioned = 1;
        private const int Absent = 2;

        // Long enough that a database which is genuinely absent is not re-probed on every request,
        // short enough that a deployment which came up before its database does not stay broken.
        //
        // PUBLIC so a guard can assert the cooldown by advancing past THIS value rather than by
        // hard-coding 60 seconds — there is no InternalsVisibleTo in this repository, and a test that
        // pinned the number would need editing every time the number is tuned, which is how a guard
        // quietly stops guarding. Same reason as InventoryApprovalService's test hook.
        public static readonly TimeSpan InconclusiveCooldown = TimeSpan.FromSeconds(60);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Func<DateTimeOffset> _now;
        private readonly string _subject;

        private int _state = Unknown;

        // Ticks rather than a DateTimeOffset because this is read outside the gate and Volatile has no
        // overload for a user-defined struct; Volatile.Read(ref long) is atomic on every architecture.
        // Zero means "no inconclusive probe yet", which reads as not in cooldown.
        private long _lastInconclusiveTicks;
        private string? _reason;

        // The subject names what is being checked ("CommTaskLinks"), and appears in every log line
        // this class writes. One cache instance per subject.
        public CommSchemaProvisioning(string subject) : this(subject, () => DateTimeOffset.UtcNow) { }

        // THE CLOCK SEAM. The cooldown is the one piece of behaviour here that cannot be observed
        // without controlling time: a test that actually waited 60 seconds is a test nobody runs.
        public CommSchemaProvisioning(string subject, Func<DateTimeOffset> now)
        {
            _subject = subject;
            _now = now;
        }

        // What the last conclusive or inconclusive probe said, for diagnostics. Null before the first.
        public string? Reason => Volatile.Read(ref _reason);

        private bool InInconclusiveCooldown()
        {
            var last = Volatile.Read(ref _lastInconclusiveTicks);
            return last != 0 && _now().UtcTicks - last < InconclusiveCooldown.Ticks;
        }

        // True only once a probe has conclusively found every expected object.
        public bool IsProvisioned(Func<CancellationToken, Task<CommSchemaAnswer>> check, ILogger? log = null)
        {
            var state = Volatile.Read(ref _state);
            if (state != Unknown) return state == Provisioned;

            if (InInconclusiveCooldown()) return false;

            // SYNCHRONOUS ON PURPOSE, AND ONLY HERE. The stores that consume this expose IsProvisioned
            // as a PROPERTY, read by callers as a gate BEFORE they do anything else (five such call
            // sites for task links alone, including TasksController.cs:2052), so it cannot become a
            // Task without changing files this platform does not own. Blocking is bounded: one
            // catalogue query, at most once per process per conclusive answer, on a host with no
            // synchronization context to deadlock against.
            return ResolveAsync(check, log, CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<bool> ResolveAsync(
            Func<CancellationToken, Task<CommSchemaAnswer>> check,
            ILogger? log = null,
            CancellationToken cancellationToken = default)
        {
            var state = Volatile.Read(ref _state);
            if (state != Unknown) return state == Provisioned;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Re-read under the gate: a second caller that queued behind the first probe must use
                // its answer rather than run a second catalogue query.
                state = Volatile.Read(ref _state);
                if (state != Unknown) return state == Provisioned;
                if (InInconclusiveCooldown()) return false;

                var answer = await check(cancellationToken);

                if (answer.Inconclusive != null)
                {
                    // NOT AN ANSWER, and NOT CACHED. Remembering this would disable the feature for
                    // the whole lifetime of the process over a transient fault, with no error anywhere
                    // and no way back except a restart. Recorded as "ask again later" instead.
                    Volatile.Write(ref _lastInconclusiveTicks, _now().UtcTicks);
                    Volatile.Write(ref _reason, answer.Inconclusive);
                    log?.LogDebug(
                        "{Subject} provisioning is undetermined ({Reason}); the feature stays off until the next check.",
                        _subject, answer.Inconclusive);
                    return false;
                }

                Volatile.Write(ref _reason,
                    answer.Provisioned ? null : "missing: " + string.Join(", ", answer.Missing));
                Volatile.Write(ref _state, answer.Provisioned ? Provisioned : Absent);

                if (answer.Provisioned)
                    log?.LogInformation("{Subject} is provisioned on this database; the feature is active.", _subject);
                else
                    log?.LogInformation(
                        "{Subject} is not provisioned on this database ({Missing}); the feature will refuse with a reason rather than fail.",
                        _subject, string.Join(", ", answer.Missing));

                return answer.Provisioned;
            }
            catch (OperationCanceledException) { throw; }
            finally { _gate.Release(); }
        }
    }
}
