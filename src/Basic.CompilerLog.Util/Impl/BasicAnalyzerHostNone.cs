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
    internal ImmutableArray<(SourceText SourceText, string Path)> GeneratedSourceTexts { get; }

    internal BasicAnalyzerHostNone(ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts)
        : base(BasicAnalyzerKind.None, CreateAnalyzerReferences(generatedSourceTexts))
    {
        GeneratedSourceTexts = generatedSourceTexts;
    }

    protected override void DisposeCore()
    {
        // Do nothing
    }

    private static ImmutableArray<AnalyzerReference> CreateAnalyzerReferences(ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts)
    {
        if (generatedSourceTexts.Length == 0)
        {
            return ImmutableArray<AnalyzerReference>.Empty;
        }

        return ImmutableArray.Create<AnalyzerReference>(new NoneReference(generatedSourceTexts));
    }
}

file sealed class NoneReference : AnalyzerReference, ISourceGenerator
{
    internal ImmutableArray<(SourceText SourceText, string Path)> GeneratedSourceTexts { get; }
    public override object Id { get; } = Guid.NewGuid();
    public override string? FullPath => null;

    internal NoneReference(ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts)
    {
        GeneratedSourceTexts = generatedSourceTexts;
    }

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) =>
        ImmutableArray<DiagnosticAnalyzer>.Empty;

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() =>
        ImmutableArray<DiagnosticAnalyzer>.Empty;

    public override ImmutableArray<ISourceGenerator> GetGenerators(string? language) => 
        ImmutableArray.Create<ISourceGenerator>(this);

    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() =>
        ImmutableArray.Create<ISourceGenerator>(this);

    public override string ToString() => $"None";

    public void Initialize(GeneratorInitializationContext context)
    {
        
    }

    public void Execute(GeneratorExecutionContext context)
    {
        // TODO: this is wrong, need correct names
        foreach (var (sourceText, filePath) in GeneratedSourceTexts)
        {
            context.AddSource(Path.GetFileName(filePath), sourceText);
        }
    }
}

