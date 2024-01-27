using Basic.CompilerLog.Util.Impl;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

public sealed class CompilerLogReader : IDisposable
{
    public static int LatestMetadataVersion => Metadata.LatestMetadataVersion;

    /// <summary>
    /// Stores the underlying archive this reader is using. Do not use directly. Instead 
    /// use <see cref="ZipArchive"/>  which will throw if the reader is disposed
    /// </summary>
    private ZipArchive _zipArchiveCore;

    private readonly Dictionary<Guid, MetadataReference> _refMap = new();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();
    private readonly Dictionary<string, BasicAnalyzerHost> _analyzersMap = new();
    private readonly Dictionary<int, CompilationInfoPack> _compilationInfoMap = new();

    /// <summary>
    /// Is this reader responsible for disposing the <see cref="CompilerLogState"/> instance
    /// </summary>
    public bool OwnsCompilerLogState { get; }

    public BasicAnalyzerHostOptions BasicAnalyzerHostOptions { get; }
    internal CompilerLogState CompilerLogState { get; }
    internal Metadata Metadata { get; }
    internal PathNormalizationUtil PathNormalizationUtil { get; }
    internal int Count => Metadata.Count;
    public int MetadataVersion => Metadata.MetadataVersion;
    public bool IsWindowsLog => Metadata.IsWindows;
    public bool IsDisposed => _zipArchiveCore is null;
    internal ZipArchive ZipArchive => !IsDisposed ? _zipArchiveCore : throw new ObjectDisposedException(nameof(CompilerLogReader));

    private CompilerLogReader(ZipArchive zipArchive, Metadata metadata, BasicAnalyzerHostOptions basicAnalyzersOptions, CompilerLogState? state)
    {
        _zipArchiveCore = zipArchive;
        OwnsCompilerLogState = state is null;
        CompilerLogState = state ?? new CompilerLogState();
        BasicAnalyzerHostOptions = basicAnalyzersOptions;
        Metadata = metadata;
        ReadAssemblyInfo();

        PathNormalizationUtil = (Metadata.IsWindows, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) switch
        {
            (true, true) => PathNormalizationUtil.Empty,
            (true, false) => PathNormalizationUtil.WindowsToUnix,
            (false, true) => PathNormalizationUtil.UnixToWindows,
            (false, false) => PathNormalizationUtil.Empty,
        };

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
        options ??= BasicAnalyzerHostOptions.Default;
        try
        {
            var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            var metadata = ReadMetadata();
            return metadata.MetadataVersion switch {
                1 => throw new CompilerLogException("Version 1 compiler logs are no longer supported"),
                2 => new CompilerLogReader(zipArchive, metadata, options, state),
                _ => throw GetInvalidCompilerLogFileException(),
            };

            Metadata ReadMetadata()
            {
                var entry = zipArchive.GetEntry(MetadataFileName) ?? throw GetInvalidCompilerLogFileException();
                using var reader = Polyfill.NewStreamReader(entry.Open(), ContentEncoding, leaveOpen: false);
                var metadata = Metadata.Read(reader);
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

    private CompilationInfoPack ReadCompilationInfo(int index)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index));
        return MessagePackSerializer.Deserialize<CompilationInfoPack>(stream);
    }

    private CompilerCall ReadCompilerCallCore(int index, CompilationInfoPack pack)
    {
        return new CompilerCall(
            NormalizePath(pack.ProjectFilePath),
            pack.CompilerCallKind,
            pack.TargetFramework,
            pack.IsCSharp,
            new Lazy<string[]>(() => GetContentPack<string[]>(pack.CommandLineArgsHash)),
            index);
    }

    private RawCompilationData ReadRawCompilationDataCore(int index, CompilationInfoPack pack)
    {
        var dataPack = GetContentPack<CompilationDataPack>(pack.CompilationDataPackHash);

        var references = dataPack
            .References
            .Select(x => new RawReferenceData(x.Mvid, x.Aliases, x.EmbedInteropTypes))
            .ToList();
        var analyzers = dataPack
            .Analyzers
            .Select(x => new RawAnalyzerData(x.Mvid, NormalizePath(x.FilePath)))
            .ToList();
        var contents = dataPack
            .ContentList
            .Select(x => new RawContent(NormalizePath(x.Item2.FilePath), x.Item2.ContentHash, (RawContentKind)x.Item1))
            .ToList();
        var resources = dataPack
            .Resources
            .Select(x => new RawResourceData(x.Name, x.FileName, x.IsPublic, x.ContentHash))
            .ToList();

        return new RawCompilationData(
            index,
            compilationName: dataPack.ValueMap["compilationName"],
            assemblyFileName: dataPack.ValueMap["assemblyFileName"]!,
            xmlFilePath: NormalizePath(dataPack.ValueMap["xmlFilePath"]),
            outputDirectory: NormalizePath(dataPack.ValueMap["outputDirectory"]),
            dataPack.ChecksumAlgorithm,
            references,
            analyzers,
            contents,
            resources,
            pack.IsCSharp,
            dataPack.IncludesGeneratedText);
    }

    private CompilationInfoPack GetOrReadCompilationInfo(int index)
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
        if (index < 0 || index >= Count)
            throw new ArgumentException("Invalid index", nameof(index));

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
        var pack = GetOrReadCompilationInfo(index);
        return ReadCompilerOptions(pack);
    }

    private (EmitOptions EmitOptions, ParseOptions ParseOptions, CompilationOptions CompilationOptions) ReadCompilerOptions(CompilationInfoPack pack)
    {
        var emitOptions = MessagePackUtil.CreateEmitOptions(GetContentPack<EmitOptionsPack>(pack.EmitOptionsHash));
        ParseOptions parseOptions;
        CompilationOptions compilationOptions;
        if (pack.IsCSharp)
        {
            var parseTuple = GetContentPack<(ParseOptionsPack, CSharpParseOptionsPack)>(pack.ParseOptionsHash);
            parseOptions = MessagePackUtil.CreateCSharpParseOptions(parseTuple.Item1, parseTuple.Item2);

            var optionsTuple = GetContentPack<(CompilationOptionsPack, CSharpCompilationOptionsPack)>(pack.CompilationOptionsHash);
            compilationOptions = MessagePackUtil.CreateCSharpCompilationOptions(optionsTuple.Item1, optionsTuple.Item2);
        }
        else
        {
            var parseTuple = GetContentPack<(ParseOptionsPack, VisualBasicParseOptionsPack)>(pack.ParseOptionsHash);
            parseOptions = MessagePackUtil.CreateVisualBasicParseOptions(parseTuple.Item1, parseTuple.Item2);

            var optionsTuple = GetContentPack<(CompilationOptionsPack, VisualBasicCompilationOptionsPack, ParseOptionsPack, VisualBasicParseOptionsPack)>(pack.CompilationOptionsHash);
            compilationOptions = MessagePackUtil.CreateVisualBasicCompilationOptions(optionsTuple.Item1, optionsTuple.Item2, optionsTuple.Item3, optionsTuple.Item4);
        }

        return (emitOptions, parseOptions, compilationOptions);
    }

    public CompilationData ReadCompilationData(int index) =>
        ReadCompilationData(ReadCompilerCall(index));

    public CompilationData ReadCompilationData(CompilerCall compilerCall)
    {
        var pack = GetOrReadCompilationInfo(GetIndex(compilerCall));
        var rawCompilationData = ReadRawCompilationData(compilerCall);
        var referenceList = RenameMetadataReferences(rawCompilationData.References);
        var (emitOptions, rawParseOptions, compilationOptions) = ReadCompilerOptions(pack);

        var hashAlgorithm = rawCompilationData.ChecksumAlgorithm;
        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var generatedTextList = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigList = new List<(SourceText SourceText, string Path)>();
        var additionalTextList = new List<AdditionalText>();

        Stream? win32ResourceStream = null;
        Stream? sourceLinkStream = null;
        List<ResourceDescription>? resources = rawCompilationData.Resources.Count == 0
            ? null
            : rawCompilationData.Resources.Select(x => ReadResourceDescription(x)).ToList();
        List<EmbeddedText>? embeddedTexts = null;

        foreach (var rawContent in rawCompilationData.Contents)
        {
            switch (rawContent.Kind)
            {
                case RawContentKind.SourceText:
                    sourceTextList.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    break;
                case RawContentKind.GeneratedText:
                    if (BasicAnalyzerHostOptions.ResolvedKind == BasicAnalyzerKind.None)
                    {
                        generatedTextList.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    }
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
                    sourceLinkStream = GetContentBytes(rawContent.ContentHash).AsSimpleMemoryStream(writable: false);
                    break;
                case RawContentKind.Win32Resource:
                    win32ResourceStream = GetContentBytes(rawContent.ContentHash).AsSimpleMemoryStream(writable: false);
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

        // Generated source code should appear last to match the compiler behavior.
        sourceTextList.AddRange(generatedTextList);

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
        if (compilerCall.Index is int i && i >= 0 && i < Count)
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

    internal MetadataReference ReadMetadataReference(Guid mvid)
    {
        if (_refMap.TryGetValue(mvid, out var metadataReference))
        {
            return metadataReference;
        }

        var bytes = GetAssemblyBytes(mvid);
        var tuple = _mvidToRefInfoMap[mvid];
        metadataReference = MetadataReference.CreateFromStream(new MemoryStream(bytes), filePath: tuple.FileName);
        _refMap.Add(mvid, metadataReference);
        return metadataReference;
    }

    internal MetadataReference RenameMetadataReference(in RawReferenceData data)
    {
        var reference = ReadMetadataReference(data.Mvid);
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

    internal List<MetadataReference> RenameMetadataReferences(List<RawReferenceData> referenceDataList)
    {
        var list = new List<MetadataReference>(capacity: referenceDataList.Count);
        foreach (var referenceData in referenceDataList)
        {
            list.Add(RenameMetadataReference(referenceData));
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

    internal ResourceDescription ReadResourceDescription(RawResourceData data)
    {
        var stream = GetContentBytes(data.ContentHash).AsSimpleMemoryStream(writable: false);
        var dataProvider = () => stream;
        return string.IsNullOrEmpty(data.FileName)
            ? new ResourceDescription(data.Name, dataProvider, data.IsPublic)
            : new ResourceDescription(data.Name, data.FileName, dataProvider, data.IsPublic);
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

    [return: NotNullIfNotNull("path")]
    private string? NormalizePath(string? path) => PathNormalizationUtil.NormalizePath(path);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        ZipArchive.Dispose();
        _zipArchiveCore = null!;

        if (OwnsCompilerLogState)
        {
            CompilerLogState.Dispose();
        }
    }
}
