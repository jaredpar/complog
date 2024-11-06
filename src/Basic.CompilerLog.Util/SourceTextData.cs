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

    [ExcludeFromCodeCoverage]
    public override string ToString() => FilePath;
}
