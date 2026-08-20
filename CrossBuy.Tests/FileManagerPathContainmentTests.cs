using System.IO;
using CrossBuy.BL;
using Xunit;

namespace CrossBuy.Tests
{
	// Pins the FileManager download containment invariant.
	//
	// These exist because of a real gap, not a hypothetical one: FileManagerController.Download built a
	// physical path from the StoredPath column and served it with no proof the result stayed inside the
	// document library. Nothing exploited it — Upload writes GUID names — so the endpoint's safety rested
	// on the writer's habits rather than the reader's refusal. Two escapes were reachable in principle
	// and are pinned closed here: traversal, and a ROOTED stored path, which the old
	// TrimStart('/', '\\') looked like it handled and did not, because Path.Combine discards its earlier
	// arguments the moment a later one is rooted.
	public class FileManagerPathContainmentTests
	{
		// An absolute root, so the tests assert containment rather than the test host's current directory.
		private static string WebRoot => Path.Combine(Path.GetTempPath(), "crossbuy-containment-tests", "wwwroot");

		private static string LibraryRoot =>
			Path.GetFullPath(Path.Combine(WebRoot, "uploads", "library"));

		[Fact]
		public void A_normal_stored_library_file_resolves_inside_the_library()
		{
			var ok = FileManagerPaths.TryResolveLibraryFile(
				WebRoot, "/uploads/library/1/9f2c1e7a4b8d43c2a1e05f6b7c8d9e01.pdf", out var physical);

			Assert.True(ok, "a GUID-named file written by Upload must remain downloadable");
			Assert.StartsWith(LibraryRoot + Path.DirectorySeparatorChar, physical);
			Assert.EndsWith("9f2c1e7a4b8d43c2a1e05f6b7c8d9e01.pdf", physical);
		}

		[Theory]
		// Traversal, in both separator flavours, including the shape that reaches real configuration.
		[InlineData("/uploads/library/1/../../../appsettings.Production.json")]
		[InlineData("/uploads/library/1/..\\..\\..\\appsettings.Production.json")]
		[InlineData("../../../../Windows/win.ini")]
		[InlineData("..")]
		// A rooted stored path. Path.Combine(webRoot, "C:\\Windows\\win.ini") IS "C:\Windows\win.ini",
		// so this is the escape that a Combine-based guard silently permits.
		[InlineData("C:\\Windows\\win.ini")]
		[InlineData("/C:/Windows/win.ini")]
		// A sibling directory whose name merely BEGINS with the root's name. This is what a StartsWith
		// test without a trailing separator would wave through.
		[InlineData("/uploads/library-evil/1/secret.pdf")]
		[InlineData("/uploads/librarysomething/secret.pdf")]
		// Outside the library but still inside the web root: containment is the library, not wwwroot.
		[InlineData("/uploads/comm/attachment.pdf")]
		// Malformed.
		[InlineData("")]
		[InlineData("   ")]
		[InlineData("/")]
		[InlineData("\\")]
		public void An_escaping_or_malformed_stored_path_is_refused_and_yields_no_path(string storedPath)
		{
			var ok = FileManagerPaths.TryResolveLibraryFile(WebRoot, storedPath, out var physical);

			Assert.False(ok, $"'{storedPath}' must not resolve to a servable path");
			// The caller answers NotFound from this, so a refusal must hand back nothing at all —
			// otherwise a physical path could reach a response body.
			Assert.Equal(string.Empty, physical);
		}

		[Fact]
		public void A_null_stored_path_is_refused()
		{
			Assert.False(FileManagerPaths.TryResolveLibraryFile(WebRoot, null, out var physical));
			Assert.Equal(string.Empty, physical);
		}

		[Fact]
		public void A_missing_web_root_is_refused_rather_than_guessed()
		{
			// WebRootPath is null when wwwroot is absent, which happens in some test hosts. Refusing is
			// the only safe answer: there is no root to contain anything against.
			Assert.False(FileManagerPaths.TryResolveLibraryFile(null, "/uploads/library/1/a.pdf", out var p1));
			Assert.Equal(string.Empty, p1);

			Assert.False(FileManagerPaths.TryResolveLibraryFile("", "/uploads/library/1/a.pdf", out var p2));
			Assert.Equal(string.Empty, p2);
		}

		[Fact]
		public void Casing_does_not_defeat_containment_on_windows()
		{
			// The filesystem is case-insensitive here, so a differently-cased library path is the same
			// directory and must still be allowed rather than refused as "outside".
			var ok = FileManagerPaths.TryResolveLibraryFile(
				WebRoot, "/UPLOADS/LIBRARY/1/file.pdf", out var physical);

			Assert.True(ok);
			Assert.StartsWith(LibraryRoot + Path.DirectorySeparatorChar, physical, System.StringComparison.OrdinalIgnoreCase);
		}
	}
}
