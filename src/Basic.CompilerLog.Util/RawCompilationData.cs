using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    internal readonly ImmutableArray<string> Aliases;
    internal readonly bool EmbedInteropTypes;
    internal readonly string? FilePath;

    internal RawReferenceData(Guid mvid, ImmutableArray<string> aliases, bool embedInteropTypes, string? filePath)
    {
        Mvid = mvid;
        Aliases = aliases;
        EmbedInteropTypes = embedInteropTypes;
        FilePath = filePath;
    }
}

internal readonly struct RawResourceData
{
    internal readonly string Name;
    internal readonly string? FileName;
    internal readonly bool IsPublic;
    internal readonly string ContentHash; 

    internal RawResourceData(string name, string? fileName, bool isPublic, string contentHash)
    {
        Name = name;
        FileName = fileName;
        IsPublic = isPublic;
        ContentHash = contentHash;
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
    internal int Index { get; }
    internal string? CompilationName { get; }
    internal string AssemblyFileName { get; }
    internal string? XmlFilePath { get; }
    internal string? OutputDirectory { get; }
    internal SourceHashAlgorithm ChecksumAlgorithm { get; }
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
    internal bool HasAllGeneratedFileContent { get; }

    internal RawCompilationData(
        int index, 
        string? compilationName,
        string assemblyFileName,
        string? xmlFilePath,
        string? outputDirectory,
        SourceHashAlgorithm checksumAlgorithm,
        List<RawReferenceData> references,
        List<RawAnalyzerData> analyzers,
        List<RawContent> contents,
        List<RawResourceData> resources,
        bool isCSharp,
        bool hasAllGeneratedFileContent)
    {
        Index = index;
        CompilationName = compilationName;
        AssemblyFileName = assemblyFileName;
        XmlFilePath = xmlFilePath;
        OutputDirectory = outputDirectory;
        ChecksumAlgorithm = checksumAlgorithm;
        References = references;
        Analyzers = analyzers;
        Contents = contents;
        Resources = resources;
        IsCSharp = isCSharp;
        HasAllGeneratedFileContent = hasAllGeneratedFileContent;
    }
}
