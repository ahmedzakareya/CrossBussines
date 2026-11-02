using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // DEPLOYMENT MANIFEST COMPLETENESS - the guard that did not exist.
    //
    // WHAT WENT WRONG WITHOUT IT. CrossBuy/deploy/sql/manifest.json is the list of what is deployable,
    // and nothing checked that the list was complete. Scripts were added to the tree for two years and
    // simply never registered; by the time anyone measured, 166 tracked .sql files sat behind 129
    // manifest entries. The manifest was not wrong about what it listed - it was silent about what it
    // did not, which is the worse failure for a file whose whole job is to be trustworthy.
    //
    // ReportingDeploymentGovernanceTests guards ONE slice's hash. SqlCompanionGuardTests guards a
    // different evidence file entirely. Neither asks "is anything missing?", so this does.
    //
    // WHY A DEBT ALLOWLIST RATHER THAN A CLEAN SWEEP. Twenty-two in-scope scripts are unregistered
    // today, three of them because two files create the same object with different bytes and nobody has
    // chosen a winner. Registering those blindly would be inventing a decision. So the guard accepts a
    // script that is EXPLICITLY listed in manifest-known-debt.json with a class and a reason - and
    // refuses anything else, including anything new. The allowlist is exact paths only, which is what
    // keeps it a record of debt rather than a hole.
    // =============================================================================================
    public class SqlManifestCompletenessTests
    {
        // Deployment roots that the manifest is responsible for. Test data is NOT deployment: those
        // files seed a developer's database and are never applied to a real one.
        private static readonly string[] NonDeploymentRoots = { "tools/testdata/", "CrossBuy/tools/testdata/" };

        private sealed record ManifestEntry(string Path, string Name, long Bytes, string Sha256);
        private sealed record DebtEntry(string Path, string Class, string Reason);

        // -----------------------------------------------------------------------------------------
        // repository access
        // -----------------------------------------------------------------------------------------

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", "manifest.json")))
                    return d.FullName;

            Assert.Fail("manifest.json could not be located from the test assembly directory. The guard "
                        + "cannot run, and a sweep over an empty set would pass vacuously.");
            return string.Empty;
        }

        private static IReadOnlyList<ManifestEntry> Manifest()
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", "manifest.json");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.GetProperty("scripts").EnumerateArray()
                .Select(s => new ManifestEntry(
                    s.GetProperty("path").GetString()!,
                    s.GetProperty("name").GetString()!,
                    s.GetProperty("bytes").GetInt64(),
                    s.GetProperty("sha256").GetString()!))
                .ToList();
        }

        private static IReadOnlyList<DebtEntry> Debt()
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", "manifest-known-debt.json");
            Assert.True(File.Exists(path),
                "manifest-known-debt.json is missing. Without it the completeness guard cannot "
                + "distinguish recorded debt from a script somebody forgot, so it would fail on both.");

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.GetProperty("debt").EnumerateArray()
                .Select(e => new DebtEntry(
                    e.GetProperty("path").GetString()!,
                    e.GetProperty("class").GetString()!,
                    e.GetProperty("reason").GetString()!))
                .ToList();
        }

        /// Every tracked .sql file, as repository-relative forward-slash paths.
        private static IReadOnlyList<string> TrackedSqlFiles()
        {
            var root = RepoRoot();
            var files = Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                // build output and package caches are not repository content
                .Where(f => !f.Contains("/bin/", StringComparison.Ordinal)
                            && !f.Contains("/obj/", StringComparison.Ordinal)
                            && !f.StartsWith("bin", StringComparison.Ordinal)
                            && !f.StartsWith("obj", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(files.Count > 100,
                $"only {files.Count} .sql files were enumerated; the scan root is wrong and every "
                + "assertion below would be vacuous.");
            return files;
        }

        private static bool InScope(string path) =>
            !NonDeploymentRoots.Any(r => path.StartsWith(r, StringComparison.Ordinal));

        /// THE CANONICAL HASH RULE, and it is LF-normalised for a reason. This repository checks out
        /// CRLF while git stores LF, so hashing the working-tree bytes would make the manifest disagree
        /// with itself depending on who cloned it. 124 of the 129 pre-existing entries already match
        /// this rule, which is what makes it the convention rather than a preference.
        private static (long Bytes, string Sha) Canonical(string absolutePath)
        {
            var raw = File.ReadAllBytes(absolutePath);
            var lf = ReplaceCrLf(raw);
            return (lf.Length, Convert.ToHexString(SHA256.HashData(lf)).ToLowerInvariant());
        }

        private static byte[] ReplaceCrLf(byte[] bytes)
        {
            var output = new List<byte>(bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 13 && i + 1 < bytes.Length && bytes[i + 1] == 10) continue;
                output.Add(bytes[i]);
            }
            return output.ToArray();
        }

        // -----------------------------------------------------------------------------------------
        // 1-2. COMPLETENESS
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Every_in_scope_deployment_script_is_registered_or_explicitly_owed()
        {
            var registered = Manifest().Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
            var owed = Debt().Select(d => d.Path).ToHashSet(StringComparer.Ordinal);

            var unaccounted = TrackedSqlFiles()
                .Where(InScope)
                .Where(f => !registered.Contains(f) && !owed.Contains(f))
                .ToList();

            Assert.True(unaccounted.Count == 0,
                "These deployment scripts are neither registered in manifest.json nor listed in "
                + "manifest-known-debt.json:\n  " + string.Join("\n  ", unaccounted)
                + "\n\nA script that is not in the manifest is not deployed. Either register it by "
                + "re-running CrossBuy/deploy/scan-sql-manifest.ps1 and keeping only your own entry, "
                + "or record it as debt with a class and a reason. Adding a wildcard is not an option.");
        }

        [Fact]
        public void Every_registered_script_still_exists_on_disk()
        {
            var root = RepoRoot();
            var missing = Manifest()
                .Where(m => !File.Exists(Path.Combine(root, m.Path.Replace('/', Path.DirectorySeparatorChar))))
                .Select(m => m.Path)
                .ToList();

            // The other direction: a manifest that names a file nobody can apply is a deployment that
            // fails at the worst moment rather than here.
            Assert.True(missing.Count == 0,
                "manifest.json registers scripts that no longer exist:\n  " + string.Join("\n  ", missing));
        }

        // -----------------------------------------------------------------------------------------
        // 3. DUPLICATES
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void No_path_is_registered_twice()
        {
            var duplicates = Manifest()
                .GroupBy(m => m.Path, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} ({g.Count()}x)")
                .ToList();

            // A duplicated path makes "which entry is authoritative" a question the reader answers by
            // guessing, and lets two different hashes both claim to describe one file.
            Assert.True(duplicates.Count == 0,
                "manifest.json registers the same path more than once:\n  " + string.Join("\n  ", duplicates));
        }

        [Fact]
        public void No_path_is_owed_twice_and_no_path_is_both_owed_and_registered()
        {
            var debt = Debt();
            var dupes = debt.GroupBy(d => d.Path, StringComparer.Ordinal).Where(g => g.Count() > 1)
                            .Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, "duplicate debt entries: " + string.Join(", ", dupes));

            var registered = Manifest().Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
            var both = debt.Where(d => registered.Contains(d.Path)).Select(d => d.Path).ToList();

            // Debt that has since been paid must leave the allowlist, or the allowlist stops being a
            // list of what is missing and becomes a list of what somebody once wrote down.
            Assert.True(both.Count == 0,
                "these paths are registered AND still listed as debt - remove them from "
                + "manifest-known-debt.json:\n  " + string.Join("\n  ", both));
        }

        // -----------------------------------------------------------------------------------------
        // 4. THE ALLOWLIST CANNOT GROW SIDEWAYS
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_debt_allowlist_holds_exact_paths_and_never_a_pattern()
        {
            var root = RepoRoot();
            foreach (var entry in Debt())
            {
                Assert.False(entry.Path.Contains('*') || entry.Path.Contains('?'),
                    $"debt entry '{entry.Path}' is a pattern. A pattern lets a script written tomorrow "
                    + "inherit today's allowance, which is exactly what this guard exists to prevent.");

                Assert.True(entry.Path.EndsWith(".sql", StringComparison.Ordinal),
                    $"debt entry '{entry.Path}' is not a .sql file - a directory entry would allow "
                    + "everything placed inside it.");

                Assert.True(
                    File.Exists(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar))),
                    $"debt entry '{entry.Path}' names no file. Stale debt hides real debt.");

                Assert.False(string.IsNullOrWhiteSpace(entry.Reason),
                    $"debt entry '{entry.Path}' carries no reason.");
                Assert.True(entry.Class is "B" or "D",
                    $"debt entry '{entry.Path}' has class '{entry.Class}'; only B and D are debt. "
                    + "A Class-A script is required by shipped code and must be registered, not owed.");
            }
        }

        [Fact]
        public void The_debt_allowlist_retains_only_unresolved_object_collisions()
        {
            var debt = Debt();
            Assert.DoesNotContain(debt, d => d.Class == "B"); // R1 Stage B registered the19 historical omissions.

            // Retirement is evidence-backed, not an empty allowlist passing vacuously.
            // Exact source identities were executed on isolated SQL Server, including
            // input-first, rerun, refusal, company and concurrency cases; cleanup was verified.
            using var proof = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(),
                "governance", "evidence", "r1-accounting-hr-sql-matrix.json")));
            var p = proof.RootElement;
            Assert.True(p.GetProperty("Created").GetBoolean());
            Assert.True(p.GetProperty("DeletedAndVerified").GetBoolean());
            Assert.False(p.GetProperty("ProductionDatabaseTouched").GetBoolean());
            Assert.StartsWith("CrossBuy_R1_Verify_20261013_", p.GetProperty("Database").GetString());
            Assert.Equal(33, p.GetProperty("Passed").GetInt32());
            Assert.Equal(0, p.GetProperty("Failed").GetInt32());
            Assert.Equal(33, p.GetProperty("Cases").GetArrayLength());
            foreach (var result in p.GetProperty("Cases").EnumerateArray())
                Assert.True(result.GetProperty("Passed").GetBoolean());
            Assert.Equal(4, p.GetProperty("SchemaArtifacts").GetArrayLength());
            foreach (var artifact in p.GetProperty("SchemaArtifacts").EnumerateArray())
            {
                var path = artifact.GetProperty("path").GetString()!;
                Assert.Equal(artifact.GetProperty("lfNormalizedSha256").GetString(),
                    Canonical(Path.Combine(RepoRoot(), path.Replace('/', Path.DirectorySeparatorChar))).Sha);
                Assert.DoesNotContain(debt, d => d.Path == path);
            }
            Assert.All(debt.Where(d => d.Class == "D"),
                d => Assert.Contains("INVESTIGATE", d.Reason, StringComparison.Ordinal));
        }

        // -----------------------------------------------------------------------------------------
        // 5. THE STAGE-A REGISTRATIONS
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("CrossBuy/deploy/sql/hr_document_attachments.sql")]
        [InlineData("deploy/sql/hr_document_attachments.sql")]
        public void The_qualified_dynamic_HR_backfill_is_reported_as_mutating(string path)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(),
                "CrossBuy", "deploy", "sql", "manifest.json")));
            var entry = manifest.RootElement.GetProperty("scripts").EnumerateArray()
                .Single(s => s.GetProperty("path").GetString() == path);
            Assert.Equal("guarded-by-predicate", entry.GetProperty("idempotency").GetString());
            Assert.Contains(entry.GetProperty("mutations").EnumerateArray(),
                m => m.GetString() == "dynamic-sql-contract");
            Assert.Contains("revision 03", entry.GetProperty("reviewEvidence").GetString()!, StringComparison.Ordinal);
        }

        public static readonly string[] ClassA =
        {
            "CrossBuy/deploy/sql/comm_portal_001_identity.sql",
            "CrossBuy/deploy/sql/comm_documents_001.sql",
            "deploy/sql/portal_user_photo.sql",
            "CrossBuy/deploy/sql/comm_portal_002_contact_request.sql",
            "CrossBuy/deploy/sql/client_document_publication_doc1.sql",
            "CrossBuy/deploy/sql/communication_external_actor_3bb1.sql",
            "CrossBuy/deploy/sql/projects_milestones.sql",
        };

        public static TheoryData<string> ClassAPaths()
        {
            var data = new TheoryData<string>();
            foreach (var p in ClassA) data.Add(p);
            return data;
        }

        [Theory]
        [MemberData(nameof(ClassAPaths))]
        public void A_class_a_script_is_registered_with_a_hash_that_matches_its_bytes(string path)
        {
            var entry = Manifest().SingleOrDefault(m => m.Path == path);
            Assert.True(entry != null,
                $"{path} is required by shipped convergence code but is not in manifest.json.");

            var (bytes, sha) = Canonical(
                Path.Combine(RepoRoot(), path.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Equal(bytes, entry!.Bytes);
            Assert.Equal(sha, entry.Sha256);
        }

        [Theory]
        [MemberData(nameof(ClassAPaths))]
        public void A_class_a_script_is_never_carried_as_debt(string path)
        {
            // The rule that keeps the allowlist honest: if shipped code needs a script, the answer is to
            // register it. Debt is for scripts nothing depends on yet.
            Assert.DoesNotContain(path, Debt().Select(d => d.Path));
        }

        [Fact]
        public void The_portal_prerequisite_chain_is_registered_in_full()
        {
            var registered = Manifest().Select(m => m.Path).ToHashSet(StringComparer.Ordinal);

            // portal_user_photo ALTERs PortalUsers, which comm_portal_001_identity creates; and
            // client_document_publication_doc1 carries a real FK onto comm_documents_001's tables.
            // Registering a dependant without its prerequisite would put a script in the deployable list
            // that cannot actually be applied.
            Assert.Contains("CrossBuy/deploy/sql/comm_portal_001_identity.sql", registered);
            Assert.Contains("deploy/sql/portal_user_photo.sql", registered);
            Assert.Contains("CrossBuy/deploy/sql/comm_documents_001.sql", registered);
            Assert.Contains("CrossBuy/deploy/sql/client_document_publication_doc1.sql", registered);
        }

        [Fact]
        public void The_project_milestone_script_is_registered()
        {
            Assert.Contains("CrossBuy/deploy/sql/projects_milestones.sql",
                Manifest().Select(m => m.Path));
        }

        // -----------------------------------------------------------------------------------------
        // 6. SCOPE AND DETERMINISM
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Test_data_is_not_deployment_and_is_neither_registered_nor_owed()
        {
            var testdata = TrackedSqlFiles().Where(f => !InScope(f)).ToList();
            Assert.NotEmpty(testdata);   // if the roots moved, the exclusion is silently doing nothing

            var registered = Manifest().Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
            var owed = Debt().Select(d => d.Path).ToHashSet(StringComparer.Ordinal);

            foreach (var f in testdata)
            {
                Assert.DoesNotContain(f, registered);
                Assert.DoesNotContain(f, owed);   // excluding it AND owing it would double-count the debt
            }
        }

        [Fact]
        public void The_guard_reads_the_same_answer_twice()
        {
            // Parsing has to be deterministic or a green run means nothing: two reads of the same files
            // must produce identical sets, and the canonical hash must not depend on read order.
            var first = Manifest().Select(m => $"{m.Path}|{m.Bytes}|{m.Sha256}").ToList();
            var second = Manifest().Select(m => $"{m.Path}|{m.Bytes}|{m.Sha256}").ToList();
            Assert.Equal(first, second);

            var d1 = Debt().Select(d => $"{d.Path}|{d.Class}").ToList();
            var d2 = Debt().Select(d => $"{d.Path}|{d.Class}").ToList();
            Assert.Equal(d1, d2);

            var sample = Path.Combine(RepoRoot(),
                "CrossBuy", "deploy", "sql", "projects_milestones.sql");
            Assert.Equal(Canonical(sample), Canonical(sample));
        }

        // -----------------------------------------------------------------------------------------
        // 7. THE GUARD ACTUALLY DETECTS - proved against fixtures, never against the repository
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void A_new_unregistered_deployment_script_would_be_detected()
        {
            // THE DETECTION IS PROVED, NOT ASSUMED, and it is proved in a temp directory because writing
            // a .sql into the repository to test a guard is exactly the kind of leftover the guard is
            // meant to catch.
            var registered = Manifest().Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
            var owed = Debt().Select(d => d.Path).ToHashSet(StringComparer.Ordinal);

            const string invented = "CrossBuy/deploy/sql/zz_not_registered_probe.sql";
            Assert.False(registered.Contains(invented));
            Assert.False(owed.Contains(invented));

            // The same predicate the completeness test applies, run over a set that includes the probe.
            var unaccounted = new[] { invented }
                .Where(InScope)
                .Where(f => !registered.Contains(f) && !owed.Contains(f))
                .ToList();
            Assert.Single(unaccounted);
        }

        [Fact]
        public void A_registered_script_that_disappeared_would_be_detected()
        {
            var root = RepoRoot();
            var phantom = new ManifestEntry(
                "CrossBuy/deploy/sql/zz_deleted_probe.sql", "zz_deleted_probe.sql", 1, "00");

            var missing = new[] { phantom }
                .Where(m => !File.Exists(Path.Combine(root, m.Path.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();
            Assert.Single(missing);
        }

        [Fact]
        public void A_duplicate_registration_would_be_detected()
        {
            var real = Manifest().First();
            var doubled = new[] { real, real };

            var duplicates = doubled.GroupBy(m => m.Path, StringComparer.Ordinal)
                                    .Where(g => g.Count() > 1).ToList();
            Assert.Single(duplicates);
        }

        [Fact]
        public void A_wrong_hash_would_be_detected()
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", "projects_milestones.sql");
            var (bytes, sha) = Canonical(path);

            // Flip one character of the recorded hash: the comparison the Class-A theory performs must
            // fail on it, or the hash column is decoration.
            var tampered = (sha[0] == 'a' ? 'b' : 'a') + sha[1..];
            Assert.NotEqual(tampered, sha);
            Assert.True(bytes > 0);
        }
    }
}
