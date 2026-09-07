using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// A ViewBag payload a runtime-compiled view can actually read.
	//
	// THE BUG THIS PINS. /Inventory/ItemLocations?warehouseId=3 died with
	//
	//     RuntimeBinderException: 'object' does not contain a definition for 'SectionId'
	//
	// while the controller demonstrably projected SectionId. The property existed; it was unreachable.
	// Views are compiled at runtime here (Program.cs: AddRazorRuntimeCompilation), so they live in their
	// own assembly; C# anonymous types are internal to the assembly that declares them; and this
	// repository grants no InternalsVisibleTo. The binder, running in the views assembly, could not see
	// one member of the row and named the nearest accessible type instead - object.
	//
	// SectionId only because it is the first member the loop touches. ItemCode, ItemName and ItemId
	// would have failed identically. The page looked healthy until a warehouse was chosen, because
	// ViewBag.Rows is populated only when warehouseId is supplied and an empty grid binds nothing.
	//
	// CrossBuy.Tests is a different assembly from CrossBuy, which is the same relationship the views
	// assembly has - so the dynamic reads below cross the real boundary rather than simulating it. What
	// they cannot exercise is Razor itself: they prove the payload is bindable, not that the page renders.
	// =================================================================================================
	public class ViewBagDynamicBindingTests
	{
		[Fact]
		public void An_anonymous_type_is_internal_which_is_the_whole_cause()
		{
			// Not incidental trivia - this is the property that broke the page, so it is stated as a fact
			// the suite checks rather than a claim in a comment. An anonymous type is never public, so one
			// handed to a runtime-compiled view is never readable by it.
			var anonymous = new { ItemId = 1, SectionId = (int?)2 }.GetType();

			Assert.False(anonymous.IsPublic);
			Assert.True(anonymous.IsNotPublic);
		}

		[Fact]
		public void The_grid_row_is_public_so_a_view_in_another_assembly_can_read_every_column()
		{
			var type = typeof(CrossBuy.Controllers.InventoryController.ItemLocationGridRow);

			Assert.True(type.IsNestedPublic || type.IsPublic,
				"ItemLocationGridRow must be publicly visible or the runtime-compiled view cannot bind it.");
			Assert.True(type.Assembly != typeof(ViewBagDynamicBindingTests).Assembly,
				"the type must come from CrossBuy.dll for this to test a cross-assembly read at all.");

			// Read exactly as ItemLocations.cshtml reads it: late-bound, from another assembly.
			dynamic row = new CrossBuy.Controllers.InventoryController.ItemLocationGridRow
			{
				ItemId = 7, ItemCode = "IT-7", ItemName = "Bolt", SectionId = 3, RackId = 9,
			};

			Assert.Equal(7, (int)row.ItemId);
			Assert.Equal("IT-7", (string)row.ItemCode);
			Assert.Equal("Bolt", (string)row.ItemName);
			Assert.Equal(3, (int?)row.SectionId);
			Assert.Equal(9, (int?)row.RackId);
		}

		[Fact]
		public void The_grid_row_carries_every_member_the_view_reads()
		{
			// Read off the view rather than restated here, so adding a column to ItemLocations.cshtml
			// without adding it to the DTO fails at build time instead of on the page.
			var view = SourceText("CrossBuy", "Views", "Inventory", "ItemLocations.cshtml");
			var needed = System.Text.RegularExpressions.Regex.Matches(view, @"(?<![A-Za-z0-9_])r\.([A-Za-z]\w*)")
				.Select(m => m.Groups[1].Value)
				// the page's JavaScript uses `r` for a DOM row; those are not Razor member reads
				.Where(n => n is not ("getAttribute" or "style" or "querySelector" or "push" or "Serialize"))
				.Distinct()
				.ToList();

			Assert.NotEmpty(needed);

			var type = typeof(CrossBuy.Controllers.InventoryController.ItemLocationGridRow);
			foreach (var member in needed)
				Assert.True(type.GetProperty(member) != null,
					$"ItemLocations.cshtml reads r.{member}, which ItemLocationGridRow does not expose.");
		}

		[Fact]
		public void The_item_locations_projection_no_longer_hands_the_view_an_anonymous_type()
		{
			// Source-pinned as well as behaviour-pinned: everything above would still pass if someone
			// reintroduced `new { ... }` at the call site, because none of it touches the controller.
			var code = SourceText("CrossBuy", "Controllers", "InventoryController.cs");
			int at = code.IndexOf("ViewBag.Rows =", StringComparison.Ordinal);
			Assert.True(at >= 0, "ViewBag.Rows assignment not found in InventoryController.");

			string projection = code.Substring(at, Math.Min(500, code.Length - at));

			Assert.Contains("new ItemLocationGridRow", projection, StringComparison.Ordinal);
			Assert.DoesNotContain("return new {", projection, StringComparison.Ordinal);
		}

		[Fact]
		public void The_post_contract_was_not_widened_to_carry_display_columns()
		{
			// ItemLocationRow is what SaveItemLocations deserializes from the form. Adding ItemCode and
			// ItemName to it would have been the shorter fix, and would have quietly changed a POST
			// contract to suit a grid.
			var post = typeof(CrossBuy.Controllers.InventoryController.ItemLocationRow);

			Assert.Null(post.GetProperty("ItemCode"));
			Assert.Null(post.GetProperty("ItemName"));
			Assert.NotNull(post.GetProperty("ItemId"));
			Assert.NotNull(post.GetProperty("SectionId"));
			Assert.NotNull(post.GetProperty("RackId"));
		}

		private static string SourceText(params string[] relativeParts)
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root from " + AppContext.BaseDirectory);
			var path = Path.Combine(new[] { dir!.FullName }.Concat(relativeParts).ToArray());
			Assert.True(File.Exists(path), "source file not found: " + path);
			return File.ReadAllText(path);
		}
	}
}
