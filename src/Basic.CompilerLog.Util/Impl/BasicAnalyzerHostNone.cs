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

    internal BasicAnalyzerHostNone(bool readGeneratedFiles, ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts, BasicAnalyzerHostOptions options)
        : base(BasicAnalyzerKind.None, options)
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

/// <summary>
/// Simple in memory generator for adding the pre-generated files in. 
/// </summary>
/// <remarks>
/// Note: this cannot be a file local type because of a Roslyn bug on .NET Framework
/// </remarks>
internal sealed class NoneAnalyzerReference : AnalyzerReference, ISourceGenerator
{
    internal bool ReadGeneratedFiles { get; }
    internal ImmutableArray<(SourceText SourceText, string Path)> GeneratedSourceTexts { get; }
    public override object Id { get; } = Guid.NewGuid();
    public override string? FullPath => null;

    internal NoneAnalyzerReference(bool readGeneratedFiles, ImmutableArray<(SourceText SourceText, string Path)> generatedSourceTexts)
    {
        ReadGeneratedFiles = readGeneratedFiles;
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
        if (!ReadGeneratedFiles)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                BasicAnalyzerHostNone.CannotReadGeneratedFiles,
                Location.None));
        }

        // The biggest challenge with adding names here is replicating the original 
        // hint name. There is no way to definitively recover the original name hence we 
        // have to go with just keeping the file name that was added then doing some basic
        // counting to keep the name unique. 
        var set = new HashSet<string>(PathUtil.Comparer);
        foreach (var (sourceText, filePath) in GeneratedSourceTexts)
        {
            var fileName = Path.GetFileName(filePath);
            if (!set.Add(fileName))
            {
                fileName = Path.Combine(set.Count.ToString(), fileName);
            }

            context.AddSource(fileName, sourceText);
        }
    }
}

