using Xunit;

namespace CrossBuy.Tests;

public sealed class TimelineCulturePropagationTests
{
    [Fact]
    public void Shared_activity_timeline_passes_page_cultures_to_its_endpoint()
    {
        var source = Read("CrossBuy/Views/Shared/_DocEventTimeline.cshtml");

        Assert.Contains("CultureInfo.CurrentCulture.Name", source, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentUICulture.Name", source, StringComparison.Ordinal);
        Assert.Contains("data-culture=\"@pageCulture\"", source, StringComparison.Ordinal);
        Assert.Contains("data-ui-culture=\"@pageUiCulture\"", source, StringComparison.Ordinal);
        Assert.Contains("box.getAttribute('data-culture')", source, StringComparison.Ordinal);
        Assert.Contains("box.getAttribute('data-ui-culture')", source, StringComparison.Ordinal);
        Assert.Contains("timelineUrl += '&culture=' + encodeURIComponent(culture)", source, StringComparison.Ordinal);
        Assert.Contains("timelineUrl += '&ui-culture=' + encodeURIComponent(uiCulture)", source, StringComparison.Ordinal);
        Assert.Contains("fetch(timelineUrl, { credentials: 'same-origin' })", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln")))
            root = root.Parent;

        Assert.NotNull(root);
        return root!.FullName;
    }
}
