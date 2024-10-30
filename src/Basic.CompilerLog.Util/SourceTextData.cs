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
    CompilerCall compilerCall,
    string filePath,
    SourceHashAlgorithm checksumAlgorithm,
    SourceTextKind sourceTextKind)
{
    public CompilerCall CompilerCall { get; } = compilerCall;
    public string FilePath { get; } = filePath;
    public SourceHashAlgorithm ChecksumAlgorithm { get; } = checksumAlgorithm;
    public SourceTextKind SourceTextKind { get; } = sourceTextKind;

    [ExcludeFromCodeCoverage]
    public override string ToString() => FilePath;
}
