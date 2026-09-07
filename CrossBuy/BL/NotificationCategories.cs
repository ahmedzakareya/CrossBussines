using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CrossBuy.BL
{
	/// <summary>
	/// The ONE place a notification category becomes a colour.
	///
	/// It was two places: /Notifications/Index declared a Dictionary in its own @{ } block and
	/// _NotificationBell.cshtml hardcoded bg-light + text-primary for every row. Both render the same
	/// rows out of the same table, so a reader saw one notification two different ways depending on
	/// whether they opened the bell or the page.
	///
	/// TWO MEASURED RULES GOVERN WHAT MAY APPEAR HERE. Both were established by rendering each
	/// Metronic state pair in the running app and computing the contrast from the COMPUTED colours,
	/// not from the token names:
	///
	///     state       icon on bg-light-{state}      badge text on badge-light-{state}
	///     primary            7.35  OK                        7.35  OK
	///     success            8.30  OK                        8.30  OK
	///     warning            6.37  OK                        6.37  OK
	///     danger             6.80  OK                        6.80  OK
	///     dark              15.29  OK                       15.29  OK
	///     info               4.93  OK                        3.01  FAIL  (needs 4.5, small text)
	///     secondary          1.07  FAIL (needs 3)           12.59  OK
	///
	/// So `secondary` MAY NOT be used - it was the previous fallback, and text-secondary #F1F1F4 on
	/// bg-light-secondary #F9F9F9 is a 1.07 icon, which is not "faint" but invisible. And `info` may
	/// not be used either, because the category badge beside the title is small text at 3.01.
	///
	/// That left five usable states for thirteen live categories, so some are shared. That is fine:
	/// the badge spells the category out in words, and the colour only has to group.
	///
	/// THE FALLBACK IS THE POINT. Three of the thirteen categories in the table - Tasks (227 rows,
	/// the second most common of all), Announcement (17) and Calendar (6) - were absent from the old
	/// map and therefore fell to `secondary`, which is how the most common category in the product
	/// came to render an invisible icon. A category added tomorrow now falls to `dark` at 15.29, so
	/// the failure mode of forgetting to update this file is a legible row, not a blank one.
	/// </summary>
	public static class NotificationCategories
	{
		/// The colour a category with no entry here takes. Deliberately the highest-contrast state
		/// available (15.29 on both surfaces): an unmapped category must stay READABLE, because
		/// forgetting to add one is the normal way this map goes out of date.
		public const string Fallback = "dark";

		private static readonly Dictionary<string, string> Map = new()
		{
			// The work itself. Tasks is the dominant category in the table, so it carries the brand.
			["Tasks"] = "primary",
			["Calendar"] = "primary",
			["HR"] = "primary",
			["CRM"] = "primary",

			// Money.
			["Sales"] = "success",
			["Accounting"] = "success",

			// Goods and broadcasts.
			["Inventory"] = "warning",
			["Announcement"] = "warning",

			// The only family that is meant to alarm.
			["Governance"] = "danger",

			// Everything else. Previously `info` (Purchasing, Chat) or `secondary` (General) - both
			// ruled out by the measurements above.
			["Purchasing"] = "dark",
			["Chat"] = "dark",
			["General"] = "dark",
		};

		/// The Metronic state name for a category: use it as bg-light-{x}, text-{x}, badge-light-{x}.
		public static string ColorOf(string? category) =>
			category != null && Map.TryGetValue(category, out var c) ? c : Fallback;

		/// The same map as JSON, for the bell dropdown - which builds its rows in the browser and so
		/// cannot call ColorOf per row. Serialising the real dictionary is what stops the two from
		/// drifting again; a second literal in the JavaScript is exactly the bug this class replaces.
		public static string MapJson() => JsonSerializer.Serialize(Map);
	}
}
