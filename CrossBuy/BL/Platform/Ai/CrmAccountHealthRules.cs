namespace CrossBuy.BL.Platform.Ai
{
    // CRM ACCOUNT HEALTH — DETERMINISTIC RULES OVER TWO COMPARABLE WINDOWS.
    //
    // RE-AUDITED BEFORE WRITING THIS FILE: crossbuy_ai still ships exactly three models — journal
    // anomaly, cashflow, inventory — and none of them takes an account, an opportunity or a lead. So
    // there is still no churn probability, no health score and no confidence figure to be had, and this
    // file invents none. Everything below is arithmetic over counts the reader can check.
    //
    // WHY THIS IS NOT A SECOND COPY OF CrmOpportunityRiskRules. That file answers "which DEALS need
    // attention" and looks at one opportunity at a time. This one answers "which RELATIONSHIPS are
    // weakening" and looks at an account's engagement over time — a question no per-opportunity rule can
    // reach, because an account can hold three healthy-looking deals and still have gone silent.
    //
    // THE HONESTY PROBLEM AT THE HEART OF "DECLINE". A percentage drop is only meaningful against a
    // baseline big enough to have a trend. Two facts that look identical as ratios are completely
    // different situations:
    //
    //     previous 12, recent 3   -> a real, measurable slowdown worth a manager's time
    //     previous 1,  recent 0   -> one call did not repeat. That is not a 100% decline; it is noise
    //                               dressed up as a statistic.
    //
    // So a DECLINE finding requires a baseline of at least MinimumBaselineActivities, and every finding
    // carries the two raw counts it was derived from. Below that baseline the honest finding is "activity
    // stopped", stated without a percentage.
    public static class CrmAccountHealthRules
    {
        // ---- the window policy, named so the screen can state it ----
        //
        // 30 and 30. Chosen because CRM engagement is a monthly-cadence activity in this product — calls,
        // meetings and follow-up tasks — so a month is the shortest window in which "we stopped talking
        // to them" is a fact rather than a gap between two calls. The two windows are EQUAL LENGTH and
        // ADJACENT, which is what makes their counts comparable at all; a 30-vs-90 comparison would show
        // a "decline" for every account in the product.
        public const int RecentWindowDays = 30;
        public const int PreviousWindowDays = 30;

        /// <summary>Activities required in the previous window before a DROP may be called a decline.</summary>
        /// <remarks>
        /// Four, not one. With a baseline of one, every account that had a single call last month and
        /// none this month becomes a "100% decline", and the list fills with noise until nobody reads it.
        /// Below this baseline the account can still be flagged — as "activity stopped", which is what
        /// actually happened — but never with a percentage.
        /// </remarks>
        public const int MinimumBaselineActivities = 4;

        /// <summary>Recent must be below this share of previous to count as a material decline.</summary>
        public const decimal MaterialDeclineRatio = 0.5m;

        /// Open pipeline value at or above which a finding is escalated. A round, stated number — not a
        /// percentile, because a percentile moves when the pipeline moves and a reader cannot check it.
        public const decimal HighExposureThreshold = 100_000m;

        /// <summary>One account reduced to the facts the rules need. No EF types, no navigation.</summary>
        /// <remarks>
        /// Counts are non-nullable because they are computed by aggregation and zero is a real answer.
        /// <see cref="LastActivityAt"/> is nullable because "never" is a genuinely different state from
        /// "a long time ago", and the screen must not print one as the other.
        /// </remarks>
        public sealed record Signal
        {
            public required int AccountId { get; init; }
            public required string Name { get; init; }
            public int? OwnerEmployeeId { get; init; }
            public string? OwnerName { get; init; }

            /// Activities in the last RecentWindowDays.
            public int RecentActivityCount { get; init; }

            /// Activities in the RecentWindowDays before that.
            public int PreviousActivityCount { get; init; }

            /// Most recent activity of any age. Null = this account has never had one.
            public DateTime? LastActivityAt { get; init; }

            /// Activities still open (Done = false) against this account or its opportunities.
            public int OpenFollowUpCount { get; init; }

            public int OpenOpportunityCount { get; init; }
            public decimal OpenOpportunityValue { get; init; }

            /// Open opportunities whose expected close date has already passed.
            public int PastDueOpportunityCount { get; init; }
            public decimal PastDueOpportunityValue { get; init; }
        }

        public enum Finding
        {
            /// Recent activity is materially below a baseline large enough to mean something.
            DecliningActivity,

            /// There is history, but nothing in the recent window. No percentage is claimed.
            ActivityStopped,

            /// No activity has ever been recorded against this account.
            NoActivityEver,

            /// Open opportunities exist and nobody has touched the account in the recent window.
            OpenOpportunitiesEngagementStopped,

            /// One or more open opportunities are past the date the owner committed to.
            OverdueOpportunityExposure,

            /// Nothing is scheduled next against this account or any of its opportunities.
            NoNextAction,
        }

        public enum Severity { Low = 0, Medium = 1, High = 2 }

        /// <summary>One surfaced signal, with the numbers it was derived from.</summary>
        /// <remarks>
        /// The counts travel WITH the finding rather than being looked up again by the view. A screen
        /// that says "activity declined" and separately renders today's numbers can drift into showing a
        /// conclusion beside evidence that no longer supports it.
        /// </remarks>
        public sealed record Insight
        {
            public required Signal Account { get; init; }
            public required Finding Finding { get; init; }
            public required Severity Severity { get; init; }

            /// Days since the last activity. Null when there has never been one.
            public int? DaysSinceLastActivity { get; init; }

            /// Whole-percent drop from previous to recent. Populated ONLY for DecliningActivity, where a
            /// baseline exists to divide by. Null everywhere else, deliberately.
            public int? DeclinePercent { get; init; }
        }

        /// <summary>Stages that mean an opportunity is finished, as a list a SQL translator accepts.</summary>
        /// <remarks>
        /// A CLOSED DEAL IS NOT EXPOSURE. Counting a Won opportunity as "open pipeline at risk" would
        /// inflate every account's exposure with money that has already landed, and counting a Lost one
        /// would invent risk from a decision already taken.
        /// </remarks>
        public static readonly List<string> ClosedStagesForExposure = new() { "Won", "Lost" };

        /// <summary>True when the drop is both large enough and measured against a real baseline.</summary>
        public static bool IsMaterialDecline(int previous, int recent) =>
            previous >= MinimumBaselineActivities && recent < previous * MaterialDeclineRatio;

        /// <summary>Everything worth surfacing for one account, most serious first.</summary>
        public static IReadOnlyList<Insight> Evaluate(Signal a, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(a);

            var today = nowUtc.Date;
            var found = new List<Insight>();
            int? quiet = a.LastActivityAt.HasValue ? (today - a.LastActivityAt.Value.Date).Days : null;

            // HIGH EXPOSURE IS AN AMPLIFIER, NEVER A FINDING. An account with a large open pipeline and
            // healthy engagement is the best kind of account; listing it as a risk would bury the real
            // ones and teach the reader that the list is noise.
            var exposed = a.OpenOpportunityValue >= HighExposureThreshold;

            Severity Escalate(Severity baseline) =>
                exposed && baseline < Severity.High ? baseline + 1 : baseline;

            // ---- 1. engagement over the two windows ----
            if (a.LastActivityAt == null)
            {
                // Never contacted. More urgent when money is already on the table against this account.
                found.Add(new Insight
                {
                    Account = a,
                    Finding = Finding.NoActivityEver,
                    Severity = a.OpenOpportunityCount > 0 ? Escalate(Severity.Medium) : Severity.Low,
                    DaysSinceLastActivity = null,
                });
            }
            else if (IsMaterialDecline(a.PreviousActivityCount, a.RecentActivityCount))
            {
                // A baseline exists, so a percentage is meaningful and is shown alongside both counts.
                var drop = (int)Math.Round(
                    (a.PreviousActivityCount - a.RecentActivityCount) * 100m / a.PreviousActivityCount,
                    MidpointRounding.AwayFromZero);

                found.Add(new Insight
                {
                    Account = a,
                    Finding = Finding.DecliningActivity,
                    Severity = Escalate(Severity.Medium),
                    DaysSinceLastActivity = quiet,
                    DeclinePercent = drop,
                });
            }
            else if (a.RecentActivityCount == 0)
            {
                // History exists but the recent window is empty, and the baseline was too small to call
                // it a trend. NO PERCENTAGE IS CLAIMED — this is the previous=1, recent=0 case, and
                // "100% decline" would be a statistic invented from a single data point.
                found.Add(new Insight
                {
                    Account = a,
                    Finding = Finding.ActivityStopped,
                    Severity = Escalate(Severity.Medium),
                    DaysSinceLastActivity = quiet,
                });
            }

            // NOTE what has NO branch here: previous = 0 and recent = 0 with a LastActivityAt older than
            // both windows falls into ActivityStopped above, which is correct and states no ratio. An
            // account with zero in both windows and no history at all is NoActivityEver. Neither is ever
            // described as a decline, because dividing by zero is not a finding.

            // ---- 2. money exposed to an engagement problem ----
            if (a.OpenOpportunityCount > 0 && a.RecentActivityCount == 0 && a.LastActivityAt != null)
            {
                found.Add(new Insight
                {
                    Account = a,
                    Finding = Finding.OpenOpportunitiesEngagementStopped,
                    Severity = Escalate(Severity.Medium),
                    DaysSinceLastActivity = quiet,
                });
            }

            if (a.PastDueOpportunityCount > 0)
            {
                found.Add(new Insight
                {
                    Account = a,
                    Finding = Finding.OverdueOpportunityExposure,
                    Severity = Escalate(Severity.Medium),
                    DaysSinceLastActivity = quiet,
                });
            }

            // ---- 3. is anything scheduled next? ----
            //
            // Proven from the authoritative relation: an activity is linked to this account directly, or
            // to one of its opportunities. Nothing is inferred from unrelated data.
            if (a.OpenFollowUpCount == 0 && a.OpenOpportunityCount > 0)
            {
                found.Add(new Insight
                {
                    Account = a,
                    Finding = Finding.NoNextAction,
                    Severity = Severity.Low,
                    DaysSinceLastActivity = quiet,
                });
            }

            return found.OrderByDescending(f => f.Severity).ThenBy(f => f.Finding).ToList();
        }

        /// <summary>Every account insight, in the order a manager should work them.</summary>
        /// <remarks>
        /// THE ORDER IS TOTAL AND EXPLAINABLE, and the screen states it in these words:
        ///
        ///     1. most serious first
        ///     2. then the longest silent
        ///     3. then the largest open pipeline value
        ///     4. then the lowest account id
        ///
        /// An account that has NEVER been contacted sorts as maximally silent, which is the intent: it
        /// has been quiet for its whole life. Step 4 makes the order total, so two identical loads never
        /// reorder. There are no hidden weights — every position is justified by a value in the row.
        /// </remarks>
        public static IReadOnlyList<Insight> Rank(IEnumerable<Insight> insights)
        {
            ArgumentNullException.ThrowIfNull(insights);
            return insights
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.DaysSinceLastActivity ?? int.MaxValue)
                .ThenByDescending(i => i.Account.OpenOpportunityValue)
                .ThenBy(i => i.Account.AccountId)
                .ThenBy(i => i.Finding)
                .ToList();
        }

        public static IReadOnlyList<Insight> Analyse(IEnumerable<Signal> accounts, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(accounts);
            return Rank(accounts.SelectMany(a => Evaluate(a, nowUtc)));
        }
    }
}
