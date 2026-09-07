using System.Globalization;
using System.Linq.Expressions;
using CrossBuy.Models.Context.Admin;

namespace CrossBuy.BL
{
    // ============================================================================================
    // ONE PLACE THAT DECIDES WHICH OF AN EMPLOYEE'S TWO NAMES TO SHOW.
    //
    // Employee carries FullName (Arabic, required) and FullNameEn (English, optional). Before this
    // class there were 66 places across 40 files that rendered FullName alone, so an English screen
    // showed "احمد زكريا" in the run history, the leave-request list, the assignee columns, the
    // employee pickers and the header. Every one of them had the same three-line rule written out,
    // or - far more often - not written out at all.
    //
    // THE RULE, and it is the whole class:
    //
    //   Arabic UI          -> FullName.
    //   English UI         -> FullNameEn when there is one, otherwise FullName.
    //
    // The fallback matters: FullNameEn is nullable, so a company that has not filled it in gets the
    // Arabic name rather than a blank cell. A NAME IS ALWAYS BETTER THAN NOTHING - a blank "assigned
    // to" column is a worse bug than one in the wrong language.
    //
    // WHAT THIS CLASS DELIBERATELY DOES NOT DO:
    //
    //   * It never touches an EDIT FIELD. A form that edits the employee must round-trip the STORED
    //     value, so `value="@Model.FullName"` on the Arabic name input stays exactly as it is.
    //     Resolving there would write the English name into the Arabic column on the next save -
    //     the same defect this session fixed in the Report Studio.
    //   * It never renames anything. Assigning to FullName is a write and is left alone.
    // ============================================================================================
    public static class EmployeeNames
    {
        // The UI language, not the thread's formatting culture: this decides what a person READS.
        public static bool Arabic =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("ar", StringComparison.OrdinalIgnoreCase);

        // IN MEMORY. For rows already materialised, and for the many call sites that fetch both
        // columns into an anonymous type or a dictionary.
        public static string Of(string? full, string? fullEn)
        {
            if (!Arabic && !string.IsNullOrWhiteSpace(fullEn)) return fullEn!;
            return full ?? "";
        }

        // The same, with the identifier as a last resort. Several lists already fell back to "#12"
        // when a name was missing, and that behaviour is preserved rather than replaced with a blank.
        public static string Of(string? full, string? fullEn, int id)
        {
            var name = Of(full, fullEn);
            return string.IsNullOrWhiteSpace(name) ? "#" + id.ToString(CultureInfo.InvariantCulture) : name;
        }

        public static string Of(Employee? employee) =>
            employee is null ? "" : Of(employee.FullName, employee.FullNameEn);

        // FOR STORED BILINGUAL TEXT, and only that. A journal entry carries Description and
        // DescriptionEn, both written once and read by everyone afterwards, so the name inside the
        // ENGLISH one has to be the English name regardless of who pressed the button. Using Of()
        // there would make the stored text depend on the operator's cookie: the same action would
        // produce "Final settlement — احمد زكريا" for one clerk and "Final settlement — ahmed
        // zakareya" for the next.
        //
        // This is also why a DENORMALISED SNAPSHOT column - LeaveEncashment.EmployeeName,
        // FinalSettlement.EmployeeName - keeps storing FullName untouched. It is the record of who
        // the payment was for, not a label on a screen, and it must not vary by UI language.
        public static string EnglishOf(string? full, string? fullEn) =>
            string.IsNullOrWhiteSpace(fullEn) ? (full ?? "") : fullEn!;

        // IN THE DATABASE. Returned as an expression rather than a method so EF Core can translate it
        // into a CASE and the choice happens server-side - which is what makes it usable for ORDER BY
        // as well as SELECT.
        //
        // Built PER CALL, on purpose: the culture is captured when the expression is created, so a
        // cached static instance would freeze whichever language happened to make the first request.
        public static Expression<Func<Employee, string>> Display()
        {
            if (Arabic) return e => e.FullName;
            return e => e.FullNameEn != null && e.FullNameEn != "" ? e.FullNameEn : e.FullName;
        }

        // ORDER BY THE NAME THE READER SEES. A list sorted on the Arabic name while displaying the
        // English one looks unsorted, which is how a "language" bug turns into "this screen is broken".
        public static IOrderedQueryable<Employee> OrderByDisplayName(this IQueryable<Employee> query) =>
            query.OrderBy(Display());

        // SEARCH BOTH NAMES. Typing an English name found nothing when only FullName was matched.
        public static IQueryable<Employee> WhereNameContains(this IQueryable<Employee> query, string term) =>
            query.Where(e =>
                (e.FullName != null && e.FullName.Contains(term)) ||
                (e.FullNameEn != null && e.FullNameEn.Contains(term)));
    }
}
