using System.Xml.Linq;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BudgetResourceTests
{
    [Fact]
    public void Budget_resources_have_matching_nonempty_Arabic_and_English_entries()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "CrossBuy", "Resources")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var resources = Path.Combine(directory!.FullName, "CrossBuy", "Resources", "Views", "Project");
        Dictionary<string, string> Read(string name) => XDocument.Load(Path.Combine(resources, name))
            .Root!.Elements("data").ToDictionary(x => (string)x.Attribute("name")!, x => (string?)x.Element("value") ?? "");
        var english = Read("BudgetVersions.resx");
        var arabic = Read("BudgetVersions.ar.resx");
        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.OrderBy(x => x), arabic.Keys.OrderBy(x => x));
        Assert.All(english, x => Assert.False(string.IsNullOrWhiteSpace(x.Value), $"Empty English resource: {x.Key}"));
        Assert.All(arabic, x => Assert.False(string.IsNullOrWhiteSpace(x.Value), $"Empty Arabic resource: {x.Key}"));
        Assert.Contains(arabic.Values, value => value.Any(c => c >= '\u0600' && c <= '\u06ff'));
    }
}
