using System.Globalization;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — the PARAMETER ENGINE is where text becomes a typed value, and where the
    // isolation guarantee is enforced at parameter level.
    public class ReportingParameterEngineTests
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static ReportParameterBindResult Bind(ReportingTestHost host,
            params (string Key, string? Value)[] supplied) =>
            host.Binder.Bind(TestReportDefinitions.Sales(),
                supplied.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
                host.Ctx, Invariant);

        // ================================================================================================
        // 1. THE ISOLATION GUARANTEE
        // ================================================================================================

        [Fact]
        public void A_caller_supplied_CompanyId_is_dropped_with_a_warning_and_the_context_wins()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);

            var result = Bind(host, (ReportSystemParameters.CompanyId, "999"));

            // NOT coerced, NOT validated, NOT honoured. CLAUDE.md: "a request-supplied companyId is
            // compatibility-only … never coerce it." Here it cannot even be expressed.
            Assert.Equal(1, result.Parameters!.CompanyId);
            Assert.Contains(result.Diagnostics, d =>
                d.Code == ReportParameterBinder.CodeSystemSuppliedIgnored && d.Field == ReportSystemParameters.CompanyId);

            // A warning, not an error: a stale bookmark carrying companyId must not break the report, it must be
            // ignored loudly.
            Assert.True(result.IsValid);
        }

        [Fact]
        public void CompanyId_EmployeeId_and_Now_are_always_present_even_when_undeclared()
        {
            using var host = new ReportingTestHost(companyId: 4, employeeId: 22);

            // A data source can never be handed a parameter set with no company. A source that forgets to filter is
            // a bug in that source; a source that COULD NOT KNOW the company would be a platform bug.
            var result = Bind(host);

            Assert.Equal(4, result.Parameters!.CompanyId);
            Assert.Equal(22, result.Parameters!.EmployeeId);
            Assert.Equal(ReportingTestHost.FixedNow, result.Parameters!.Now);
        }

        [Fact]
        public void The_parameter_hash_excludes_system_keys_so_the_same_question_hashes_the_same_in_two_companies()
        {
            using var one = new ReportingTestHost(companyId: 1);
            using var two = new ReportingTestHost(companyId: 2);

            var a = Bind(one, ("From", "2026-01-01"), ("To", "2026-01-31"));
            var b = Bind(two, ("From", "2026-01-01"), ("To", "2026-01-31"));

            Assert.Equal(
                ReportRequest.HashParameters(a.Parameters!.RawText),
                ReportRequest.HashParameters(b.Parameters!.RawText));
        }

        [Fact]
        public void The_parameter_hash_is_order_independent()
        {
            var one = new Dictionary<string, string?> { ["From"] = "2026-01-01", ["To"] = "2026-01-31" };
            var two = new Dictionary<string, string?> { ["To"] = "2026-01-31", ["From"] = "2026-01-01" };

            Assert.Equal(ReportRequest.HashParameters(one), ReportRequest.HashParameters(two));
        }

        // ================================================================================================
        // 2. REQUIRED, DEFAULTS AND RELATIVE DATE TOKENS
        // ================================================================================================

        [Fact]
        public void A_required_parameter_falls_back_to_its_declared_default_rather_than_failing()
        {
            using var host = new ReportingTestHost();

            var result = Bind(host);

            Assert.True(result.IsValid);

            // "month-start" and "today" resolved against the FIXED clock (2026-05-14).
            Assert.Equal(new DateTime(2026, 5, 1), result.Parameters!.GetDate("From"));
            Assert.Equal(new DateTime(2026, 5, 14), result.Parameters!.GetDate("To"));
        }

        [Fact]
        public void A_required_parameter_with_no_default_and_no_value_fails_rather_than_guessing()
        {
            using var host = new ReportingTestHost();

            var definition = TestReportDefinitions.Sales();
            var stripped = new ReportDefinition
            {
                Code = definition.Code, Module = definition.Module,
                TitleAr = definition.TitleAr, TitleEn = definition.TitleEn,
                DataSourceKey = definition.DataSourceKey, PermissionKey = definition.PermissionKey,
                Columns = definition.Columns,
                Parameters = new[]
                {
                    new ReportParameterDescriptor
                    {
                        Key = "From", TitleAr = "من", TitleEn = "From",
                        Type = ReportFieldType.Date, Required = true,   // no DefaultValue
                    },
                },
            };

            var result = host.Binder.Bind(stripped, new Dictionary<string, string?>(), host.Ctx, Invariant);

            // A trial balance that silently defaulted to "this month" is how a report gets signed for the wrong
            // period. It must fail.
            Assert.False(result.IsValid);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportParameterBinder.CodeRequiredMissing);
        }

        [Theory]
        [InlineData("today", 2026, 5, 14)]
        [InlineData("yesterday", 2026, 5, 13)]
        [InlineData("week-start", 2026, 5, 10)]   // 2026-05-14 is a Thursday; week starts Sunday
        [InlineData("month-start", 2026, 5, 1)]
        [InlineData("month-end", 2026, 5, 31)]
        [InlineData("quarter-start", 2026, 4, 1)]
        [InlineData("quarter-end", 2026, 6, 30)]
        [InlineData("year-start", 2026, 1, 1)]
        [InlineData("year-end", 2026, 12, 31)]
        [InlineData("today-7", 2026, 5, 7)]
        [InlineData("month-start+14", 2026, 5, 15)]
        public void Relative_date_tokens_resolve_against_the_clock(string token, int y, int m, int d)
        {
            Assert.True(ReportValues.TryResolveDateToken(token, ReportingTestHost.FixedNow, out var resolved));
            Assert.Equal(new DateTime(y, m, d), resolved);
        }

        [Fact]
        public void Month_end_is_clamped_to_the_real_length_of_the_month()
        {
            // February in a non-leap year. The reason this is asserted separately: "month-end" implemented as
            // "day 31" would produce an invalid date, and implemented as "+1 month -1 day" would be wrong in a
            // different way.
            Assert.True(ReportValues.TryResolveDateToken("month-end", new DateTime(2026, 2, 10), out var feb));
            Assert.Equal(new DateTime(2026, 2, 28), feb);

            Assert.True(ReportValues.TryResolveDateToken("month-end", new DateTime(2028, 2, 10), out var leap));
            Assert.Equal(new DateTime(2028, 2, 29), leap);
        }

        // ================================================================================================
        // 3. CONVERSION AND VALIDATION
        // ================================================================================================

        [Fact]
        public void An_invalid_value_is_an_error_diagnostic_not_an_exception()
        {
            using var host = new ReportingTestHost();

            var result = Bind(host, ("From", "not-a-date"));

            Assert.False(result.IsValid);
            var error = Assert.Single(result.Diagnostics.Where(d => d.Code == ReportParameterBinder.CodeInvalidValue));
            Assert.Equal("From", error.Field);
        }

        [Fact]
        public void A_value_outside_a_closed_option_set_is_refused()
        {
            using var host = new ReportingTestHost();

            var result = Bind(host, ("Branch", "East"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportParameterBinder.CodeNotInOptions);
        }

        [Fact]
        public void A_value_outside_the_declared_range_is_refused_at_both_ends()
        {
            using var host = new ReportingTestHost();

            Assert.Contains(Bind(host, ("MinAmount", "-1")).Diagnostics,
                d => d.Code == ReportParameterBinder.CodeOutOfRange);
            Assert.Contains(Bind(host, ("MinAmount", "9999999")).Diagnostics,
                d => d.Code == ReportParameterBinder.CodeOutOfRange);
            Assert.True(Bind(host, ("MinAmount", "500")).IsValid);
        }

        [Fact]
        public void A_list_supplied_to_a_single_valued_parameter_is_an_error_not_a_silent_first_value()
        {
            using var host = new ReportingTestHost();

            var result = Bind(host, ("Branch", "North,South"));

            // Taking the first would answer a NARROWER question than the user asked, without saying so.
            Assert.False(result.IsValid);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportParameterBinder.CodeMultipleNotAllowed);
        }

        [Fact]
        public void An_undeclared_parameter_is_dropped_with_a_warning_and_does_not_break_the_report()
        {
            using var host = new ReportingTestHost();

            var result = Bind(host, ("_cacheBuster", "12345"));

            Assert.True(result.IsValid);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportParameterBinder.CodeUnknownParameter);
        }

        // ================================================================================================
        // 4. VALUE SEMANTICS: invariant first, then culture
        // ================================================================================================

        [Fact]
        public void A_stored_invariant_decimal_is_not_reinterpreted_by_a_group_separator_culture()
        {
            // German reads '.' as a group separator. "1234.56" is machine data from a stored layout and must NOT
            // become 123456 — that is a 100x error in a money column.
            var german = CultureInfo.GetCultureInfo("de-DE");

            Assert.True(ReportValues.TryConvert("1234.56", ReportFieldType.Money, german,
                ReportingTestHost.FixedNow, out var value));
            Assert.Equal(1234.56m, value);
        }

        [Fact]
        public void An_iso_date_is_read_as_iso_regardless_of_culture()
        {
            var us = CultureInfo.GetCultureInfo("en-US");

            Assert.True(ReportValues.TryConvert("2026-01-31", ReportFieldType.Date, us,
                ReportingTestHost.FixedNow, out var value));
            Assert.Equal(new DateTime(2026, 1, 31), value);
        }

        [Theory]
        [InlineData("true")]
        [InlineData("1")]
        [InlineData("on")]
        [InlineData("YES")]
        public void Boolean_accepts_the_notations_that_actually_arrive(string text)
        {
            Assert.True(ReportValues.TryConvert(text, ReportFieldType.Boolean, Invariant,
                ReportingTestHost.FixedNow, out var value));
            Assert.True((bool)value!);
        }

        [Fact]
        public void An_unrecognised_boolean_is_refused_rather_than_treated_as_false()
        {
            // A mistyped flag silently meaning "no" is how a report gets filtered to nothing.
            Assert.False(ReportValues.TryConvert("mabye", ReportFieldType.Boolean, Invariant,
                ReportingTestHost.FixedNow, out _));
        }

        [Fact]
        public void Percent_is_formatted_as_a_percentage_not_multiplied_by_a_hundred()
        {
            var column = new ReportColumn
            {
                Key = "Margin", TitleAr = "ه", TitleEn = "Margin", Type = ReportFieldType.Percent,
            };

            // The value is ALREADY a percentage (12.5 means 12.5%). Using "P" would print 1250%.
            Assert.Equal("12.5%", ReportValues.Format(12.5m, column, Invariant));
        }

        [Fact]
        public void Formatting_never_changes_the_underlying_value()
        {
            var money = new ReportColumn
            {
                Key = "Amount", TitleAr = "م", TitleEn = "Amount", Type = ReportFieldType.Money,
            };

            // Display rounds to the column's scale; ForExport carries the FULL decimal. A report must not print a
            // number the ledger disagrees with, and an export must not lose fils.
            Assert.Equal("25.50", ReportValues.Format(25.499m, money, Invariant).Replace(",", ""));
            Assert.Equal(25.499m, ReportValues.ForExport(25.499m, money));
        }

        [Fact]
        public void Nulls_sort_first_ascending()
        {
            var values = new object?[] { 3, null, 1 };
            var sorted = values
                .OrderBy(v => v, Comparer<object?>.Create((a, b) =>
                    ReportValues.Compare(a, b, ReportFieldType.Integer, Invariant)))
                .ToList();

            Assert.Null(sorted[0]);
            Assert.Equal(1, sorted[1]);
            Assert.Equal(3, sorted[2]);
        }
    }
}
