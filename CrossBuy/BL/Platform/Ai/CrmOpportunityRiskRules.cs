namespace CrossBuy.BL.Platform.Ai
{
    // CRM OPPORTUNITY INSIGHTS — TRANSPARENT BUSINESS RULES, NOT A MODEL.
    //
    // READ THIS BEFORE ADDING A CLASSIFICATION. There is no CRM model anywhere in this product. The
    // on-premises AI service (crossbuy_ai) ships exactly three models — journal anomaly, cashflow
    // projection and inventory analysis — and none of them takes an opportunity, a lead or an account.
    // Nothing in the repository computes a win probability, a churn probability, a revenue forecast, a
    // confidence score or a predicted close date.
    //
    // So this file is DETERMINISTIC BUSINESS RULES over fields the CRM already stores, and the screen
    // says so in those words. That is a deliberate product decision rather than a limitation worked
    // around: an "AI score" with no model behind it is the single most damaging thing this surface could
    // ship. A salesperson who is told "AI says 23% win chance" will believe it, act on it, and never
    // learn that the number came from nowhere. A salesperson told "last activity was 24 days ago" can
    // check the claim in one click and decide for themselves.
    //
    // WHAT THE CRM ACTUALLY STORES, and therefore what may be said here:
    //     Opportunity  Stage, Amount, Probability, ExpectedCloseDate, CreatedAt, OwnerEmployeeId
    //     Activity     OpportunityId, DueDate, Done, CreatedAt
    //
    // NOTE WHAT IS ABSENT. Opportunity has NO modification timestamp, so "recently changed opportunity
    // needing review" cannot be computed and is not offered. `Probability` IS stored — but a human typed
    // it, so it is displayed as the owner's own estimate and is never treated as a model output.
    public static class CrmOpportunityRiskRules
    {
        // ---- thresholds, named and in one place so the screen can state them ----

        /// Days without any CRM activity before an open opportunity is called stale.
        public const int StaleActivityDays = 30;

        /// Days open before an opportunity is called long-running, regardless of activity.
        public const int LongOpenDays = 120;

        /// Days ahead within which an expected close date counts as imminent.
        public const int ClosingSoonDays = 14;

        /// Stages that mean the opportunity is finished. Everything else is open.
        public static readonly IReadOnlyList<string> ClosedStages = new[] { "Won", "Lost" };

        public static bool IsOpen(string? stage) =>
            !ClosedStages.Contains(stage ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        /// <summary>One opportunity, reduced to the facts the rules need. No EF types, no navigation.</summary>
        public sealed record Signal
        {
            public required int OpportunityId { get; init; }
            public required string Title { get; init; }
            public int? AccountId { get; init; }
            public string? AccountName { get; init; }
            public string? Stage { get; init; }
            public decimal Amount { get; init; }
            public int Probability { get; init; }
            public DateTime? ExpectedCloseDate { get; init; }
            public DateTime? CreatedAt { get; init; }
            public int? OwnerEmployeeId { get; init; }
            public string? OwnerName { get; init; }

            /// The most recent activity of any kind on this opportunity. Null = none has ever existed.
            public DateTime? LastActivityAt { get; init; }

            /// Activities still open (Done = false). Zero means nobody has a next step written down.
            public int OpenActivityCount { get; init; }
        }

        public enum Finding
        {
            /// The date the owner committed to has passed and the opportunity is still open.
            PastExpectedClose,

            /// No activity row has ever referenced this opportunity.
            NoActivityEver,

            /// There is activity history, but the most recent is older than StaleActivityDays.
            StaleActivity,

            /// Open, and the expected close date is inside ClosingSoonDays.
            ClosingSoon,

            /// Nobody has an open follow-up recorded.
            NoNextAction,

            /// Open for longer than LongOpenDays.
            LongOpen,

            /// Open with no expected close date recorded at all — a data gap, not a judgement.
            NoExpectedCloseDate,
        }

        public enum Severity { Low = 0, Medium = 1, High = 2 }

        /// <summary>One surfaced insight: what was noticed, how serious, and the evidence for it.</summary>
        /// <remarks>
        /// <see cref="Evidence"/> is a structured fact — a count of days — rather than a sentence, so the
        /// view can render it in the reader's own language. The rules layer states no prose in any
        /// language; if it did, one of the two cultures would always be a translation of the other.
        /// </remarks>
        public sealed record Insight
        {
            public required Signal Opportunity { get; init; }
            public required Finding Finding { get; init; }
            public required Severity Severity { get; init; }

            /// The number the finding rests on: days overdue, days since activity, days open.
            public int? EvidenceDays { get; init; }
        }

        /// <summary>
        /// Everything worth surfacing for one opportunity, most serious first. Empty when nothing applies.
        /// </summary>
        /// <remarks>
        /// CLOSED OPPORTUNITIES ARE NEVER FLAGGED. A Won deal with no activity for six months is finished,
        /// not neglected, and putting it on a work list would train the reader to ignore the list.
        /// </remarks>
        public static IReadOnlyList<Insight> Evaluate(Signal s, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (!IsOpen(s.Stage)) return Array.Empty<Insight>();

            var found = new List<Insight>();
            var today = nowUtc.Date;

            // HIGH VALUE IS AN AMPLIFIER, NEVER A FINDING ON ITS OWN. A large healthy deal is not a
            // problem, and listing it as one would bury the real ones. It raises the severity of a
            // finding that already exists.
            var highValue = s.Amount >= HighValueThreshold;

            // ---- 1. the commitment that has already slipped ----
            if (s.ExpectedCloseDate.HasValue && s.ExpectedCloseDate.Value.Date < today)
            {
                var overdue = (today - s.ExpectedCloseDate.Value.Date).Days;
                found.Add(new Insight
                {
                    Opportunity = s,
                    Finding = Finding.PastExpectedClose,
                    Severity = Severity.High,
                    EvidenceDays = overdue,
                });
            }
            else if (s.ExpectedCloseDate.HasValue
                     && (s.ExpectedCloseDate.Value.Date - today).Days <= ClosingSoonDays)
            {
                // Imminent is only worth a reader's attention when nobody is working it.
                if (s.OpenActivityCount == 0)
                {
                    found.Add(new Insight
                    {
                        Opportunity = s,
                        Finding = Finding.ClosingSoon,
                        Severity = highValue ? Severity.High : Severity.Medium,
                        EvidenceDays = (s.ExpectedCloseDate.Value.Date - today).Days,
                    });
                }
            }
            else if (s.ExpectedCloseDate == null)
            {
                found.Add(new Insight
                {
                    Opportunity = s,
                    Finding = Finding.NoExpectedCloseDate,
                    Severity = Severity.Low,
                    EvidenceDays = null,
                });
            }

            // ---- 2. contact history ----
            //
            // "Never" and "not lately" are separated deliberately. They call for different actions: one
            // opportunity was never worked, the other was dropped.
            if (s.LastActivityAt == null)
            {
                found.Add(new Insight
                {
                    Opportunity = s,
                    Finding = Finding.NoActivityEver,
                    Severity = highValue ? Severity.High : Severity.Medium,
                    EvidenceDays = s.CreatedAt.HasValue ? (today - s.CreatedAt.Value.Date).Days : null,
                });
            }
            else
            {
                var quiet = (today - s.LastActivityAt.Value.Date).Days;
                if (quiet >= StaleActivityDays)
                {
                    found.Add(new Insight
                    {
                        Opportunity = s,
                        Finding = Finding.StaleActivity,
                        Severity = highValue ? Severity.High : Severity.Medium,
                        EvidenceDays = quiet,
                    });
                }
            }

            // ---- 3. is anyone going to do anything next? ----
            if (s.OpenActivityCount == 0 && !found.Any(f => f.Finding == Finding.ClosingSoon))
            {
                found.Add(new Insight
                {
                    Opportunity = s,
                    Finding = Finding.NoNextAction,
                    Severity = Severity.Low,
                    EvidenceDays = null,
                });
            }

            // ---- 4. age ----
            if (s.CreatedAt.HasValue)
            {
                var age = (today - s.CreatedAt.Value.Date).Days;
                if (age >= LongOpenDays)
                {
                    found.Add(new Insight
                    {
                        Opportunity = s,
                        Finding = Finding.LongOpen,
                        Severity = Severity.Medium,
                        EvidenceDays = age,
                    });
                }
            }

            return found.OrderByDescending(f => f.Severity).ThenBy(f => f.Finding).ToList();
        }

        /// Amount at or above which a finding is escalated. A round, stated number — not a percentile,
        /// because a percentile moves when the pipeline moves and the reader cannot check it.
        public const decimal HighValueThreshold = 100_000m;

        /// <summary>Every insight across the pipeline, in the order a salesperson should work them.</summary>
        /// <remarks>
        /// THE ORDER IS TOTAL AND EXPLAINABLE, and it is stated on the screen in these words:
        ///
        ///     1. most serious first
        ///     2. then the longest overdue or longest neglected
        ///     3. then the largest amount
        ///     4. then the lowest opportunity id
        ///
        /// Step 4 exists so the list never reorders between two identical loads. There is no opaque
        /// score: every position is justified by a value the reader can see in the row.
        /// </remarks>
        public static IReadOnlyList<Insight> Rank(IEnumerable<Insight> insights)
        {
            ArgumentNullException.ThrowIfNull(insights);
            return insights
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.EvidenceDays ?? 0)
                .ThenByDescending(i => i.Opportunity.Amount)
                .ThenBy(i => i.Opportunity.OpportunityId)
                .ThenBy(i => i.Finding)
                .ToList();
        }

        /// <summary>Evaluates and ranks a whole pipeline in one call.</summary>
        public static IReadOnlyList<Insight> Analyse(IEnumerable<Signal> pipeline, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            return Rank(pipeline.SelectMany(s => Evaluate(s, nowUtc)));
        }
    }
}
