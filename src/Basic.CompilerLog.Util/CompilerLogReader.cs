using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogReader : IDisposable
{
    private readonly Dictionary<Guid, MetadataReference> _refMap = new Dictionary<Guid, MetadataReference>();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();

    internal ZipArchive ZipArchive { get; set; }
    internal int CompilationCount { get; }

    internal CompilerLogReader(Stream stream, bool leaveOpen)
    {
        try
        {
            ZipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        }
        catch (InvalidDataException)
        {
            // Happens when this is not a valid zip file
            throw GetInvalidCompilerLogFileException();
        }

        CompilationCount = ReadMetadata();
        ReadAssemblyInfo();

        int ReadMetadata()
        {
            var entry = ZipArchive.GetEntry(MetadataFileName);
            if (entry is null)
                throw GetInvalidCompilerLogFileException();

            using var reader = new StreamReader(entry.Open(), ContentEncoding, leaveOpen: false);
            var line = reader.ReadLineOrThrow();
            var items = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (items.Length != 2 || !int.TryParse(items[1], out var count))
                throw new InvalidOperationException();
            return count;
        }

        void ReadAssemblyInfo()
        {
            using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(AssemblyInfoFileName), ContentEncoding, leaveOpen: false);
            while (reader.ReadLine() is string line)
            {
                var items = line.Split(':', count: 3);
                var mvid = Guid.Parse(items[1]);
                var assemblyName = new AssemblyName(items[2]);
                _mvidToRefInfoMap[mvid] = (items[0], assemblyName);
            }
        }

        static Exception GetInvalidCompilerLogFileException() => new ArgumentException("Provided stream is not a compiler log file");
    }

    // TODO: wrong layer, needs to be in CompilationLogUtil
    internal CompilerCall ReadCompilerCall(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        return ReadCompilerCallCore(reader);
    }

    internal (CompilerCall, RawCompilationData) ReadRawCompilationData(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        var compilerCall = ReadCompilerCallCore(reader);

        CommandLineArguments args = compilerCall.IsCSharp 
            ? CSharpCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFilePath), sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFilePath), sdkDirectory: null, additionalReferenceDirectories: null);
        var references = new List<MetadataReference>();
        var analyzers = new List<Guid>();
        var contents = new List<(string FilePath, string ContentHash, RawContentKind Kind, SourceHashAlgorithm HashAlgorithm)>();

        while (reader.ReadLine() is string line)
        {
            switch (line[0])
            {
                case 'm':
                    ParseMetadataReference(line);
                    break;
                case 'a':
                    ParseAnalyzer(line);
                    break;
                case 's':
                    ParseContent(line, RawContentKind.SourceText);
                    break;
                case 'c':
                    ParseContent(line, RawContentKind.AnalyzerConfig);
                    break;
                case 't':
                    ParseContent(line, RawContentKind.AdditionalText);
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized line: {line}");
            }
        }

        var data = new RawCompilationData(
            args,
            references,
            analyzers,
            contents);

        return (compilerCall, data);

        void ParseMetadataReference(string line)
        {
            var items = line.Split(':');
            if (items.Length == 5 &&
                Guid.TryParse(items[1], out var mvid) &&
                int.TryParse(items[2], out var kind))
            {
                var reference = GetMetadataReference(mvid);
                if (items[3] == "1")
                    reference = reference.WithEmbedInteropTypes(true);

                if (!string.IsNullOrEmpty(items[4]))
                {
                    var aliases = items[4].Split(',');
                    reference = reference.WithAliases(aliases);
                }

                references.Add(reference);
                return;
            }

            throw new InvalidOperationException();
        }

        void ParseContent(string line, RawContentKind kind)
        {
            var items = line.Split(':', count: 3);
            contents.Add((items[2], items[1], kind, args.ChecksumAlgorithm));
        }

        void ParseAnalyzer(string line)
        {
            var items = line.Split(':', count: 2);
            var mvid = Guid.Parse(items[1]);
            analyzers.Add(mvid);
        }
    }

    /// <summary>
    /// Get the content hash of all the source files in the compiler log
    /// </summary>
    internal List<string> ReadSourceContentHashes()
    {
        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(SourceInfoFileName), ContentEncoding, leaveOpen: false);
        var list = new List<string>();
        while (reader.ReadLine() is string line)
        {
            list.Add(line);
        }

        return list;
    }

    private CompilerCall ReadCompilerCallCore(StreamReader reader)
    {
        var projectFile = reader.ReadLineOrThrow();
        var isCSharp = reader.ReadLineOrThrow() == "C#";
        var targetFramework = reader.ReadLineOrThrow();
        if (string.IsNullOrEmpty(targetFramework))
        {
            targetFramework = null;
        }

        var kind = Enum.Parse<CompilerCallKind>(reader.ReadLineOrThrow());
        var count = int.Parse(reader.ReadLineOrThrow());
        var arguments = new string[count];
        for (int i = 0; i < count; i++)
        {
            arguments[i] = reader.ReadLineOrThrow();
        }

        return new CompilerCall(projectFile, kind, targetFramework, isCSharp, arguments);
    }

    internal MetadataReference GetMetadataReference(Guid mvid)
    {
        if (_refMap.TryGetValue(mvid, out var metadataReference))
        {
            return metadataReference;
        }

        using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        var bytes = entryStream.ReadAllBytes();

        var tuple = _mvidToRefInfoMap[mvid];
        metadataReference = MetadataReference.CreateFromStream(new MemoryStream(bytes.ToArray()), filePath: tuple.FileName);
        _refMap.Add(mvid, metadataReference);
        return metadataReference;
    }

    internal SourceText GetSourceText(string contentHash, SourceHashAlgorithm checksumAlgorithm)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));

        // TODO: need to expose the real API for how the compiler reads source files. 
        // move this comment to the rehydration code when we write it.
        return SourceText.From(stream, checksumAlgorithm: checksumAlgorithm);
    }

    internal List<byte> GetAssemblyBytes(Guid mvid)
    {
        using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        return entryStream.ReadAllBytes();
    }

    internal BasicAssemblyLoadContext CreateAssemblyLoadContext(string name, List<Guid> analyzers) =>
        new BasicAssemblyLoadContext(name, this, analyzers);

    public void Dispose()
    {
        ZipArchive.Dispose();
        ZipArchive = null!;
    }
}
