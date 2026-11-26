using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;

namespace CrossBuy.BL
{
	// ============================================================================================
	// THE COMPANY'S NAME, for a screen that needs to say whose data it is showing.
	//
	// Four insight screens printed "Company 1" - the integer primary key. That is an internal
	// handle: it tells a reader nothing, and it reads identically on every installation, so the one
	// line on the page whose job was to say "these figures are yours" said nothing at all.
	//
	// IT IS A HELPER AND NOT THREE COPIES because the bilingual rule has to live somewhere, and
	// three controllers each carrying their own culture check is three places for it to drift. The
	// entity's Arabic column is spelled `ComoanyNameAr` in the schema; that typo is load-bearing
	// and is confined to this file rather than repeated at every call site.
	// ============================================================================================
	public static class CompanyDisplay
	{
		/// <summary>
		/// The company's name in the reader's language, falling back to the other language when one
		/// is missing, and to null when the company cannot be found. A caller that gets null is
		/// expected to show the id instead - saying something true beats showing a blank.
		/// </summary>
		public static async Task<string?> NameAsync(CrossDbContext db, int companyId,
			CancellationToken cancellationToken = default)
		{
			if (companyId <= 0) return null;

			var row = await db.Companies.AsNoTracking()
				.Where(c => c.CompanyID == companyId)
				.Select(c => new { c.CompanyName, c.ComoanyNameAr })
				.FirstOrDefaultAsync(cancellationToken);

			if (row == null) return null;

			bool isArabic = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName == "ar";

			var name = isArabic
				? (row.ComoanyNameAr ?? row.CompanyName)
				: (row.CompanyName ?? row.ComoanyNameAr);

			return string.IsNullOrWhiteSpace(name) ? null : name;
		}
	}
}
