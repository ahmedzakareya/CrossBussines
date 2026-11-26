using Xunit;

namespace CrossBuy.Tests.Communication;

public sealed class CommLinkCultureNavigationTests
{
    [Fact]
    public void Conversation_task_links_preserve_the_current_page_culture()
    {
        var source = Read("CrossBuy/Views/Shared/_EntityConversation.cshtml");

        Assert.Contains("function withCurrentCulture(path)", source, StringComparison.Ordinal);
        Assert.Contains("current.get('culture') || fallback", source, StringComparison.Ordinal);
        Assert.Contains("current.get('ui-culture') || fallback || culture", source, StringComparison.Ordinal);
        Assert.Equal(2, source.Split("withCurrentCulture(t.route", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Task_linked_message_routes_preserve_the_current_page_culture()
    {
        var source = Read("CrossBuy/Views/Tasks/Detail.cshtml");

        Assert.Contains("function cbWithCurrentCulture(path)", source, StringComparison.Ordinal);
        Assert.Contains("esc(cbWithCurrentCulture(m.route))", source, StringComparison.Ordinal);
        Assert.Contains("target.origin !== window.location.origin", source, StringComparison.Ordinal);
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
