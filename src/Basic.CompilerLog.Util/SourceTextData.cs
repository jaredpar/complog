using Microsoft.CodeAnalysis.Text;
using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLog.Util;

public enum SourceTextKind
{
    SourceCode,
    AnalyzerConfig,
    AdditionalText,
}

public sealed class SourceTextData(
    object id,
    string filePath,
    SourceHashAlgorithm checksumAlgorithm,
    SourceTextKind sourceTextKind)
{
    public object Id { get; } = id;
    public string FilePath { get; } = filePath;
    public SourceHashAlgorithm ChecksumAlgorithm { get; } = checksumAlgorithm;
    public SourceTextKind SourceTextKind { get; } = sourceTextKind;

    internal RawContentKind RawContentKind => SourceTextKind switch
    {
        SourceTextKind.SourceCode => RawContentKind.SourceText,
        SourceTextKind.AnalyzerConfig => RawContentKind.AnalyzerConfig,
        SourceTextKind.AdditionalText => RawContentKind.AdditionalText,
        _ => throw new InvalidOperationException($"Unknown {nameof(SourceTextKind)} value {SourceTextKind}"),
    };

    [ExcludeFromCodeCoverage]
    public override string ToString() => FilePath;
}
