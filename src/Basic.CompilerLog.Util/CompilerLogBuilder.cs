using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogBuilder : IDisposable
{
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();
    private readonly Dictionary<string, Guid> _assemblyPathToMvidMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceHashMap = new(StringComparer.Ordinal);

    private int _compilationCount;
    private bool _closed;

    internal ZipArchive ZipArchive { get; set;  }
    internal List<string> Diagnostics { get; set; }

    internal bool IsOpen => !_closed;
    internal bool IsClosed => _closed;

    internal CompilerLogBuilder(Stream stream, List<string> diagnostics)
    {
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Diagnostics = diagnostics;
    }

    internal bool Add(CompilerCall compilerCall)
    {
        var memoryStream = new MemoryStream();
        using var compilationWriter = new StreamWriter(memoryStream, ContentEncoding, leaveOpen: true);
        compilationWriter.WriteLine(compilerCall.ProjectFile);
        compilationWriter.WriteLine(compilerCall.IsCSharp ? "C#" : "VB");
        compilationWriter.WriteLine(compilerCall.TargetFramework);
        compilationWriter.WriteLine(compilerCall.Kind);

        var arguments = compilerCall.Arguments;
        compilationWriter.WriteLine(arguments.Length);
        foreach (var arg in arguments)
        {
            compilationWriter.WriteLine(arg);
        }

        var baseDirectory = Path.GetDirectoryName(compilerCall.ProjectFile);
        CommandLineArguments commandLineArguments = compilerCall.IsCSharp
            ? CSharpCommandLineParser.Default.Parse(arguments, baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(arguments, baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null);

        try
        {
            AddReferences(compilationWriter, commandLineArguments);
            AddAnalyzers(compilationWriter, commandLineArguments);
            AddSources(compilationWriter, commandLineArguments);
            AddAdditionalTexts(compilationWriter, commandLineArguments);

            compilationWriter.Flush();

            var entry = ZipArchive.CreateEntry(GetCompilerEntryName(_compilationCount), CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            memoryStream.Position = 0;
            memoryStream.CopyTo(entryStream);
            entryStream.Close();

            _compilationCount++;
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Add($"Error adding {compilerCall.ProjectFile}: {ex.Message}");
            return false;
        }
    }

    private void EnsureOpen()
    {
        if (IsClosed)
            throw new InvalidOperationException();
    }

    public void Close()
    {
        try
        {
            EnsureOpen();
            WriteMetadata();
            WriteAssemblyInfo();
            ZipArchive.Dispose();
            ZipArchive = null!;
        }
        finally
        {
            _closed = true;
        }

        void WriteMetadata()
        {
            var entry = ZipArchive.CreateEntry(MetadataFileName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), ContentEncoding, leaveOpen: false);
            writer.WriteLine($"count:{_compilationCount}");
        }

        void WriteAssemblyInfo()
        {
            var entry = ZipArchive.CreateEntry(AssemblyInfoFileName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), ContentEncoding, leaveOpen: false);
            foreach (var kvp in _mvidToRefInfoMap.OrderBy(x => x.Value.FileName).ThenBy(x => x.Key))
            {
                writer.WriteLine($"{kvp.Value.FileName}:{kvp.Key:N}:{kvp.Value.AssemblyName}");
            }
        }
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

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = sha.ComputeHash(fileStream);
        var hashText = GetHashText();
        var fileExtension = Path.GetExtension(filePath);

        if (_sourceHashMap.Add(hashText))
        {
            var entry = ZipArchive.CreateEntry(GetContentEntryName(hashText), CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            fileStream.Position = 0;
            fileStream.CopyTo(entryStream);
        }

        return hashText;

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
            compilationWriter.Write(reference.Properties.EmbedInteropTypes ? '1' : '0');
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
            Debug.Assert(_mvidToRefInfoMap.ContainsKey(mvid));
            return mvid;
        }

        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new PEReader(file);
        var mdReader = reader.GetMetadataReader();
        GuidHandle handle = mdReader.GetModuleDefinition().Mvid;
        mvid = mdReader.GetGuid(handle);

        _assemblyPathToMvidMap[filePath] = mvid;

        // If the assembly was already loaded from a different path then no more
        // work is needed here
        if (_mvidToRefInfoMap.ContainsKey(mvid))
        {
            return mvid;
        }

        var entry = ZipArchive.CreateEntry(GetAssemblyEntryName(mvid), CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        file.Position = 0;
        file.CopyTo(entryStream);

        // There are some assemblies for which MetadataReader will return an AssemblyName which 
        // fails ToString calls which is why we use AssemblyName.GetAssemblyName here.
        //
        // Example: .nuget\packages\microsoft.visualstudio.interop\17.2.32505.113\lib\net472\Microsoft.VisualStudio.Interop.dll
        var assemblyName = AssemblyName.GetAssemblyName(filePath);
        _mvidToRefInfoMap[mvid] = (Path.GetFileName(filePath), assemblyName);
        return mvid;
    }

    public void Dispose()
    {
        if (IsOpen)
        {
            Close();
        }
    }
}
