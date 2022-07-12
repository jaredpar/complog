using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLogger;

internal sealed class CompilerLogBuilder : IDisposable
{
    private readonly Dictionary<Guid, string> _mvidToRefNameMap = new();
    private readonly Dictionary<string, Guid> _assemblyPathToMvidMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceHashMap = new(StringComparer.Ordinal);

    private int _compilationCount;

    internal ZipArchive ZipArchive { get; set;  }
    internal List<string> Diagnostics { get; set; }

    internal CompilerLogBuilder(Stream stream, List<string> diagnostics)
    {
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Diagnostics = diagnostics;
    }

    internal void Add(CompilerInvocation invocation)
    {
        var memoryStream = new MemoryStream();
        using var compilationWriter = new StreamWriter(memoryStream, Encoding.UTF8, leaveOpen: true);
        compilationWriter.WriteLine(invocation.ProjectFile);

        AddReferences(compilationWriter, invocation.CommandLineArguments);
        AddAnalyzers(compilationWriter, invocation.CommandLineArguments);
        AddSources(compilationWriter, invocation.CommandLineArguments);
        AddAdditionalTexts(compilationWriter, invocation.CommandLineArguments);

        compilationWriter.Flush();

        var entry = ZipArchive.CreateEntry($"compilations/{_compilationCount}.txt", CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        memoryStream.Position = 0;
        memoryStream.CopyTo(entryStream);
        entryStream.Close();

        _compilationCount++;
    }

    private void AddSources(StreamWriter compilationWriter, CommandLineArguments args)
    {
        foreach (var commandLineFile in args.SourceFiles)
        {
            var hashFileName = AddContent(commandLineFile.Path);
            compilationWriter.WriteLine($"s:{hashFileName}:{commandLineFile.Path}");
        }
    }

    /// <summary>
    /// Add a source file to the storage and return the stored name of the file.
    /// </summary>
    private string AddContent(string filePath)
    {
        var sha = SHA256.Create();

        // TODO: need to expose the real API for how the compiler reads source files. 
        // move this comment to the rehydration code when we write it.
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = sha.ComputeHash(fileStream);
        var hashText = GetHashText();
        var fileExtension = Path.GetExtension(filePath);
        var hashFileName = $"{hashText}{fileExtension}";

        if (_sourceHashMap.Add(hashText))
        {
            var entry = ZipArchive.CreateEntry($"content/{hashFileName}", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            fileStream.Position = 0;
            fileStream.CopyTo(entryStream);
        }

        return hashFileName;

        string GetHashText()
        {
            var builder = new StringBuilder();
            builder.Length = 0;
            foreach (var b in hash)
            {
                builder.Append($"{b:X2}");
            }

            return builder.ToString();
        }
    }

    private void AddReferences(StreamWriter compilationWriter, CommandLineArguments args)
    {
        foreach (var reference in args.MetadataReferences)
        {
            var mvid = AddAssembly(reference.Reference);
            compilationWriter.Write($"m:{mvid}:");
            compilationWriter.Write((int)reference.Properties.Kind);
            compilationWriter.Write(":");
            compilationWriter.Write(reference.Properties.EmbedInteropTypes);
            compilationWriter.Write(":");

            var any = false;
            foreach (var alias in reference.Properties.Aliases)
            {
                if (any)
                    compilationWriter.Write(",");
                compilationWriter.Write(alias);
                any = true;
            }
            compilationWriter.WriteLine();
        }
    }

    private void AddAdditionalTexts(StreamWriter compilationWriter, CommandLineArguments args)
    {
        foreach (var additionalText in args.AdditionalFiles)
        {
            var hashFilePath = AddContent(additionalText.Path);
            compilationWriter.WriteLine($"t:{hashFilePath}:{additionalText.Path}");
        }
    }

    private void AddAnalyzers(StreamWriter compilationWriter, CommandLineArguments args)
    {
        foreach (var analyzer in args.AnalyzerReferences)
        {
            var mvid = AddAssembly(analyzer.FilePath);
            compilationWriter.WriteLine($"a:{mvid}");
        }
    }

    /// <summary>
    /// Add the assembly into the storage and return tis MVID
    /// </summary>
    private Guid AddAssembly(string filePath)
    {
        if (_assemblyPathToMvidMap.TryGetValue(filePath, out var mvid))
        {
            Debug.Assert(_mvidToRefNameMap.ContainsKey(mvid));
            return mvid;
        }

        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using var reader = new PEReader(file);
        var mdReader = reader.GetMetadataReader();
        GuidHandle handle = mdReader.GetModuleDefinition().Mvid;
        mvid = mdReader.GetGuid(handle);

        if (_mvidToRefNameMap.TryGetValue(mvid, out var name))
        {
            _assemblyPathToMvidMap[filePath] = mvid;
        }

        var entry = ZipArchive.CreateEntry($"ref/{mvid:N}", CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        file.CopyTo(entryStream);

        _mvidToRefNameMap[mvid] = Path.GetFileName(filePath);
        _assemblyPathToMvidMap[filePath] = mvid;
        return mvid;
    }

    public void Dispose()
    {
        ZipArchive.Dispose();
        ZipArchive = null!;
    }
}
