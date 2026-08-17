using CrossBuy.BL.Platform;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// The certification clock lets a conformance run render relative-time labels against a fixed
/// instant, so screenshot baselines stop drifting with the wall clock. It is also, by construction,
/// a way to make the server report a different "now" — so what is worth testing is not that it
/// works, but that it CANNOT work outside a Development conformance run.
///
/// Two gates guard it and both are asserted here: the pure enable decision, and the middleware's
/// own explicit flag. The middleware takes that flag as a constructor argument rather than reading
/// ambient state, which is what makes this testable without spinning up a host.
/// </summary>
public sealed class ConformanceClockGuardTests
{
    // ---- the security decision, tested directly -------------------------------------------------

    [Theory]
    [InlineData(false, "1")]     // production, flag set        -> OFF
    [InlineData(false, null)]    // production, no flag         -> OFF
    [InlineData(true, null)]     // development, no flag        -> OFF
    [InlineData(true, "0")]      // development, flag not "1"   -> OFF
    [InlineData(true, "true")]   // development, truthy-ish     -> OFF (exact "1" required)
    [InlineData(true, "")]       // development, empty          -> OFF
    public void The_clock_is_refused_unless_development_AND_the_exact_flag_are_both_present(bool isDevelopment, string? flag)
        => Assert.False(ConformanceClock.ShouldEnable(isDevelopment, flag));

    [Fact]
    public void The_clock_is_permitted_only_in_development_with_the_exact_flag()
        => Assert.True(ConformanceClock.ShouldEnable(isDevelopment: true, flagValue: "1"));

    // ---- the middleware ------------------------------------------------------------------------

    /// PRODUCTION SAFETY. A request carrying the header against a disabled middleware must see real
    /// time. In production the middleware is not even added to the pipeline; this proves that even
    /// if it were, the header alone achieves nothing.
    [Fact]
    public async Task The_header_is_ignored_when_the_middleware_gate_is_off()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ConformanceClock.HeaderName] = "2020-01-01T00:00:00.0000000";

        DateTime seen = default;
        var middleware = new ConformanceClockMiddleware(_ => { seen = ConformanceClock.Now; return Task.CompletedTask; }, enabled: false);
        await middleware.InvokeAsync(context);

        Assert.False(ConformanceClock.IsFrozen);
        Assert.True(seen > DateTime.Now.AddMinutes(-1),
            "a disabled middleware still froze the clock — the header must be inert without the gate");
    }

    [Fact]
    public async Task With_the_gate_on_the_header_freezes_the_clock_for_that_request()
    {
        var fixedAt = new DateTime(2026, 3, 14, 9, 26, 53);
        var context = new DefaultHttpContext();
        context.Request.Headers[ConformanceClock.HeaderName] = fixedAt.ToString("O");

        DateTime seen = default;
        var middleware = new ConformanceClockMiddleware(_ => { seen = ConformanceClock.Now; return Task.CompletedTask; }, enabled: true);
        await middleware.InvokeAsync(context);

        Assert.Equal(fixedAt, seen);
    }

    /// A frozen clock must not outlive its request, or one conformance call would pin time for
    /// every later request on the same execution context.
    [Fact]
    public async Task The_frozen_clock_is_released_after_the_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ConformanceClock.HeaderName] = "2026-03-14T09:26:53.0000000";

        var middleware = new ConformanceClockMiddleware(_ => Task.CompletedTask, enabled: true);
        await middleware.InvokeAsync(context);

        Assert.False(ConformanceClock.IsFrozen);
        Assert.True(ConformanceClock.Now > DateTime.Now.AddMinutes(-1));
    }

    /// …including when the pipeline throws. A leaked frozen clock after an error would be worse
    /// than the drift this whole mechanism exists to remove.
    [Fact]
    public async Task The_frozen_clock_is_released_even_when_the_pipeline_throws()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ConformanceClock.HeaderName] = "2026-03-14T09:26:53.0000000";

        var middleware = new ConformanceClockMiddleware(_ => throw new InvalidOperationException("boom"), enabled: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.False(ConformanceClock.IsFrozen);
    }

    [Fact]
    public async Task A_malformed_header_is_ignored_rather_than_throwing()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ConformanceClock.HeaderName] = "not-a-timestamp";

        var reached = false;
        var middleware = new ConformanceClockMiddleware(_ => { reached = true; return Task.CompletedTask; }, enabled: true);
        await middleware.InvokeAsync(context);

        Assert.True(reached);
        Assert.False(ConformanceClock.IsFrozen);
    }

    [Fact]
    public void With_no_request_in_flight_the_clock_is_real_wall_clock_time()
    {
        Assert.False(ConformanceClock.IsFrozen);

        var before = DateTime.Now;
        var reported = ConformanceClock.Now;
        var after = DateTime.Now;

        Assert.InRange(reported, before.AddSeconds(-1), after.AddSeconds(1));
    }
}
