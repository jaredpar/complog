using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    internal List<(SourceText SourceText, string FilePath)> GeneratedSourceTexts { get; }

    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    /// <summary>
    /// This creates a host with a single analyzer that returns <paramref name="generatedSourceTexts"/>. This
    /// should be used if there is an analyzer even if it generated no files.
    /// </summary>
    /// <param name="generatedSourceTexts"></param>
    internal BasicAnalyzerHostNone(List<(SourceText SourceText, string FilePath)> generatedSourceTexts)
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = generatedSourceTexts;
        AnalyzerReferencesCore = [new BasicGeneratedFilesAnalyzerReference(generatedSourceTexts)];
    }

    internal BasicAnalyzerHostNone(Diagnostic diagnostic)
        : this([])
    {
        AnalyzerReferencesCore = [new BasicErrorAnalyzerReference(diagnostic)];
    }

    /// <summary>
    /// This creates a none host with no analyzers.
    /// </summary>
    internal BasicAnalyzerHostNone()
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = [];
        AnalyzerReferencesCore = [];
    }

    protected override void DisposeCore()
    {
        // Do nothing
    }
}

/// <summary>
/// This _cannot_ be a file class. The full generated name is used in file paths of generated files. Those
/// cannot include many characters that are in the full name of a file type.
/// </summary>
internal sealed class BasicGeneratedFilesAnalyzerReference(List<(SourceText SourceText, string FilePath)> generatedSourceTexts) : AnalyzerReference, IIncrementalGenerator, IBasicAnalyzerReference
{
    internal List<(SourceText SourceText, string FilePath)> GeneratedSourceTexts { get; } = generatedSourceTexts;

    public override string? FullPath => null;

    [ExcludeFromCodeCoverage]
    public override object Id => this;

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language, List<Diagnostic>? diagnostics) => [];

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

    [ExcludeFromCodeCoverage]
    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];

    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => [this.AsSourceGenerator()];

    public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => GetGenerators(language, null);
 
    public ImmutableArray<ISourceGenerator> GetGenerators(string language, List<Diagnostic>? diagnostics) => [this.AsSourceGenerator()];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(context =>
        {
            var set = new HashSet<string>(PathUtil.Comparer);
            foreach (var tuple in GeneratedSourceTexts)
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

internal sealed class BasicErrorAnalyzerReference(Diagnostic diagnostic) : AnalyzerReference, IBasicAnalyzerReference
{
    [ExcludeFromCodeCoverage]
    public override string? FullPath => null;

    [ExcludeFromCodeCoverage]
    public override object Id => this;

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language, List<Diagnostic>? diagnostics)
    {
        diagnostics?.Add(diagnostic);
        return [];
    }

    public ImmutableArray<ISourceGenerator> GetGenerators(string language, List<Diagnostic> diagnostics) => [];

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => GetAnalyzers(language, null);

    [ExcludeFromCodeCoverage]
    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];

    [ExcludeFromCodeCoverage]
    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => [];

    public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => [];
}
