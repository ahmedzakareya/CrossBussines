using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.6, Phases 3–6 — THE DURABLE AI EGRESS AUDIT.
    //
    // Until now the audit was ILogger only: correct in CONTENT and ephemeral in NATURE. "Which tenant
    // sent what to which provider, and what did it cost" must survive a log rotation, because it becomes
    // a compliance question the moment a paid provider is approved.
    //
    // The hardest requirement here is NEGATIVE — the audit must never become a second copy of the thing
    // the classification matrix exists to keep inside the estate. Several tests below assert what CANNOT
    // be stored rather than what can.
    public class AiEgressAuditDurabilityTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        private static AiEgressAuditRecord Record(
            int company = CompanyA,
            AiProviderOutcome outcome = AiProviderOutcome.Success,
            string? failure = null,
            decimal? cost = null,
            string? currency = null,
            int inTokens = 100,
            int outTokens = 50)
            => new()
            {
                CompanyId = company,
                ProviderId = "OpenAI",
                Feature = AiEgressPurpose.CashflowForecast,
                Classification = AiDataClassification.FinancialAggregate,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                GovernanceDecision = "allow:CashflowForecast@ApprovedExternalProcessor:FinancialAggregate",
                ApprovalReference = "allow:CashflowForecast@ApprovedExternalProcessor:FinancialAggregate",
                Model = "test-model",
                CorrelationId = "corr-1",
                OccurredAtUtc = new DateTime(2026, 8, 16, 9, 0, 0, DateTimeKind.Utc),
                Duration = TimeSpan.FromMilliseconds(1234),
                Outcome = outcome,
                RequestBytes = 512,
                InputTokens = inTokens,
                OutputTokens = outTokens,
                EstimatedCost = cost,
                Currency = currency,
                FailureCategory = failure,
            };

        private static SqlAiEgressAuditStore Store(PlatformTestHost host)
            => new(host.Db, NullLogger<SqlAiEgressAuditStore>.Instance);

        // =========================================================================================
        // PERSISTENCE
        // =========================================================================================

        [Fact]
        public async Task A_successful_call_is_persisted_with_its_metadata()
        {
            using var host = new PlatformTestHost();
            Assert.True(await Store(host).WriteAsync(Record()));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();

            Assert.Equal(CompanyA, row.CompanyID);
            Assert.Equal("OpenAI", row.ProviderId);
            Assert.Equal("CashflowForecast", row.Feature);
            Assert.Equal("FinancialAggregate", row.Classification);
            Assert.Equal("ApprovedExternalProcessor", row.DestinationClass);
            Assert.Equal("test-model", row.Model);
            Assert.Equal("corr-1", row.CorrelationId);
            Assert.True(row.Success);
            Assert.Equal("Success", row.Outcome);
            Assert.Equal(1234, row.DurationMs);
            Assert.Equal(100, row.InputTokens);
            Assert.Equal(50, row.OutputTokens);
            Assert.Null(row.FailureCategory);
        }

        // A failure must always carry a category — the database constraint says so too
        // (CK_AiEgressAudits_Failure). Normalised in the store so the constraint is a backstop, not the
        // thing that discovers a bug on a request path.
        [Fact]
        public async Task A_failure_always_carries_a_category_even_when_the_caller_omits_one()
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(outcome: AiProviderOutcome.ProviderError, failure: null));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();

            Assert.False(row.Success);
            Assert.Equal("unspecified", row.FailureCategory);
        }

        [Fact]
        public async Task A_success_never_carries_a_failure_category()
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(outcome: AiProviderOutcome.Success, failure: "should-be-dropped"));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();
            Assert.Null(row.FailureCategory);
        }

        // An audit row that cannot say whose data left is not an audit row.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task An_unresolved_company_is_refused_rather_than_stored(int company)
        {
            using var host = new PlatformTestHost();

            Assert.False(await Store(host).WriteAsync(Record(company: company)));
            Assert.Equal(0, await host.NewContext().Set<AiEgressAudit>().CountAsync());
        }

        [Fact]
        public async Task Rows_from_different_companies_are_both_stored_and_distinguishable()
        {
            using var host = new PlatformTestHost();
            var store = Store(host);

            await store.WriteAsync(Record(company: CompanyA));
            await store.WriteAsync(Record(company: CompanyB));

            var rows = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.CompanyID == CompanyA);
            Assert.Contains(rows, r => r.CompanyID == CompanyB);
        }

        // =========================================================================================
        // COST — unpriced is UNPRICED, never zero
        // =========================================================================================

        [Fact]
        public async Task An_unpriced_call_stores_null_cost_and_null_currency()
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(cost: null, currency: null));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();

            // NOT 0.00. A zero beside real token counts reads as "this cost nothing", which is a
            // different and wrong claim.
            Assert.Null(row.EstimatedCost);
            Assert.Null(row.CostCurrency);
            Assert.Equal(150, row.InputTokens + row.OutputTokens);   // tokens still counted
        }

        [Fact]
        public async Task A_priced_call_stores_amount_and_currency_together()
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(cost: 0.000375m, currency: "USD"));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();
            Assert.Equal(0.000375m, row.EstimatedCost);
            Assert.Equal("USD", row.CostCurrency);
        }

        // Amount and currency travel together or neither is stored — CK_AiEgressAudits_Cost.
        [Theory]
        [InlineData(0.5, null)]
        [InlineData(null, "USD")]
        public async Task A_half_specified_cost_is_stored_as_no_cost_at_all(double? cost, string? currency)
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(cost: cost is null ? null : (decimal)cost.Value, currency: currency));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();
            Assert.Null(row.EstimatedCost);
            Assert.Null(row.CostCurrency);
        }

        // =========================================================================================
        // PHASE 3 — THE DATA BOUNDARY. What CANNOT be stored.
        // =========================================================================================

        // The record type is the boundary: there is no property for a prompt, a response, a body or a
        // credential, so persisting one is a compile error rather than a review finding.
        [Theory]
        [InlineData("Prompt")]
        [InlineData("Response")]
        [InlineData("Completion")]
        [InlineData("Body")]
        [InlineData("Payload")]
        [InlineData("ApiKey")]
        [InlineData("Authorization")]
        [InlineData("Token")]     // "InputTokens" is a COUNT; this asserts no bare Token/AuthToken field
        [InlineData("Secret")]
        [InlineData("Header")]
        public void Neither_the_audit_record_nor_the_entity_has_a_payload_or_credential_property(string forbidden)
        {
            foreach (var t in new[] { typeof(AiEgressAuditRecord), typeof(AiEgressAudit) })
                foreach (var p in t.GetProperties())
                {
                    // Token counts are legitimate and are named InputTokens / OutputTokens / TotalTokens.
                    if (forbidden == "Token" && p.Name.EndsWith("Tokens", StringComparison.Ordinal)) continue;

                    Assert.False(p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"{t.Name}.{p.Name} looks like it could carry {forbidden}. The audit is metadata only.");
                }
        }

        // Every persisted string field is a bounded enum, identifier or machine code. None is free text,
        // so none can carry business content even by accident.
        [Fact]
        public async Task Every_persisted_string_is_bounded()
        {
            using var host = new PlatformTestHost();

            var overlong = new string('x', 5_000);
            await Store(host).WriteAsync(new AiEgressAuditRecord
            {
                CompanyId = CompanyA,
                ProviderId = overlong,
                Feature = AiEgressPurpose.CashflowForecast,
                Classification = AiDataClassification.FinancialAggregate,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                GovernanceDecision = overlong,
                ApprovalReference = overlong,
                Model = overlong,
                CorrelationId = overlong,
                OccurredAtUtc = DateTime.UtcNow,
                Outcome = AiProviderOutcome.ProviderError,
                FailureCategory = overlong,
            });

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();

            Assert.True(row.ProviderId.Length <= 64);
            Assert.True(row.GovernanceDecision.Length <= 256);
            Assert.True(row.ApprovalReference!.Length <= 256);
            Assert.True(row.Model!.Length <= 128);
            Assert.True(row.CorrelationId.Length <= 128);
            Assert.True(row.FailureCategory!.Length <= 128);
        }

        // Negative counters would corrupt every SUM built on this table.
        [Fact]
        public async Task Negative_counters_are_clamped_to_zero()
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(new AiEgressAuditRecord
            {
                CompanyId = CompanyA,
                ProviderId = "OpenAI",
                Feature = AiEgressPurpose.CashflowForecast,
                Classification = AiDataClassification.FinancialAggregate,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                GovernanceDecision = "d",
                CorrelationId = "c",
                OccurredAtUtc = DateTime.UtcNow,
                Duration = TimeSpan.FromMilliseconds(-99),
                Outcome = AiProviderOutcome.Success,
                InputTokens = -5,
                OutputTokens = -7,
                RequestBytes = -1,
            });

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();
            Assert.Equal(0, row.InputTokens);
            Assert.Equal(0, row.OutputTokens);
            Assert.Equal(0, row.RequestBytes);
            Assert.Equal(0, row.DurationMs);
        }

        // =========================================================================================
        // PHASE 5 — THE FAILURE POLICY
        // =========================================================================================

        private sealed class FailingStore : IAiEgressAuditStore
        {
            public int Attempts;
            public readonly bool Throws;
            public FailingStore(bool throws) => Throws = throws;

            public Task<bool> WriteAsync(AiEgressAuditRecord r, CancellationToken ct = default)
            {
                Attempts++;
                if (Throws) throw new InvalidOperationException("database unavailable");
                return Task.FromResult(false);
            }
        }

        // THE CENTRAL DECISION. The provider call already happened: the money is spent and the model has
        // already seen the data. Throwing would hide a completed egress from the user while doing nothing
        // to un-send it — and on a paid provider it invites pressing the button again, which is a second
        // charge for the same answer.
        [Theory]
        [InlineData(false)]   // store returns false
        [InlineData(true)]    // store throws
        public async Task An_audit_persistence_failure_never_propagates_to_the_caller(bool throws)
        {
            var store = new FailingStore(throws);
            var sink = new AiEgressAuditSink(store, NullLogger<AiEgressAuditSink>.Instance);

            // No exception escapes. The failure is loud in the log, not in the call stack.
            await sink.RecordAsync(Record());

            Assert.Equal(1, store.Attempts);
        }

        // SINGLE ATTEMPT, deliberately. Retrying the AUDIT is safe in isolation, but keeping a retry loop
        // next to a paid HTTP call is how a retry of the audit later becomes a retry of the CALL.
        [Fact]
        public async Task A_failed_audit_write_is_not_retried()
        {
            var store = new FailingStore(throws: false);
            await new AiEgressAuditSink(store, NullLogger<AiEgressAuditSink>.Instance).RecordAsync(Record());

            Assert.Equal(1, store.Attempts);
        }

        // Cancellation propagates rather than being swallowed: re-entering the data layer on a cancelled
        // scope would be worse than losing the row.
        [Fact]
        public async Task Cancellation_during_the_audit_is_propagated()
        {
            var sink = new AiEgressAuditSink(new CancellingStore(), NullLogger<AiEgressAuditSink>.Instance);
            await Assert.ThrowsAsync<OperationCanceledException>(() => sink.RecordAsync(Record()));
        }

        private sealed class CancellingStore : IAiEgressAuditStore
        {
            public Task<bool> WriteAsync(AiEgressAuditRecord r, CancellationToken ct = default)
                => throw new OperationCanceledException();
        }

        // The composite writes the log line FIRST and the row SECOND — the copy that cannot fail before
        // the one that can — so a database outage still leaves evidence somewhere.
        [Fact]
        public async Task The_sink_writes_the_durable_row_when_the_store_is_healthy()
        {
            using var host = new PlatformTestHost();
            var sink = new AiEgressAuditSink(Store(host), NullLogger<AiEgressAuditSink>.Instance);

            await sink.RecordAsync(Record());

            Assert.Equal(1, await host.NewContext().Set<AiEgressAudit>().CountAsync());
        }

        // =========================================================================================
        // PHASE 6 — WHICH DENIALS ARE DURABLE
        //
        // THE CLASSIFICATION: the durable audit covers the EXTERNAL PROVIDER BOUNDARY. Everything that
        // reaches the adapter is persisted, allowed or refused. Policy-level denials — which happen
        // before any adapter exists — remain structured-log-only, because:
        //
        //   * they include INTERNAL/loopback denials, which are not egress events at all;
        //   * they can fire in a tight loop during a misconfiguration, which is exactly the noise Phase 6
        //     warns against;
        //   * no data can have left, so there is nothing to account for.
        //
        // The security-relevant one, ProviderNotApproved, is observable in the log and cannot coexist
        // with data leaving.
        // =========================================================================================

        [Theory]
        [InlineData(AiProviderOutcome.Refused)]          // switch off · rate limit · usage · credential · circuit
        [InlineData(AiProviderOutcome.ProviderError)]
        [InlineData(AiProviderOutcome.Timeout)]
        [InlineData(AiProviderOutcome.Cancelled)]
        [InlineData(AiProviderOutcome.InvalidResponse)]
        public async Task Every_adapter_level_outcome_is_durably_audited(AiProviderOutcome outcome)
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(outcome: outcome, failure: "some-category"));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();
            Assert.Equal(outcome.ToString(), row.Outcome);
            Assert.Equal(outcome == AiProviderOutcome.Success, row.Success);
        }

        // The refusals are the rows an operator most needs, so they must be filterable without parsing:
        // Success = 0 is the filtered index (IX_AiEgressAudits_Failed).
        [Fact]
        public async Task Refusals_are_findable_by_the_success_flag_alone()
        {
            using var host = new PlatformTestHost();
            var store = Store(host);

            await store.WriteAsync(Record(outcome: AiProviderOutcome.Success));
            await store.WriteAsync(Record(outcome: AiProviderOutcome.Refused, failure: "rate:exceeded:30/60s"));
            await store.WriteAsync(Record(outcome: AiProviderOutcome.Refused, failure: "provider-switch:OpenAI:disabled"));

            var failed = await host.NewContext().Set<AiEgressAudit>().AsNoTracking()
                .Where(r => !r.Success).ToListAsync();

            Assert.Equal(2, failed.Count);
            Assert.All(failed, r => Assert.False(string.IsNullOrWhiteSpace(r.FailureCategory)));
        }

        // A provider error body frequently quotes the offending request back. Only the status CATEGORY is
        // ever stored, and this asserts the store does not smuggle a message through.
        [Fact]
        public async Task A_failure_category_is_a_code_and_never_a_provider_message()
        {
            using var host = new PlatformTestHost();
            await Store(host).WriteAsync(Record(
                outcome: AiProviderOutcome.ProviderError, failure: "provider-error:429"));

            var row = await host.NewContext().Set<AiEgressAudit>().AsNoTracking().SingleAsync();

            Assert.Equal("provider-error:429", row.FailureCategory);
            Assert.DoesNotContain(" ", row.FailureCategory!);   // a code, not prose
        }

        // The adapter is the thing that feeds the audit, so the two must agree about the boundary: the
        // adapter's sink is IAiEgressAuditSink, and the sink is what persists.
        [Fact]
        public void The_adapter_records_through_the_audit_sink_abstraction()
        {
            var ctor = typeof(OpenAiProviderAdapter).GetConstructors().Single();
            Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(IAiEgressAuditSink));
        }
    }
}
