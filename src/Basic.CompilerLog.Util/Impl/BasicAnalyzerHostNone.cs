using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// This is the analyzer host which doesn't actually run generators / analyzers. Instead it
/// uses the source texts that were generated at the original build time.
/// </summary>
internal sealed class BasicAnalyzerHostNone : BasicAnalyzerHost
{
    public static readonly DiagnosticDescriptor CannotReadGeneratedFiles =
        new DiagnosticDescriptor(
            "BCLA0001",
            "Cannot read generated files",
            "Error reading generated files: {0}",
            "BasicCompilerLog",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    internal ImmutableArray<(SourceText SourceText, string FilePath)> GeneratedSourceTexts { get; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    internal BasicAnalyzerHostNone(ImmutableArray<(SourceText SourceText, string FilePath)> generatedSourceTexts)
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = generatedSourceTexts;
        AnalyzerReferencesCore = [new BasicAnalyzerHostNoneAnalyzerReference(this)];
    }

    internal BasicAnalyzerHostNone(string errorMessage)
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = ImmutableArray<(SourceText SourceText, string FilePath)>.Empty;
        AnalyzerReferencesCore = [new BasicAnalyzerHostNoneAnalyzerReference(this)];
        AddDiagnostic(Diagnostic.Create(CannotReadGeneratedFiles, Location.None, errorMessage));
    }

    protected override void DisposeCore()
    {
        // Do nothing
    }

    /// <summary>
    /// Gets the appropriate hint name for the generated source file paths. This will ensure that the 
    /// invariant around every hint name being unique is maintained while trying to maintain the 
    /// original hint names when possible.
    /// </summary>
    internal static ImmutableArray<(SourceText SourceText, string HintName)> ConvertToHintNames(string projectDirectory, ImmutableArray<(SourceText SourceText, string FilePath)> generatedSourceTexts)
    {
#if NETCOREAPP
        Debug.Assert(!Path.EndsInDirectorySeparator(projectDirectory));
#endif

        var builder = ImmutableArray.CreateBuilder<(SourceText SourceText, string HintName)>(generatedSourceTexts.Length);
        for (int i = 0; i < generatedSourceTexts.Length; i++)
        {
            var tuple = generatedSourceTexts[i];
            var hintName = tuple.FilePath.StartsWith(projectDirectory, PathUtil.Comparison)
                ? tuple.FilePath.Substring(projectDirectory.Length + 1)
                : Path.Combine("generated-compiler-log", i.ToString(), Path.GetFileName(tuple.FilePath));
            builder.Add((tuple.SourceText, hintName));
        }
        return builder.MoveToImmutable();
    }
}

/// <summary>
/// This _cannot_ be a file class. The full generated name is used in file paths of generated files. Those
/// cannot include many characters that are in the full name of a file type.
/// </summary>
/// <param name="host"></param>
internal sealed class BasicAnalyzerHostNoneAnalyzerReference(BasicAnalyzerHostNone host) : AnalyzerReference, IIncrementalGenerator
{
    internal BasicAnalyzerHostNone Host { get; } = host;

    public override string? FullPath => null;

    public override object Id => this;

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];

    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => [this.AsSourceGenerator()];

    public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => GetGeneratorsForAllLanguages();

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(context =>
        {
            var set = new HashSet<string>(PathUtil.Comparer);
            foreach (var tuple in Host.GeneratedSourceTexts)
            {
                var fileName = Path.GetFileName(tuple.FilePath);
                int count = 0;
                while (!set.Add(fileName))
                {
                    fileName = Path.Combine(count.ToString(), fileName);
                    count++;
                }

                context.AddSource(fileName, tuple.SourceText);
            }
        });
    }
}
