using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // UI CERTIFICATION — the production safety gate for the certification DATABASE (D1–D3, D12).
    //
    // Certification now runs against CrossBuyCert, restored from a golden snapshot before every run.
    // The danger this guards is not a browser attack — a connection string is host configuration and can
    // never be request-supplied — it is a MISCONFIGURED HOST: a production deployment that inherits a
    // certification connection string would serve stale snapshot data to real users, silently.
    //
    // Tested as a pure function, exactly like ConformanceClock, so the refusal matrix is provable
    // without standing up a host.
    public class CertificationDataGateTests
    {
        private const string CertConn =
            "Server=localhost\\SQLEXPRESS;Database=CrossBuyCert;Trusted_Connection=True;";
        private const string DevConn =
            "Server=localhost\\SQLEXPRESS;Database=CrossBuyDev;Trusted_Connection=True;";
        private const string ProdConn =
            "Server=prod-sql;Database=CrossBuyDB2;Trusted_Connection=True;";

        // ---- D1: certification data behaviour is impossible in Production ----
        //
        // The flag being present does NOT help: production plus the flag is still refused, which is the
        // case that matters because a flag is exactly the sort of thing that leaks into an environment
        // file.
        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData(null)]
        public void D1_a_production_host_may_never_use_a_certification_database(string? flag)
        {
            Assert.False(CertificationDataGate.ShouldAllowCertificationDatabase(isDevelopment: false, flag));
            Assert.NotNull(CertificationDataGate.RefusalReason(isDevelopment: false, flag, CertConn));
            Assert.Throws<InvalidOperationException>(
                () => CertificationDataGate.AssertSafe(isDevelopment: false, flag, CertConn));
        }

        // ---- D2: development WITHOUT the flag is also refused ----
        [Fact]
        public void D2_development_without_the_flag_may_not_use_a_certification_database()
        {
            Assert.False(CertificationDataGate.ShouldAllowCertificationDatabase(isDevelopment: true, null));
            Assert.Throws<InvalidOperationException>(
                () => CertificationDataGate.AssertSafe(isDevelopment: true, null, CertConn));
        }

        // ---- D3: a malformed / permissive flag value does NOT enable it ----
        //
        // Only the exact string "1". A lenient parse is how a half-configured host quietly enables a mode
        // nobody intended.
        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("0")]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("yes")]
        [InlineData("1 ")]
        [InlineData("01")]
        public void D3_only_the_exact_flag_value_enables_certification_data(string flag)
            => Assert.False(CertificationDataGate.ShouldAllowCertificationDatabase(isDevelopment: true, flag));

        [Fact]
        public void The_correct_combination_is_permitted()
        {
            Assert.True(CertificationDataGate.ShouldAllowCertificationDatabase(isDevelopment: true, "1"));
            Assert.Null(CertificationDataGate.RefusalReason(isDevelopment: true, "1", CertConn));
            CertificationDataGate.AssertSafe(isDevelopment: true, "1", CertConn);   // must not throw
        }

        // ---- the normal cases must stay completely untouched ----
        //
        // Only the DANGEROUS combination fails. An ordinary development or production host pointed at an
        // ordinary database is unaffected, flag or no flag — otherwise this gate would break every
        // deployment it was meant to protect.
        [Theory]
        [InlineData(true, "1", DevConn)]
        [InlineData(true, null, DevConn)]
        [InlineData(false, null, ProdConn)]
        [InlineData(false, "1", ProdConn)]
        [InlineData(true, "1", null)]
        [InlineData(false, null, "")]
        public void An_ordinary_database_is_never_refused(bool isDev, string? flag, string? conn)
        {
            Assert.Null(CertificationDataGate.RefusalReason(isDev, flag, conn));
            CertificationDataGate.AssertSafe(isDev, flag, conn);
        }

        // Database names are case-insensitive in SQL Server, so a differently cased name reaches the same
        // database and must reach the same decision — otherwise the gate is trivially bypassable.
        [Theory]
        [InlineData("Server=x;Database=CROSSBUYCERT;")]
        [InlineData("Server=x;Database=crossbuycert;")]
        [InlineData("Server=x;Database=CrossBuyCert_Backup;")]
        public void Certification_database_detection_is_case_insensitive(string conn)
        {
            Assert.True(CertificationDataGate.TargetsCertificationDatabase(conn));
            Assert.Throws<InvalidOperationException>(
                () => CertificationDataGate.AssertSafe(isDevelopment: false, "1", conn));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Server=x;Database=CrossBuyDev;")]
        [InlineData("Server=x;Database=CrossBuyDB2;")]
        public void An_ordinary_connection_string_is_not_detected_as_certification(string? conn)
            => Assert.False(CertificationDataGate.TargetsCertificationDatabase(conn));

        // ---- D12: nothing request-controlled can activate it ----
        //
        // Structural, and the strongest form the claim can take: the gate's inputs are an environment
        // flag and a connection string. It has no parameter that could carry a header, a query value or
        // a body, so there is no request-shaped surface to attack.
        [Fact]
        public void D12_the_gate_takes_no_request_controlled_input()
        {
            foreach (var m in typeof(CertificationDataGate).GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                foreach (var p in m.GetParameters())
                {
                    var n = p.ParameterType.FullName ?? "";
                    foreach (var banned in new[] { "HttpContext", "HttpRequest", "IHeaderDictionary", "IQueryCollection", "IFormCollection" })
                        Assert.DoesNotContain(banned, n, StringComparison.Ordinal);
                }
            }
        }

        // The refusal must never carry the connection string — it holds credentials.
        [Fact]
        public void The_refusal_message_never_contains_the_connection_string()
        {
            var reason = CertificationDataGate.RefusalReason(
                isDevelopment: false, "1", "Server=s;Database=CrossBuyCert;User Id=sa;Password=hunter2;");

            Assert.NotNull(reason);
            Assert.DoesNotContain("Password", reason!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hunter2", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("User Id", reason, StringComparison.OrdinalIgnoreCase);
        }

        // D11 — the two determinism gates share one flag deliberately: one switch for "this host runs the
        // certification harness" is harder to half-enable than two independent ones.
        [Fact]
        public void D11_the_data_gate_and_the_clock_gate_share_one_flag_and_one_rule()
        {
            Assert.Equal(ConformanceClock.EnableEnvironmentVariable, CertificationDataGate.EnableEnvironmentVariable);

            foreach (var (isDev, flag) in new[] { (true, "1"), (true, "0"), (true, (string?)null), (false, "1"), (false, (string?)null) })
                Assert.Equal(
                    ConformanceClock.ShouldEnable(isDev, flag),
                    CertificationDataGate.ShouldAllowCertificationDatabase(isDev, flag));
        }
    }
}
