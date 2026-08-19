using System.Globalization;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — VALUE SEMANTICS.
    //
    // One place that decides what a text value MEANS, how two values COMPARE, and how a value is DISPLAYED.
    //
    // Why one place: parameters arrive as text from a query string, filters arrive as text from a stored
    // template, and a schedule's frozen parameters arrive as text from JSON. If each had its own parser,
    // "2026-01-31" would mean three subtly different things and a report would disagree with itself depending
    // on how it was launched. Everything funnels through TryConvert.
    // ============================================================================================
    public static class ReportValues
    {
        // Accepted date forms, tried in this order. ISO FIRST, deliberately: a stored template or a URL is
        // machine data and must not be reinterpreted when a user switches UI language. Only after the machine
        // forms fail do we try the caller's culture, which is what makes a hand-typed "31/01/2026" work.
        private static readonly string[] IsoDateFormats =
        {
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyyMMdd",
        };

        // ------------------------------------------------------------------------------------------------
        // Relative date tokens.
        //
        // These exist because of SCHEDULING. A daily sales report cannot store a literal date — it would email
        // the same day's figures forever. A cron-style engine solves this with expressions; we solve it with a
        // closed, translatable token set that is validated when the schedule is saved.
        //
        // Resolved against an injected clock (IReportClock), never DateTime.Now directly, so a test can assert
        // "month-end on 2026-02-10 is 2026-02-28" without waiting for February.
        // ------------------------------------------------------------------------------------------------
        public static readonly IReadOnlyList<string> DateTokens = new[]
        {
            "today", "yesterday", "tomorrow",
            "week-start", "week-end",
            "month-start", "month-end",
            "quarter-start", "quarter-end",
            "year-start", "year-end",
        };

        public static bool IsDateToken(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim().ToLowerInvariant();
            if (DateTokens.Contains(t)) return true;
            return TrySplitOffsetToken(t, out _, out _);
        }

        // "today+7", "month-start-1" → (baseToken, offsetDays). The offset is in DAYS for every token; a
        // month-relative offset would need a second unit and nobody has asked for one.
        private static bool TrySplitOffsetToken(string text, out string baseToken, out int offsetDays)
        {
            baseToken = text;
            offsetDays = 0;

            foreach (var token in DateTokens.OrderByDescending(t => t.Length))
            {
                if (!text.StartsWith(token, StringComparison.Ordinal)) continue;
                var rest = text[token.Length..];
                if (rest.Length == 0) { baseToken = token; return true; }
                if ((rest[0] == '+' || rest[0] == '-')
                    && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                {
                    baseToken = token;
                    offsetDays = offset;
                    return true;
                }
            }
            return false;
        }

        public static bool TryResolveDateToken(string? text, DateTime now, out DateTime resolved)
        {
            resolved = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!TrySplitOffsetToken(text.Trim().ToLowerInvariant(), out var token, out var offsetDays))
                return false;

            var today = now.Date;
            // Week starts Sunday — the working-week convention in the deployment region, and the same
            // convention DayOfWeek numbering uses (0 = Sunday), so the two never drift.
            var weekStart = today.AddDays(-(int)today.DayOfWeek);
            var quarterStartMonth = ((today.Month - 1) / 3) * 3 + 1;

            resolved = token switch
            {
                "today" => today,
                "yesterday" => today.AddDays(-1),
                "tomorrow" => today.AddDays(1),
                "week-start" => weekStart,
                "week-end" => weekStart.AddDays(6),
                "month-start" => new DateTime(today.Year, today.Month, 1),
                "month-end" => new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
                "quarter-start" => new DateTime(today.Year, quarterStartMonth, 1),
                "quarter-end" => new DateTime(today.Year, quarterStartMonth, 1).AddMonths(3).AddDays(-1),
                "year-start" => new DateTime(today.Year, 1, 1),
                "year-end" => new DateTime(today.Year, 12, 31),
                _ => today,
            };
            resolved = resolved.AddDays(offsetDays);
            return true;
        }

        // ------------------------------------------------------------------------------------------------
        // Text → typed value.
        //
        // Returns false rather than throwing: bad input is an expected condition that becomes a diagnostic on
        // the result, not an exception a controller has to catch.
        // ------------------------------------------------------------------------------------------------
        public static bool TryConvert(string? text, ReportFieldType type, CultureInfo culture, DateTime now,
            out object? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return true;   // empty = "no value", which is valid
            var s = text.Trim();

            switch (type)
            {
                case ReportFieldType.String:
                    value = s;
                    return true;

                case ReportFieldType.Integer:
                case ReportFieldType.EntityRef:
                    // An EntityRef is an entity id, so it converts as an integer. The entity CODE lives on the
                    // column/parameter; the value is just the key.
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    { value = i; return true; }
                    if (int.TryParse(s, NumberStyles.Integer, culture, out i))
                    { value = i; return true; }
                    return false;

                case ReportFieldType.Decimal:
                case ReportFieldType.Money:
                case ReportFieldType.Percent:
                    // Invariant first, then culture — a stored "1234.56" must not become 123456 for a culture
                    // that reads '.' as a group separator.
                    if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    { value = d; return true; }
                    if (decimal.TryParse(s, NumberStyles.Number, culture, out d))
                    { value = d; return true; }
                    return false;

                case ReportFieldType.Date:
                case ReportFieldType.DateTime:
                    if (TryResolveDateToken(s, now, out var token))
                    {
                        // A Date token normalises to midnight; a DateTime token keeps midnight too, because a
                        // token names a DAY and inventing a time-of-day would be a fabricated number.
                        value = token;
                        return true;
                    }
                    if (DateTime.TryParseExact(s, IsoDateFormats, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var dt))
                    { value = type == ReportFieldType.Date ? dt.Date : dt; return true; }
                    if (DateTime.TryParse(s, culture, DateTimeStyles.None, out dt))
                    { value = type == ReportFieldType.Date ? dt.Date : dt; return true; }
                    return false;

                case ReportFieldType.Boolean:
                    // Accepts the three notations that actually arrive: checkbox ("true"/"on"), numeric ("1"),
                    // and the word. Anything else is rejected rather than treated as false — a mistyped flag
                    // silently meaning "no" is how a report gets filtered to nothing.
                    switch (s.ToLowerInvariant())
                    {
                        case "true" or "1" or "yes" or "on" or "y": value = true; return true;
                        case "false" or "0" or "no" or "off" or "n": value = false; return true;
                        default: return false;
                    }

                default:
                    value = s;
                    return true;
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Comparison — used by sorting, grouping and range filters.
        //
        // Nulls sort FIRST ascending (and therefore last descending). Stated because the alternative convention
        // is equally defensible and a silent change would reorder every report.
        // ------------------------------------------------------------------------------------------------
        public static int Compare(object? a, object? b, ReportFieldType type, CultureInfo culture)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            switch (type)
            {
                case ReportFieldType.Integer:
                case ReportFieldType.EntityRef:
                case ReportFieldType.Decimal:
                case ReportFieldType.Money:
                case ReportFieldType.Percent:
                    return AsDecimal(a).CompareTo(AsDecimal(b));

                case ReportFieldType.Date:
                case ReportFieldType.DateTime:
                    return AsDateTime(a).CompareTo(AsDateTime(b));

                case ReportFieldType.Boolean:
                    return AsBool(a).CompareTo(AsBool(b));

                default:
                    // Culture-aware string comparison: Arabic text ordered by ordinal byte value is not
                    // alphabetical, and a report ordered by codepoint reads as unordered to its user.
                    return string.Compare(AsString(a, culture), AsString(b, culture), culture,
                        CompareOptions.StringSort);
            }
        }

        // Group-key equality. Distinct from Compare only in that it never needs an ordering, but it must agree
        // with it — two values that Compare as 0 must land in the same group, or subtotals would not add up.
        public static bool AreEqual(object? a, object? b, ReportFieldType type, CultureInfo culture) =>
            Compare(a, b, type, culture) == 0;

        public static decimal AsDecimal(object? v) => v switch
        {
            null => 0m,
            decimal d => d,
            int i => i,
            long l => l,
            short s => s,
            double db => (decimal)db,
            float f => (decimal)f,
            bool b => b ? 1m : 0m,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
            _ => 0m,
        };

        public static DateTime AsDateTime(object? v) => v switch
        {
            null => DateTime.MinValue,
            DateTime dt => dt,
            DateTimeOffset dto => dto.LocalDateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
            _ => DateTime.MinValue,
        };

        public static bool AsBool(object? v) => v switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            decimal d => d != 0m,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
            _ => false,
        };

        public static string AsString(object? v, CultureInfo culture) => v switch
        {
            null => "",
            string s => s,
            IFormattable f => f.ToString(null, culture),
            _ => v.ToString() ?? "",
        };

        // ------------------------------------------------------------------------------------------------
        // Display formatting.
        //
        // RULE (ADR-037 §Numbers): formatting NEVER changes a value. There is no rounding here — a Money column
        // is formatted to the scale the column declares, and the underlying decimal is whatever the data source
        // computed. Rounding is a business decision owned by ICurrencyRounding at the point of computation; a
        // report that re-rounded would print totals that disagree with the ledger it came from.
        // ------------------------------------------------------------------------------------------------
        public static string Format(object? value, ReportColumn column, CultureInfo culture)
        {
            if (value == null) return "";

            var format = column.Format;
            switch (column.Type)
            {
                case ReportFieldType.Money:
                    return AsDecimal(value).ToString(format ?? "N2", culture);

                case ReportFieldType.Decimal:
                    return AsDecimal(value).ToString(format ?? "N2", culture);

                case ReportFieldType.Percent:
                    // The value is already a percentage (12.5 = 12.5%), so it is formatted as a number with a
                    // sign appended — NOT with "P", which would multiply by 100 and print 1250%.
                    return AsDecimal(value).ToString(format ?? "N1", culture) + "%";

                case ReportFieldType.Integer:
                    return AsDecimal(value).ToString(format ?? "N0", culture);

                case ReportFieldType.Date:
                    return AsDateTime(value).ToString(format ?? "yyyy-MM-dd", culture);

                case ReportFieldType.DateTime:
                    return AsDateTime(value).ToString(format ?? "yyyy-MM-dd HH:mm", culture);

                case ReportFieldType.Boolean:
                    // Bilingual by culture, matching how the rest of the product renders a flag.
                    var isArabic = culture.TwoLetterISOLanguageName == "ar";
                    return AsBool(value) ? (isArabic ? "نعم" : "Yes") : (isArabic ? "لا" : "No");

                default:
                    return AsString(value, culture);
            }
        }

        // The value written into a data export (XLSX/CSV), which is NOT the display string.
        //
        // An export must carry the machine value so the receiving tool can sum a column and sort a date. A
        // "1,234.56" string in a spreadsheet cell is text, and a user who sums it gets zero — this is the single
        // most common reporting export defect, so the two paths are separated here by design.
        public static object? ForExport(object? value, ReportColumn column) => column.Type switch
        {
            ReportFieldType.Money or ReportFieldType.Decimal or ReportFieldType.Percent => AsDecimal(value),
            ReportFieldType.Integer or ReportFieldType.EntityRef => (int)AsDecimal(value),
            ReportFieldType.Date or ReportFieldType.DateTime => value == null ? null : AsDateTime(value),
            ReportFieldType.Boolean => AsBool(value),
            _ => value,
        };
    }

    // The clock the reporting platform reads. A seam, not a convenience: relative date tokens, schedule
    // next-run calculation and run timing all depend on "now", and every one of them needs to be assertable in a
    // test without waiting for the calendar.
    public interface IReportClock
    {
        // Server-local wall clock. Reporting periods are local business dates ("January's sales"), never UTC
        // instants, so the platform reads local time and each schedule carries its own TimeZoneId for firing.
        DateTime LocalNow { get; }
        DateTime UtcNow { get; }
    }

    public sealed class SystemReportClock : IReportClock
    {
        public DateTime LocalNow => DateTime.Now;
        public DateTime UtcNow => DateTime.UtcNow;
    }

    // Fixed clock for tests and for a deterministic replay of an archived report.
    public sealed class FixedReportClock : IReportClock
    {
        public FixedReportClock(DateTime localNow) { LocalNow = localNow; }
        public DateTime LocalNow { get; }
        public DateTime UtcNow => LocalNow.ToUniversalTime();
    }
}