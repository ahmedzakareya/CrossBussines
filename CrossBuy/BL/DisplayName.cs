using System.Globalization;

namespace CrossBuy.BL
{
    // ============================================================================================
    // THE FALLBACK FOR EVERY BILINGUAL COLUMN PAIR THAT IS NOT AN EMPLOYEE NAME.
    //
    // Master data in this schema is stored as a pair: an Arabic column that is required and an
    // English one that is not. Item.Name/NameEn, ItemCategory.Name/NameEn, Warehouse.Name/NameEn,
    // Account.Name/NameEn, ReportDescriptor.TitleAr/TitleEn, and so on.
    //
    // Screens picked between them by hand:
    //
    //     @(isAr ? account.Name : account.NameEn)
    //
    // and that ternary is wrong in one specific, invisible way: NameEn is NULLABLE. While Arabic was
    // the default culture almost nobody took the English branch, so a row whose English name had
    // never been filled in rendered fine. Making English the default turns every one of those rows
    // into a BLANK CELL - a blank account in the journal picker, a blank warehouse in the transfer
    // form, a blank unit on the item form. A missing name reads as a broken screen, and it is a
    // worse bug than a name in the wrong language.
    //
    // Or() takes the preferred value and the one to fall back to, and is deliberately blank-aware
    // rather than just null-aware: these columns are populated by an import that writes "" as often
    // as it writes NULL, and `??` does not catch "".
    //
    // WHY THIS DOES NOT DECIDE THE LANGUAGE ITSELF:
    //
    //   The call sites keep their own condition. Most read the UI culture, but some do NOT -
    //   ReportEngine renders a report in the language the REPORT was requested in, which is a
    //   parameter and not the viewer's cookie. Folding the culture check in here would silently
    //   retarget those. Or() adds the fallback and changes nothing else.
    //
    // For an employee name use EmployeeNames - it carries the same rule plus the EF-translatable
    // Display() expression that ORDER BY and search need.
    // ============================================================================================
    public static class DisplayName
    {
        /// <summary>
        /// The UI language, not the thread's formatting culture: this decides what a person READS.
        /// Read it into a LOCAL before building an EF query — a ternary over a captured bool
        /// translates to a CASE and the choice happens server-side, which is what ORDER BY needs.
        /// Never cache the local in a static: the culture would freeze on the first request.
        /// </summary>
        public static bool IsArabic =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("ar", StringComparison.OrdinalIgnoreCase);

        /// <summary>The preferred name, or the other one when it was never filled in.</summary>
        public static string Or(string? preferred, string? fallback) =>
            !string.IsNullOrWhiteSpace(preferred) ? preferred! : (fallback ?? "");

        /// <summary>Arabic UI reads the Arabic column; English UI reads English, falling back.</summary>
        public static string Of(string? arabic, string? english) =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("ar", StringComparison.OrdinalIgnoreCase)
                ? Or(arabic, english)
                : Or(english, arabic);
    }
}
