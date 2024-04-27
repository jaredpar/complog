using System.Collections.Immutable;
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

    internal ImmutableArray<(SourceText SourceText, string Path)> GeneratedSourceTexts { get; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    internal BasicAnalyzerHostNone(ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts)
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = generatedSourceTexts;
        AnalyzerReferencesCore = ImmutableArray<AnalyzerReference>.Empty;
    }

    internal BasicAnalyzerHostNone(string errorMessage)
        : base(BasicAnalyzerKind.None)
    {
        GeneratedSourceTexts = ImmutableArray<(SourceText SourceText, string Path)>.Empty;
        AnalyzerReferencesCore = ImmutableArray<AnalyzerReference>.Empty;
        AddDiagnostic(Diagnostic.Create(CannotReadGeneratedFiles, Location.None, errorMessage));
    }

    protected override void DisposeCore()
    {
        // Do nothing
    }
}