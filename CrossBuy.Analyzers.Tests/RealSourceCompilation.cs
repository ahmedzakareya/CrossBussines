using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// A real semantic compilation of the CrossBuy application, built from its own sources.
///
/// WHY FROM SOURCE, AND WHY NOT THROUGH THE BUILD.
///
/// The obvious way to dogfood an analyzer is to add it to CrossBuy.csproj. That is deliberately NOT done here: the
/// brief forbids modifying production code, CrossBuy.csproj is a shared file under the selective-commit rule, and
/// wiring a warning-producing analyzer into the application build is a decision for the next increment rather than
/// a side effect of this one. Build wiring is designed and documented, not applied.
///
/// So the reconciliation compiles the application's own .cs files against the application's own reference set,
/// which produces the same symbols the compiler would. CrossBuy.dll itself is EXCLUDED from that reference set —
/// the sources are compiled from scratch, and referencing the compiled form as well would make every CrossBuy type
/// ambiguous between source and metadata.
///
/// DECLARED LIMITATION: Razor views are not compiled. No controller lives in a .cshtml file, and the accepted
/// Stage 1 measurement did not scan them either, so this changes no number — but it is a limitation, not an
/// absence of one.
/// </summary>
internal static class RealSourceCompilation
{
    private static readonly Lazy<CSharpCompilation> Instance = new(Build);

    internal static CSharpCompilation Value => Instance.Value;

    internal static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "CrossBuy.sln was not found above " + AppContext.BaseDirectory +
            "; the reconciliation cannot locate the application sources.");
    }

    private static CSharpCompilation Build()
    {
        var appDirectory = Path.Combine(RepoRoot, "CrossBuy");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest)
            // TestRun and Debug both define DEBUG. Compiling without it would analyze a DIFFERENT program than the
            // one that ships — the exact defect the declared TestRun configuration exists to prevent.
            .WithPreprocessorSymbols("DEBUG", "TRACE");

        var trees = new List<SyntaxTree>();

        foreach (var file in Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');

            // obj/ and bin/ hold generated and copied code. The ONE exception is the implicit-usings file: the
            // project sets ImplicitUsings=enable, so without it hundreds of files fail to bind and the semantic
            // model degrades — which would silently lower the measured coverage.
            var isGenerated = normalized.Contains("/obj/") || normalized.Contains("/bin/");
            if (isGenerated && !normalized.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            if (isGenerated && !normalized.Contains("/obj/Debug/")) continue;   // one copy, not one per config

            trees.Add(CSharpSyntaxTree.ParseText(
                SourceText(file), parseOptions, path: file));
        }

        if (!trees.Any(t => t.FilePath.Replace('\\', '/').EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            // Fall back to the SDK's implicit using set rather than compiling without it. Recorded as a fallback
            // so a missing obj/ produces a slightly different path, not a silently wrong number.
            trees.Add(CSharpSyntaxTree.ParseText(SdkImplicitUsings, parseOptions, path: "GlobalUsings.fallback.cs"));
        }

        return CSharpCompilation.Create(
            "CrossBuy.FromSource",
            trees,
            References(),
            new CSharpCompilationOptions(
                // ConsoleApplication, not DynamicallyLinkedLibrary: Program.cs uses top-level statements, and a
                // library output rejects them with CS8805. The application really is an executable — compiling it
                // as a library would leave one error in the compilation and cast doubt on every symbol resolved.
                OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true));
    }

    private static Microsoft.CodeAnalysis.Text.SourceText SourceText(string path)
    {
        using var stream = File.OpenRead(path);
        return Microsoft.CodeAnalysis.Text.SourceText.From(stream);
    }

    private const string SdkImplicitUsings = """
global using global::Microsoft.AspNetCore.Builder;
global using global::Microsoft.AspNetCore.Hosting;
global using global::Microsoft.AspNetCore.Http;
global using global::Microsoft.AspNetCore.Routing;
global using global::Microsoft.Extensions.Configuration;
global using global::Microsoft.Extensions.DependencyInjection;
global using global::Microsoft.Extensions.Hosting;
global using global::Microsoft.Extensions.Logging;
global using global::System;
global using global::System.Collections.Generic;
global using global::System.IO;
global using global::System.Linq;
global using global::System.Net.Http;
global using global::System.Net.Http.Json;
global using global::System.Threading;
global using global::System.Threading.Tasks;
""";

    /// <summary>
    /// Everything the test host can see, minus the assemblies that would collide with the sources being compiled.
    /// </summary>
    private static ImmutableArray<MetadataReference> References()
    {
        var excluded = new[]
        {
            "CrossBuy.dll",                  // compiled from source here; both would be ambiguous
            "CrossBuy.Analyzers.dll",
            "CrossBuy.Analyzers.Tests.dll",
        };

        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var path in trusted.Split(Path.PathSeparator))
        {
            if (path.Length == 0 || !File.Exists(path)) continue;

            var name = Path.GetFileName(path);
            if (excluded.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase))) continue;

            builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
    }
}
