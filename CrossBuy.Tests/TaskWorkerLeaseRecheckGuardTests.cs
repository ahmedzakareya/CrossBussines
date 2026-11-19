using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    /// D-16 CALL SITES — THE TAB-5 BACKGROUND WORKERS, GUARDED.
    ///
    /// 4e471e4 gave IWorkerGate a per-tick re-check, IsStillAllowedAsync, because the lease can be lost
    /// mid-run: a dropped connection, a failover, another process taking it. The gate used to be asked ONCE
    /// before the loop, so an instance that lost primacy kept working beside the new primary and duplicated
    /// its output, unobserved. da7b97b wired TAB-1's three workers. These are TAB-5's three.
    ///
    /// WHY THIS IS A SOURCE-TEXT TEST AND NOT A BEHAVIOURAL ONE. Each worker opens with a multi-minute
    /// Task.Delay and then a PeriodicTimer measured in quarter-hours, and every tick's body needs a database.
    /// Driving one real tick would mean injecting a clock and a scope factory into three workers — changing
    /// the code under test to make it testable, in a change whose whole point is not to alter worker
    /// semantics. So this pins the SHAPE, and the shape is pinned at the four places it can go wrong rather
    /// than by matching one line.
    ///
    /// THESE ASSERTIONS FAIL OPEN BY NATURE, so each was proved by mutation: the re-check deleted, moved
    /// after the first CreateScope, and its park replaced with a bare return — each mutant was run and seen
    /// to go red before the file was restored. A green run here is only worth what that exercise established.
    ///
    /// NO DATABASE. Every assertion reads committed source text. It opens no connection of any kind.
    public sealed class TaskWorkerLeaseRecheckGuardTests
    {
        /// All six lease-gated background workers. TAB-5's three and — at TAB-1's request, 2026-11-04 —
        /// TAB-1's three from da7b97b. Only this test file is TAB-5's; none of those three sources is
        /// touched by it. The files differ in indentation (tabs, and spaces in BusinessEventDispatchWorker)
        /// and in line endings (TAB-1's three are mixed), so every assertion below is agnostic to both.
        public static TheoryData<string, string> Workers => new()
        {
            { "CrossBuy/BL/TaskGeneratorHostedService.cs", "TaskGeneratorHostedService" },
            { "CrossBuy/BL/TaskScheduleMatchHostedService.cs", "TaskScheduleMatchHostedService" },
            { "CrossBuy/BL/TasksCalendar/TaskEscalationHostedService.cs", "TaskEscalationHostedService" },
            { "CrossBuy/BL/IntegrityCheckHostedService.cs", "IntegrityCheckHostedService" },
            { "CrossBuy/BL/CrmReminderHostedService.cs", "CrmReminderHostedService" },
            { "CrossBuy/BL/Communication/CommWorkerHostedService.cs", "CommWorkerHostedService" },
            { "CrossBuy/BL/Platform/BusinessEventDispatchWorker.cs", "BusinessEventDispatchWorker" },
        };

        private static string Source(string relative)
        {
            var rel = relative.Replace('/', Path.DirectorySeparatorChar);

            // CROSSBUY_REPO_ROOT, the repo's existing escape hatch, is honoured for ONE reason: three of the
            // six workers belong to TAB-1, and a guard must be mutation-proved before it is believed. Proving
            // it by editing those files would mean modifying another tab's source in a shared tree. Instead a
            // shadow root holds a mutated COPY at the same relative path and this resolver reads that. A path
            // absent from the shadow root falls through to the real tree, so a shadow containing one file
            // leaves the other five rows reading the real sources and still green — which is what makes the
            // mutation evidence legible.
            var root = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(root))
            {
                var shadowed = Path.Combine(root, rel);
                if (File.Exists(shadowed)) return File.ReadAllText(shadowed);
            }

            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, rel)))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.True(dir is not null, $"Could not locate {rel} by walking up from {AppContext.BaseDirectory}.");
            return File.ReadAllText(Path.Combine(dir!, rel));
        }

        /// Comments are stripped first: all three files DISCUSS the lease and the re-check in prose, so a
        /// correct explanation must not satisfy — or defeat — an assertion about code.
        private static string CodeOnly(string source)
        {
            var code = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(code, @"^[^\S\r\n]*//.*$", string.Empty, RegexOptions.Multiline);
        }

        /// The body of the worker's `do { ... } while (...)` tick loop, as code with comments removed, taken
        /// from just AFTER the opening brace. Indentation- and line-ending-agnostic on purpose: the six files
        /// use tabs and spaces, LF and CRLF, and a guard that only matched one of those would quietly cover
        /// fewer workers than its TheoryData claims.
        private static string TickLoopBody(string code)
        {
            var open = Regex.Match(code, @"(?m)^[ \t]*do[ \t]*\r?\n[ \t]*\{[ \t]*\r?\n");
            Assert.True(open.Success, "Could not find the worker's `do {` tick loop.");

            var after = open.Index + open.Length;
            var end = code.IndexOf("while (", after, StringComparison.Ordinal);
            Assert.True(end > after, "Could not find the tick loop's closing `while`.");
            return code[after..end];
        }

        /// 1. THE RE-CHECK EXISTS, AND IT IS ASKED INSIDE THE TICK LOOP. Asked outside it, it is the startup
        /// gate again and buys nothing — losing the lease on tick 400 would still go unnoticed.
        [Theory]
        [MemberData(nameof(Workers))]
        public void Every_tick_asks_whether_the_lease_is_still_held(string relative, string worker)
        {
            var body = TickLoopBody(CodeOnly(Source(relative)));

            Assert.Contains($"IsStillAllowedAsync(nameof({worker}), stoppingToken)", body, StringComparison.Ordinal);
        }

        /// 2. ORDER, NOT PRESENCE — the re-check is the FIRST statement of the tick, ahead of the original
        /// `try`. Requested by TAB-1, and it is the right assertion: "the call exists somewhere in the loop"
        /// fails open, because a re-check that runs after the tick has opened its scope and written reports
        /// the duplicate rather than preventing it. Asserting position cannot be satisfied by a call that
        /// has drifted below the work.
        ///
        /// This replaces an earlier, weaker form that only required the re-check to precede the first
        /// CreateScope(). That was both laxer and more brittle — it would have failed on a worker that
        /// legitimately stopped opening a scope, while still passing a re-check placed after other work.
        [Theory]
        [MemberData(nameof(Workers))]
        public void The_re_check_is_the_first_statement_of_every_tick(string relative, string worker)
        {
            var body = TickLoopBody(CodeOnly(Source(relative))).TrimStart();

            Assert.StartsWith($"if (!await _gate.IsStillAllowedAsync(nameof({worker}), stoppingToken))",
                body, StringComparison.Ordinal);
        }

        /// 3. A LOST LEASE PARKS; IT DOES NOT END THE WORKER. This is the one that matters most and the one an
        /// ordinary reading of the code invites getting wrong: `if (!still allowed) return;` looks obviously
        /// correct and is the defect. Returning ends the BackgroundService for good, so if the process that
        /// took the lease later dies, nothing reacquires it and NO worker runs anywhere until a restart.
        [Theory]
        [MemberData(nameof(Workers))]
        public void Losing_the_lease_parks_the_worker_rather_than_ending_it(string relative, string worker)
        {
            var code = CodeOnly(Source(relative));
            var body = TickLoopBody(code);

            var m = Regex.Match(body,
                // The argument list contains nameof(...), so the parentheses nest one level.
                @"if\s*\(\s*!\s*await\s+_gate\.IsStillAllowedAsync\((?:[^()]|\([^()]*\))*\)\s*\)\s*"
                + @"(?<consequent>\{(?:[^{}]|\{[^{}]*\})*\}|[^;]*;)",
                RegexOptions.Singleline);
            Assert.True(m.Success, $"{worker}: could not read the re-check's false branch.");

            var consequent = m.Groups["consequent"].Value;
            // The park must name THIS worker's lease. Asserting only that WaitUntilAllowedAsync appears
            // leaves the argument unsaid, and a worker parking on another worker's lease would satisfy it
            // while waiting on the wrong thing — raised by TAB-1 as "what would this still pass?", and it
            // would have passed exactly that.
            Assert.Contains($"WaitUntilAllowedAsync(nameof({worker}), stoppingToken)", consequent,
                StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"^\s*return\s*;"), consequent);
        }

        /// 4. THE STARTUP GATE SURVIVES. The per-tick check is an addition, not a replacement: without the
        /// blocking wait before the loop, a standby would spin through ticks instead of waiting quietly.
        [Theory]
        [MemberData(nameof(Workers))]
        public void The_startup_gate_is_still_there(string relative, string worker)
        {
            var code = CodeOnly(Source(relative));
            var loop = Regex.Match(code, @"(?m)^[ \t]*do[ \t]*\r?$");
            Assert.True(loop.Success, "Could not find the worker's `do` tick loop.");

            var beforeLoop = code[..loop.Index];
            Assert.Contains($"WaitUntilAllowedAsync(nameof({worker}), stoppingToken)", beforeLoop, StringComparison.Ordinal);
        }
    }
}
