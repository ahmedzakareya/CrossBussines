using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CrossBuy.BL.Platform
{
    // ==========================================================================================
    // THE CERTIFICATION CLOCK — a fixed "now" for rendered UI conformance runs, and nothing else.
    //
    // WHY IT EXISTS, measured rather than assumed. Screenshot baselines could not be re-verified:
    // /Workspace/Notifications rendered "3h" at capture and "4h" an hour later. A DOM text diff over
    // a 65-second gap showed ten text nodes changing — every one of them a relative-time label, and
    // NOTHING else on the page. The timestamps are stable; the REFERENCE POINT moves. So this fixes
    // the reference point, never the data.
    //
    // WHAT IT IS NOT. It is not a general system clock, not an application-wide time abstraction,
    // and not a way to travel in time. Two other clocks already exist for their own domains
    // (IReportClock for report schedules, TaskCalendarTime.UtcNow for recurrence); neither is
    // consulted by a Razor view rendering "3h ago", which is why neither could be reused here. This
    // deliberately does NOT compete with them: it answers exactly one question — "what instant should
    // a relative-time LABEL be measured against while rendering this request".
    //
    // SECURITY. Three independent conditions must ALL hold before a request can move it:
    //   1. the host is in the Development environment,
    //   2. UI_CONFORMANCE=1 is set in the process environment,
    //   3. the request carries the X-UI-Conformance-Now header.
    // (1) and (2) are evaluated ONCE at startup in Program.cs, and the middleware is not even added
    // to the pipeline unless both are true — so in Production the header reaches no code that reads
    // it, and the property below can only ever return DateTime.Now. ConformanceClockGuardTests pins
    // that wiring. A browser cannot spoof production time because in production nothing is listening.
    //
    // AsyncLocal, not a static field: the value belongs to ONE request. A plain static would leak a
    // frozen clock into concurrent requests and into background workers.
    // ==========================================================================================
    public static class ConformanceClock
    {
        public const string HeaderName = "X-UI-Conformance-Now";
        public const string EnableEnvironmentVariable = "UI_CONFORMANCE";

        private static readonly AsyncLocal<DateTime?> Frozen = new();

        /// The instant a relative-time label should be measured against. Real wall-clock time unless
        /// a conformance run has frozen it for THIS request.
        public static DateTime Now => Frozen.Value ?? DateTime.Now;

        /// True only inside a conformance request. Exposed so a view or a test can assert the
        /// difference rather than guess at it.
        public static bool IsFrozen => Frozen.Value.HasValue;

        internal static void FreezeForRequest(DateTime at) => Frozen.Value = at;
        internal static void Release() => Frozen.Value = null;

        /// THE WHOLE SECURITY DECISION, as one pure function so it can be tested directly rather
        /// than inferred from wiring. Both conditions are required; neither alone is sufficient.
        public static bool ShouldEnable(bool isDevelopment, string? flagValue) =>
            isDevelopment && string.Equals(flagValue, "1", StringComparison.Ordinal);

        /// Informational only — what the host decided at startup. Nothing reads this to make a
        /// security decision; the middleware carries its own explicit flag.
        public static bool IsEnabledForThisHost { get; private set; }
        internal static void RecordHostDecision(bool enabled) => IsEnabledForThisHost = enabled;
    }

    public sealed class ConformanceClockMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly bool _enabled;

        /// `enabled` is an EXPLICIT constructor dependency rather than a static read: a middleware
        /// whose security gate is ambient can be enabled by anything that touches the static, and
        /// cannot be tested in isolation. Here the gate travels with the instance.
        public ConformanceClockMiddleware(RequestDelegate next, bool enabled)
        { _next = next; _enabled = enabled; }

        public async Task InvokeAsync(HttpContext context)
        {
            if (_enabled &&
                context.Request.Headers.TryGetValue(ConformanceClock.HeaderName, out var raw) &&
                DateTime.TryParse(raw.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            {
                ConformanceClock.FreezeForRequest(at);
                try { await _next(context); }
                finally { ConformanceClock.Release(); }
                return;
            }

            await _next(context);
        }
    }

    public static class ConformanceClockExtensions
    {
        /// Adds the middleware ONLY when both gates hold, and reports which decision was taken so a
        /// run cannot quietly proceed on real time while believing it is frozen.
        public static IApplicationBuilder UseConformanceClockIfEnabled(
            this IApplicationBuilder app, bool isDevelopment, ILogger? log = null)
        {
            var flag = Environment.GetEnvironmentVariable(ConformanceClock.EnableEnvironmentVariable);
            var enabled = ConformanceClock.ShouldEnable(isDevelopment, flag);

            ConformanceClock.RecordHostDecision(enabled);
            if (!enabled) return app;

            log?.LogWarning(
                "UI CONFORMANCE CLOCK ENABLED. Requests carrying {Header} will render relative-time " +
                "labels against a fixed instant. This is Development-only and must never be set in production.",
                ConformanceClock.HeaderName);

            return app.UseMiddleware<ConformanceClockMiddleware>(true);
        }
    }
}
