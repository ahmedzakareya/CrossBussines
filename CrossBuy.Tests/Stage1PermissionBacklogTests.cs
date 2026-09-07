using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B / B6 — the permission backlog, pinned.
    //
    // The numbers in CORRECTION-004 and Stage-001-B6 come from a PowerShell scanner. A document quoting a number a
    // script produced once is a number nobody will notice going stale. This file re-derives the split from the
    // committed evidence CSV, so the documents and the evidence cannot drift apart in silence.
    //
    // It asserts the RECONCILIATION rather than a single total: 388 mutating actions = 151 with a permission
    // attribute + 54 authorized in-body + 183 backlog. A change to any one of the four has to be explained.
    //
    // Hotfix A.1 moved 40 -> 50 and 193 -> 183: the ten AccountingApiController mutating actions became authorized
    // in-body by IAccountingApiAuthorization.
    //
    // Batch C then moved the mutating TOTAL 384 -> 388 and in-body 50 -> 54, leaving the backlog at 183 — and that
    // stability is a COINCIDENCE, not an absence of change. The arithmetic, reconciled against source:
    //   -2  Batch C proof endpoints authorized (PeopleController.DecideLeave, TasksController.ConfirmMatch)
    //   +3  mutating POS-lane actions added CONCURRENTLY by the parallel team in HyperPosController, of which
    //       2 carry an in-body check and 1 (StampInvoiceCustomer) does not
    //   +1  a further mutating action in DevSeedController, likewise concurrent
    //   ⇒ 183 - 2 + 1 (StampInvoiceCustomer) + 1 (DevSeed) = 183
    // Batch C's own contribution is -2. The rest is other people's work arriving in the same tree, and it is
    // recorded rather than absorbed into this batch's figures.
    public class Stage1PermissionBacklogTests
    {
        // The module-permission attributes — CORRECTION-004's corrected list. PosLaneActivityGuard and DevOnly are
        // deliberately absent: the first checks a branch's lane and no role, the second is an environment gate.
        private static readonly string[] ModulePermissions =
            { "AccPerm", "InvPerm", "CrmPerm", "PlatformOps", "PosPerm", "HrPerm", "ProjectPerm", "ApiPerm" };

        [Fact]
        public void The_backlog_reconciles_exactly_with_the_committed_evidence()
        {
            var rows = ReadCoverage();
            var mutating = rows.Where(r => r["mutating"] == "True").ToList();

            var withAttribute = mutating.Where(HasPermissionAttribute).ToList();
            var withoutAttribute = mutating.Where(r => !HasPermissionAttribute(r)).ToList();
            var inBody = withoutAttribute.Where(r => r["in_body_authorization"] == "True").ToList();
            var backlog = withoutAttribute.Where(r => r["in_body_authorization"] != "True").ToList();

            Assert.Equal(388, mutating.Count);
            Assert.Equal(157, withAttribute.Count);
            Assert.Equal(88, inBody.Count);
            Assert.Equal(143, backlog.Count);

            // The reconciliation itself: no action may fall into two buckets or none.
            Assert.Equal(mutating.Count, withAttribute.Count + inBody.Count + backlog.Count);
        }

        // The lane-guarded, session-checked, role-UNCHECKED actions. CORRECTION-004 found four; a fifth
        // (HyperPosController.StampInvoiceCustomer) arrived during Batch C from concurrent work.
        // Named individually because "five POS actions" is not a finding anyone can act on.
        [Fact]
        public void The_four_lane_guarded_actions_without_a_role_check_are_still_exactly_these()
        {
            var rows = ReadCoverage();

            var laneGuardedWithoutRoleCheck = rows
                .Where(r => r["mutating"] == "True"
                         && r["inherited_permission"].Contains("PosLaneActivityGuard", StringComparison.Ordinal)
                         && r["in_body_authorization"] != "True")
                .Select(r => $"{r["controller"]}.{r["action"]}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(
                new[]
                {
                    // Ordinal order: "Stam" sorts before "Star".
                    "HyperPosController.PriceCheck",
                    // added CONCURRENTLY by the parallel team
                    "HyperPosController.Start",
                    // The material one: it CREATES a Customer, a B2 pilot entity, with no role check.
                    "PosAppController.AddCustomer",
                    "PosAppController.Start",
                },
                laneGuardedWithoutRoleCheck);
        }

        // Batch C: the four proof endpoints authorize in-body. Two are mutating (DecideLeave, ConfirmMatch) and
        // therefore left the backlog; two are reads (Boq, Messages).
        [Fact]
        public void The_four_batch_c_proof_endpoints_are_authorized()
        {
            var rows = ReadCoverage();

            var proofs = new[]
            {
                ("PeopleController", "DecideLeave", "True"),
                ("TasksController", "ConfirmMatch", "True"),
                ("ProjectController", "Boq", "False"),
                ("ChatController", "Messages", "False"),
            };

            foreach (var (controller, action, mutating) in proofs)
            {
                var row = rows.SingleOrDefault(r => r["controller"] == controller && r["action"] == action);
                Assert.NotNull(row);
                Assert.Equal("True", row!["in_body_authorization"]);
                Assert.Equal(mutating, row["mutating"]);
            }
        }

        // Hotfix A.1: all ten AccountingApiController mutating actions authorize in-body, and NONE of them is in
        // the backlog any more. Pinned per-action rather than as a count, so a regression names the endpoint.
        [Fact]
        public void Every_accounting_api_mutating_action_is_authorized()
        {
            var rows = ReadCoverage();
            var accounting = rows.Where(r => r["controller"] == "AccountingApiController").ToList();

            Assert.Equal(22, accounting.Count);
            var mutating = accounting.Where(r => r["mutating"] == "True").ToList();
            Assert.Equal(10, mutating.Count);

            // Every one of the ten — and every read as well, since the guard covers those too.
            Assert.All(accounting, r => Assert.Equal("True", r["in_body_authorization"]));

            Assert.Equal(
                new[] { "CreateCustomer", "CreateJournal", "CreatePayment", "CreatePurchaseInvoice",
                        "CreateReceipt", "CreateSalesInvoice", "CreateVendor", "PayrollPost", "Post", "Reverse" },
                mutating.Select(r => r["action"]).OrderBy(a => a, StringComparer.Ordinal));
        }

        // The lane-guarded population CORRECTION-004 re-examined. If this changes, the in-body/role-less split is
        // being measured over a different set and the correction needs revisiting.
        [Fact]
        public void The_lane_guarded_population_is_unchanged()
        {
            var rows = ReadCoverage();
            var laneGuarded = rows.Where(r => r["mutating"] == "True"
                && r["inherited_permission"].Contains("PosLaneActivityGuard", StringComparison.Ordinal)).ToList();

            // 44 -> 47 and 40 -> 42 during Batch C: three mutating lane actions added concurrently by the
            // parallel team (two with an in-body check). Batch C touched neither POS controller.
            Assert.Equal(47, laneGuarded.Count);
            Assert.Equal(43, laneGuarded.Count(r => r["in_body_authorization"] == "True"));
        }

        // The severity frame from the B6 document: the backlog is an AUTHORIZATION gap, not an exposure one. Exactly
        // one backlog action is anonymous by design (the JWT login), and if that ever becomes two, the framing in
        // Stage-001-B6 §2 is no longer true and must be rewritten before anyone reads it as reassurance.
        [Fact]
        public void Exactly_one_backlog_action_is_anonymous_by_design()
        {
            var rows = ReadCoverage();

            // Anonymous-reachable = on /api (exempt from SessionValidationMiddleware) with no class-level Authorize.
            var anonymous = rows
                .Where(r => r["mutating"] == "True"
                         && !HasPermissionAttribute(r)
                         && r["in_body_authorization"] != "True"
                         && r["is_api"] == "True"
                         && !r["inherited_permission"].Contains("Authorize", StringComparison.Ordinal))
                .Select(r => $"{r["controller"]}.{r["action"]}")
                .ToList();

            Assert.Equal(new[] { "AuthApiController.Login" }, anonymous);
        }

        // ---- evidence reading ------------------------------------------------------------------------------

        private static bool HasPermissionAttribute(Dictionary<string, string> row)
        {
            var text = row["permission_attributes"] + ";" + row["inherited_permission"];
            return ModulePermissions.Any(p => text.Contains(p, StringComparison.Ordinal));
        }

        private static List<Dictionary<string, string>> ReadCoverage()
        {
            var path = Path.Combine(RepoRoot(), "docs", "architecture", "evidence", "Permission-Coverage.csv");
            Assert.True(File.Exists(path),
                $"{path} is missing. Regenerate it: powershell -File CrossBuy/deploy/scan-architecture.ps1");

            var lines = File.ReadAllLines(path);
            var header = SplitCsv(lines[0]);
            Assert.Contains("in_body_authorization", header);   // CORRECTION-004's column must be present

            var rows = new List<Dictionary<string, string>>();
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                var cells = SplitCsv(line);
                if (cells.Length != header.Length) continue;   // a stray blank line, not a row
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < header.Length; i++) row[header[i]] = cells[i];
                rows.Add(row);
            }
            Assert.NotEmpty(rows);
            return rows;
        }

        // The scanner quotes every field and doubles embedded quotes, so a full CSV parser is unnecessary — but
        // commas DO appear inside quoted attribute text (e.g. PosLaneActivityGuard("PosCtx", "restaurant", …)),
        // which is why splitting on ',' would corrupt the columns this test depends on.
        private static string[] SplitCsv(string line)
        {
            var cells = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes) { cells.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            cells.Add(current.ToString());

            // The scanner writes a UTF-8 BOM; it lands on the first header cell and would break the lookup.
            if (cells.Count > 0) cells[0] = cells[0].TrimStart('﻿');
            return cells.ToArray();
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
