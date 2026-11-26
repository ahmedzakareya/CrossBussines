using Microsoft.Data.SqlClient;

namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // THE CATALOG SNAPSHOT — step 3. What SQL Server actually has, and nothing more.
    //
    // THIS TYPE CONTAINS EXACTLY ONE SQL STATEMENT, and it is a SELECT. There is no CREATE, DROP, ALTER,
    // RESTORE, INSERT, UPDATE or DELETE anywhere in it, no retry that could mutate server state, and no
    // cleanup path of any kind. The statement is exposed as a public constant so a test can assert that
    // about it rather than a reviewer having to take it on trust - a detector for a defect caused by
    // uncontrolled DROPs has no business being able to issue one.
    //
    // IT IS ALSO OPTIONAL. Synthetic() builds a snapshot with no server at all, which is what lets the
    // classifier - the component that actually makes the decisions - be tested without SQL Server.
    //
    // UNAVAILABLE IS NOT EMPTY. A snapshot that could not be taken reports itself unavailable, because
    // "I could not look" and "there is nothing there" are the two answers this programme exists to stop
    // anyone confusing.
    // =================================================================================================

    /// <summary>
    /// One row of sys.databases, reduced to what classification needs.
    ///
    /// CreateDateServerLocal IS NOT UTC, AND ITS NAME SAYS SO. sys.databases.create_date is a wall-clock
    /// reading from the SQL Server HOST's own time zone, which this process has not established. It used
    /// to be labelled CreateDateUtc and stamped DateTimeKind.Utc - a relabelling, not a conversion - and
    /// on this UTC+3 host that made every creation date read three hours late while asserting it was UTC.
    /// The honest Kind for a wall-clock whose zone is unknown is Unspecified.
    /// </summary>
    public sealed record CatalogDatabase(string Name, DateTime? CreateDateServerLocal, string? StateDescription);

    public sealed class ScratchCatalogSnapshot
    {
        /// <summary>
        /// The ONLY statement this component issues. Ownership comes from SqlEvidenceGuards.OwnedScratchPrefixes,
        /// parameterised, with ESCAPE '\' because '_' is a single-character wildcard in T-SQL and every owned
        /// prefix ends in one.
        /// </summary>
        public const string CatalogQuery =
            "SELECT name, create_date, state_desc FROM sys.databases WHERE {0} ORDER BY name;";

        private readonly Dictionary<string, CatalogDatabase> _byName;

        private ScratchCatalogSnapshot(IEnumerable<CatalogDatabase> rows, bool available, string? unavailableReason)
        {
            _byName = rows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            Available = available;
            UnavailableReason = unavailableReason;
        }

        /// <summary>False when no read happened. When false, NOTHING may be concluded about presence or absence.</summary>
        public bool Available { get; }

        public string? UnavailableReason { get; }

        public IReadOnlyCollection<CatalogDatabase> Databases => _byName.Values;

        public bool Contains(string name) => _byName.ContainsKey(name);

        public CatalogDatabase? Find(string name) => _byName.TryGetValue(name, out var row) ? row : null;

        /// <summary>
        /// How a create_date becomes a value this process may hold. IT IS EXTRACTED SO IT CAN BE TESTED
        /// WITHOUT SQL SERVER - the read path needs a live instance, this does not, and the rule it carries
        /// is the entire subject of the correction.
        ///
        /// THE RULE: keep the wall-clock digits exactly as the server reported them, and mark the Kind
        /// Unspecified. There is deliberately no ToUniversalTime, no ToLocalTime, no TimeZoneInfo and no
        /// offset arithmetic, because this process does not know the SQL Server host's time zone and the
        /// test runner's zone is not evidence of it. Converting with the runner's offset would be right
        /// only by coincidence - here, where both happen to sit at UTC+3 - and silently wrong anywhere else.
        /// </summary>
        public static DateTime AsServerLocal(DateTime createDate) =>
            DateTime.SpecifyKind(createDate, DateTimeKind.Unspecified);

        /// <summary>A snapshot built in memory. No SQL Server, no connection string, no I/O.</summary>
        public static ScratchCatalogSnapshot Synthetic(params CatalogDatabase[] rows) =>
            new(rows, available: true, unavailableReason: null);

        /// <summary>A snapshot that could not be taken. Classification degrades rather than guesses.</summary>
        public static ScratchCatalogSnapshot Unavailable(string reason) =>
            new(Array.Empty<CatalogDatabase>(), available: false, unavailableReason: reason);

        /// <summary>
        /// Reads sys.databases, read-only. The connection is forced to the master catalog and a connection
        /// string pointing at a production database is REFUSED by the same guard the fixture uses, so a
        /// mistyped environment variable cannot even open a reader against real data.
        /// </summary>
        public static async Task<ScratchCatalogSnapshot> ReadAsync(
            string? connectionString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return Unavailable($"{SqlServerFixture.ConnectionStringVariable} is not set");

            var verdict = SqlEvidenceGuards.InspectCatalog(connectionString);
            if (verdict.Refused)
                return Unavailable($"{SqlServerFixture.ConnectionStringVariable}: {verdict.Reason}");

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };

                var prefixes = SqlEvidenceGuards.OwnedScratchPrefixes;
                var predicate = string.Join(" OR ",
                    prefixes.Select((_, i) => $"name LIKE @p{i} + '%' ESCAPE '\\'"));

                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = new SqlCommand(string.Format(CatalogQuery, predicate), connection)
                { CommandTimeout = 60 };

                for (var i = 0; i < prefixes.Length; i++)
                    command.Parameters.AddWithValue("@p" + i, SqlEvidenceGuards.EscapeLikePattern(prefixes[i]));

                var rows = new List<CatalogDatabase>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new CatalogDatabase(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : AsServerLocal(reader.GetDateTime(1)),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }

                return new ScratchCatalogSnapshot(rows, available: true, unavailableReason: null);
            }
            catch (Exception ex)
            {
                // NO RETRY. A failed read is reported as a failed read; retrying would be the first step
                // towards a component that does something to the server to make itself succeed.
                return Unavailable(ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
