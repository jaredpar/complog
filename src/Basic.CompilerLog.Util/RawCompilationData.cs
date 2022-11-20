using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Configuration.Internal;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal enum RawContentKind
{
    SourceText,
    AdditionalText,
    AnalyzerConfig,
    Embed,
    SourceLink,
    RuleSet,
    AppConfig,
    Win32Manifest,
    Win32Resource,
    Win32Icon,
    CryptoKeyFile,
}

internal readonly struct RawAnalyzerData
{
    internal readonly Guid Mvid;
    internal readonly string FilePath;

    internal string FileName => Path.GetFileName(FilePath);

    internal RawAnalyzerData(Guid mvid, string filePath)
    {
        Mvid = mvid;
        FilePath = filePath;
    }
}

internal readonly struct RawReferenceData
{
    internal readonly Guid Mvid;
    internal readonly string[]? Aliases;
    internal readonly bool EmbedInteropTypes;

    internal RawReferenceData(Guid mvid, string[]? aliases, bool embedInteropTypes)
    {
        Mvid = mvid;
        Aliases = aliases;
        EmbedInteropTypes = embedInteropTypes;
    }
}

internal readonly struct RawResourceData
{
    internal readonly string ContentHash; 
    internal readonly ResourceDescription ResourceDescription;

    internal RawResourceData(string contentHash, ResourceDescription d)
    {
        ContentHash = contentHash;
        ResourceDescription = d;
    }
}

internal sealed class RawCompilationData
{
    // TODO: should not expose this, it's only needed to the checksum algorithm, fix that.
    internal CommandLineArguments Arguments { get; }
    internal List<RawReferenceData> References { get; }
    internal List<RawAnalyzerData> Analyzers { get; }
    internal List<(string FilePath, string ContentHash, RawContentKind Kind)> Contents { get; }
    internal List<RawResourceData> Resources { get; }

    internal RawCompilationData(
        CommandLineArguments arguments,
        List<RawReferenceData> references,
        List<RawAnalyzerData> analyzers,
        List<(string FilePath, string ContentHash, RawContentKind Kind)> contents,
        List<RawResourceData> resources)
    {
        Arguments = arguments;
        References = references;
        Analyzers = analyzers;
        Contents = contents;
        Resources = resources;
    }
}
