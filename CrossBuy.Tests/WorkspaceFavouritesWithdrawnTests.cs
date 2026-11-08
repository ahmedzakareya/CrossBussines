using System.IO;
using CrossBuy.BL.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// THE FAVOURITES SOURCE IS WITHDRAWN, AND THESE TWO FACTS ARE WHY IT STAYS WITHDRAWN.
	//
	// WHAT WAS WRONG: ReportFavoritesWorkspaceSource was registered as an IWorkspaceFavoritesSource and
	// awaited on EVERY dashboard load. It called IReportLibraryService.GetFavoritesAsync - a database
	// round trip on the first screen every user sees - and projected the rows onto
	// WorkspaceDashboard.Favorites, which NO VIEW RENDERS. Its rows also carried
	// Url = "/Workspace/Reports", the generic-list destination removed from every other Workspace row;
	// it was invisible only because nobody drew the panel.
	//
	// WHY IT SURVIVED THE EARLIER CLEAN-UP: when ReportLibraryWorkspaceReportSource was withdrawn for
	// being a second producer of report-favourite facts, this one was spared on the reasoning that it
	// was "a different seam feeding a different panel". The seam part was true. The panel part was not -
	// there is no such panel - so the duplicate-producer argument applied to it all along. Reporting
	// already contributes pinned reports through ReportingWorkspaceSource with working /Reports/Viewer
	// urls.
	//
	// THE SECOND FACT IS A COUPLING, NOT A BAN. The source and the card have to move together: a card
	// with no source draws nothing, and a source with no card is the defect above. So if you add a
	// Favourites card, this test will fail and tell you to register a source in the same change. That
	// failure is the point of it, not an obstacle to it.
	// =================================================================================================
	public class WorkspaceFavouritesWithdrawnTests
	{
		[Fact]
		public void The_workspace_registers_no_favourites_source_of_its_own()
		{
			var services = new ServiceCollection();
			services.AddCrossBusinessWorkspace();

			Assert.DoesNotContain(services,
				d => d.ServiceType == typeof(IWorkspaceFavoritesSource));
		}

		[Fact]
		public void No_workspace_view_renders_the_favourites_panel()
		{
			// The justification for the withdrawal, asserted rather than asserted-in-a-comment. If this
			// goes red somebody has drawn the card - good - and the same change must re-register a
			// source, or the card renders an empty panel for ever.
			var views = Directory.GetFiles(
				Path.Combine(RepoRoot(), "CrossBuy", "Views", "Workspace"), "*.cshtml");

			Assert.NotEmpty(views);   // a wrong scan root would otherwise pass this vacuously

			foreach (var view in views)
			{
				var text = File.ReadAllText(view);
				Assert.False(text.Contains("Model.Favorites", System.StringComparison.Ordinal),
					Path.GetFileName(view) + " renders Model.Favorites, but no IWorkspaceFavoritesSource "
					+ "is registered - register one in this same change or the panel is always empty. "
					+ "See the NOTE in WorkspaceRegistration.");
			}
		}

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
