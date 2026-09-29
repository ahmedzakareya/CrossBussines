using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Orchestration
{
    // =============================================================================================
    // ONE SOURCE FOR ESCALATION CADENCE.
    //
    // Phase 2 put grace, interval and max level behind configuration keys inside the escalation
    // service and said plainly that the defaults were not confirmed business policy. Phase 4's job is
    // not to confirm them — that is the owner's — but to stop them being three private constants in
    // one service that nothing else can see or agree with.
    //
    // TWO LAYERS, BECAUSE TWO LAYERS ARE WHAT THE PLATFORM CAN ACTUALLY EXPRESS TODAY:
    //
    //     platform default        Tasks:Escalation:GraceHours
    //     company override        Tasks:Escalation:Company:{id}:GraceHours
    //
    // A RULE-LEVEL OVERRIDE IS DELIBERATELY ABSENT. It would be easy to add and there is no evidence
    // for it: every kind of work in this estate escalates on the same clock today, and a per-rule
    // cadence is a product decision with a support cost, not a missing feature. Adding it silently
    // would also make "why did this escalate at that moment?" a per-rule question.
    //
    // WHY CONFIGURATION AND NOT A TABLE. A per-company timing store is one column set away, and it is
    // written up as a schema proposal rather than executed — Phase 4 runs at zero DDL. Configuration
    // gives the company layer honestly for the number of tenants this estate has, and the proposal
    // records what to do when that stops being true.
    // =============================================================================================
    public sealed class TaskEscalationTiming
    {
        /// The Phase-2 defaults, unchanged. They are still PRODUCT POLICY DEBT: nothing in the
        /// repository confirms 24/24/2 as a business rule, and this class does not promote them into
        /// one — it only makes them addressable.
        public const int DefaultGraceHours = 24;
        public const int DefaultLevelAdvanceHours = 24;
        public const int DefaultMaxLevel = 2;

        /// A ceiling no configuration can raise. An unbounded climb on a user-maintained org tree is
        /// how one task ends up in a whole company's inbox.
        public const int AbsoluteMaxLevel = 5;

        public required int GraceHours { get; init; }
        public required int LevelAdvanceHours { get; init; }
        public required int MaxLevel { get; init; }

        /// Which layer each value came from, so a support question is answerable without guessing.
        public required bool CompanyOverridden { get; init; }

        public static TaskEscalationTiming Defaults => new()
        {
            GraceHours = DefaultGraceHours,
            LevelAdvanceHours = DefaultLevelAdvanceHours,
            MaxLevel = DefaultMaxLevel,
            CompanyOverridden = false,
        };

        /// `companyId` 0 resolves the platform layer only — used where no company is in hand yet.
        public static TaskEscalationTiming Resolve(IConfiguration? config, int companyId)
        {
            int platformGrace = Read(config, "Tasks:Escalation:GraceHours", DefaultGraceHours, min: 0);
            int platformAdvance = Read(config, "Tasks:Escalation:LevelAdvanceHours", DefaultLevelAdvanceHours, min: 1);
            int platformMax = Read(config, "Tasks:Escalation:MaxLevel", DefaultMaxLevel, min: 1);

            int grace = platformGrace, advance = platformAdvance, max = platformMax;
            bool overridden = false;

            if (companyId > 0 && config != null)
            {
                string prefix = $"Tasks:Escalation:Company:{companyId}:";
                int g = Read(config, prefix + "GraceHours", grace, min: 0);
                int a = Read(config, prefix + "LevelAdvanceHours", advance, min: 1);
                int m = Read(config, prefix + "MaxLevel", max, min: 1);
                overridden = g != grace || a != advance || m != max;
                grace = g; advance = a; max = m;
            }

            return new TaskEscalationTiming
            {
                GraceHours = grace,
                LevelAdvanceHours = advance,
                MaxLevel = Math.Min(max, AbsoluteMaxLevel),
                CompanyOverridden = overridden,
            };
        }

        private static int Read(IConfiguration? config, string key, int fallback, int min) =>
            int.TryParse(config?[key], out var v) && v >= min ? v : fallback;
    }
}
