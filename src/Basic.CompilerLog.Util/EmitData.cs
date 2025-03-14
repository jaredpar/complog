﻿using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Data about a compilation that is only interesting at Emit time
/// </summary>
public sealed class EmitData
{
    public string AssemblyFileName { get; }
    public string? XmlFilePath { get; }
    public bool EmitPdb { get; }
    public MemoryStream? Win32ResourceStream { get; }
    public MemoryStream? SourceLinkStream { get; }
    public IEnumerable<ResourceDescription>? Resources { get; }
    public IEnumerable<EmbeddedText>? EmbeddedTexts { get; }

    public EmitData(
        string assemblyFileName,
        string? xmlFilePath,
        bool emitPdb,
        MemoryStream? win32ResourceStream,
        MemoryStream? sourceLinkStream,
        IEnumerable<ResourceDescription>? resources,
        IEnumerable<EmbeddedText>? embeddedTexts)
    {
        AssemblyFileName = assemblyFileName;
        EmitPdb = emitPdb;
        XmlFilePath = xmlFilePath;
        Win32ResourceStream = win32ResourceStream;
        SourceLinkStream = sourceLinkStream;
        Resources = resources;
        EmbeddedTexts = embeddedTexts;
    }
}

public interface IEmitResult
{
    public bool Success { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
}


public readonly struct EmitDiskResult : IEmitResult
{
    public bool Success { get; }
    public string Directory { get; }
    public string AssemblyFileName { get; }
    public string AssemblyFilePath { get; }
    public string? PdbFilePath { get; }
    public string? XmlFilePath { get; }
    public string? MetadataFilePath { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public EmitDiskResult(
        bool success,
        string directory,
        string assemblyFileName,
        string? pdbFilePath,
        string? xmlFilePath,
        string? metadataFilePath,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Success = success;
        Directory = directory;
        AssemblyFileName = assemblyFileName;
        AssemblyFilePath = Path.Combine(Directory, assemblyFileName);
        PdbFilePath = pdbFilePath;
        XmlFilePath = xmlFilePath;
        MetadataFilePath  = metadataFilePath;
        Diagnostics = diagnostics;
    }
}

public readonly struct EmitMemoryResult : IEmitResult
{
    public bool Success { get; }
    public MemoryStream AssemblyStream { get; }
    public MemoryStream? PdbStream { get; }
    public MemoryStream? XmlStream { get; }
    public MemoryStream? MetadataStream { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public EmitMemoryResult(
        bool success,
        MemoryStream assemblyStream,
        MemoryStream? pdbStream,
        MemoryStream? xmlStream,
        MemoryStream? metadataStream,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Success = success;
        AssemblyStream = assemblyStream;
        PdbStream = pdbStream;
        XmlStream = xmlStream;
        MetadataStream = metadataStream;
        Diagnostics = diagnostics;
    }
}
