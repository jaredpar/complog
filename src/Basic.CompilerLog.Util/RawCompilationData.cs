using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal enum RawContentKind
{
    SourceText,
    AdditionalText,
    AnalyzerConfig,
}

internal sealed class RawCompilationData
{
    // TODO: should not expose this, it's only needed to the checksum algorithm, fix that.
    internal CommandLineArguments Arguments { get; }
    internal List<MetadataReference> References { get; }
    internal List<Guid> Analyzers { get; }
    internal List<(string FilePath, string ContentHash, RawContentKind Kind, SourceHashAlgorithm HashAlgorithm)> Contents { get; }

    internal RawCompilationData(
        CommandLineArguments arguments,
        List<MetadataReference> references,
        List<Guid> analyzers,
        List<(string FilePath, string ContentHash, RawContentKind Kind, SourceHashAlgorithm HashAlgorithm)> contents)
    {
        Arguments = arguments;
        References = references;
        Analyzers = analyzers;
        Contents = contents;
    }
}
