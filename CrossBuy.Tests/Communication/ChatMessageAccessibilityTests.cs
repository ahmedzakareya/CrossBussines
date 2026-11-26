using Xunit;
using CrossBuy.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace CrossBuy.Tests.Communication;

public sealed class ChatMessageAccessibilityTests
{
    [Theory]
    [InlineData("ed", "Edit")]
    [InlineData("dl", "Delete")]
    public void Own_message_actions_have_localized_accessible_names(string actionClass, string resourceKey)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Chat", "Index.cshtml"));
        var action = source.Split("</button>", StringSplitOptions.None)
            .FirstOrDefault(part => part.Contains($" {actionClass}\"", StringComparison.Ordinal));

        Assert.NotNull(action);
        Assert.Contains($"title=\"@SR[\"{resourceKey}\"]\"", action, StringComparison.Ordinal);
        Assert.Contains($"aria-label=\"@SR[\"{resourceKey}\"]\"", action, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln")))
            root = root.Parent;

        Assert.NotNull(root);
        return root!.FullName;
    }
}

public sealed class ChatControllerAntiforgeryTests
{
    [Fact]
    public void Chat_controller_validates_antiforgery_for_all_unsafe_actions()
    {
        Assert.Contains(typeof(AutoValidateAntiforgeryTokenAttribute),
            typeof(ChatController).GetCustomAttributes(inherit: true).Select(attribute => attribute.GetType()));
    }

    [Fact]
    public void Chat_page_sends_the_razor_antiforgery_token_with_post_requests()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Chat", "Index.cshtml"));

        Assert.Contains("@Html.AntiForgeryToken()", source, StringComparison.Ordinal);
        Assert.Contains("'RequestVerificationToken': chatRequestToken ? chatRequestToken.value : ''", source, StringComparison.Ordinal);
        Assert.Equal(2, source.Split("'RequestVerificationToken': chatRequestToken", StringSplitOptions.None).Length - 1);
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln")))
            root = root.Parent;

        Assert.NotNull(root);
        return root!.FullName;
    }
}

public sealed class CommunicationMailAntiforgeryTests
{
    [Fact]
    public void Star_endpoint_requires_antiforgery_validation()
    {
        var action = typeof(CommController).GetMethod(nameof(CommController.Star));
        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<ValidateAntiForgeryTokenAttribute>(inherit: true));
    }

    [Fact]
    public void Mail_star_ajax_request_sends_the_page_antiforgery_token()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Comm", "Index.cshtml"));
        Assert.Contains("@Html.AntiForgeryToken()", source, StringComparison.Ordinal);
        Assert.Contains("'RequestVerificationToken': tok ? tok.value : ''", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln")))
            root = root.Parent;

        Assert.NotNull(root);
        return root!.FullName;
    }
}
