using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // D-5 — a GUARD on the probe-database schema generator.
    //
    // WHAT THIS EXISTS TO PREVENT. UatSqlProbeFixture builds every disposable test database from
    // EF's GenerateCreateScript() with the foreign keys stripped out. The stripper used to be a
    // cross-line regular expression, and it silently corrupted the script for any COMPOSITE foreign
    // key: it consumed up to the first comma of the REFERENCES list and left the remainder dangling
    // on the previous line. Twenty-seven tables were affected, every probe database failed to build,
    // and two full regression runs died with the test host crashing. The failure surfaced as
    // "The definition for column 'CompanyID' must include a data type" — a message that points at a
    // column, which is three layers away from the actual cause.
    //
    // WHY A GUARD AND NOT JUST A FIX. The defect was invisible for as long as it existed because
    // nothing asserted the stripper's OUTPUT. These tests assert the invariant directly, so the next
    // model change that outruns the stripper fails here, in milliseconds, naming the problem —
    // instead of ten minutes later as a crashed host and a misleading SQL error.
    //
    // No database is touched: the script is generated from the model and inspected as text.
    // =============================================================================================
    public class ProbeSchemaGeneratorDiagnosticTests
    {
        private static string GenerateScript()
        {
            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlServer("Server=.;Database=__schema_only__;Trusted_Connection=True;TrustServerCertificate=True")
                .Options;
            using var ctx = new CrossDbContext(options);
            return ctx.Database.GenerateCreateScript();
        }

        // BOTH FIXTURES, because the logic exists twice.
        //
        // This is the lesson of the first repair: UatSqlProbeFixture and SqlServerFixture each carried a
        // byte-identical copy of the broken expression. Fixing one made this guard pass while every
        // SqlServer.* suite kept failing, because those build their probe databases through the other.
        // A guard that checks one copy certifies nothing about the other, so every test below runs
        // against both and names which one failed.
        public static TheoryData<Type> BothFixtures => new()
        {
            typeof(UatSqlProbeFixture),
            typeof(CrossBuy.Tests.SqlServer.SqlServerFixture),
        };

        private static string Strip(Type fixture, string script) =>
            (string)fixture
                .GetMethod("StripForeignKeys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { script })!;

        [Theory]
        [MemberData(nameof(BothFixtures))]
        public void No_foreign_key_syntax_survives_the_strip(Type fixture)
        {
            // The whole point of the strip. If any of these three appear as KEYWORDS, something was
            // removed partially — which is exactly how the original defect expressed itself.
            //
            // WORD BOUNDARIES ARE LOAD-BEARING, and the first version of this test proved it by failing:
            // a plain substring search matches "REFERENCES" inside the table name
            // [CommNotificationPreferences]. An identifier that happens to contain a keyword is not a
            // keyword, and a guard that cannot tell the difference reports a defect that is not there —
            // which is how a guard gets disabled and stops guarding anything.
            var stripped = Strip(fixture, GenerateScript());

            foreach (var keyword in new[] { @"\bFOREIGN\s+KEY\b", @"\bREFERENCES\s+\[", @"\bON\s+DELETE\b" })
            {
                var hit = Regex.Match(stripped, keyword, RegexOptions.IgnoreCase);
                Assert.False(hit.Success,
                    $"{fixture.Name}: keyword {keyword} survived the strip at offset {hit.Index}: " +
                    stripped.Substring(Math.Max(0, hit.Index - 60), Math.Min(160, stripped.Length - Math.Max(0, hit.Index - 60))));
            }
        }

        [Theory]
        [MemberData(nameof(BothFixtures))]
        public void No_constraint_line_carries_a_second_closing_parenthesis(Type fixture)
        {
            // The precise shape of the original corruption: a UNIQUE alternate-key line that ended
            // correctly, followed by the orphaned tail of the FK that was removed beneath it —
            //   CONSTRAINT [AK_...] UNIQUE ([ID], [CompanyID]), [CompanyID]) ON DELETE NO ACTION
            // Balanced parentheses on every constraint line catch that without naming the symptom.
            var stripped = Strip(fixture, GenerateScript());

            var unbalanced = stripped.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => Regex.IsMatch(l, @"^\s*CONSTRAINT \[", RegexOptions.IgnoreCase))
                .Where(l => l.Count(c => c == '(') != l.Count(c => c == ')'))
                .Take(5)
                .ToList();

            Assert.True(unbalanced.Count == 0,
                $"{fixture.Name}: constraint lines with unbalanced parentheses:" + Environment.NewLine +
                string.Join(Environment.NewLine, unbalanced));
        }

        [Theory]
        [MemberData(nameof(BothFixtures))]
        public void No_create_table_body_ends_with_a_dangling_comma(Type fixture)
        {
            // Removing the last item of a table body leaves `...,` immediately before `);`, which SQL
            // Server rejects. The stripper repairs this; without the repair the error would again name
            // something other than the cause.
            var stripped = Strip(fixture, GenerateScript());
            var lines = stripped.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

            var dangling = Enumerable.Range(0, lines.Length - 1)
                .Where(i => Regex.IsMatch(lines[i + 1], @"^\s*\)\s*;?\s*$")
                            && lines[i].TrimEnd().EndsWith(","))
                .Select(i => lines[i].Trim())
                .Take(5)
                .ToList();

            Assert.True(dangling.Count == 0,
                $"{fixture.Name}: table bodies ending in a dangling comma:" + Environment.NewLine +
                string.Join(Environment.NewLine, dangling));
        }

        [Theory]
        [MemberData(nameof(BothFixtures))]
        public void The_strip_removes_something_and_not_everything(Type fixture)
        {
            // A stripper that returned the script unchanged, or returned almost nothing, would pass the
            // three assertions above for the wrong reason. This bounds it: the current model declares
            // 157 inline foreign keys, so the removal is substantial but nowhere near the whole script.
            var raw = GenerateScript();
            var stripped = Strip(fixture, raw);

            Assert.True(stripped.Length < raw.Length, $"{fixture.Name}: the strip removed nothing");
            Assert.True(stripped.Length > raw.Length * 0.5,
                $"{fixture.Name}: the strip removed too much: {raw.Length} -> {stripped.Length}");
        }
    }
}
