using System.Linq;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// AN EXPORT MUST NOT CARRY ITS OWN COPY OF THE LIST QUERY.
	//
	// THE BUG THIS PINS. Every list screen in InventoryController has at least two readers of the same
	// filter: the rows (XData) and the Excel file (XExport). Five exports built their own query instead
	// of calling the screen's, and all five had already drifted in the SAME direction: XData searched
	//
	//     PartyName  ||  PartyNameEn
	//
	// and the export's copy searched PartyName only - it did not even project the English column. So
	// searching the Purchase Orders screen for a vendor by its ENGLISH name listed the matching rows on
	// screen and downloaded a file WITHOUT them. A silent subset is worse than an error: the file looks
	// authoritative and nobody re-counts it. StockMovementsExport had drifted the same way earlier (no
	// bin-location join, one fewer search field) and was fixed by this same extraction.
	//
	// WHY A SOURCE TEST AND NOT A BEHAVIOURAL ONE. Proving two endpoints return the same rows means
	// executing both against data, and this repository is under a no-new-databases rule - no probe, no
	// SQLite, no in-memory host. So this test proves the STRUCTURAL property that makes the drift
	// impossible rather than the row counts: both readers name ONE helper, and neither builds a query.
	// That is weaker than a row-count comparison and is not pretending otherwise. It is also the
	// property that actually failed here - the counts were only ever wrong BECAUSE there were two
	// queries. When a database is available again, the behavioural check is: call XData and XExport
	// with the same q and compare the X-Total header against the row count in the workbook.
	//
	// THIS TEST READS METHOD BODIES, NOT THE FILE. A whole-file grep for "PredicateBuilder" would pass
	// the moment any method in a 3000-line controller still used one, which is always - it would fail
	// OPEN and guard nothing. Each body is brace-matched from its own signature so an assertion can
	// only be satisfied by the method it names.
	// =================================================================================================
	public class InventoryExportSharesListQueryTests
	{
		// export action, the list action beside it, and the one helper both must call.
		public static TheoryData<string, string, string> Pairs() => new()
		{
			{ "PurchaseOrdersExport", "PurchaseOrdersData", "PurchaseOrdersFiltered" },
			{ "GoodsReceiptsExport",  "GoodsReceiptsData",  "GoodsReceiptsFiltered"  },
			{ "SalesOrdersExport",    "SalesOrdersData",    "SalesOrdersFiltered"    },
			{ "QuotationsExport",     "QuotationsData",     "QuotationsFiltered"     },
			{ "DeliveriesExport",     "DeliveriesData",     "DeliveriesFiltered"     },
			{ "StockMovementsExport", "StockMovementsData", "MovementsFiltered"      },
		};

		[Theory]
		[MemberData(nameof(Pairs))]
		public void The_screen_and_the_file_read_one_query(string export, string data, string filter)
		{
			var src = SourceText("CrossBuy", "Controllers", "InventoryController.cs");

			var exportBody = MethodBody(src, export);
			var dataBody = MethodBody(src, data);

			Assert.Contains(filter + "(", exportBody);
			Assert.Contains(filter + "(", dataBody);

			// The export must not rebuild what the helper already decided. A second query here is how
			// the drift got in, so the absence is the guarantee - not the presence of the call above.
			Assert.DoesNotContain("PredicateBuilder", exportBody);
			Assert.DoesNotContain("SearchTerms.Parse", exportBody);
			Assert.DoesNotContain("_context.", exportBody);
		}

		[Theory]
		[InlineData("PurchaseOrdersFiltered")]
		[InlineData("GoodsReceiptsFiltered")]
		[InlineData("SalesOrdersFiltered")]
		[InlineData("QuotationsFiltered")]
		[InlineData("DeliveriesFiltered")]
		public void The_shared_filter_searches_both_languages(string filter)
		{
			// The specific field whose loss produced the wrong file. A vendor or customer carries an
			// Arabic name and an optional English one; a search that reads only the Arabic column finds
			// nothing for an English term, and with the two queries merged that failure would now hit
			// the SCREEN too - which is exactly why it has to be asserted on the surviving query.
			var body = MethodBody(SourceText("CrossBuy", "Controllers", "InventoryController.cs"), filter);

			Assert.Contains("PartyNameEn = ", body);                 // projected
			Assert.Contains("r.PartyNameEn.Contains(s)", body);      // and searched
		}

		[Fact]
		public void The_exported_party_cell_falls_back_when_one_language_is_blank()
		{
			// DisplayName.Of, not a bare ternary: NameEn is nullable and the import writes "" as often
			// as NULL, so `isAr ? Name : NameEn` prints a BLANK vendor into the file. The screen's own
			// partial still uses the ternary; the file is held to the documented rule.
			var src = SourceText("CrossBuy", "Controllers", "InventoryController.cs");

			foreach (var export in new[] { "PurchaseOrdersExport", "GoodsReceiptsExport", "SalesOrdersExport", "QuotationsExport", "DeliveriesExport" })
				Assert.Contains("DisplayName.Of(", MethodBody(src, export));
		}

		// ---- helpers -------------------------------------------------------------------------------

		/// <summary>The brace-matched body of the one method with this name, signature excluded.</summary>
		private static string MethodBody(string source, string methodName)
		{
			// The DECLARATION, not a call site: a bare " Name(" also matches "= Name(q, status);", and
			// after this refactor every helper is called as well as declared.
			var declarations = System.Text.RegularExpressions.Regex.Matches(source,
				@"^[ \t]*(?:\[[^\]]+\][ \t]*)*(?:private|public|protected|internal)[^\n(]*?\b"
				+ System.Text.RegularExpressions.Regex.Escape(methodName) + @"[ \t]*\(",
				System.Text.RegularExpressions.RegexOptions.Multiline);

			Assert.True(declarations.Count > 0, "no method named " + methodName + " in InventoryController");
			Assert.True(declarations.Count == 1,
				methodName + " is declared " + declarations.Count + " times; this test would assert against the wrong body");

			var open = source.IndexOf('{', declarations[0].Index);
			Assert.True(open > 0, methodName + " has no body");

			var depth = 0;
			for (var i = open; i < source.Length; i++)
			{
				if (source[i] == '{') depth++;
				else if (source[i] == '}' && --depth == 0) return source.Substring(open + 1, i - open - 1);
			}

			Assert.Fail(methodName + " has an unbalanced body");
			return "";
		}

		private static string SourceText(params string[] relativeParts)
		{
			var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
			while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root from " + System.AppContext.BaseDirectory);
			var path = System.IO.Path.Combine(new[] { dir!.FullName }.Concat(relativeParts).ToArray());
			Assert.True(System.IO.File.Exists(path), "source file not found: " + path);
			return System.IO.File.ReadAllText(path);
		}
	}
}
