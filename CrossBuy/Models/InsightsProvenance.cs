namespace CrossBuy.Models
{
	// ============================================================================================
	// WHERE AN INSIGHTS LIST CAME FROM — the strip that sits under the explanation on all four
	// insight screens (Accounting, CRM accounts, CRM opportunities, Inventory risk).
	//
	// IT WAS COPIED FOUR TIMES, identically, which is how the four of them came to render a raw
	// company ID as "Company 1". Fixing that in one view would have left three screens quietly
	// saying something different about the same facts, so the markup moved to one partial and the
	// shape it needs moved here.
	//
	// It carries no logic. Every value is something the calling screen already holds; the partial
	// decides only how to present it.
	// ============================================================================================
	public sealed class InsightsProvenance
	{
		/// <summary>The company the figures cover, by NAME. The screens used to print the integer
		/// primary key, which is an internal handle and tells a reader nothing.</summary>
		public string? CompanyName { get; init; }

		/// <summary>Only used when no name could be resolved, so the strip still says something
		/// true rather than going blank.</summary>
		public int CompanyId { get; init; }

		/// <summary>"Business rules", "Model", … — how the list was produced. Stated because these
		/// screens deliberately do NOT predict anything, and a reader is entitled to know that
		/// before acting on a row.</summary>
		public string Method { get; init; } = "";

		/// <summary>Whatever else this screen wants to state, in order. The four screens do NOT agree
		/// on these - two show counts, one shows comparison windows, one shows nothing - so they
		/// travel as label/value pairs instead of fixed fields. `Numeric` only decides presentation:
		/// a count is set in tabular figures and direction-isolated, a phrase is not.</summary>
		public IReadOnlyList<(string Label, string Value, bool Numeric)> Facts { get; init; }
			= new List<(string, string, bool)>();

		/// <summary>When the list was computed. UTC, and the partial is responsible for saying so —
		/// a zoneless stamp shown to a user is the bug this type exists to stop repeating.</summary>
		public DateTime? GeneratedAtUtc { get; init; }

		/// <summary>True when the strip is being drawn INSIDE an existing explanation notice, which
		/// two of the four screens have. It then renders only the facts, under a hairline, instead of
		/// carrying its own notice skin - so the screen shows one block saying what this is and where
		/// it came from, rather than two blocks in two visual languages.</summary>
		public bool Inline { get; init; }
	}
}
