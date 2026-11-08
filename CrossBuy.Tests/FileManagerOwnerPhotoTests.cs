using System;
using System.IO;
using System.Linq;
using CrossBuy.BL;
using Xunit;

namespace CrossBuy.Tests
{
	// =============================================================================================
	// SP - THE FILE MANAGER SHOWS THE OWNER, NOT JUST THEIR NAME.
	//
	// The standing rule is that any screen naming a person shows their photograph. The chain for that
	// almost always breaks in the SAME PLACE: the DTO. A listing resolves an owner's name, the view
	// renders the name, and nobody notices that the photo was never carried across - because nothing
	// fails. A missing face is not an exception, it is just an absence, so only a test sees it.
	//
	// THIS SUITE IS STRUCTURAL ON PURPose and database-free by construction. Proving that a REAL photo
	// reaches a REAL row would need EmployeePhotos to query employees, which means a database, which the
	// standing policy forbids. So these assertions pin the CHAIN rather than the data: the DTO carries
	// the two fields, the controller fills them through the shared helper under a re-checked company,
	// and the view actually renders them. Each link is the one that silently went missing before.
	//
	// WHAT IT CANNOT TELL YOU, stated plainly so nobody reads more into a green run: it cannot prove a
	// photo is correct, that tenancy holds at runtime, or that the image resolves. It proves only that
	// no link of the chain is absent. A grep-shaped test fails OPEN, so treat it as a tripwire against
	// silent removal, never as evidence the feature works.
	// =============================================================================================
	public class FileManagerOwnerPhotoTests
	{
		// ==========================================================================================
		// The DTO link - where this chain has broken before
		// ==========================================================================================

		[Fact]
		public void LibItemDto_carries_the_owner_identity_and_the_photo()
		{
			// Reflection, not a constructed instance: the point is the SHAPE of the contract. If either
			// property is renamed or dropped the view silently renders undefined and no build fails.
			var properties = typeof(LibItemDto).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);

			Assert.True(properties.ContainsKey("ownerEmployeeId"),
				"LibItemDto must carry ownerEmployeeId - without it no photo can ever be resolved for a row.");
			Assert.True(properties.ContainsKey("ownerAvatarUrl"),
				"LibItemDto must carry ownerAvatarUrl - this is the field the owner cell renders.");
			Assert.Equal(typeof(int), properties["ownerEmployeeId"]);
			Assert.Equal(typeof(string), properties["ownerAvatarUrl"]);
		}

		// ==========================================================================================
		// The controller link - filled through the shared helper, under a re-checked company
		// ==========================================================================================

		[Fact]
		public void The_listing_fills_the_photo_through_the_shared_helper()
		{
			// EmployeePhotos owns the tenancy predicate and the stored-path normalisation. A local query
			// here would be a second producer of one fact, and the two would drift.
			var controller = Source("CrossBuy", "Controllers", "FileManagerController.cs");

			Assert.Contains("EmployeePhotos.ResolveAsync", controller, StringComparison.Ordinal);
			Assert.Contains("ownerAvatarUrl", controller, StringComparison.Ordinal);
		}

		[Fact]
		public void The_listing_rechecks_the_company_before_decorating_rows()
		{
			// MeAsync already agreed a company. Re-resolving the context without comparing it would let a
			// row be decorated with a face resolved under a different tenant.
			var controller = Compact(Source("CrossBuy", "Controllers", "FileManagerController.cs"));

			Assert.Contains("context.CompanyId!=me.Value.companyId", controller, StringComparison.Ordinal);
		}

		// ==========================================================================================
		// The view link - and the regression that would be invisible
		// ==========================================================================================

		[Fact]
		public void The_owner_cell_renders_the_person_and_not_the_bare_name()
		{
			// BOTH directions. The first assertion fails if the cell is reverted to the plain name; the
			// second fails if ownerHtml is called without the photo, which renders initials forever and
			// looks exactly like "this user has no photo". That second failure is the invisible one.
			// Compared with whitespace stripped. The exact spacing of a JS argument list is not the
			// invariant, and pinning it would make this fail the first time somebody reformats the view -
			// a guard that cries wolf gets deleted, and then the real regression ships.
			var view = Compact(Source("CrossBuy", "Views", "FileManager", "Index.cshtml"));

			Assert.DoesNotContain("'<td>'+esc(it.ownerName)+'</td>'", view, StringComparison.Ordinal);
			Assert.Contains("ownerHtml(it.ownerName,it.ownerAvatarUrl)", view, StringComparison.Ordinal);
		}

		[Fact]
		public void A_missing_photo_degrades_to_initials_never_a_broken_image()
		{
			// The ordinary case is no photo at all, so the fallback is the common path, not the edge. A
			// torn-image frame on every row would be worse than the glyph this replaced.
			var view = Source("CrossBuy", "Views", "FileManager", "Index.cshtml");

			Assert.Contains("function ownerHtml", view, StringComparison.Ordinal);
			Assert.Contains("fm-owner-fallback", view, StringComparison.Ordinal);
			Assert.Contains("onerror=", view, StringComparison.Ordinal);
		}

		// ==========================================================================================
		// harness - the house repo-root pattern, so an exact tree can be pointed at with an env var
		// ==========================================================================================

		private static string Compact(string s)
			=> new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

		private static string Source(params string[] relative)
			=> File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

		private static string RepoRoot()
		{
			var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
			if (!string.IsNullOrWhiteSpace(fromEnvironment)
				&& File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
			{
				return fromEnvironment;
			}

			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
			{
				dir = dir.Parent;
			}

			Assert.NotNull(dir);
			return dir!.FullName;
		}
	}
}
