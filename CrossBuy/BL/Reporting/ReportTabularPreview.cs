using System.Globalization;
using System.Text;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform — PREVIEWING A DATA EXPORT AS A GRID.
    //
    // A DATA EXPORT HAD NO HONEST PREVIEW, and the two halves of that failed differently:
    //
    //   XLSX  showed nothing at all. It is a zip, a browser cannot draw it, so the modal offered a
    //         description and a download button — which quietly breaks the product's own rule that no
    //         click downloads and the modal is what shows you the file.
    //   CSV   showed something worse than nothing: correct bytes in a misleading order. A CSV line is
    //         Arabic labels joined by ';', the semicolon is a bidi-NEUTRAL character, so the Unicode
    //         algorithm absorbs the separators and lays the adjacent Arabic fields out as ONE
    //         right-to-left run. Field 3 appears left of field 1 and the numeric tail clumps together.
    //         The file was right and the preview lied about which column was which.
    //
    // Both are the same mistake: the preview was built from the FILE FORMAT instead of from the DATA.
    // A grid answers both, because a cell is its own bidi context — there is no separator left for the
    // algorithm to absorb — and because a grid is drawable whatever the download is packed in.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY IT PARSES THE CSV RATHER THAN RE-RENDERING THE VIEW
    //
    // "The preview shows exactly what the download contains" has to be TRUE, not maintained. Rendering
    // the grid from the report view with a second copy of the cell rules would put two formatters in the
    // codebase, and the day one changes — a decimal place, a date pattern, the formula-injection guard —
    // the preview starts describing a file that no longer exists. Parsing the exporter's own output
    // makes the two identical BY CONSTRUCTION: this class cannot disagree with CsvReportExporter because
    // it has no opinion of its own.
    //
    // The XLSX preview shows the same grid. Both exporters are handed the same ReportView, so the values
    // are the values; only the packaging differs, and packaging is not what a preview is for.
    //
    // Self-contained by the same rule HtmlReportRenderer follows: inline <style>, no external
    // stylesheet, no font URL, no script. It is served into an iframe under `default-src 'none'`.
    // ============================================================================================
    public static class ReportTabularPreview
    {
        // The exporter writes a UTF-8 BOM for Excel's benefit; it is transport, not content.
        private const char Bom = '﻿';

        public static string Html(byte[] csvBytes, CultureInfo culture, bool arabic,
                                  string title, ReportOutputFormat downloadFormat)
        {
            var text = Encoding.UTF8.GetString(csvBytes).TrimStart(Bom);
            var separator = ListSeparator(culture);
            var rows = Parse(text, separator);

            var sb = new StringBuilder(16 * 1024);
            sb.Append("<!doctype html><html dir=\"").Append(arabic ? "rtl" : "ltr")
              .Append("\" lang=\"").Append(arabic ? "ar" : "en").Append("\"><head><meta charset=\"utf-8\">")
              .Append("<title>").Append(E(title)).Append("</title><style>");
            Css(sb, arabic);
            sb.Append("</style></head><body>");

            if (rows.Count == 0)
            {
                sb.Append("<p class=\"cbt-empty\">")
                  .Append(arabic ? "لا توجد بيانات في هذا التصدير." : "This export contains no data.")
                  .Append("</p>");
            }
            else
            {
                // A NOTE THAT NAMES THE FILE, because the grid is a view of a download the reader has not
                // taken yet, and a preview that does not say what it is previewing invites the assumption
                // that it IS the file.
                sb.Append("<p class=\"cbt-note\">")
                  .Append(arabic
                      ? "معاينة بيانات الملف — " + rows.Count.ToString("N0", culture) + " سطر · "
                      : "Data preview — " + rows.Count.ToString("N0", culture) + " lines · ")
                  .Append(downloadFormat == ReportOutputFormat.Xlsx ? "XLSX" : "CSV")
                  .Append("</p>");

                sb.Append("<table class=\"cbt\"><thead><tr><th class=\"cbt-n\"></th>");
                foreach (var cell in rows[0]) sb.Append("<th>").Append(Isolated(cell)).Append("</th>");
                sb.Append("</tr></thead><tbody>");

                var width = rows[0].Count;
                for (var r = 1; r < rows.Count; r++)
                {
                    // THE LINE NUMBER IS THE FILE'S, so a reader comparing this against the downloaded file
                    // is looking at the same row. Header is line 1, so the first body row is line 2.
                    sb.Append("<tr><td class=\"cbt-n\">").Append((r + 1).ToString(CultureInfo.InvariantCulture))
                      .Append("</td>");

                    for (var c = 0; c < width; c++)
                    {
                        var value = c < rows[r].Count ? rows[r][c] : "";

                        // A RAGGED LINE IS SHOWN, not hidden. Extra fields past the header width mean the
                        // file is malformed, and silently dropping them would make the preview claim a file
                        // is fine when the receiving program will choke on it.
                        sb.Append(IsNumeric(value) ? "<td class=\"cbt-num\">" : "<td>")
                          .Append(Isolated(value)).Append("</td>");
                    }
                    if (rows[r].Count > width)
                    {
                        sb.Append("<td class=\"cbt-extra\" colspan=\"")
                          .Append((rows[r].Count - width).ToString(CultureInfo.InvariantCulture)).Append("\">")
                          .Append(arabic ? "حقول زائدة" : "extra fields").Append("</td>");
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }

        // ---- RFC 4180, with the separator the exporter actually used --------------------------------
        //
        // Quotes are handled because the exporter writes them: any field containing the separator, a quote
        // or a newline is quoted, and an inner quote is doubled. A split on the separator would tear those
        // fields apart — which is exactly the class of bug that makes a preview disagree with the file.
        private static List<List<string>> Parse(string text, char separator)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var started = false;

            void EndField() { row.Add(field.ToString()); field.Clear(); }
            void EndRow()
            {
                EndField();
                // A trailing newline must not produce a phantom empty row.
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
                started = false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (quoted)
                {
                    if (ch != '"') { field.Append(ch); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; continue; }
                    quoted = false;
                    continue;
                }

                if (ch == '"' && !started) { quoted = true; started = true; continue; }
                if (ch == separator) { EndField(); started = false; continue; }
                if (ch == '\r') continue;
                if (ch == '\n') { EndRow(); continue; }

                field.Append(ch);
                started = true;
            }

            if (field.Length > 0 || row.Count > 0) EndRow();
            return rows;
        }

        private static char ListSeparator(CultureInfo culture)
        {
            var s = culture.TextInfo.ListSeparator;
            return string.IsNullOrEmpty(s) ? ',' : s[0];
        }

        // The exporter writes numbers INVARIANT and unquoted, so this recognises the same shape it wrote
        // rather than trying to parse in the UI culture — a comma-decimal culture would otherwise read
        // "1.5" as fifteen and right-align a value it had misread.
        private static bool IsNumeric(string value) =>
            value.Length > 0
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        // EVERY CELL IS AN ISOLATE. A grid already gives each value its own box, but a value that MIXES
        // scripts — "برايم تك 3" or a path with Latin codes in it — still reorders inside that box unless
        // it is isolated. <bdi> is exactly this: an element whose bidi context does not leak either way.
        private static string Isolated(string value) => "<bdi>" + E(value) + "</bdi>";

        private static void Css(StringBuilder sb, bool arabic)
        {
            sb.Append("*{box-sizing:border-box}")
              .Append("body{margin:0;padding:12px;background:#fff;color:#181c32;")
              .Append("font-family:").Append(ReportTypography.FamilyFor(arabic)).Append(";font-size:12px;}")
              .Append(".cbt-note{margin:0 0 10px;color:#7e8299;font-size:11px;}")
              .Append(".cbt-empty{color:#7e8299;}")
              .Append(".cbt{border-collapse:collapse;width:100%;}")
              .Append(".cbt th,.cbt td{border:1px solid #e4e6ef;padding:4px 8px;text-align:start;")
              .Append("vertical-align:top;white-space:nowrap;}")

              // STICKY HEADER, because a data export is read by scrolling and a column title that scrolls
              // away turns the grid back into the thing it replaced.
              .Append(".cbt thead th{position:sticky;top:0;background:#f9f9f9;font-weight:600;z-index:1;}")

              // Numbers read left-to-right in every locale, and tabular figures keep a column of digits
              // aligned on the same grid.
              .Append(".cbt td.cbt-num{direction:ltr;unicode-bidi:isolate;text-align:end;")
              .Append("font-variant-numeric:tabular-nums;}")

              // The line-number gutter is the reader's link back to the downloaded file.
              .Append(".cbt .cbt-n{direction:ltr;unicode-bidi:isolate;text-align:end;color:#a1a5b7;")
              .Append("background:#f9f9f9;font-variant-numeric:tabular-nums;width:1%;}")
              .Append(".cbt td.cbt-extra{color:#f1416c;}")
              .Append(".cbt tbody tr:nth-child(even) td{background:#fcfcfc;}");
        }

        private static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}
