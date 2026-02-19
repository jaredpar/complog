using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StructuredLogViewer;

namespace Basic.CompilerLog.Util;

public readonly struct AssemblyData(Guid mvid, string filePath)
{
    public Guid Mvid { get; } = mvid;

    /// <summary>
    /// The file path for the given assembly
    /// </summary>
    /// <remarks>
    /// This path is only valid on the machine where the log was generated. It's 
    /// generally only useful for informational diagnostics.
    /// </remarks>
    public string FilePath { get; } = filePath;
}

public sealed class ReferenceData(
    AssemblyIdentityData assemblyIdentityData,
    string filePath,
    ImmutableArray<string> aliases,
    bool embedInteropTypes,
    bool isImplicit = false)
{
    public AssemblyIdentityData AssemblyIdentityData { get; } = assemblyIdentityData;

    /// <inheritdoc cref="AssemblyData.FilePath"/>
    public string FilePath { get; } = filePath;
    public ImmutableArray<string> Aliases { get; } = aliases;
    public bool EmbedInteropTypes { get; } = embedInteropTypes;
    public bool IsImplicit { get; } = isImplicit;

    public AssemblyData AssemblyData => new(Mvid, FilePath);
    public Guid Mvid => AssemblyIdentityData.Mvid;
    public string FileName => Path.GetFileName(FilePath);

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{FileName} {Mvid}";
}

public sealed class AnalyzerData(
    AssemblyIdentityData assemblyIdentityData,
    string filePath)
{
    public AssemblyIdentityData AssemblyIdentityData { get; } = assemblyIdentityData;

    /// <inheritdoc cref="AssemblyData.FilePath"/>
    public string FilePath { get; } = filePath;

    public AssemblyData AssemblyData => new(Mvid, FilePath);
    public Guid Mvid => AssemblyIdentityData.Mvid;
    public string FileName => Path.GetFileName(FilePath);

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{FileName} {Mvid}";
}

public sealed class ResourceData(
    string contentHash,
    string? fileName,
    string name,
    bool isPublic)
{
    public string ContentHash { get; } = contentHash;
    public string? FileName { get; } = fileName;
    public string Name { get; } = name;
    public bool IsPublic { get; } = isPublic;

    [ExcludeFromCodeCoverage]
    public override string ToString() => Name;
}

public readonly struct AssemblyFileData(string fileName, MemoryStream Image)
{
    public string FileName { get; } = fileName;
    public MemoryStream Image { get; } = Image;

    [ExcludeFromCodeCoverage]
    public override string ToString() => FileName;
}


