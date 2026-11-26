using System.Text;

namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // THE REPORT — step 6. Text out, and nothing else.
    //
    // IT WRITES NO FILE. Render() returns a string and Write() takes a TextWriter that defaults to stdout.
    // There is no path, no default output location, and nothing here can touch SQL-Evidence-Summary.md.
    // That is a deliberate response to a defect already on the books: the guard suite regenerates a
    // TRACKED evidence file on every run, so a post-commit verification looks like landing drift. A
    // detector that did the same would add a second one.
    //
    // THE SENTENCE AT THE BOTTOM IS LOAD-BEARING. "NO CLEANUP WAS PERFORMED" is asserted by a test,
    // because a report about orphaned databases that does not say plainly that it removed none of them
    // will eventually be read as though it had.
    // =================================================================================================
    public static class ScratchOrphanReport
    {
        public const string NoCleanupStatement =
            "NO CLEANUP WAS PERFORMED. This detector is read-only: it issues one SELECT against sys.databases " +
            "and never CREATE, DROP, ALTER or RESTORE. No database was created, removed, renamed or modified.";

        /// <summary>
        /// Invariant, and with no zone marker. The custom specifier is applied with InvariantCulture
        /// because ':' in a custom format string is the CULTURE's time separator, not a literal colon.
        /// </summary>
        public const string ServerLocalFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>Says out loud which clock the preceding timestamp came from.</summary>
        public const string ServerLocalSuffix = "(server local)";

        public const string UnattributedCaveat =
            "Unattributed databases are NOT confirmed safe for deletion. They are databases nothing can currently " +
            "account for, which is the opposite of a clearance.";

        public static void Write(ScratchAssessment assessment, TextWriter? writer = null) =>
            (writer ?? Console.Out).Write(Render(assessment));

        public static string Render(ScratchAssessment assessment)
        {
            var text = new StringBuilder();

            text.AppendLine("SQL SCRATCH ORPHAN DETECTOR - READ-ONLY ASSESSMENT");
            text.AppendLine("==================================================");
            text.AppendLine();

            // ---- evidence availability comes FIRST, before any count ---------------------------------
            // A reader who sees "0 orphan candidates" without first seeing whether anything could be read
            // has been misled, so the limits of the evidence are stated before its conclusions.
            text.AppendLine("Evidence");
            text.AppendLine("--------");
            text.AppendLine($"  manifest directory : {assessment.ManifestDirectory}");

            if (assessment.ManifestDirectoryAccessible)
            {
                text.AppendLine("  manifest readable  : yes");
            }
            else
            {
                text.AppendLine("  manifest readable  : NO - " + (assessment.ManifestAccessError ?? "no reason recorded"));
                text.AppendLine("                       NOTHING below may be read as an absence of orphans.");
            }

            text.AppendLine(assessment.CatalogAvailable
                ? "  SQL catalog        : read"
                : "  SQL catalog        : NOT READ - " + (assessment.CatalogUnavailableReason ?? "no reason recorded"));
            text.AppendLine();

            // ---- counts -------------------------------------------------------------------------------
            text.AppendLine("Counts by classification");
            text.AppendLine("------------------------");
            foreach (var classification in Enum.GetValues<ScratchClassification>())
                text.AppendLine($"  {classification,-26} {assessment.CountOf(classification)}");

            var unattributed = assessment.CountOf(ScratchClassification.UnattributedScratch);
            text.AppendLine();
            text.AppendLine($"  Unattributed legacy databases: {unattributed}");
            text.AppendLine("  " + UnattributedCaveat);
            text.AppendLine();

            // ---- integrity ----------------------------------------------------------------------------
            text.AppendLine("Manifest integrity warnings");
            text.AppendLine("---------------------------");
            if (assessment.IntegrityWarnings.Count == 0)
            {
                text.AppendLine("  none");
            }
            else
            {
                foreach (var warning in assessment.IntegrityWarnings)
                    text.AppendLine("  * " + warning);
            }
            text.AppendLine();

            // ---- findings -----------------------------------------------------------------------------
            text.AppendLine("Findings");
            text.AppendLine("--------");
            if (assessment.Findings.Count == 0)
            {
                text.AppendLine("  no scratch databases were observed");
            }
            else
            {
                foreach (var finding in assessment.Findings)
                {
                    text.AppendLine($"  [{finding.Classification}] {finding.Database}");
                    text.AppendLine($"      evidence    : {finding.Evidence}   confidence: {finding.Confidence}");
                    text.AppendLine($"      run         : {finding.RunId ?? "(none)"}");
                    // TWO CLOCKS, RENDERED DIFFERENTLY ON PURPOSE.
                    //
                    // A manifest event timestamp IS a genuine UTC instant - the writer records
                    // DateTime.UtcNow - so "O" is correct and its trailing Z is a true statement.
                    //
                    // A catalog creation date is NOT. It is a wall-clock reading from the SQL Server host,
                    // whose time zone this process has not established, so it is printed with no zone
                    // marker and labelled for what it is. The previous "u" format appended Z to it
                    // unconditionally - for EVERY DateTimeKind, Unspecified included - which is why fixing
                    // the Kind alone would have left the report asserting UTC exactly as before.
                    text.AppendLine($"      last event  : {finding.LatestEvent ?? "(none)"}" +
                                    (finding.LatestEventUtc is { } at ? $" at {at:O}" : string.Empty));
                    text.AppendLine($"      catalog     : {(finding.PresentInCatalog ? "present" : "absent")}" +
                                    (finding.CreateDateServerLocal is { } created
                                        ? ", created " + created.ToString(ServerLocalFormat, System.Globalization.CultureInfo.InvariantCulture)
                                          + " " + ServerLocalSuffix
                                        : string.Empty) +
                                    (finding.CatalogState is { } state ? $", {state}" : string.Empty));

                    foreach (var limitation in finding.Limitations)
                        text.AppendLine("      limitation  : " + limitation);

                    text.AppendLine("      action      : " + finding.RecommendedAction);
                    text.AppendLine();
                }
            }

            text.AppendLine(NoCleanupStatement);
            text.AppendLine();

            return text.ToString();
        }
    }
}
