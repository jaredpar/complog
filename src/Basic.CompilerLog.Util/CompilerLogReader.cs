using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

public sealed class CompilerLogReader : IDisposable
{
    private readonly Dictionary<Guid, MetadataReference> _refMap = new Dictionary<Guid, MetadataReference>();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();

    internal ZipArchive ZipArchive { get; set; }
    internal int Count { get; }

    private CompilerLogReader(Stream stream, bool leaveOpen)
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

        Count = ReadMetadata();
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

    public static CompilerLogReader Create(Stream stream, bool leaveOpen = false) => new CompilerLogReader(stream, leaveOpen);

    public static CompilerLogReader Create(string filePath)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new CompilerLogReader(stream, leaveOpen: false);
    }

    public CompilerCall ReadCompilerCall(int index)
    {
        if (index >= Count)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        return ReadCompilerCallCore(reader, index);
    }

    public List<CompilerCall> ReadCompilerCalls(Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        var list = new List<CompilerCall>();
        for (int i = 0; i < Count; i++)
        {
            var compilerCall = ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                list.Add(compilerCall);
            }
        }

        return list;
    }

    public CompilationData ReadCompilationData(int index) =>
        ReadCompilationData(ReadCompilerCall(index));

    public CompilationData ReadCompilationData(CompilerCall compilerCall)
    {
        var rawCompilationData = ReadRawCompilationData(compilerCall);
        var referenceList = GetMetadataReferences(rawCompilationData.References);

        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigList = new List<(SourceText SourceText, string Path)>();
        var additionalTextList = new List<AdditionalText>();

        foreach (var tuple in rawCompilationData.Contents)
        {
            switch (tuple.Kind)
            {
                case RawContentKind.SourceText:
                    sourceTextList.Add((GetSourceText(tuple.ContentHash, tuple.HashAlgorithm), tuple.FilePath));
                    break;
                case RawContentKind.AnalyzerConfig:
                    analyzerConfigList.Add((GetSourceText(tuple.ContentHash, tuple.HashAlgorithm), tuple.FilePath));
                    break;
                case RawContentKind.AdditionalText:
                    additionalTextList.Add(new BasicAdditionalTextFile(
                        tuple.FilePath,
                        GetSourceText(tuple.ContentHash, tuple.HashAlgorithm)));
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        return compilerCall.IsCSharp
            ? CreateCSharp()
            : CreateVisualBasic();

        (SyntaxTreeOptionsProvider, AnalyzerConfigOptionsProvider) CreateOptionsProviders(IEnumerable<SyntaxTree> syntaxTrees, IEnumerable<AdditionalText> additionalTexts)
        {
            AnalyzerConfigOptionsResult globalConfigOptions = default;
            AnalyzerConfigSet? analyzerConfigSet = null;
            var resultList = new List<(object, AnalyzerConfigOptionsResult)>();

            if (analyzerConfigList.Count > 0)
            {
                var list = new List<AnalyzerConfig>();
                foreach (var tuple in analyzerConfigList)
                {
                    list.Add(AnalyzerConfig.Parse(tuple.SourceText, tuple.Path));
                }

                analyzerConfigSet = AnalyzerConfigSet.Create(list);
                globalConfigOptions = analyzerConfigSet.GlobalConfigOptions;
            }

            foreach (var syntaxTree in syntaxTrees)
            {
                resultList.Add((syntaxTree, analyzerConfigSet?.GetOptionsForSourcePath(syntaxTree.FilePath) ?? default));
            }

            foreach (var additionalText in additionalTexts)
            {
                resultList.Add((additionalText, analyzerConfigSet?.GetOptionsForSourcePath(additionalText.Path) ?? default));
            }

            var syntaxOptionsProvider = new BasicSyntaxTreeOptionsProvider(
                isConfigEmpty: analyzerConfigList.Count == 0,
                globalConfigOptions,
                resultList);
            var analyzerConfigOptionsProvider = new BasicAnalyzerConfigOptionsProvider(
                isConfigEmpty: analyzerConfigList.Count == 0,
                globalConfigOptions,
                resultList);
            return (syntaxOptionsProvider, analyzerConfigOptionsProvider);
        }

        CSharpCompilationData CreateCSharp()
        {
            var csharpArgs = (CSharpCommandLineArguments)rawCompilationData.Arguments;
            var parseOptions = csharpArgs.ParseOptions;

            var syntaxTrees = new SyntaxTree[sourceTextList.Count];
            Parallel.For(
                0,
                sourceTextList.Count,
                i =>
                {
                    var t = sourceTextList[i];
                    syntaxTrees[i] = CSharpSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
                });

            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextList);
            var compilation = CSharpCompilation.Create(
                rawCompilationData.Arguments.CompilationName,
                syntaxTrees,
                referenceList,
                csharpArgs.CompilationOptions.WithSyntaxTreeOptionsProvider(syntaxProvider));

            return new CSharpCompilationData(
                compilerCall,
                compilation,
                csharpArgs,
                additionalTextList.ToImmutableArray(),
                CreateAssemblyLoadContext(compilerCall.ProjectFilePath, rawCompilationData.Analyzers),
                analyzerProvider);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            var basicArgs = (VisualBasicCommandLineArguments)rawCompilationData.Arguments;
            var parseOptions = basicArgs.ParseOptions;
            var syntaxTrees = new SyntaxTree[sourceTextList.Count];
            Parallel.For(
                0,
                sourceTextList.Count,
                i =>
                {
                    var t = sourceTextList[i];
                    syntaxTrees[i] = VisualBasicSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
                });

            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextList);

            var compilation = VisualBasicCompilation.Create(
                rawCompilationData.Arguments.CompilationName,
                syntaxTrees,
                referenceList,
                basicArgs.CompilationOptions.WithSyntaxTreeOptionsProvider(syntaxProvider));

            return new VisualBasicCompilationData(
                compilerCall,
                compilation,
                basicArgs,
                additionalTextList.ToImmutableArray(),
                CreateAssemblyLoadContext(compilerCall.ProjectFilePath, rawCompilationData.Analyzers),
                analyzerProvider);
        }
    }

    public List<CompilationData> ReadCompilationDatas(Func<CompilerCall, bool>? predicate = null)
    {
        var calls = ReadCompilerCalls(predicate);
        var list = new List<CompilationData>(capacity: calls.Count);
        foreach (var compilerCall in ReadCompilerCalls(predicate))
        {
            list.Add(ReadCompilationData(compilerCall));
        }
        return list;
    }

    internal (CompilerCall, RawCompilationData) ReadRawCompilationData(int index)
    {
        var compilerCall = ReadCompilerCall(index);
        return (compilerCall, ReadRawCompilationData(compilerCall));
    }

    internal int GetIndex(CompilerCall compilerCall)
    {
        if (compilerCall.Index is int i && i < Count)
        {
            return i;
        }

        throw new ArgumentException($"Invalid index");
    }

    internal RawCompilationData ReadRawCompilationData(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);

        // TODO: re-reading the call is a bit inefficient here, better to just skip 
        _ = ReadCompilerCallCore(reader, index);

        CommandLineArguments args = compilerCall.IsCSharp 
            ? CSharpCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFilePath), sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFilePath), sdkDirectory: null, additionalReferenceDirectories: null);
        var references = new List<RawReferenceData>();
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

        return data;

        void ParseMetadataReference(string line)
        {
            var items = line.Split(':');
            if (items.Length == 5 &&
                Guid.TryParse(items[1], out var mvid) &&
                int.TryParse(items[2], out var kind))
            {
                var embedInteropTypes = items[3] == "1";

                string[]? aliases = null;
                if (!string.IsNullOrEmpty(items[4]))
                {
                    aliases = items[4].Split(',');
                }

                references.Add(new RawReferenceData(
                    mvid,
                    aliases,
                    embedInteropTypes));
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

    public List<(string FileName, List<byte> ImageBytes)> ReadReferenceFileInfo(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var (_, rawCompilationData) = ReadRawCompilationData(index);
        var list = new List<(string, List<byte>)>(rawCompilationData.References.Count);
        foreach (var referenceData in rawCompilationData.References)
        {
            list.Add((
                GetMetadataReferenceFileName(referenceData.Mvid),
                GetMetadataReferenceBytes(referenceData.Mvid)));
        }

        return list;
    }


    private CompilerCall ReadCompilerCallCore(StreamReader reader, int index)
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

        return new CompilerCall(projectFile, kind, targetFramework, isCSharp, arguments, index);
    }

    internal List<byte> GetMetadataReferenceBytes(Guid mvid)
    {
        using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        return entryStream.ReadAllBytes();
    }

    internal string GetMetadataReferenceFileName(Guid mvid)
    {
        if (_mvidToRefInfoMap.TryGetValue(mvid, out var tuple))
        {
            return tuple.FileName;
        }

        throw new ArgumentException($"{mvid} is not a valid MVID");
    }

    internal MetadataReference GetMetadataReference(Guid mvid)
    {
        if (_refMap.TryGetValue(mvid, out var metadataReference))
        {
            return metadataReference;
        }

        var bytes = GetMetadataReferenceBytes(mvid);
        var tuple = _mvidToRefInfoMap[mvid];
        metadataReference = MetadataReference.CreateFromStream(new MemoryStream(bytes.ToArray()), filePath: tuple.FileName);
        _refMap.Add(mvid, metadataReference);
        return metadataReference;
    }

    internal MetadataReference GetMetadataReference(in RawReferenceData data)
    {
        var reference = GetMetadataReference(data.Mvid);
        if (data.EmbedInteropTypes)
        {
            reference = reference.WithEmbedInteropTypes(true);
        }

        if (data.Aliases is { Length: > 0 })
        {
            reference = reference.WithAliases(data.Aliases);
        }

        return reference;
    }

    internal List<MetadataReference> GetMetadataReferences(List<RawReferenceData> referenceDataList)
    {
        var list = new List<MetadataReference>(capacity: referenceDataList.Count);
        foreach (var referenceData in referenceDataList)
        {
            list.Add(GetMetadataReference(referenceData));
        }
        return list;
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
