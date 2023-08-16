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
    GeneratedText,
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
    internal CommandLineArguments Arguments { get; }
    internal List<RawReferenceData> References { get; }
    internal List<RawAnalyzerData> Analyzers { get; }
    internal List<(string FilePath, string ContentHash, RawContentKind Kind)> Contents { get; }
    internal List<RawResourceData> Resources { get; }

    /// <summary>
    /// This is true when the generated files were successfully read from the original 
    /// compilation. This can be true when there are no generated files. A successful read
    /// for example happens on a compilation where there are no analyzers (successfully 
    /// read zero files)
    /// </summary>
    internal bool ReadGeneratedFiles { get; }

    internal RawCompilationData(
        CommandLineArguments arguments,
        List<RawReferenceData> references,
        List<RawAnalyzerData> analyzers,
        List<(string FilePath, string ContentHash, RawContentKind Kind)> contents,
        List<RawResourceData> resources,
        bool readGeneratedFiles)
    {
        Arguments = arguments;
        References = references;
        Analyzers = analyzers;
        Contents = contents;
        Resources = resources;
        ReadGeneratedFiles = readGeneratedFiles;
    }
}
