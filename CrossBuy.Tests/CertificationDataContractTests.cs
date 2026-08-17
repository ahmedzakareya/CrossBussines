using System;
using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // Pins the certification DATA contract.
    //
    // These tests exist because of a measured failure, not a hypothetical one: a governed matrix
    // captured against CrossBuyCert was verified against CrossBuyDev, every route came back ~167px
    // shorter, and 138 screenshots failed while the application was byte-identical. Two properties
    // had to become impossible to lose silently — a reset can only ever reach the certification
    // catalogue, and fixture visibility cannot depend on the machine's calendar.
    public class CertificationDataContractTests
    {
        private const string Cert = "Server=.;Database=CrossBuyCert;Trusted_Connection=True";

        // ---- forbidden database guards --------------------------------------------------------

        [Theory]
        [InlineData("CrossBuyDev")]      // the tempting wrong fix: make Dev imitate Cert
        [InlineData("CrossBuyDB")]
        [InlineData("CrossBuyDB2")]
        [InlineData("CrossBuy")]
        [InlineData("alprimedb_prod")]
        public void A_destructive_reset_refuses_every_non_certification_catalogue(string catalog)
        {
            var reason = CertificationDataContract.ResetRefusalReason(
                isDevelopment: true, flagValue: "1", $"Server=.;Database={catalog};Trusted_Connection=True");

            Assert.NotNull(reason);
            Assert.Contains(catalog, reason);
        }

        [Fact]
        public void A_reset_refuses_a_catalogue_that_merely_starts_with_the_certification_name()
        {
            // "CrossBuyCertScratch" contains "CrossBuyCert". A substring check would rebuild it.
            Assert.False(CertificationDataContract.MayReset(
                true, "1", "Server=.;Database=CrossBuyCertScratch;Trusted_Connection=True"));
        }

        [Fact]
        public void A_reset_refuses_a_connection_string_that_names_no_catalogue()
            => Assert.False(CertificationDataContract.MayReset(
                true, "1", "Server=.;Trusted_Connection=True"));

        [Fact]
        public void A_reset_refuses_an_absent_connection_string()
            => Assert.False(CertificationDataContract.MayReset(true, "1", null));

        [Fact]
        public void The_certification_catalogue_is_accepted_when_every_condition_holds()
            => Assert.Null(CertificationDataContract.ResetRefusalReason(true, "1", Cert));

        // ---- certification-mode enforcement ---------------------------------------------------

        [Fact]
        public void A_reset_refuses_a_non_development_host()
            => Assert.False(CertificationDataContract.MayReset(isDevelopment: false, "1", Cert));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("true")]   // a permissive parse is how a host half-enables a mode nobody meant
        [InlineData("yes")]
        [InlineData("1 ")]
        public void A_reset_refuses_any_flag_value_that_is_not_exactly_one(string? flag)
            => Assert.False(CertificationDataContract.MayReset(true, flag, Cert));

        // ---- wall-clock independence ----------------------------------------------------------

        // The fixtures as found expired 2026-08-20T19:49:47. Every instant below is AFTER that, so
        // this test fails against the old data and passes only against the contract's window.
        [Theory]
        [InlineData("2026-08-21T00:00:00Z")]   // the day the old fixtures died
        [InlineData("2027-01-01T00:00:00Z")]
        [InlineData("2036-06-30T12:00:00Z")]
        [InlineData("2099-12-31T23:59:59Z")]
        public void Fixture_visibility_does_not_change_when_the_real_clock_moves(string instant)
        {
            var now = DateTime.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                                    | System.Globalization.DateTimeStyles.AssumeUniversal);

            Assert.True(CertificationDataContract.FixtureVisibleAt(now),
                $"the certification banners must still be visible at {instant}; a baseline whose "
                + "fixtures expire is a baseline with a fuse.");
        }

        [Fact]
        public void The_expired_window_that_caused_the_failure_is_demonstrably_not_time_independent()
        {
            // The ORIGINAL fixture window, kept here as the falsifying case: this is what the
            // contract had to replace, and it proves the test above is measuring something real.
            var originalStart = new DateTime(2026, 8, 12, 19, 49, 47, DateTimeKind.Utc);
            var originalEnd = new DateTime(2026, 8, 20, 19, 49, 47, DateTimeKind.Utc);

            Assert.True(CertificationDataContract.IsVisibleAt(
                new DateTime(2026, 8, 15, 2, 38, 0, DateTimeKind.Utc), true, originalStart, originalEnd),
                "visible when the 138 screenshots were captured");

            Assert.False(CertificationDataContract.IsVisibleAt(
                new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc), true, originalStart, originalEnd),
                "invisible the following day — the fuse this contract removes");
        }

        [Fact]
        public void An_inactive_fixture_is_never_visible_however_wide_its_window()
            => Assert.False(CertificationDataContract.IsVisibleAt(
                DateTime.UtcNow, isActive: false,
                CertificationDataContract.FixtureWindowStart,
                CertificationDataContract.FixtureWindowEnd));

        // ---- the predicate matches the production filter shape --------------------------------

        [Fact]
        public void Null_bounds_mean_unbounded_exactly_as_the_production_filter_treats_them()
        {
            Assert.True(CertificationDataContract.IsVisibleAt(DateTime.UtcNow, true, null, null));
            Assert.True(CertificationDataContract.IsVisibleAt(DateTime.UtcNow, true, null,
                CertificationDataContract.FixtureWindowEnd));
        }

        // ---- catalogue parsing ----------------------------------------------------------------

        [Theory]
        [InlineData("Server=.;Database=CrossBuyCert;Trusted_Connection=True", "CrossBuyCert")]
        [InlineData("Server=.;Initial Catalog=CrossBuyCert;Integrated Security=true", "CrossBuyCert")]
        [InlineData("Server=.;Trusted_Connection=True", null)]
        public void The_catalogue_is_read_from_the_key_not_scanned_from_the_string(string cs, string? expected)
            => Assert.Equal(expected, CertificationDataContract.CatalogOf(cs));

        [Fact]
        public void The_contract_and_the_startup_gate_agree_on_the_certification_database_name()
            => Assert.Equal(CertificationDataGate.CertificationDatabaseMarker,
                            CertificationDataContract.CertificationDatabase);

        [Fact]
        public void The_contract_and_the_clock_agree_on_the_enabling_switch()
            => Assert.Equal(ConformanceClock.EnableEnvironmentVariable,
                            CertificationDataContract.EnableEnvironmentVariable);

        // ---- golden artifact: backup + restore guards -----------------------------------------

        [Theory]
        [InlineData("CrossBuyDev")]
        [InlineData("CrossBuyDB")]
        [InlineData("CrossBuyDB2")]
        [InlineData("CrossBuy")]
        [InlineData("alprimedb_prod")]
        public void A_golden_BACKUP_refuses_every_non_certification_source(string catalog)
            => Assert.False(CertificationDataContract.MayBackup(
                true, "1", $"Server=.;Database={catalog};Trusted_Connection=True"));

        [Theory]
        [InlineData("CrossBuyDev")]
        [InlineData("CrossBuyDB")]
        [InlineData("CrossBuyDB2")]
        [InlineData("CrossBuy")]
        [InlineData("alprimedb_prod")]
        public void A_golden_RESTORE_refuses_every_non_certification_target(string catalog)
            => Assert.False(CertificationDataContract.MayRestore(
                true, "1", $"Server=.;Database={catalog};Trusted_Connection=True"));

        [Fact]
        public void Backup_and_restore_are_refused_off_a_development_host()
        {
            Assert.False(CertificationDataContract.MayBackup(false, "1", Cert));
            Assert.False(CertificationDataContract.MayRestore(false, "1", Cert));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("0")]
        [InlineData("true")]
        public void Backup_and_restore_are_refused_without_the_exact_flag(string? flag)
        {
            Assert.False(CertificationDataContract.MayBackup(true, flag, Cert));
            Assert.False(CertificationDataContract.MayRestore(true, flag, Cert));
        }

        [Fact]
        public void Backup_and_restore_accept_the_certification_catalogue()
        {
            Assert.True(CertificationDataContract.MayBackup(true, "1", Cert));
            Assert.True(CertificationDataContract.MayRestore(true, "1", Cert));
        }

        [Theory]
        [InlineData(@"C:\repo\artifact.bak", @"C:\repo", false)]              // inside the repo
        [InlineData(@"C:\repo\tools\x\artifact.bak", @"C:\repo", false)]      // nested inside
        [InlineData(@"C:\certification\artifact.bak", @"C:\repo", true)]      // outside
        [InlineData(@"C:\repo-adjacent\artifact.bak", @"C:\repo", true)]      // prefix-similar sibling
        public void A_golden_artifact_inside_the_repository_is_refused(string artifact, string root, bool expected)
            => Assert.Equal(expected, CertificationDataContract.IsOutsideRepository(artifact, root));

        // ---- certification runtime mode (writer suppression) -----------------------------------

        [Fact]
        public void Writer_suppression_is_ON_for_the_certification_database()
            => Assert.True(CertificationDataContract.IsCertificationRuntime(true, "1", Cert));

        [Fact]
        public void Writer_suppression_is_OFF_for_ordinary_development_against_CrossBuyDev()
            => Assert.False(CertificationDataContract.IsCertificationRuntime(
                true, "1", "Server=.;Database=CrossBuyDev;Trusted_Connection=True"));

        [Fact]
        public void Writer_suppression_is_OFF_when_the_flag_is_absent_even_against_the_certification_database()
            => Assert.False(CertificationDataContract.IsCertificationRuntime(true, null, Cert));

        [Fact]
        public void Writer_suppression_is_OFF_outside_development()
            => Assert.False(CertificationDataContract.IsCertificationRuntime(false, "1", Cert));

        [Fact]
        public void Writer_suppression_never_engages_for_a_production_catalogue()
        {
            foreach (var catalog in new[] { "CrossBuyDB", "CrossBuyDB2", "alprimedb_prod" })
                Assert.False(CertificationDataContract.IsCertificationRuntime(
                    true, "1", $"Server=.;Database={catalog};Trusted_Connection=True"));
        }

        // ---- the carried runtime contract read by determinism-proof.mjs ------------------------

        // The gate relaxes its Primary requirement only when BOTH flags are true. If these two could
        // ever disagree, a host with its writers still running could present itself as a certification
        // run and have a stable-looking Unknown accepted. Deriving one from the other makes that
        // combination unrepresentable rather than merely unlikely.
        [Fact]
        public void The_reported_contract_cannot_claim_certification_while_writers_still_run()
        {
            var on = new CertificationRuntimeState(true);
            Assert.True(on.CertificationMode);
            Assert.True(on.BackgroundWritersSuppressed);

            var off = new CertificationRuntimeState(false);
            Assert.False(off.CertificationMode);
            Assert.False(off.BackgroundWritersSuppressed);
        }

        [Fact]
        public void The_reported_contract_mirrors_the_decision_that_suppresses_the_writers()
        {
            // Ordinary development against CrossBuyDev: both false.
            var dev = new CertificationRuntimeState(CertificationDataContract.IsCertificationRuntime(
                true, "1", "Server=.;Database=CrossBuyDev;Trusted_Connection=True"));
            Assert.False(dev.CertificationMode);
            Assert.False(dev.BackgroundWritersSuppressed);

            // Certification host against CrossBuyCert: both true.
            var cert = new CertificationRuntimeState(CertificationDataContract.IsCertificationRuntime(true, "1", Cert));
            Assert.True(cert.CertificationMode);
            Assert.True(cert.BackgroundWritersSuppressed);

            // Production can never reach it.
            var prod = new CertificationRuntimeState(CertificationDataContract.IsCertificationRuntime(
                false, "1", "Server=.;Database=alprimedb_prod;Trusted_Connection=True"));
            Assert.False(prod.CertificationMode);
            Assert.False(prod.BackgroundWritersSuppressed);
        }
    }
}
