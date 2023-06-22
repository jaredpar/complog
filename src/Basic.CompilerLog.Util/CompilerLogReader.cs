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
using System.Text;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

public sealed class CompilerLogReader : IDisposable
{
    private readonly Dictionary<Guid, MetadataReference> _refMap = new ();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();
    private readonly Dictionary<string, BasicAnalyzerHost> _analyzersMap = new ();
    private readonly bool _ownsCompilerLogState;

    public BasicAnalyzerHostOptions BasicAnalyzerHostOptions { get; }
    internal CompilerLogState CompilerLogState { get; }
    internal ZipArchive ZipArchive { get; set; }
    internal int Count { get; }

    private CompilerLogReader(Stream stream, bool leaveOpen, BasicAnalyzerHostOptions? basicAnalyzersOptions = null, CompilerLogState? state = null)
    {
        try
        {
            ZipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            CompilerLogState = state ?? new CompilerLogState();
            _ownsCompilerLogState = state is null;
        }
        catch (InvalidDataException)
        {
            // Happens when this is not a valid zip file
            throw GetInvalidCompilerLogFileException();
        }

        BasicAnalyzerHostOptions = basicAnalyzersOptions ?? BasicAnalyzerHostOptions.Default;
        Count = ReadMetadata();
        ReadAssemblyInfo();

        int ReadMetadata()
        {
            var entry = ZipArchive.GetEntry(MetadataFileName) ?? throw GetInvalidCompilerLogFileException();
            using var reader = Polyfill.NewStreamReader(entry.Open(), ContentEncoding, leaveOpen: false);
            var line = reader.ReadLineOrThrow();
            var items = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (items.Length != 2 || !int.TryParse(items[1], out var count))
                throw new InvalidOperationException();
            return count;
        }

        void ReadAssemblyInfo()
        {
            using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(AssemblyInfoFileName), ContentEncoding, leaveOpen: false);
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

    public static CompilerLogReader Create(
        Stream stream,
        bool leaveOpen = false,
        BasicAnalyzerHostOptions? options = null,
        CompilerLogState? state = null) => new CompilerLogReader(stream, leaveOpen, options, state);

    public static CompilerLogReader Create(
        string filePath,
        BasicAnalyzerHostOptions? options = null,
        CompilerLogState? state = null)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new CompilerLogReader(stream, leaveOpen: false, options, state);
    }

    public CompilerCall ReadCompilerCall(int index)
    {
        if (index >= Count)
            throw new InvalidOperationException();

        using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        return ReadCompilerCallCore(reader, index);
    }

    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null)
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
        var compilationOptions = rawCompilationData.Arguments.CompilationOptions;

        var hashAlgorithm = rawCompilationData.Arguments.ChecksumAlgorithm;
        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigList = new List<(SourceText SourceText, string Path)>();
        var additionalTextList = new List<AdditionalText>();

        Stream? win32ResourceStream = null;
        Stream? sourceLinkStream = null;
        List<ResourceDescription>? resources = rawCompilationData.Resources.Count == 0
            ? null
            : rawCompilationData.Resources.Select(x => x.ResourceDescription).ToList();
        List<EmbeddedText>? embeddedTexts = null;

        foreach (var tuple in rawCompilationData.Contents)
        {
            switch (tuple.Kind)
            {
                case RawContentKind.SourceText:
                    sourceTextList.Add((GetSourceText(tuple.ContentHash, hashAlgorithm), tuple.FilePath));
                    break;
                case RawContentKind.GeneratedText:
                    if (BasicAnalyzerHostOptions.ResolvedKind == BasicAnalyzerKind.None)
                    {
                        sourceTextList.Add((GetSourceText(tuple.ContentHash, hashAlgorithm), tuple.FilePath));
                    }
                    break;
                case RawContentKind.AnalyzerConfig:
                    analyzerConfigList.Add((GetSourceText(tuple.ContentHash, hashAlgorithm), tuple.FilePath));
                    break;
                case RawContentKind.AdditionalText:
                    additionalTextList.Add(new BasicAdditionalTextFile(
                        tuple.FilePath,
                        GetSourceText(tuple.ContentHash, hashAlgorithm)));
                    break;
                case RawContentKind.CryptoKeyFile:
                    HandleCryptoKeyFile(tuple.ContentHash);
                    break;
                case RawContentKind.SourceLink:
                    sourceLinkStream = GetStateAwareContentStream(tuple.ContentHash);
                    break;
                case RawContentKind.Win32Resource:
                    win32ResourceStream = GetStateAwareContentStream(tuple.ContentHash);
                    break;
                case RawContentKind.Embed:
                {
                    if (embeddedTexts is null)
                    {
                        embeddedTexts = new List<EmbeddedText>();
                    }

                    var sourceText = GetSourceText(tuple.ContentHash, hashAlgorithm, canBeEmbedded: true);
                    var embeddedText = EmbeddedText.FromSource(tuple.FilePath, sourceText);
                    embeddedTexts.Add(embeddedText);
                    break;
                }

                // not exposed yet
                case RawContentKind.RuleSet:
                case RawContentKind.AppConfig:
                case RawContentKind.Win32Manifest:
                case RawContentKind.Win32Icon:
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        var emitData = new EmitData(
            win32ResourceStream: win32ResourceStream,
            sourceLinkStream: sourceLinkStream,
            resources: resources,
            embeddedTexts: embeddedTexts);

        return compilerCall.IsCSharp
            ? CreateCSharp()
            : CreateVisualBasic();

        void HandleCryptoKeyFile(string contentHash)
        {
            var dir = Path.Combine(CompilerLogState.CryptoKeyFileDirectory, GetIndex(compilerCall).ToString());
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"{contentHash}.snk");
            File.WriteAllBytes(filePath, GetContentBytes(contentHash));
            compilationOptions = compilationOptions.WithCryptoKeyFile(filePath);
        }

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
            var csharpOptions = (CSharpCompilationOptions)compilationOptions;
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

            csharpOptions = csharpOptions
                .WithSyntaxTreeOptionsProvider(syntaxProvider)
                .WithStrongNameProvider(new DesktopStrongNameProvider());

            var compilation = CSharpCompilation.Create(
                rawCompilationData.Arguments.CompilationName,
                syntaxTrees,
                referenceList,
                csharpOptions);

            return new CSharpCompilationData(
                compilerCall,
                compilation,
                emitData,
                csharpArgs,
                additionalTextList.ToImmutableArray(),
                ReadAnalyzers(rawCompilationData.Analyzers),
                analyzerProvider);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            var basicArgs = (VisualBasicCommandLineArguments)rawCompilationData.Arguments;
            var basicOptions = (VisualBasicCompilationOptions)compilationOptions;
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

            basicOptions = basicOptions
                .WithSyntaxTreeOptionsProvider(syntaxProvider)
                .WithStrongNameProvider(new DesktopStrongNameProvider());

            var compilation = VisualBasicCompilation.Create(
                rawCompilationData.Arguments.CompilationName,
                syntaxTrees,
                referenceList,
                basicOptions);

            return new VisualBasicCompilationData(
                compilerCall,
                compilation,
                emitData,
                basicArgs,
                additionalTextList.ToImmutableArray(),
                ReadAnalyzers(rawCompilationData.Analyzers),
                analyzerProvider);
        }
    }

    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null)
    {
        var calls = ReadAllCompilerCalls(predicate);
        var list = new List<CompilationData>(capacity: calls.Count);
        foreach (var compilerCall in ReadAllCompilerCalls(predicate))
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
        using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);

        // TODO: re-reading the call is a bit inefficient here, better to just skip 
        _ = ReadCompilerCallCore(reader, index);

        CommandLineArguments args = compilerCall.IsCSharp 
            ? CSharpCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFilePath), sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFilePath), sdkDirectory: null, additionalReferenceDirectories: null);
        var references = new List<RawReferenceData>();
        var analyzers = new List<RawAnalyzerData>();
        var contents = new List<(string FilePath, string ContentHash, RawContentKind Kind)>();
        var resources = new List<RawResourceData>();

        while (reader.ReadLine() is string line)
        {
            var colonIndex = line.IndexOf(':');
            switch (line.AsSpan().Slice(0, colonIndex))
            {
                case "m":
                    ParseMetadataReference(line);
                    break;
                case "a":
                    ParseAnalyzer(line);
                    break;
                case "source":
                    ParseContent(line, RawContentKind.SourceText);
                    break;
                case "generated":
                    ParseContent(line, RawContentKind.GeneratedText);
                    break;
                case "config":
                    ParseContent(line, RawContentKind.AnalyzerConfig);
                    break;
                case "text":
                    ParseContent(line, RawContentKind.AdditionalText);
                    break;
                case "embed":
                    ParseContent(line, RawContentKind.Embed);
                    break;
                case "link":
                    ParseContent(line, RawContentKind.SourceLink);
                    break;
                case "ruleset":
                    ParseContent(line, RawContentKind.RuleSet);
                    break;
                case "appconfig":
                    ParseContent(line, RawContentKind.AppConfig);
                    break;
                case "win32manifest":
                    ParseContent(line, RawContentKind.Win32Manifest);
                    break;
                case "win32resource":
                    ParseContent(line, RawContentKind.Win32Resource);
                    break;
                case "cryptokeyfile":
                    ParseContent(line, RawContentKind.CryptoKeyFile);
                    break;
                case "r":
                    ParseResource(line);
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized line: {line}");
            }
        }

        var data = new RawCompilationData(
            args,
            references,
            analyzers,
            contents,
            resources);

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
            contents.Add((items[2], items[1], kind));
        }

        void ParseResource(string line)
        {
            var items = line.Split(':', count: 5);
            var fileName = items[4];
            var isPublic = bool.Parse(items[3]);
            var contentHash = items[1];
            var dataProvider = () =>
            {
                var bytes = GetContentBytes(contentHash);
                return new MemoryStream(bytes);
            };

            var d = string.IsNullOrEmpty(fileName)
                ? new ResourceDescription(items[2], dataProvider, isPublic)
                : new ResourceDescription(items[2], fileName, dataProvider, isPublic);
            resources.Add(new(contentHash, d));
        }

        void ParseAnalyzer(string line)
        {
            var items = line.Split(':', count: 3);
            var mvid = Guid.Parse(items[1]);
            analyzers.Add(new RawAnalyzerData(mvid, items[2]));
        }
    }

    /// <summary>
    /// Get the content hash of all the source files in the compiler log
    /// </summary>
    internal List<string> ReadSourceContentHashes()
    {
        using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(SourceInfoFileName), ContentEncoding, leaveOpen: false);
        var list = new List<string>();
        while (reader.ReadLine() is string line)
        {
            list.Add(line);
        }

        return list;
    }

    public List<(string FileName, byte[] ImageBytes)> ReadReferenceFileInfo(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var (_, rawCompilationData) = ReadRawCompilationData(index);
        var list = new List<(string, byte[])>(rawCompilationData.References.Count);
        foreach (var referenceData in rawCompilationData.References)
        {
            list.Add((
                GetMetadataReferenceFileName(referenceData.Mvid),
                GetAssemblyBytes(referenceData.Mvid)));
        }

        return list;
    }

    public List<(string FilePath, byte[] ImageBytes)> ReadAnalyzerFileInfo(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var (_, rawCompilationData) = ReadRawCompilationData(index);
        var list = new List<(string, byte[])>(rawCompilationData.Analyzers.Count);
        foreach (var analyzerData in rawCompilationData.Analyzers)
        {
            list.Add((
                analyzerData.FilePath,
                GetAssemblyBytes(analyzerData.Mvid)));
        }

        return list;
    }

    internal BasicAnalyzerHost ReadAnalyzers(List<RawAnalyzerData> analyzers)
    {
        string? key = null;
        BasicAnalyzerHost? basicAnalyzerHost;
        if (BasicAnalyzerHostOptions.Cacheable)
        {
            key = GetKey();
            if (_analyzersMap.TryGetValue(key, out basicAnalyzerHost) && !basicAnalyzerHost.IsDisposed)
            {
                return basicAnalyzerHost;
            }
        }

        basicAnalyzerHost = BasicAnalyzerHostOptions.ResolvedKind switch
        {
            BasicAnalyzerKind.OnDisk => BasicAnalyzerHostOnDisk.Create(this, analyzers, BasicAnalyzerHostOptions),
            BasicAnalyzerKind.InMemory => BasicAnalyzerHostInMemory.Create(this, analyzers, BasicAnalyzerHostOptions),
            BasicAnalyzerKind.None => new BasicAnalyzerHostNone(),
            _ => throw new InvalidOperationException()
        };

        CompilerLogState.BasicAnalyzerHosts.Add(basicAnalyzerHost);

        if (BasicAnalyzerHostOptions.Cacheable)
        {
            _analyzersMap[key!] = basicAnalyzerHost;
        }

        return basicAnalyzerHost;

        string GetKey()
        {
            var builder = new StringBuilder();
            foreach (var analyzer in analyzers.OrderBy(x => x.Mvid))
            {
                builder.AppendLine($"{analyzer.Mvid}");
            }
            return builder.ToString();
        }
    }

    private static CompilerCall ReadCompilerCallCore(StreamReader reader, int index)
    {
        var projectFile = reader.ReadLineOrThrow();
        var isCSharp = reader.ReadLineOrThrow() == "C#";
        var targetFramework = reader.ReadLineOrThrow();
        if (string.IsNullOrEmpty(targetFramework))
        {
            targetFramework = null;
        }

        var kind = (CompilerCallKind)Enum.Parse(typeof(CompilerCallKind), reader.ReadLineOrThrow());
        var count = int.Parse(reader.ReadLineOrThrow());
        var arguments = new string[count];
        for (int i = 0; i < count; i++)
        {
            arguments[i] = reader.ReadLineOrThrow();
        }

        return new CompilerCall(projectFile, kind, targetFramework, isCSharp, arguments, index);
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

        var bytes = GetAssemblyBytes(mvid);
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

    internal byte[] GetContentBytes(string contentHash) =>
        ZipArchive.ReadAllBytes(GetContentEntryName(contentHash));

    internal Stream GetContentStream(string contentHash) =>
        ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));

    internal void CopyContentTo(string contentHash, Stream destination)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));
        stream.CopyTo(destination);
    }

    /// <summary>
    /// This gets a content <see cref="Stream"/> instance that is aware of the state
    /// lifetime. If the <see cref="CompilerLogState" /> is owned by this instance then 
    /// it's safe to expose streams into the underlying zip. Otherwise a copy is created
    /// to ensure it's safe to use after this zip is closed
    /// </summary>
    internal Stream GetStateAwareContentStream(string contentHash)
    {
        if (_ownsCompilerLogState)
        {
            return GetContentStream(contentHash);
        }

        var bytes = GetContentBytes(contentHash);
        return bytes.AsSimpleMemoryStream(writable: false);
    }

    internal SourceText GetSourceText(string contentHash, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded = false)
    {
        Stream? stream = null;
        try
        {
            if (canBeEmbedded)
            {
                // Zip streams don't have length so we have to go the byte[] route
                var bytes = GetContentBytes(contentHash);
                stream = bytes.AsSimpleMemoryStream();
            }
            else
            {
                stream = ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));
            }

            // TODO: need to expose the real API for how the compiler reads source files. 
            // move this comment to the rehydration code when we write it.
            return SourceText.From(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal byte[] GetAssemblyBytes(Guid mvid) =>
        ZipArchive.ReadAllBytes(GetAssemblyEntryName(mvid));

    internal Stream GetAssemblyStream(Guid mvid) =>
        ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));

    internal void CopyAssemblyBytes(Guid mvid, Stream destination)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        stream.CopyTo(destination);
    }

    public void Dispose()
    {
        if (ZipArchive is null)
        {
            return;
        }

        ZipArchive.Dispose();
        ZipArchive = null!;

        if (_ownsCompilerLogState)
        {
            CompilerLogState.Dispose();
        }
    }
}
