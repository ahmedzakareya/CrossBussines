using System.Collections.Concurrent;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 5. CIRCUIT BREAKER for external AI providers.
    //
    // WHY HAND-ROLLED RATHER THAN A PACKAGE. .NET 8 ships no circuit breaker for HttpClient;
    // Microsoft.Extensions.Http.Resilience and Polly are both real dependencies, and this repository has
    // zero resilience packages today. Roughly a hundred lines with a clear state machine is a smaller and
    // more reviewable change than adding a dependency tree to a project that has deliberately avoided one.
    // If resilience is later wanted across the whole application, the package is the right answer and this
    // class is the thing it replaces.
    //
    // ON RETRIES — THERE ARE NONE, DELIBERATELY.
    //
    // Every retry of an LLM call is a second paid call. The usage guard exists but has never run against a
    // real provider, so retry logic added now would be untested multiplication of an unmeasured cost. The
    // breaker handles the failure mode that matters (a provider that is down stays down for a while) and a
    // bounded retry can be added later once real usage numbers exist. "No infinite retries, no retry
    // storm" is satisfied by there being no retry at all.
    //
    // WHAT MUST NEVER OPEN THE CIRCUIT: an authorization or governance refusal. A 401 means the credential
    // is wrong and a governance denial means we should not be calling at all — neither is transient, and
    // counting them as failures would trip the breaker for a reason a breaker cannot fix, hiding the real
    // error behind a generic "circuit open".

    public enum AiCircuitState
    {
        Closed = 0,   // normal
        Open,         // failing; calls are refused without being attempted
        HalfOpen,     // one probe permitted
    }

    public sealed class AiCircuitDecision
    {
        public required bool Allowed { get; init; }
        public required AiCircuitState State { get; init; }
        public required string Reason { get; init; }
        public DateTime? RetryAfterUtc { get; init; }
    }

    public interface IAiCircuitBreaker
    {
        AiCircuitDecision TryEnter(string providerId, DateTime nowUtc);
        void RecordSuccess(string providerId, DateTime nowUtc);

        /// <param name="transient">
        /// False for authorization, governance and request-shape failures. Only transient failures count
        /// towards opening the circuit.
        /// </param>
        void RecordFailure(string providerId, DateTime nowUtc, bool transient);

        AiCircuitState StateOf(string providerId, DateTime nowUtc);
    }

    public sealed class AiCircuitBreaker : IAiCircuitBreaker
    {
        public const int DefaultFailureThreshold = 5;
        public static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromMinutes(1);

        private readonly int _threshold;
        private readonly TimeSpan _openFor;
        private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

        public AiCircuitBreaker() : this(DefaultFailureThreshold, DefaultOpenDuration) { }

        public AiCircuitBreaker(int failureThreshold, TimeSpan openDuration)
        {
            _threshold = failureThreshold > 0 ? failureThreshold : DefaultFailureThreshold;
            _openFor = openDuration > TimeSpan.Zero ? openDuration : DefaultOpenDuration;
        }

        private sealed class Circuit
        {
            public readonly object Gate = new();
            public AiCircuitState State = AiCircuitState.Closed;
            public int ConsecutiveFailures;
            public DateTime OpenedUtc;
            public bool ProbeInFlight;
        }

        private Circuit Get(string providerId) => _circuits.GetOrAdd(providerId ?? "", _ => new Circuit());

        public AiCircuitState StateOf(string providerId, DateTime nowUtc)
        {
            var c = Get(providerId);
            lock (c.Gate) { Transition(c, nowUtc); return c.State; }
        }

        /// Open → HalfOpen once the cool-down has elapsed. Called under the lock by every entry point so
        /// the state is never read stale.
        private void Transition(Circuit c, DateTime nowUtc)
        {
            if (c.State == AiCircuitState.Open && nowUtc - c.OpenedUtc >= _openFor)
            {
                c.State = AiCircuitState.HalfOpen;
                c.ProbeInFlight = false;
            }
        }

        public AiCircuitDecision TryEnter(string providerId, DateTime nowUtc)
        {
            var c = Get(providerId);
            lock (c.Gate)
            {
                Transition(c, nowUtc);

                switch (c.State)
                {
                    case AiCircuitState.Closed:
                        return new AiCircuitDecision { Allowed = true, State = c.State, Reason = "circuit:closed" };

                    case AiCircuitState.HalfOpen:
                        // Exactly ONE probe. Without this flag every queued caller would be admitted the
                        // instant the cool-down elapsed, which is the retry storm the breaker exists to
                        // prevent — arriving on schedule.
                        if (c.ProbeInFlight)
                            return new AiCircuitDecision
                            {
                                Allowed = false, State = c.State, Reason = "circuit:half-open-probe-in-flight",
                                RetryAfterUtc = c.OpenedUtc + _openFor,
                            };

                        c.ProbeInFlight = true;
                        return new AiCircuitDecision { Allowed = true, State = c.State, Reason = "circuit:half-open-probe" };

                    default:
                        return new AiCircuitDecision
                        {
                            Allowed = false, State = c.State, Reason = "circuit:open",
                            RetryAfterUtc = c.OpenedUtc + _openFor,
                        };
                }
            }
        }

        public void RecordSuccess(string providerId, DateTime nowUtc)
        {
            var c = Get(providerId);
            lock (c.Gate)
            {
                c.ConsecutiveFailures = 0;
                c.ProbeInFlight = false;
                c.State = AiCircuitState.Closed;
            }
        }

        public void RecordFailure(string providerId, DateTime nowUtc, bool transient)
        {
            var c = Get(providerId);
            lock (c.Gate)
            {
                c.ProbeInFlight = false;

                // A non-transient failure is recorded by the caller's audit and log, but it must not move
                // the breaker: a wrong API key would otherwise open the circuit and replace a precise
                // "unauthorized" with an opaque "circuit open" for the next minute.
                if (!transient) return;

                c.ConsecutiveFailures++;

                if (c.State == AiCircuitState.HalfOpen || c.ConsecutiveFailures >= _threshold)
                {
                    c.State = AiCircuitState.Open;
                    c.OpenedUtc = nowUtc;
                }
            }
        }
    }
}
