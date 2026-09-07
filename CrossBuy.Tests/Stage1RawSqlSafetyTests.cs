using System.Text.RegularExpressions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B / B5 — raw SQL and direct-DbContext safety for the pilot.
    //
    // WHY THIS FILE EXISTS
    //
    // B2's filters are the floor for LINQ. They are NOT a floor for raw SQL, and the difference is easy to state and
    // easy to forget:
    //
    //   * `Database.SqlQueryRaw<T>` / `ExecuteSqlRaw` — a scalar or a command. There is no entity, so there is no
    //     query filter. NOTHING is applied. A raw read of a pilot table sees every company unless its own WHERE
    //     clause says otherwise.
    //   * `DbSet.FromSql*` — EF composes the global filter as an OUTER predicate around the raw query. The filter
    //     does apply, but the raw text runs as a subquery, which is why StockBalance is excluded from the pilot:
    //     its `WITH (UPDLOCK, HOLDLOCK)` row lock is taken by the inner query while the company predicate becomes
    //     an outer filter, changing the plan for the lock that prevents overselling.
    //
    // So raw SQL cannot be secured by the pilot; it can only be INVENTORIED and reviewed. This file is that
    // inventory, expressed as a test: it re-derives every raw-SQL call site from the source and fails if the set
    // changes. A new raw query against a pilot table therefore cannot land without a reviewer seeing it.
    public class Stage1RawSqlSafetyTests
    {
        // The raw-SQL entry points EF exposes. `SqlQuery`/`SqlQueryRaw` are scalars; `FromSql*` are entity queries.
        private static readonly Regex RawSqlCall = new(
            @"\.(FromSqlRaw|FromSqlInterpolated|SqlQueryRaw|SqlQuery|ExecuteSqlRaw|ExecuteSqlRawAsync|ExecuteSqlInterpolated|ExecuteSqlInterpolatedAsync)\s*[<(]",
            RegexOptions.Compiled);

        // The REVIEWED inventory of production raw SQL. One entry per call site, each with the verdict that made it
        // acceptable. `PilotTable` means the statement reads or writes one of B2's twelve.
        //
        // If this list and the source disagree, the test fails — deliberately. The failure message says which file
        // gained or lost a call, so the reviewer's job is to add the verdict, not to silence the test.
        private static readonly (string File, int Count, string Verdict)[] ReviewedProductionRawSql =
        {
            ("BL/IntegrityCheckService.cs", 3,
                "1) barcode_cross_table_dup reads Items (PILOT) — company predicate ADDED in B5; it previously " +
                "counted every company's barcodes. 2) fixed_barcode_in_scale_range reads Items (PILOT) with an " +
                "explicit CompanyID. 3) sys.tables probe — no business data."),

            ("BL/JournalEntryService.cs", 2,
                "NumberSequences only (NOT a pilot entity), with CompanyID as an explicit parameter in both the " +
                "ambient and the isolated-scope JV allocation paths. Verified in B2: the isolated scope reads no " +
                "pilot entity, so an unresolved holder in that short-lived context cannot affect numbering."),

            ("BL/PosOrderService.cs", 2,
                "PosTerminals only (NOT a pilot entity), addressed by terminal id. A terminal id is globally " +
                "unique, so no company predicate is required; recorded rather than assumed."),

            ("BL/StockService.cs", 2,
                "TWO FromSqlInterpolated reads of StockBalances, both with an explicit CompanyID under " +
                "WITH (UPDLOCK, HOLDLOCK): the movement path and the goods-receipt path. StockBalance is EXCLUDED " +
                "from the pilot by instruction precisely because a global filter would compose around these and " +
                "change the plan for the sole-writer overselling guard. " +
                "PROVENANCE: the inventory recorded 1 when written; the second appeared DURING Batch B, from an " +
                "uncommitted change to StockService by the parallel team. StockService is one of our two " +
                "architectural-invariant writers, so the change is flagged for coordination (see the Batch B " +
                "report) — its raw-SQL shape is identical to the first and carries the same verdict."),

            ("BL/Platform/PlatformGrantWriter.cs", 1,
                "ONE FromSqlRaw read of PlatformRoleAssignments under WITH (UPDLOCK, ROWLOCK), in " +
                "LoadForUpdateAsync, used by revoke and by the validity update. BOTH the grant id AND CompanyID are " +
                "explicit parameters, so the read is company-scoped by predicate rather than by filter — which " +
                "matters here because PlatformRoleAssignments is deliberately NOT a B2 pilot entity (its own " +
                "reader, PlatformRoleDirectory, states that its company predicate IS the isolation, not a second " +
                "line of defence). " +
                "WHY RAW SQL AT ALL: the locked read is the concurrency control. Batch A's A9 requires that two " +
                "concurrent validity updates not silently overwrite each other, and the project's established " +
                "idiom for that is a locked read inside the transaction rather than a new rowversion column — the " +
                "same choice StockService makes above. Including CompanyID in the lookup also makes a " +
                "foreign-company id return NotFound instead of found-then-denied, so a caller cannot learn that " +
                "another company's grant exists."),
        };

        // Files exempt from the inventory, each for a stated reason rather than convenience.
        private static readonly string[] NotProductionCode =
        {
            "Controllers/Api/DevSeedController.cs",   // [DevOnly] test harness — see the dev-scope test below
            "BL/Platform/CompanyQueryFilters.cs",     // mentions the API names in comments only
        };

        // =====================================================================================
        // 1. THE INVENTORY IS COMPLETE AND UNCHANGED
        // =====================================================================================

        [Fact]
        public void Every_production_raw_SQL_call_site_is_in_the_reviewed_inventory()
        {
            var found = ScanRawSql();

            var unreviewed = found.Keys
                .Where(f => !ReviewedProductionRawSql.Any(r => r.File == f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(unreviewed.Count == 0,
                "New raw SQL appeared in production code with no company-scope verdict:\n  " +
                string.Join("\n  ", unreviewed.Select(f => $"{f} ({found[f]} call site(s))")) +
                "\n\nRaw SQL is NOT covered by the B2 query filters. Add the file to " +
                "ReviewedProductionRawSql with the predicate that scopes it — or scope the query.");

            foreach (var (file, count, verdict) in ReviewedProductionRawSql)
            {
                Assert.True(found.ContainsKey(file), $"{file} no longer contains raw SQL — remove its inventory entry.");
                Assert.True(count == found[file],
                    $"{file} now has {found[file]} raw-SQL call site(s), the inventory records {count}. " +
                    $"Review the new one against: {verdict}");
            }
        }

        // The specific regression B5 fixed, pinned so it cannot come back: the barcode duplicate check must carry a
        // company predicate. Asserted on the SQL text because there is no other way to test a raw string's scope.
        [Fact]
        public void The_barcode_duplicate_check_scopes_its_raw_read_to_one_company()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "IntegrityCheckService.cs"));

            int start = source.IndexOf("barcode_cross_table_dup", StringComparison.Ordinal);
            Assert.True(start > 0, "The barcode_cross_table_dup check no longer exists under that key.");

            // The raw statement sits immediately above the result row that names the key.
            int queryStart = source.LastIndexOf("SqlQueryRaw", start, StringComparison.Ordinal);
            Assert.True(queryStart > 0, "The barcode duplicate check no longer uses SqlQueryRaw.");
            var statement = source[queryStart..start];

            Assert.Contains("Items i", statement);
            Assert.Contains("i.CompanyID = {0}", statement);      // the pilot table IS scoped
            Assert.Contains("i2.CompanyID = {0}", statement);      // ...and so is the join through ItemBarcodes
        }

        // =====================================================================================
        // 2. WHAT A FILTER DOES AND DOES NOT DO TO RAW SQL — DEMONSTRATED, NOT ASSERTED IN PROSE
        // =====================================================================================

        // A scalar raw query is NOT filtered. This is the fact the inventory above exists to manage, so it is shown
        // rather than described: the same count differs between LINQ (filtered) and raw SQL (not).
        [Fact]
        public async Task A_scalar_raw_query_is_not_filtered_at_all()
        {
            using var host = new PlatformTestHost(companyId: 1);
            host.Seed.Customers.AddRange(
                new Customer { CompanyID = 1, Name = "mine" },
                new Customer { CompanyID = 2, Name = "theirs" });
            await host.Seed.SaveChangesAsync();

            // LINQ: filtered to company 1.
            Assert.Equal(1, await host.Db.Customers.AsNoTracking().CountAsync());

            // Raw scalar: sees BOTH. No filter is applied, and none can be.
            var raw = await host.Db.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Customers")
                .FirstAsync();
            Assert.Equal(2, raw);
        }

        // FromSql, by contrast, IS composed with the filter — EF wraps the raw query and applies the predicate
        // outside it. This is why the StockBalance exclusion is about the LOCK PLAN rather than about correctness.
        [Fact]
        public async Task A_FromSql_entity_query_is_still_composed_with_the_filter()
        {
            using var host = new PlatformTestHost(companyId: 1);
            host.Seed.Customers.AddRange(
                new Customer { CompanyID = 1, Name = "mine" },
                new Customer { CompanyID = 2, Name = "theirs" });
            await host.Seed.SaveChangesAsync();

            // The raw text asks for EVERY row; the filter narrows it to company 1 anyway.
            var rows = await host.Db.Customers
                .FromSqlRaw("SELECT * FROM Customers")
                .AsNoTracking()
                .ToListAsync();

            Assert.Single(rows);
            Assert.Equal(1, rows[0].CompanyID);

            // ...and IgnoreQueryFilters removes it, which is the escape hatch that must never be used casually —
            // it takes no authorization and writes no audit line, unlike ICompanyIsolationBypass.
            var unfiltered = await host.Db.Customers
                .FromSqlRaw("SELECT * FROM Customers")
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync();
            Assert.Equal(2, unfiltered.Count);
        }

        // IgnoreQueryFilters is the one call that silently undoes B2 with no authorization and no audit trail. It
        // must not appear in production code — the sanctioned route is ICompanyIsolationBypass (ADR-023).
        [Fact]
        public void IgnoreQueryFilters_is_not_used_anywhere_in_production_code()
        {
            var offenders = EnumerateProductionSources()
                .Where(f => File.ReadAllLines(f)
                    .Any(line => StripComment(line).Contains("IgnoreQueryFilters", StringComparison.Ordinal)))
                .Select(Relative)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(offenders.Count == 0,
                "IgnoreQueryFilters() bypasses company isolation with no authorization and no audit line:\n  " +
                string.Join("\n  ", offenders) +
                "\nUse ICompanyIsolationBypass instead — it authorizes, scopes and audits (ADR-023).");
        }

        // =====================================================================================
        // 3. DIRECT DbContext CONSTRUCTION
        // =====================================================================================

        // Production must not construct a CrossDbContext by hand: DI supplies the ICompanyScopeHolder, and the
        // single-argument constructor exists only as a fail-closed fallback. A `new CrossDbContext(options)` in
        // production would read NOTHING from the twelve — a silent blank rather than a leak, but still a bug.
        [Fact]
        public void Production_code_never_constructs_a_CrossDbContext_by_hand()
        {
            // Comment-aware: CrossDbContext.cs's own constructor documentation quotes the very expression this test
            // forbids, and a scan that could not tell code from prose would report the explanation as the offence.
            var construction = new Regex(@"new\s+(CrossBuy\.Models\.Context\.)?CrossDbContext\s*\(", RegexOptions.Compiled);

            var offenders = EnumerateProductionSources()
                .Where(f => File.ReadAllLines(f).Any(line => construction.IsMatch(StripComment(line))))
                .Select(Relative)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(offenders.Count == 0,
                "A CrossDbContext constructed by hand does not get the request's company scope:\n  " +
                string.Join("\n  ", offenders));
        }

        // Every per-company worker binds its DI scope to the company it is processing. Without this, the worker's
        // holder is unresolved and — because the filters fail closed — the worker reads NOTHING: CRM reminders stop
        // firing, and the integrity check passes by examining zero rows. Enforced structurally rather than trusted.
        [Theory]
        [InlineData("BL/CrmReminderHostedService.cs")]
        [InlineData("BL/TaskGeneratorHostedService.cs")]
        [InlineData("BL/IntegrityCheckHostedService.cs")]
        [InlineData("BL/TaskScheduleMatchHostedService.cs")]
        public void Every_per_company_worker_binds_its_scope_to_that_company(string file)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", file.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Contains("WorkerScope.ForCompany(_scopes, companyId)", source);

            // ...and it does NOT also create a bare scope inside the per-company body, which would silently read
            // nothing. The outer `scopeRoot` (used only to resolve IWorkerCompanyScope) is the one permitted bare
            // CreateScope, and it reads Companies — not a pilot entity.
            Assert.Equal(1, Regex.Matches(source, @"_scopes\.CreateScope\(\)").Count);
        }

        // ---- source scanning -------------------------------------------------------------------------------
        //
        // The scan is over SOURCE, deliberately: the claim is about what the code says, and a reflection-based
        // check could not see a raw SQL string at all.

        private static Dictionary<string, int> ScanRawSql()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in EnumerateProductionSources())
            {
                var relative = Relative(file);
                if (NotProductionCode.Contains(relative, StringComparer.Ordinal)) continue;

                int count = 0;
                foreach (var line in File.ReadAllLines(file))
                {
                    var code = StripComment(line);
                    if (code.Length == 0) continue;
                    count += RawSqlCall.Matches(code).Count;
                }
                if (count > 0) result[relative] = count;
            }
            return result;
        }

        // A `//` inside a string literal is not a comment. Only a `//` outside quotes starts one — without this the
        // scanner would drop half of a raw SQL statement containing a URL or a date format.
        private static string StripComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"') inString = !inString;
                else if (!inString && line[i] == '/' && line[i + 1] == '/') return line[..i];
            }
            return line;
        }

        private static IEnumerable<string> EnumerateProductionSources()
        {
            var root = Path.Combine(RepoRoot(), "CrossBuy");
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         // Migrations/ is a dead snapshot by project rule; it is not compiled behaviour.
                         && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
        }

        private static string Relative(string fullPath)
        {
            var prefix = Path.Combine(RepoRoot(), "CrossBuy") + Path.DirectorySeparatorChar;
            return fullPath[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }

        // Walks up from the test binary to the directory holding CrossBuy.sln. Fails with a clear message rather
        // than silently scanning nothing — a source scanner that finds no files would report "all clear".
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
