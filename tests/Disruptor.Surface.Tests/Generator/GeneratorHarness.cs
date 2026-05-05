using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Disruptor.Surface.Generator;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// Tiny wrapper around <see cref="CSharpGeneratorDriver"/> for compiling a fixture-
/// program string against the runtime assembly and inspecting the generator's emitted
/// output + diagnostics. Keeps each test self-contained; nothing carries between runs.
/// </summary>
internal static class GeneratorHarness
{
    public static (GeneratorDriverRunResult Result, Compilation OutputCompilation, ImmutableArray<Diagnostic> RunDiagnostics, ImmutableArray<Diagnostic> CompileDiagnostics) Run(string source)
    {
        var compilation = CreateCompilation(source);
        // The driver's parseOptions must match the fixture's so generator-emitted trees
        // share the language version — otherwise CSharpCompilation rejects the merged
        // tree set with "Inconsistent language versions".
        var parseOpts = new CSharpParseOptions(LanguageVersion.Preview);
        var driver = CSharpGeneratorDriver.Create([new ModelGenerator().AsSourceGenerator()], parseOptions: parseOpts)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var runResult = driver.GetRunResult();
        var compileDiagnostics = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        return (runResult, output, diagnostics, compileDiagnostics);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = ReferenceAssemblies();
        // Unique-per-call assembly name so CompileAndLoad can be called many times in
        // the same xUnit process — the default ALC won't load two assemblies with the
        // same name, and we don't want each E2E test to need its own ALC plumbing.
        return CSharpCompilation.Create(
            assemblyName: $"GeneratorTestHost_{Guid.NewGuid():N}",
            // Preview rather than Latest — partial properties (C# 13+) need it under
            // the bundled Roslyn version. End-to-end tests need the fixture to actually
            // compile, not just have the generator run on it.
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static List<MetadataReference> ReferenceAssemblies()
    {
        var refs = new List<MetadataReference>();

        // Runtime assemblies the source we compile depends on. AppDomain hosts Disruptor.Surface.Runtime
        // already (it's a project ref of this test project), so we can grab it via reflection.
        var trustedPaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator)
            ?? [];
        foreach (var path in trustedPaths)
        {
            // Cherry-pick: System.* / netstandard / mscorlib / System.Runtime / System.Text.Json /
            // System.Collections / System.Threading.Tasks. Skipping unrelated runtime DLLs keeps
            // compilation fast and removes ambiguity from duplicate type forwarders.
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName is "System.Runtime"
                         or "System.Private.CoreLib"
                         or "mscorlib"
                         or "netstandard"
                         or "System.Collections"
                         or "System.Linq"
                         or "System.Threading.Tasks"
                         or "System.Text.Json"
                         or "System.Text.RegularExpressions"
                         or "System.Memory"
                         or "System.Net.Http"
                         or "System.ObjectModel"
                         or "System.Console"
                         or "System.Runtime.Extensions"
                         or "System.ComponentModel"
                         or "System.ComponentModel.Primitives")
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Disruptor.Surface.Runtime
        refs.Add(MetadataReference.CreateFromFile(typeof(Disruptor.Surface.Runtime.SurrealSession).Assembly.Location));
        // Ulid (transitive dep of Disruptor.Surface.Runtime)
        refs.Add(MetadataReference.CreateFromFile(typeof(Ulid).Assembly.Location));

        return refs;
    }

    /// <summary>Returns the concatenated source of every emitted file, for substring assertions.</summary>
    public static string AllGeneratedSource(GeneratorDriverRunResult result)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var src in result.GeneratedTrees)
        {
            sb.AppendLine($"// === {Path.GetFileName(src.FilePath)} ===");
            sb.AppendLine(src.ToString());
        }
        return sb.ToString();
    }

    public static SyntaxTree? FindGeneratedFile(GeneratorDriverRunResult result, string fileNameContains) =>
        result.GeneratedTrees.FirstOrDefault(t => Path.GetFileName(t.FilePath).Contains(fileNameContains, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compiles the fixture+generated output into an in-memory assembly and loads it.
    /// Used by end-to-end tests that exercise the generated entity types at runtime —
    /// shape assertions catch source drift, but only round-tripping through Track / SetField
    /// / CommitAsync catches "looks right but doesn't run right" bugs.
    /// </summary>
    public static System.Reflection.Assembly CompileAndLoad(string source)
    {
        var compilation = CreateCompilation(source);
        // Generator-emitted syntax trees need to share the fixture's language version,
        // otherwise CSharpCompilation rejects the merged tree set with "Inconsistent
        // language versions". Wire the same Preview-language ParseOptions into the
        // driver as we used for the fixture itself.
        var parseOpts = new CSharpParseOptions(LanguageVersion.Preview);
        CSharpGeneratorDriver.Create([new ModelGenerator().AsSourceGenerator()], parseOptions: parseOpts)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var compileErrors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (compileErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Fixture failed to compile after generator emit:\n  " +
                string.Join("\n  ", compileErrors.Select(d => d.ToString())));
        }

        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Emit failed:\n  " +
                string.Join("\n  ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
        }
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }
}
