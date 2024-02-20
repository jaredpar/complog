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
            "Generated files could not be read when compiler log was created",
            "BasicCompilerLog",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

    internal bool ReadGeneratedFiles { get; }
    internal ImmutableArray<(SourceText SourceText, string Path)> GeneratedSourceTexts { get; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    internal BasicAnalyzerHostNone(bool readGeneratedFiles, ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts)
        : base(BasicAnalyzerKind.None)
    {
        ReadGeneratedFiles = readGeneratedFiles;
        GeneratedSourceTexts = generatedSourceTexts;
        AnalyzerReferencesCore = ImmutableArray<AnalyzerReference>.Empty;

        if (!ReadGeneratedFiles)
        {
            AddDiagnostic(Diagnostic.Create(CannotReadGeneratedFiles, Location.None));
        }
    }

    protected override void DisposeCore()
    {
        // Do nothing
    }
}