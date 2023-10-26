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

    /// <summary>
    /// This represents a #line directive target in a file that was embedded. These are different
    /// than normal line directives in that they are embedded into the compilation as well so the
    /// file is read from disk.
    /// </summary>
    EmbedLine,
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

internal readonly struct RawContent
{
    internal string FilePath { get; }
    internal string ContentHash { get; }
    internal RawContentKind Kind { get; }

    internal RawContent(
        string filePath,
        string contentHash,
        RawContentKind kind)
    {
        FilePath = filePath;
        ContentHash = contentHash;
        Kind = kind;
    }

    public override string ToString() => $"{Path.GetFileName(FilePath)} {Kind}";
}

internal sealed class RawCompilationData
{
    internal string? CompilationName { get; }
    internal string AssemblyFileName { get; }
    internal string? XmlFilePath { get; }
    internal string EmitOptionsHash { get; }
    internal string ParseOptionsHash { get; }
    internal string CompilationOptionsHash { get; }
    internal SourceHashAlgorithm ChecksumAlgorithm { get; }
    internal CommandLineArguments Arguments { get; }
    internal List<RawReferenceData> References { get; }
    internal List<RawAnalyzerData> Analyzers { get; }
    internal List<RawContent> Contents { get; }
    internal List<RawResourceData> Resources { get; }
    internal bool IsCSharp { get; }

    /// <summary>
    /// This is true when the generated files were successfully read from the original 
    /// compilation. This can be true when there are no generated files. A successful read
    /// for example happens on a compilation where there are no analyzers (successfully 
    /// read zero files)
    /// </summary>
    internal bool ReadGeneratedFiles { get; }

    internal RawCompilationData(
        string? compilationName,
        string assemblyFileName,
        string? xmlFilePath,
        string emitOptionsHash,
        string parseOptionsHash,
        string compilationOptionsHash,
        SourceHashAlgorithm checksumAlgorithm,
        CommandLineArguments arguments,
        List<RawReferenceData> references,
        List<RawAnalyzerData> analyzers,
        List<RawContent> contents,
        List<RawResourceData> resources,
        bool isCSharp,
        bool readGeneratedFiles)
    {
        CompilationName = compilationName;
        AssemblyFileName = assemblyFileName;
        XmlFilePath = xmlFilePath;
        EmitOptionsHash = emitOptionsHash;
        ParseOptionsHash = parseOptionsHash;
        CompilationOptionsHash = compilationOptionsHash;
        ChecksumAlgorithm = checksumAlgorithm;
        Arguments = arguments;
        References = references;
        Analyzers = analyzers;
        Contents = contents;
        Resources = resources;
        IsCSharp = isCSharp;
        ReadGeneratedFiles = readGeneratedFiles;
    }
}
