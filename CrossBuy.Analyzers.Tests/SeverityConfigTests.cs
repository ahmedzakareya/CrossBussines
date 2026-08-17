using System.Text.RegularExpressions;

namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// The severity configuration itself, tested.
///
/// Two files declare the CBA severities, for a reason found by a real build (entry-gate PROOF C): a `[*.cs]`
/// .editorconfig section is matched by PATH and therefore cannot apply a severity to a diagnostic reported without a
/// location, which CBA004 can be. `.globalconfig` closes that case.
///
/// Two files means they can drift, and a security severity that disagrees with itself is worse than either value
/// alone — the build would enforce one thing while the reviewed document says another. So they are reconciled here.
/// </summary>
public class SeverityConfigTests
{
    private static readonly string[] AllRules = { "CBA001", "CBA002", "CBA003", "CBA004", "CBA005", "CBA006" };

    /// <summary>The approved rollout. Changing a value here is a deliberate, reviewable act.</summary>
    private static readonly Dictionary<string, string> Approved = new()
    {
        ["CBA001"] = "error",        // new debt
        ["CBA002"] = "suggestion",   // 124 pre-existing; raised in phase 3
        ["CBA003"] = "warning",
        ["CBA004"] = "error",        // stale allowance
        ["CBA005"] = "warning",
        ["CBA006"] = "error",        // suppression
    };

    private static Dictionary<string, string> Parse(string fileName)
    {
        var path = Path.Combine(RealSourceCompilation.RepoRoot, fileName);
        Assert.True(File.Exists(path), $"{fileName} is missing — the analyzer would fall back to its Warning defaults");

        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var match in Regex.Matches(
                     File.ReadAllText(path),
                     @"^\s*dotnet_diagnostic\.(CBA00\d)\.severity\s*=\s*(\w+)\s*$",
                     RegexOptions.Multiline).Cast<Match>())
        {
            found[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return found;
    }

    [Fact]
    public void The_editorconfig_declares_the_approved_severity_for_every_rule()
    {
        var declared = Parse(".editorconfig");

        Assert.Equal(Approved.Count, declared.Count);
        foreach (var rule in AllRules)
            Assert.Equal(Approved[rule], declared[rule]);
    }

    [Fact]
    public void The_globalconfig_declares_the_approved_severity_for_every_rule()
    {
        var declared = Parse(".globalconfig");

        Assert.Equal(Approved.Count, declared.Count);
        foreach (var rule in AllRules)
            Assert.Equal(Approved[rule], declared[rule]);
    }

    [Fact]
    public void The_two_configuration_files_cannot_drift_apart()
    {
        var editorConfig = Parse(".editorconfig");
        var globalConfig = Parse(".globalconfig");

        Assert.Equal(editorConfig.OrderBy(k => k.Key, StringComparer.Ordinal),
                     globalConfig.OrderBy(k => k.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void The_globalconfig_is_marked_global_or_it_silently_does_nothing()
    {
        // Without `is_global = true` the file is treated as an ordinary editorconfig with no sections, which matches
        // nothing — it would sit in the repo looking like configuration while configuring nothing at all.
        var text = File.ReadAllText(Path.Combine(RealSourceCompilation.RepoRoot, ".globalconfig"));

        Assert.Matches(@"(?m)^\s*is_global\s*=\s*true\s*$", text);
    }

    [Fact]
    public void Every_rule_the_analyzer_supports_has_a_declared_severity()
    {
        // A new diagnostic added to the analyzer without a severity decision would ship at its Warning default and
        // quietly not be enforced. This fails the moment SupportedDiagnostics grows.
        var supported = new AuthorizationAnalyzer().SupportedDiagnostics.Select(d => d.Id).OrderBy(id => id).ToList();

        Assert.Equal(AllRules.OrderBy(id => id).ToList(), supported);
        Assert.All(supported, id => Assert.True(Approved.ContainsKey(id), $"{id} has no approved severity"));
    }

    [Fact]
    public void CBA002_is_NOT_escalated_in_this_increment()
    {
        // Explicitly instructed: 124 authenticated-but-unauthorized endpoints exist, and escalating them now would
        // add build noise without removing debt. Pinned so a later edit cannot do it silently.
        Assert.Equal("suggestion", Parse(".editorconfig")["CBA002"]);
        Assert.Equal("suggestion", Parse(".globalconfig")["CBA002"]);
    }
}
