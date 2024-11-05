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

// TODO: do we need this type anymore?
internal readonly struct RawAnalyzerData
{
    internal readonly Guid Mvid;
    internal readonly string FilePath;
    internal readonly string? AssemblyName;
    internal readonly string? AssemblyInformationalVersion;

    internal string FileName => Path.GetFileName(FilePath);

    internal RawAnalyzerData(Guid mvid, string filePath, string? assemblyName, string? assemblyInformationalVersion)
    {
        Mvid = mvid;
        FilePath = filePath;
        AssemblyName = assemblyName;
        AssemblyInformationalVersion = assemblyInformationalVersion;
    }
}

// TODO: do we need this type anymore?
internal readonly struct RawReferenceData
{
    internal readonly Guid Mvid;
    internal readonly ImmutableArray<string> Aliases;
    internal readonly bool EmbedInteropTypes;
    internal readonly string? FilePath;
    internal readonly string? AssemblyName;
    internal readonly string? AssemblyInformationalVersion;

    internal RawReferenceData(Guid mvid, ImmutableArray<string> aliases, bool embedInteropTypes, string? filePath, string? assemblyName, string? assemblyInformationalVersion)
    {
        Mvid = mvid;
        Aliases = aliases;
        EmbedInteropTypes = embedInteropTypes;
        FilePath = filePath;
        AssemblyName = assemblyName;
        AssemblyInformationalVersion = assemblyInformationalVersion;
    }
}

// TODO: do we need this type anymore?
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
    internal string OriginalFilePath { get; }
    internal string NormalizedFilePath { get; }
    internal string ContentHash { get; }
    internal RawContentKind Kind { get; }

    internal RawContent(
        string originalFilePath,
        string normalizedFilePath,
        string contentHash,
        RawContentKind kind)
    {
        OriginalFilePath = originalFilePath;
        NormalizedFilePath = normalizedFilePath;
        ContentHash = contentHash;
        Kind = kind;
    }

    public override string ToString() => $"{Path.GetFileName(OriginalFilePath)} {Kind}";
}

