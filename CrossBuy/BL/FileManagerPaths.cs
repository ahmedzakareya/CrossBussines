using System;
using System.IO;

namespace CrossBuy.BL
{
	// =================================================================================================
	// FileManager physical-path containment.
	//
	// WHY THIS EXISTS
	//
	// FileManagerController.Download built a filesystem path by combining WebRootPath with the
	// StoredPath column and then served it, with no proof that the result stayed inside the document
	// library. Exploitation was never demonstrated — Upload writes GUID names, and all eleven stored
	// rows sit under /uploads/library/ — but the INVARIANT was absent from committed code, so the
	// safety of that endpoint rested on what the writer happens to do rather than on what the reader
	// refuses. This closes it at the reader.
	//
	// TWO ESCAPES THE OLD CODE DID NOT STOP
	//
	//   1. Traversal. "../../appsettings.Production.json" normalises out of the library entirely.
	//   2. A ROOTED StoredPath. Path.Combine DISCARDS its earlier arguments the moment a later one is
	//      rooted, so Path.Combine(webRoot, "C:\\Windows\\win.ini") IS "C:\Windows\win.ini". The old
	//      TrimStart('/', '\\') looked like it handled this and did not: it strips separators, not a
	//      drive qualifier. A rooted stored path is therefore refused outright rather than trimmed.
	//
	// SEPARATOR-SAFE BOUNDARY
	//
	// The allowed root is compared WITH a trailing separator. Without it, "…\uploads\library-evil\x"
	// passes a StartsWith test against "…\uploads\library" — a sibling directory whose name merely
	// begins with the root's name. Comparison is OrdinalIgnoreCase because this is a Windows
	// filesystem, where "…\Uploads\Library\x" and "…\uploads\library\x" are the same file.
	//
	// FAIL-CLOSED: every unexpected shape — null, empty, whitespace, rooted, traversing, or resolving
	// outside the library — returns false and yields no path. The caller answers NotFound, so nothing
	// here can leak a physical path into a response.
	// =================================================================================================
	public static class FileManagerPaths
	{
		/// <summary>The document library root, relative to the web root. Upload writes only here.</summary>
		public const string LibraryRelativeRoot = "uploads/library";

		/// <summary>
		/// Resolves a LibraryItem.StoredPath to a physical file path, but only if that path provably
		/// stays inside the document library. Returns false — with no path — for anything else.
		/// </summary>
		public static bool TryResolveLibraryFile(string? webRootPath, string? storedPath, out string physicalPath)
		{
			physicalPath = string.Empty;

			if (string.IsNullOrWhiteSpace(webRootPath) || string.IsNullOrWhiteSpace(storedPath))
			{
				return false;
			}

			// StoredPath is web-shaped and RELATIVE by contract. Refuse anything rooted before it ever
			// reaches Path.Combine, for the reason in the header.
			var trimmed = storedPath.Trim().TrimStart('/', '\\');
			if (trimmed.Length == 0 || Path.IsPathRooted(trimmed))
			{
				return false;
			}

			string root;
			string candidate;
			try
			{
				root = Path.GetFullPath(Path.Combine(webRootPath, LibraryRelativeRoot.Replace('/', Path.DirectorySeparatorChar)));
				candidate = Path.GetFullPath(Path.Combine(webRootPath, trimmed.Replace('/', Path.DirectorySeparatorChar)));
			}
			catch (Exception)
			{
				// GetFullPath throws on shapes such as invalid characters or an over-long path. An
				// unresolvable path is not a containment decision we can make, so refuse it.
				return false;
			}

			var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
				? root
				: root + Path.DirectorySeparatorChar;

			if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			physicalPath = candidate;
			return true;
		}
	}
}
