using System.Text.Json;

namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // THE MANIFEST READER — step 1 of the read-only orphan detector (Batch 4C).
    //
    // It reads the append-only JSONL run manifests that SqlScratchManifest writes, and it does nothing
    // else: no SQL connection, no process inspection, no judgement about orphans. Those live in later
    // components precisely so this one can be exercised against a string.
    //
    // THE RULE THAT SHAPES EVERYTHING HERE: an unreadable manifest directory is NOT zero orphans. The
    // defect this whole programme exists to close was a system that reported a clean instance while 300
    // databases accumulated on it. A reader that returns an empty list when it cannot see would rebuild
    // that lie one layer up, so inaccessibility is a REPORTED STATE, never an empty result.
    //
    // WHY PER-LINE PARSING, AND WHY A TORN TAIL IS NOT CORRUPTION. The manifest format was chosen so a
    // process killed mid-write costs at most its final line. This reader honours that: a last line that
    // does not parse, in a file that does not end in a newline, is TruncatedTail - every earlier line is
    // kept and usable. A bad line ANYWHERE ELSE is Degraded, because something wrote over history rather
    // than stopping, and nothing derived from that file may be trusted at high confidence.
    // =================================================================================================

    /// <summary>How much of a manifest file can be believed.</summary>
    public enum ManifestIntegrity
    {
        /// <summary>Every line parsed and the sequence is contiguous.</summary>
        Intact,

        /// <summary>The final line is torn - expected when a run was killed mid-write. Earlier lines stand.</summary>
        TruncatedTail,

        /// <summary>A line other than the last is unusable, or the sequence has a gap. Treat derived facts as unreliable.</summary>
        Degraded,

        /// <summary>The file exists but could not be read at all.</summary>
        Unreadable,
    }

    /// <summary>One manifest line, already validated. Field names mirror the writer exactly.</summary>
    public sealed record ManifestEvent(
        int Version,
        DateTime TimestampUtc,
        string RunId,
        int Sequence,
        string Event,
        string? Role,
        string? Database,
        string? Family,
        string? Owner,
        string CleanupStatus,
        string Machine,
        int ProcessId,
        string? Error);

    /// <summary>One manifest file: its events, and how much of it survived.</summary>
    public sealed record ManifestFileResult(
        string Path,
        string? RunId,
        ManifestIntegrity Integrity,
        IReadOnlyList<ManifestEvent> Events,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Everything found in one manifest directory. <see cref="DirectoryAccessible"/> is the field that
    /// stops silence reading as safety: when it is false, NOTHING about orphans may be concluded.
    /// </summary>
    public sealed record ManifestSet(
        string Directory,
        bool DirectoryAccessible,
        string? AccessError,
        IReadOnlyList<ManifestFileResult> Files)
    {
        public static ManifestSet Inaccessible(string directory, string reason) =>
            new(directory, false, reason, Array.Empty<ManifestFileResult>());
    }

    public static class ScratchManifestReader
    {
        /// <summary>The newest schema version this reader understands. Anything higher is not guessed at.</summary>
        public const int SupportedSchemaVersion = SqlScratchManifest.SchemaVersion;

        public const string FilePattern = "run-*.jsonl";

        /// <summary>
        /// Mirrors the writer's directory resolution. DELIBERATE DUPLICATION, and the only one in this
        /// component: the writer's resolver is private and SqlServerFixture.cs is read-only to this batch.
        /// The environment variable name itself comes from the writer, so the two cannot drift on the part
        /// that actually names the location.
        /// </summary>
        public static string ResolveDirectory()
        {
            var custom = Environment.GetEnvironmentVariable(SqlScratchManifest.DirectoryVariable);
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();

            return Path.Combine(local, "CrossBuy", "sql-scratch-manifest");
        }

        /// <summary>Reads every manifest in a directory. A directory that cannot be listed is REPORTED, not skipped.</summary>
        public static ManifestSet ReadDirectory(string? directory = null)
        {
            var dir = directory ?? ResolveDirectory();

            string[] paths;
            try
            {
                if (!Directory.Exists(dir))
                    return ManifestSet.Inaccessible(dir, "the manifest directory does not exist");

                paths = Directory.GetFiles(dir, FilePattern);
            }
            catch (Exception ex)
            {
                return ManifestSet.Inaccessible(dir, ex.GetType().Name + ": " + ex.Message);
            }

            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            return new ManifestSet(dir, true, null, paths.Select(ReadFile).ToList());
        }

        /// <summary>
        /// Reads one manifest file. OPENED WITH FileShare.ReadWrite ON PURPOSE: a run that is still going
        /// holds the path between its own writes, and a detector that refused to read a live run's manifest
        /// would be blind exactly while the evidence mattered most. The file is never written here.
        /// </summary>
        public static ManifestFileResult ReadFile(string path)
        {
            string content;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                return new ManifestFileResult(path, null, ManifestIntegrity.Unreadable,
                    Array.Empty<ManifestEvent>(),
                    new[] { $"could not be read: {ex.GetType().Name}: {ex.Message}" });
            }

            return ParseContent(path, content);
        }

        /// <summary>
        /// The whole parser, over a string. Every synthetic test in the suite enters here, which is what
        /// makes the eighteen scenarios runnable with no filesystem and no SQL Server.
        /// </summary>
        public static ManifestFileResult ParseContent(string path, string content)
        {
            var warnings = new List<string>();
            var events = new List<ManifestEvent>();

            // A trailing newline means the last line was COMPLETED. Without it the final line may be torn,
            // and that is the one failure this format is designed to absorb.
            var endsComplete = content.Length == 0 || content.EndsWith("\n", StringComparison.Ordinal);

            var lines = content.Split('\n');
            var lastIndex = lines.Length - 1;
            while (lastIndex >= 0 && lines[lastIndex].Length == 0) lastIndex--;

            var truncatedTail = false;
            var degraded = false;

            for (var i = 0; i <= lastIndex; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0)
                {
                    degraded = true;
                    warnings.Add($"line {i + 1}: blank line inside the manifest");
                    continue;
                }

                if (TryParseLine(line, out var parsed, out var reason))
                {
                    events.Add(parsed!);
                    continue;
                }

                var isFinalLine = i == lastIndex && !endsComplete;
                if (isFinalLine)
                {
                    truncatedTail = true;
                    warnings.Add($"line {i + 1}: the final record is incomplete ({reason}) - consistent with a run " +
                                 "that was killed mid-write; earlier records are unaffected");
                }
                else
                {
                    degraded = true;
                    warnings.Add($"line {i + 1}: unusable record ({reason})");
                }
            }

            // SEQUENCE IS THE SECOND INTEGRITY SIGNAL. The writer numbers every event from 1 without gaps,
            // so a gap means a write FAILED - the run should also have reported [ManifestDegraded] at
            // teardown. Evidence with a hole in it is not evidence of absence.
            var sequences = events.Select(e => e.Sequence).OrderBy(x => x).ToList();
            for (var i = 0; i < sequences.Count; i++)
            {
                var expected = i + 1;
                if (sequences[i] == expected) continue;

                degraded = true;
                warnings.Add($"sequence gap: expected {expected} but the next recorded event is {sequences[i]} " +
                             "- at least one event was not persisted");
                break;
            }

            var runIds = events.Select(e => e.RunId).Distinct(StringComparer.Ordinal).ToList();
            if (runIds.Count > 1)
            {
                degraded = true;
                warnings.Add("more than one runId appears in a single manifest file: " + string.Join(", ", runIds));
            }

            var integrity = degraded ? ManifestIntegrity.Degraded
                          : truncatedTail ? ManifestIntegrity.TruncatedTail
                          : ManifestIntegrity.Intact;

            return new ManifestFileResult(path, runIds.FirstOrDefault(), integrity, events, warnings);
        }

        // Validation is explicit rather than reflective: a record missing any of these fields is not a
        // manifest record, whatever else it happens to contain.
        private static bool TryParseLine(string line, out ManifestEvent? result, out string reason)
        {
            result = null;

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException ex) { reason = "not valid JSON: " + ex.Message; return false; }

            if (root.ValueKind != JsonValueKind.Object) { reason = "not a JSON object"; return false; }

            if (!TryInt(root, "v", out var version)) { reason = "missing or non-numeric 'v'"; return false; }
            if (version > SupportedSchemaVersion)
            {
                reason = $"schema version {version} is newer than this reader understands ({SupportedSchemaVersion})";
                return false;
            }

            if (!TryInt(root, "seq", out var seq)) { reason = "missing or non-numeric 'seq'"; return false; }
            if (!TryInt(root, "pid", out var pid)) { reason = "missing or non-numeric 'pid'"; return false; }

            var @event = Str(root, "event");
            if (string.IsNullOrEmpty(@event)) { reason = "missing 'event'"; return false; }

            var runId = Str(root, "runId");
            if (string.IsNullOrEmpty(runId)) { reason = "missing 'runId'"; return false; }

            var machine = Str(root, "machine");
            if (string.IsNullOrEmpty(machine)) { reason = "missing 'machine'"; return false; }

            var rawTimestamp = Str(root, "ts");
            if (!DateTime.TryParse(rawTimestamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                reason = "missing or unparseable 'ts'";
                return false;
            }

            result = new ManifestEvent(
                version, timestamp, runId!, seq, @event!,
                Str(root, "role"), Str(root, "database"), Str(root, "family"), Str(root, "owner"),
                Str(root, "cleanupStatus") ?? "None", machine!, pid, Str(root, "error"));

            reason = "";
            return true;
        }

        private static bool TryInt(JsonElement element, string name, out int value)
        {
            value = 0;
            return element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out value);
        }

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
