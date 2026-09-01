using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Models.Context.Accounting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE POSTING CHOKEPOINT, re-proved on current master — and the SECOND enforcement point that
    // re-proving it turned up.
    //
    // AccountingPeriodCloseTests owns the control surface: who may close, on what evidence, with what
    // history. This file owns the other half of the question the batch asks — can anything reach a
    // financial row while a period says no.
    //
    // WHAT THE ENUMERATION FOUND. JournalEntryService is the only writer of JournalEntries and its
    // four posting entry points all funnel through one private method carrying the canonical guard.
    // That part held. But StockService keeps a period guard of its OWN, and it is not redundant:
    // its own comment says why, and the reason is load-bearing — a same-branch transfer moves stock
    // and posts NO journal, so it never reaches the GL writer at all. The chokepoint there cannot see
    // it, and deleting the second guard would open the hole rather than tidy it.
    //
    // The defect was that the second guard tested the LITERAL "Closed". So SoftClosed — the state a
    // controller puts a month into while it finishes closing — blocked GL postings and admitted stock
    // movements. Inventory valuation could move inside a period finance believed was sealing, and
    // nothing would have reported it, because the movement that does it writes no journal.
    //
    // TWO ENFORCEMENT POINTS ARE NECESSARY. TWO DEFINITIONS WERE THE DEFECT. Both now ask
    // AccountingPeriodStatuses.BlocksPosting.
    // =============================================================================================
    public class PeriodPostingChokepointTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        private static IEnumerable<FileInfo> ProductionSources()
        {
            foreach (var folder in new[] { "BL", "Controllers", "Models" })
            {
                var d = new DirectoryInfo(RepoFile("CrossBuy", folder));
                if (!d.Exists) continue;
                foreach (var f in d.GetFiles("*.cs", SearchOption.AllDirectories)) yield return f;
            }
        }

        // -----------------------------------------------------------------------------------------
        // THE GL CHOKEPOINT — enumerated, not assumed
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Only_the_GL_writer_creates_a_JournalEntry_row()
        {
            // The claim the whole control rests on: if a second file could add a JournalEntry, the
            // period guard would be advisory. Measured across BL, Controllers and Models rather than
            // asserted about the one file we already trust.
            var offenders = ProductionSources()
                .Where(f => f.Name != "JournalEntryService.cs")
                .Select(f => new { f.Name, Code = File.ReadAllText(f.FullName) })
                .Where(f => f.Code.Contains("JournalEntries.Add(", StringComparison.Ordinal)
                         || f.Code.Contains("Set<JournalEntry>().Add(", StringComparison.Ordinal))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "JournalEntry rows are created outside the single GL writer by: " + string.Join(", ", offenders));
        }

        [Fact]
        public void No_module_inserts_a_journal_row_behind_the_writer_with_raw_sql()
        {
            // An ORM chokepoint is only a chokepoint while nobody reaches past the ORM. Cheap to check,
            // and the one way the test above could be satisfied while the property was false.
            var offenders = ProductionSources()
                .Select(f => new { f.Name, Code = File.ReadAllText(f.FullName) })
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    f.Code, @"INSERT\s+INTO\s+(dbo\.)?JournalEntries",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "raw SQL inserts into JournalEntries in: " + string.Join(", ", offenders));
        }

        [Fact]
        public void Every_public_posting_entry_point_funnels_through_the_one_guarded_method()
        {
            var src = File.ReadAllText(RepoFile("CrossBuy", "BL", "JournalEntryService.cs"));

            // The four that post. CreateDraftAsync is deliberately absent: a draft is not a posting, and
            // it can only become one by coming back through PostAsync.
            foreach (var entry in new[] { "PostAsync", "CreateAndPostAsync", "CreateAndPostNoTxAsync", "ReverseAsync" })
                Assert.True(src.Contains(entry, StringComparison.Ordinal), "missing entry point: " + entry);

            // Four call sites, one guarded method. If a fifth posting path appeared that did not call it,
            // the count would not move and this would not catch it — which is why the enumeration tests
            // above measure the WRITE rather than the call.
            var calls = System.Text.RegularExpressions.Regex.Matches(src, @"PostInternalAsync\(").Count;
            Assert.True(calls >= 5, "expected the four posting paths plus the declaration; found " + calls);

            Assert.Contains("AccountingPeriodStatuses.BlocksPosting(period.Status)", src);
        }

        // -----------------------------------------------------------------------------------------
        // THE SECOND ENFORCEMENT POINT — behavioural
        // -----------------------------------------------------------------------------------------

        /// Answers whatever a test hands it, so the guard can be driven across all four period states
        /// without seeding items, warehouses and balances that have nothing to do with the question.
        private sealed class ScriptedPeriods : IFiscalPeriodService
        {
            public FiscalPeriod? Period;
            public Task<FiscalPeriod?> ResolveAsync(int companyId, DateTime date) => Task.FromResult(Period);
            public Task<List<FiscalPeriodRow>> ListAsync(int companyId) => Task.FromResult(new List<FiscalPeriodRow>());
        }

        /// Everything StockService needs to be constructed and nothing the guard touches. Each member
        /// throws, so a guard that reached past _periods would fail loudly rather than pass quietly.
        private sealed class NeverJournals : IJournalEntryService
        {
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId) => throw new InvalidOperationException();
            public Task<(bool ok, string? error)> PostAsync(int entryId, int? userId) => throw new InvalidOperationException();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput input, int? userId) => throw new InvalidOperationException();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput input, int? userId) => throw new InvalidOperationException();
            public Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int entryId, int? userId, string? reason) => throw new InvalidOperationException();
        }

        private static async Task<string?> GuardAsync(PlatformTestHost host, string? status)
        {
            var periods = new ScriptedPeriods
            {
                Period = status == null ? null : new FiscalPeriod
                {
                    ID = 1, FiscalYearId = 1, PeriodNo = 1,
                    StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 31),
                    Status = status,
                },
            };

            var stock = new StockService(
                host.Db, new NeverJournals(), periods,
                (ICurrencyService)null!, NullLogger<StockService>.Instance, (ICurrencyRounding)null!);

            var guard = typeof(StockService).GetMethod(
                "PeriodGuardAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(guard);

            var task = (Task<string?>)guard!.Invoke(stock, new object[] { 1, new DateTime(2026, 1, 15) })!;
            return await task;
        }

        [Fact]
        public async Task An_OPEN_period_admits_a_stock_movement()
        {
            using var host = new PlatformTestHost(companyId: 1);
            Assert.Null(await GuardAsync(host, AccountingPeriodStatuses.Open));
        }

        [Theory]
        [InlineData("SoftClosed")]
        [InlineData("Closed")]
        public async Task A_period_that_blocks_posting_blocks_a_stock_movement_too(string status)
        {
            // SoftClosed is the case that used to pass. A same-branch transfer posts no journal, so the
            // GL chokepoint never sees it — this guard is the only thing standing between a soft-closed
            // month and a change in inventory valuation.
            using var host = new PlatformTestHost(companyId: 1);
            Assert.NotNull(await GuardAsync(host, status));
        }

        [Fact]
        public async Task A_date_no_period_covers_is_refused_rather_than_waved_through()
        {
            using var host = new PlatformTestHost(companyId: 1);
            Assert.NotNull(await GuardAsync(host, null));
        }

        [Fact]
        public void The_stock_guard_asks_the_canonical_predicate_rather_than_a_literal()
        {
            // The mutation this pins: restoring `p.Status == "Closed"` here compiles, passes every GL
            // test, and silently un-does SoftClosed on the one path the GL guard cannot see.
            var src = File.ReadAllText(RepoFile("CrossBuy", "BL", "StockService.cs"));
            var i = src.IndexOf("PeriodGuardAsync", StringComparison.Ordinal);
            Assert.True(i > 0);
            var guard = src[i..Math.Min(src.Length, i + 800)];

            Assert.Contains("AccountingPeriodStatuses.BlocksPosting(p.Status)", guard);
            Assert.DoesNotContain("p.Status == \"Closed\"", guard);
        }

        // -----------------------------------------------------------------------------------------
        // ONE DEFINITION
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Nothing_else_decides_for_itself_whether_a_FISCAL_period_blocks_posting()
        {
            // Deliberately narrow. Plenty of things in this product have a "Closed" status — a POS
            // table, a work order, a CRM ticket, a fiscal YEAR — and asserting about the word would
            // fail on all of them for no reason. This looks only at code that has a FiscalPeriod in
            // hand and then decides about its status itself.
            var allowed = new[] { "JournalEntryService.cs", "StockService.cs", "AccountingPeriodControlService.cs" };

            var offenders = ProductionSources()
                .Where(f => !allowed.Contains(f.Name, StringComparer.Ordinal))
                .Select(f => new { f.Name, Code = File.ReadAllText(f.FullName) })
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    f.Code,
                    @"(FiscalPeriod|_periods\.ResolveAsync|period)\w*\s*[\.\?]?\s*Status\s*==\s*""(Closed|SoftClosed)"""))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "these decide for themselves what a closed fiscal period means, instead of asking " +
                "AccountingPeriodStatuses.BlocksPosting: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_CONCRETE_period_service_exposes_no_status_mutator_either()
        {
            // AccountingPeriodCloseTests reflects over IFiscalPeriodService. That is the right check and
            // it is not the whole one: a public method on the CONCRETE class is just as reachable, and
            // this codebase resolves concrete types on purpose — the reworked SetPeriodStatus does it
            // three lines apart, `GetService(typeof(AccountingAccessService)) as AccountingAccessService`.
            //
            // Measured, not assumed: re-adding SetStatusAsync to the class alone, leaving the interface
            // untouched, passed every other test in this suite. This is the one that fails.
            var methods = typeof(FiscalPeriodService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .ToList();

            Assert.DoesNotContain("SetStatusAsync", methods);

            // Named by CAPABILITY rather than by that one name, so a differently-spelled resurrection is
            // caught too. A period's state moves through IAccountingPeriodControlService or not at all.
            var mutators = methods.Where(m =>
                m.Contains("Status", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Close", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Reopen", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Seal", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(mutators.Count == 0,
                "FiscalPeriodService exposes what looks like a period-state mutator: "
                + string.Join(", ", mutators)
                + ". Period state moves through IAccountingPeriodControlService, which resolves the "
                + "company from the period's own fiscal year, demands period-close/period-reopen "
                + "authority, gates on readiness and writes an audit row.");

            // What remains is read-only.
            Assert.Contains("ResolveAsync", methods);
            Assert.Contains("ListAsync", methods);
        }

        // -----------------------------------------------------------------------------------------
        // DDL / MODEL PARITY — the audit row has to FIT
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void An_override_note_fits_the_column_however_many_warnings_were_overridden()
        {
            // MEASURED, not imagined: IntegrityCheckService ships thirty checks, each surfacing as a
            // warning code of the form "integrity:<key>". A company failing most of them produces an
            // override note around 839 characters for a column of 500 — and the tests run on SQLite,
            // where the string is unbounded and the truncation never appears. It would have surfaced on
            // SQL Server, at the moment a controller closed a month, as a failed close nobody could fix.
            var many = Enumerable.Range(0, 40).Select(i => $"integrity:some-fairly-long-check-key-{i}");

            var note = AccountingPeriodControlService.FitWarnings(many);

            Assert.True(note.Length <= AccountingPeriodControlService.ReasonMaxLength,
                $"the override note is {note.Length} characters and the column holds " +
                $"{AccountingPeriodControlService.ReasonMaxLength}");

            // It must not silently drop the ones that did not fit — an audit row that understates what
            // was overridden is worse than a long one.
            Assert.Contains("+", note, StringComparison.Ordinal);
        }

        [Fact]
        public void A_short_list_of_warnings_is_recorded_in_full_with_no_summary_tail()
        {
            var note = AccountingPeriodControlService.FitWarnings(new[] { "unposted-journals" });

            Assert.Contains("unposted-journals", note, StringComparison.Ordinal);
            Assert.DoesNotContain("+", note, StringComparison.Ordinal);
        }

        [Fact]
        public void The_declared_column_width_matches_the_deployment_slice()
        {
            // The constant the service writes against and the width the SQL declares are two statements
            // of one fact, in two files, in two languages. This is the only thing that keeps them equal.
            var sql = File.ReadAllText(RepoFile("CrossBuy", "deploy", "sql", "accounting_period_control.sql"));

            Assert.Contains($"ReopenReason NVARCHAR({AccountingPeriodControlService.ReasonMaxLength}) NULL",
                sql, StringComparison.Ordinal);
            Assert.Contains($"Reason           nvarchar({AccountingPeriodControlService.ReasonMaxLength})",
                sql, StringComparison.Ordinal);
        }

        [Fact]
        public void The_canonical_predicate_answers_for_both_closed_states_and_only_those()
        {
            Assert.False(AccountingPeriodStatuses.BlocksPosting(AccountingPeriodStatuses.Open));
            Assert.True(AccountingPeriodStatuses.BlocksPosting(AccountingPeriodStatuses.SoftClosed));
            Assert.True(AccountingPeriodStatuses.BlocksPosting(AccountingPeriodStatuses.Closed));

            // An unknown or absent status is NOT treated as blocking. That is the right way round: a
            // period whose status nobody recognises is a data fault to fix, and silently sealing the
            // ledger over it would turn a bad row into an outage.
            Assert.False(AccountingPeriodStatuses.BlocksPosting(null));
            Assert.False(AccountingPeriodStatuses.BlocksPosting("Whatever"));
        }
    }
}
