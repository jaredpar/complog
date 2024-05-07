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
    public static readonly DiagnosticDescriptor CannotReadGeneratedFiles =
        new DiagnosticDescriptor(
            "BCLA0001",
            "Cannot read generated files",
            "Error reading generated files: {0}",
            "BasicCompilerLog",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    internal ImmutableArray<(SourceText SourceText, string FilePath)> GeneratedSourceTexts { get; }
    internal BasicAnalyzerHostNoneAnalyzerReference Generator { get; }

    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    internal BasicAnalyzerHostNone(ImmutableArray<(SourceText SourceText, string FilePath)> generatedSourceTexts)
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = generatedSourceTexts;
        Generator = new BasicAnalyzerHostNoneAnalyzerReference(this);
        AnalyzerReferencesCore = [Generator];
    }

    internal BasicAnalyzerHostNone(string errorMessage)
        : this(ImmutableArray<(SourceText SourceText, string FilePath)>.Empty)
    {
        AddDiagnostic(Diagnostic.Create(CannotReadGeneratedFiles, Location.None, errorMessage));
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
/// <param name="host"></param>
internal sealed class BasicAnalyzerHostNoneAnalyzerReference(BasicAnalyzerHostNone host) : AnalyzerReference, IIncrementalGenerator
{
    internal BasicAnalyzerHostNone Host { get; } = host;

    public override string? FullPath => null;

    [ExcludeFromCodeCoverage]
    public override object Id => this;

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

    [ExcludeFromCodeCoverage]
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
