using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

public static class Extensions
{
    internal static void CheckEmitFlags(this EmitFlags flags)
    {
        if ((flags & EmitFlags.IncludePdbStream) != 0 &&
            (flags & EmitFlags.MetadataOnly) != 0)
        {
            throw new ArgumentException($"Cannot mix {EmitFlags.MetadataOnly} and {EmitFlags.IncludePdbStream}");
        }
    }

    internal static ZipArchiveEntry GetEntryOrThrow(this ZipArchive zipArchive, string name)
    {
        var entry = zipArchive.GetEntry(name);
        if (entry is null)
            throw new InvalidOperationException($"Could not find entry with name {name}");
        return entry;
    }

    internal static Stream OpenEntryOrThrow(this ZipArchive zipArchive, string name)
    {
        var entry = GetEntryOrThrow(zipArchive, name);
        return entry.Open();
    }

    internal static byte[] ReadAllBytes(this ZipArchive zipArchive, string name)
    {
        var entry = GetEntryOrThrow(zipArchive, name);
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes.AsSpan());
        return bytes;
    }

    internal static string ReadLineOrThrow(this TextReader reader)
    {
        if (reader.ReadLine() is { } line)
        {
            return line;
        }

        throw new InvalidOperationException();
    }

#if !NET
    internal static void AddRange<T>(this List<T> list, ReadOnlySpan<T> span)
    {
        foreach (var item in span)
        {
            list.Add(item);
        }
    }
#endif

    /// <summary>
    /// Creates a <see cref="MemoryStream"/> that is a simple wrapper around the array. The intent
    /// is for consumers to be able to access the underlying array via <see cref="MemoryStream.TryGetBuffer(out ArraySegment{byte})"/>
    /// and similar methods.
    /// </summary>
    internal static MemoryStream AsSimpleMemoryStream(this byte[] array, bool writable = true) =>
        new MemoryStream(array, 0, count: array.Length, writable: writable, publiclyVisible: true);

    internal static string GetResourceName(this ResourceDescription d) => ReflectionUtil.ReadField<string>(d, "ResourceName");
    internal static string? GetFileName(this ResourceDescription d) => ReflectionUtil.ReadField<string?>(d, "FileName");
    internal static bool IsPublic(this ResourceDescription d) => ReflectionUtil.ReadField<bool>(d, "IsPublic");
    internal static Func<Stream> GetDataProvider(this ResourceDescription d) => ReflectionUtil.ReadField<Func<Stream>>(d, "DataProvider");

    public static MetadataReference With(this MetadataReference mdRef, ImmutableArray<string> aliases, bool embedInteropTypes)
    {
        if (aliases is { Length: > 0 })
        {
            mdRef = mdRef.WithAliases(aliases);
        }

        if (embedInteropTypes)
        {
            mdRef = mdRef.WithEmbedInteropTypes(true);
        }

        return mdRef;
    }

    public static (List<string> Analyzers, List<string> Generators) ReadAnalyzerFullTypeNames(this ICompilerCallReader compilerCallReader, AnalyzerData analyzerData, bool? isCSharp = null)
    {
        var languageName = isCSharp switch 
        {
            true => LanguageNames.CSharp,
            false => LanguageNames.VisualBasic,
            null => null,
        };

        var stream = new MemoryStream();
        compilerCallReader.CopyAssemblyBytes(analyzerData.AssemblyData, stream);
        stream.Position = 0;
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var analyzers = RoslynUtil.GetAnalyzerTypeDefinitions(metadataReader, languageName)
            .Select(t => RoslynUtil.GetFullyQualifiedName(metadataReader, t.TypeDefinition))
            .ToList();
        var generators = RoslynUtil.GetGeneratorTypeDefinitions(metadataReader, languageName)
            .Select(t => RoslynUtil.GetFullyQualifiedName(metadataReader, t.TypeDefinition))
            .ToList();

        return (analyzers, generators);
    }

    public static string AsHexString(this byte[] bytes) => AsHexString(bytes.AsSpan());

    public static string AsHexString(this ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder();
        foreach (var b in bytes)
        {
            builder.Append($"{b:X2}");
        }

        return builder.ToString();
    }

    public static IBasicAnalyzerReference AsBasicAnalyzerReference(this AnalyzerReference analyzerReference)
    {
        if (analyzerReference is IBasicAnalyzerReference basicAnalyzerReference)
        {
            return basicAnalyzerReference;
        }

        return new BasicAnalyzerReferenceWrapper(analyzerReference);
    }
}

file sealed class BasicAnalyzerReferenceWrapper(AnalyzerReference analyzerReference) : IBasicAnalyzerReference
{
    internal AnalyzerReference AnalyzerReference { get; } = analyzerReference;

    // This is the place where we are generating safe wrappers for the APIs we don't want called directly
#pragma warning disable RS0030

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language, List<Diagnostic> diagnostics) =>
        WithDiagnostics(AnalyzerReference, language, diagnostics, static (ar, l) => ar.GetAnalyzers(l));

    public ImmutableArray<ISourceGenerator> GetGenerators(string language, List<Diagnostic> diagnostics) =>
        WithDiagnostics(AnalyzerReference, language, diagnostics, static (ar, l) => ar.GetGenerators(l));

#pragma warning restore RS0030

    private static T WithDiagnostics<T>(
        AnalyzerReference analyzerReference,
        string language,
        List<Diagnostic> diagnostics,
        Func<AnalyzerReference, string, T> func)
    {
        EventHandler<AnalyzerLoadFailureEventArgs> handler = (sender, args) =>
        {
            var d = Diagnostic.Create(
                RoslynUtil.CannotLoadTypesDiagnosticDescriptor,
                Location.None,
                $"{args.TypeName}:{args.Exception?.Message}");
            diagnostics.Add(d); 
        };

        if (analyzerReference is AnalyzerFileReference afr)
        {
            afr.AnalyzerLoadFailed += handler;
            try
            {
                return func(analyzerReference, language);
            }
            finally
            {
                afr.AnalyzerLoadFailed -= handler;
            }
        }
        else
        {
            return func(analyzerReference, language);
        }
    }
}
