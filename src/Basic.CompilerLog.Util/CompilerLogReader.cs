using Basic.CompilerLog.Util.Impl;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

public abstract class CompilerLogReader : IDisposable
{
    public static int LatestMetadataVersion => Metadata.LatestMetadataVersion;

    private readonly Dictionary<Guid, MetadataReference> _refMap = new();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();
    private readonly Dictionary<string, BasicAnalyzerHost> _analyzersMap = new();
    private readonly bool _ownsCompilerLogState;
    private readonly Dictionary<int, object> _compilationInfoMap = new();

    public BasicAnalyzerHostOptions BasicAnalyzerHostOptions { get; }
    internal CompilerLogState CompilerLogState { get; }
    internal ZipArchive ZipArchive { get; private set; }
    internal Metadata Metadata { get; }
    internal int Count => Metadata.Count;
    public int MetadataVersion => Metadata.MetadataVersion;

    internal CompilerLogReader(ZipArchive zipArchive, Metadata metadata, BasicAnalyzerHostOptions? basicAnalyzersOptions, CompilerLogState? state)
    {
        ZipArchive = zipArchive;
        CompilerLogState = state ?? new CompilerLogState();
        _ownsCompilerLogState = state is null;
        BasicAnalyzerHostOptions = basicAnalyzersOptions ?? BasicAnalyzerHostOptions.Default;
        Metadata = metadata;
        ReadAssemblyInfo();

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
    }

    public static CompilerLogReader Create(
        Stream stream,
        bool leaveOpen = false,
        BasicAnalyzerHostOptions? options = null,
        CompilerLogState? state = null)
    {
        try
        {
            var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            var metadata = ReadMetadata();
            return metadata.MetadataVersion switch {
                1 => new CompilerLogReaderVersion1(zipArchive, metadata, options, state),
                2 => new CompilerLogReaderVersion2(zipArchive, metadata, options, state),
                _ => throw GetInvalidCompilerLogFileException(),
            };

            Metadata ReadMetadata()
            {
                var entry = zipArchive.GetEntry(MetadataFileName) ?? throw GetInvalidCompilerLogFileException();
                using var reader = Polyfill.NewStreamReader(entry.Open(), ContentEncoding, leaveOpen: false);
                var metadata = Metadata.Read(reader);

                /*
                if (metadata.IsWindows is { } isWindows && isWindows != RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var produced = GetName(isWindows);
                    var current = GetName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
                    throw new Exception("Compiler log created on {produced} cannot be consumed on {current}");

                    string GetName(bool isWindows) => isWindows ? "Windows" : "Unix";
                }
                */

                return metadata;
            }
        }
        catch (InvalidDataException)
        {
            // Happens when this is not a valid zip file
            throw GetInvalidCompilerLogFileException();
        }

        static Exception GetInvalidCompilerLogFileException() => new ArgumentException("Provided stream is not a compiler log file");
    } 

    public static CompilerLogReader Create(
        string filePath,
        BasicAnalyzerHostOptions? options = null,
        CompilerLogState? state = null)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return Create(stream, leaveOpen: false, options, state);
    }

    private protected abstract object ReadCompilationInfo(int index);

    private protected abstract CompilerCall ReadCompilerCallCore(int index, object rawInfo);

    private protected abstract RawCompilationData ReadRawCompilationDataCore(int index, object rawInfo);

    private protected abstract (EmitOptions, ParseOptions, CompilationOptions) ReadCompilerOptionsCore(int index, object rawInfo);

    private object GetOrReadCompilationInfo(int index)
    {
        if (!_compilationInfoMap.TryGetValue(index, out var info))
        {
            info = ReadCompilationInfo(index);
            _compilationInfoMap[index] = info;
        }

        return info;
    }

    public CompilerCall ReadCompilerCall(int index)
    {
        if (index >= Count)
            throw new InvalidOperationException();

        return ReadCompilerCallCore(index, GetOrReadCompilationInfo(index));
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

    public (EmitOptions EmitOptions, ParseOptions ParseOptions, CompilationOptions CompilationOptions) ReadCompilerOptions(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var info = GetOrReadCompilationInfo(index);
        return ReadCompilerOptionsCore(index, info);
    }

    public CompilationData ReadCompilationData(int index) =>
        ReadCompilationData(ReadCompilerCall(index));

    public CompilationData ReadCompilationData(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var info = GetOrReadCompilationInfo(index);
        var rawCompilationData = ReadRawCompilationData(compilerCall);
        var referenceList = GetMetadataReferences(rawCompilationData.References);
        var (emitOptions, rawParseOptions, compilationOptions) = ReadCompilerOptionsCore(index, info);

        var hashAlgorithm = rawCompilationData.ChecksumAlgorithm;
        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigList = new List<(SourceText SourceText, string Path)>();
        var additionalTextList = new List<AdditionalText>();

        Stream? win32ResourceStream = null;
        Stream? sourceLinkStream = null;
        List<ResourceDescription>? resources = rawCompilationData.Resources.Count == 0
            ? null
            : rawCompilationData.Resources.Select(x => x.ResourceDescription).ToList();
        List<EmbeddedText>? embeddedTexts = null;

        foreach (var rawContent in rawCompilationData.Contents)
        {
            switch (rawContent.Kind)
            {
                case RawContentKind.SourceText:
                    sourceTextList.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    break;
                case RawContentKind.GeneratedText:
                    // Handled when creating the analyzer host
                    break;
                case RawContentKind.AnalyzerConfig:
                    analyzerConfigList.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    break;
                case RawContentKind.AdditionalText:
                    additionalTextList.Add(new BasicAdditionalTextFile(
                        rawContent.FilePath,
                        GetSourceText(rawContent.ContentHash, hashAlgorithm)));
                    break;
                case RawContentKind.CryptoKeyFile:
                    HandleCryptoKeyFile(rawContent.ContentHash);
                    break;
                case RawContentKind.SourceLink:
                    sourceLinkStream = GetStateAwareContentStream(rawContent.ContentHash);
                    break;
                case RawContentKind.Win32Resource:
                    win32ResourceStream = GetStateAwareContentStream(rawContent.ContentHash);
                    break;
                case RawContentKind.Embed:
                {
                    if (embeddedTexts is null)
                    {
                        embeddedTexts = new List<EmbeddedText>();
                    }

                    var sourceText = GetSourceText(rawContent.ContentHash, hashAlgorithm, canBeEmbedded: true);
                    var embeddedText = EmbeddedText.FromSource(rawContent.FilePath, sourceText);
                    embeddedTexts.Add(embeddedText);
                    break;
                }

                // not exposed as #line embeds don't matter for most API usages, it's only used in 
                // command line compiles
                case RawContentKind.EmbedLine:
                    break;

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
            rawCompilationData.AssemblyFileName,
            rawCompilationData.XmlFilePath,
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
            var csharpOptions = (CSharpCompilationOptions)compilationOptions;
            var parseOptions = (CSharpParseOptions)rawParseOptions;
            var syntaxTrees = RoslynUtil.ParseAllCSharp(sourceTextList, parseOptions);
            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextList);

            csharpOptions = csharpOptions
                .WithSyntaxTreeOptionsProvider(syntaxProvider)
                .WithStrongNameProvider(new DesktopStrongNameProvider());

            var compilation = CSharpCompilation.Create(
                rawCompilationData.CompilationName,
                syntaxTrees,
                referenceList,
                csharpOptions);

            return new CSharpCompilationData(
                compilerCall,
                compilation,
                parseOptions,
                emitOptions,
                emitData,
                additionalTextList.ToImmutableArray(),
                ReadAnalyzers(rawCompilationData),
                analyzerProvider);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            var basicOptions = (VisualBasicCompilationOptions)compilationOptions;
            var parseOptions = (VisualBasicParseOptions)rawParseOptions;
            var syntaxTrees = RoslynUtil.ParseAllVisualBasic(sourceTextList, parseOptions);
            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextList);

            basicOptions = basicOptions
                .WithSyntaxTreeOptionsProvider(syntaxProvider)
                .WithStrongNameProvider(new DesktopStrongNameProvider());

            var compilation = VisualBasicCompilation.Create(
                rawCompilationData.CompilationName,
                syntaxTrees,
                referenceList,
                basicOptions);

            return new VisualBasicCompilationData(
                compilerCall,
                compilation,
                parseOptions,
                emitOptions,
                emitData,
                additionalTextList.ToImmutableArray(),
                ReadAnalyzers(rawCompilationData),
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
        var info = GetOrReadCompilationInfo(index);
        var compilerCall = ReadCompilerCallCore(index, info);
        var rawCompilationData = ReadRawCompilationDataCore(index, info);
        return (compilerCall, rawCompilationData);
    }

    internal RawCompilationData ReadRawCompilationData(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var info = GetOrReadCompilationInfo(index);
        return ReadRawCompilationDataCore(index, info);
    }

    internal int GetIndex(CompilerCall compilerCall)
    {
        if (compilerCall.Index is int i && i < Count)
        {
            return i;
        }

        throw new ArgumentException($"Invalid index");
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

    internal BasicAnalyzerHost ReadAnalyzers(RawCompilationData rawCompilationData)
    {
        var analyzers = rawCompilationData.Analyzers;
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
            BasicAnalyzerKind.OnDisk => new BasicAnalyzerHostOnDisk(this, analyzers, BasicAnalyzerHostOptions),
            BasicAnalyzerKind.InMemory => new BasicAnalyzerHostInMemory(this, analyzers, BasicAnalyzerHostOptions),
            BasicAnalyzerKind.None => new BasicAnalyzerHostNone(rawCompilationData.ReadGeneratedFiles, ReadGeneratedSourceTexts(), BasicAnalyzerHostOptions),
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

            if (BasicAnalyzerHostOptions.ResolvedKind == BasicAnalyzerKind.None)
            {
                foreach (var tuple in rawCompilationData.Contents.Where(static x => x.Kind == RawContentKind.GeneratedText))
                {
                    builder.AppendLine(tuple.ContentHash);
                }

                builder.AppendLine(rawCompilationData.ReadGeneratedFiles.ToString());
            }

            return builder.ToString();
        }

        ImmutableArray<(SourceText SourceText, string Path)> ReadGeneratedSourceTexts()
        {
            var builder = ImmutableArray.CreateBuilder<(SourceText SourceText, string Path)>();
            foreach (var tuple in rawCompilationData.Contents.Where(static x => x.Kind == RawContentKind.GeneratedText))
            {
                builder.Add((GetSourceText(tuple.ContentHash, rawCompilationData.ChecksumAlgorithm), tuple.FilePath));
            }
            return builder.ToImmutableArray();
        }
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

    internal T GetContentPack<T>(string contentHash)
    {
        var stream = GetContentStream(contentHash);
        return MessagePackSerializer.Deserialize<T>(stream, SerializerOptions);
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

            return RoslynUtil.GetSourceText(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);
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
