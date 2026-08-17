using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CrossBuy.Analyzers.Tests;

/// <summary>A baseline supplied to the analyzer as an AdditionalFile.</summary>
internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly SourceText _text;

    internal InMemoryAdditionalText(string path, string text)
    {
        Path = path;
        _text = SourceText.From(text);
    }

    public override string Path { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
}

/// <summary>
/// Drives the analyzer over a compilation.
///
/// WHY NOT Microsoft.CodeAnalysis.Testing. The analyzer-testing SDK is not in the local package cache and the
/// brief forbids acquiring dependencies. Driving Roslyn directly is a handful of lines and has an advantage
/// besides: the test asserts on real <see cref="Diagnostic"/> objects produced by the real analyzer driver,
/// including their effective severity after configuration — which is what the escalation test needs.
/// </summary>
internal static class AnalyzerHarness
{
    /// <summary>
    /// Every assembly the test host itself can see, so a test compilation has the real ASP.NET MVC types
    /// (ControllerBase, HttpPostAttribute, AllowAnonymousAttribute...) rather than stubs of them. Stubbing the
    /// framework would let the analyzer pass a test by matching a shape the real framework does not have.
    /// </summary>
    private static readonly Lazy<ImmutableArray<MetadataReference>> HostReferences = new(() =>
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var path in trusted.Split(Path.PathSeparator))
        {
            if (path.Length == 0 || !File.Exists(path)) continue;
            builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
    });

    /// <summary>
    /// A compilation from several files. Needed for the partial-helper test: "the gate is in another file" is one
    /// of the three defects the syntactic scanner declares it cannot handle, so proving it must involve two real
    /// syntax trees rather than two regions of one.
    /// </summary>
    internal static CSharpCompilation CompileFiles(params (string Name, string Source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Source,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: Path.Combine("C:", "repo", "CrossBuy", "Controllers", f.Name)));

        return CSharpCompilation.Create(
            "TestAsm",
            trees,
            HostReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>A compilation whose file path is OUTSIDE the Controllers folder, for the scoping test.</summary>
    internal static CSharpCompilation CompileOutsideControllers(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: Path.Combine("C:", "repo", "CrossBuy", "BL", "NotAController.cs"));

        return CSharpCompilation.Create(
            "TestAsm",
            new[] { tree },
            HostReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>
    /// Compilation errors invalidate a semantic test: an unbound call cannot be resolved to an authority, so a
    /// broken test source would "prove" the analyzer reports a gap when it really proves nothing. Every pattern
    /// test asserts this first.
    /// </summary>
    internal static void AssertCompiles(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            "the test source must compile, or the semantic result is meaningless:\n  " +
            string.Join("\n  ", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// A compilation of one test controller against the declared surface.
    ///
    /// The ORDER is the harness's job: usings, then the controller, then the surface's namespace declarations.
    /// Every using in C# must precede every declaration, so the surface cannot come first.
    /// </summary>
    internal static CSharpCompilation CompileController(string controllerSource) =>
        Compile(Surface.Preamble + controllerSource + "\n" + Surface.Source);

    internal static CSharpCompilation Compile(string source, string assemblyName = "TestAsm")
    {
        // The file path matters: the inventory is scoped to the Controllers folder, exactly as the accepted
        // Stage 1 measurement was. A test source with no Controllers path would be invisible, so the harness puts
        // it there — and a dedicated test asserts that the scoping is real rather than incidental.
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: Path.Combine("C:", "repo", "CrossBuy", "Controllers", "TestController.cs"));

        return CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            HostReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>Runs the analyzer and returns only its own diagnostics, ordered for stable assertions.</summary>
    internal static ImmutableArray<Diagnostic> Run(
        Compilation compilation,
        string? baselineJson = null,
        ImmutableDictionary<string, ReportDiagnostic>? severityOverrides = null)
    {
        if (severityOverrides != null)
            compilation = compilation.WithOptions(
                compilation.Options.WithSpecificDiagnosticOptions(severityOverrides));

        var additionalFiles = baselineJson == null
            ? ImmutableArray<AdditionalText>.Empty
            : ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(
                    Path.Combine("C:", "repo", "engineering", "authorization-baseline.json"), baselineJson));

        var options = new AnalyzerOptions(additionalFiles);

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new AuthorizationAnalyzer()),
            options);

        // GetAllDiagnosticsAsync (not GetAnalyzerDiagnosticsAsync) so compilation-end diagnostics are produced
        // and effective-severity configuration is applied — the escalation test depends on both.
        var all = withAnalyzers.GetAllDiagnosticsAsync().GetAwaiter().GetResult();

        return all
            .Where(d => d.Id.StartsWith("CBA00", StringComparison.Ordinal))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.GetMessage(), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    internal static IEnumerable<Diagnostic> OfId(this ImmutableArray<Diagnostic> diagnostics, string id) =>
        diagnostics.Where(d => d.Id == id);

    /// <summary>A baseline JSON document listing exactly the given ids.</summary>
    internal static string Baseline(params string[] ids) => BaselineWith(ids.Select(id => (id, "AuthorizationGap")));

    internal static string BaselineWith(IEnumerable<(string Id, string Classification)> entries)
    {
        var list = entries.ToList();
        var rows = list.Select(e =>
        {
            var dot = e.Id.IndexOf('.');
            var controller = dot > 0 ? e.Id[..dot] : e.Id;
            var action = dot > 0 ? e.Id[(dot + 1)..] : string.Empty;
            return $@"    {{ ""id"": ""{e.Id}"", ""controller"": ""{controller}"", ""action"": ""{action}"",
      ""classification"": ""{e.Classification}"" }}";
        });

        return $@"{{
  ""schema"": 1,
  ""frozenBaseline"": {{ ""mutating"": 0, ""attributeProtected"": 0, ""inBodyProtected"": 0, ""gaps"": {list.Count} }},
  ""count"": {list.Count},
  ""entries"": [
{string.Join(",\n", rows)}
  ]
}}";
    }
}
