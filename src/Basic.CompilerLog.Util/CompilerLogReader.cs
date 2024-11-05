using Basic.CompilerLog.Util.Impl;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Microsoft.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

public sealed class CompilerLogReader : ICompilerCallReader, IBasicAnalyzerHostDataProvider
{
    private sealed class CompilerCallState(CompilerLogReader reader, int index)
    {
        internal CompilerLogReader Reader { get; } = reader;
        internal int Index { get; } = index;
        [ExcludeFromCodeCoverage]
        public override string ToString() => Index.ToString();
    }

    /// <summary>
    /// Stores the underlying archive this reader is using. Do not use directly. Instead 
    /// use <see cref="ZipArchive"/>  which will throw if the reader is disposed
    /// </summary>
    private ZipArchive _zipArchiveCore;

    private readonly Dictionary<Guid, MetadataReference> _refMap = new();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();
    private readonly Dictionary<int, CompilationInfoPack> _compilationInfoPackMap = new();
    private readonly Dictionary<int, CompilationDataPack> _compilationDataPackMap = new();

    /// <summary>
    /// This stores the map between an assembly MVID and the <see cref="CompilerCall"/> that 
    /// produced it. This is useful for building up items like a project reference map.
    /// </summary>
    private readonly Dictionary<Guid, int> _mvidToCompilerCallIndexMap = new();

    /// <summary>
    /// Is this reader responsible for disposing the <see cref="LogReaderState"/> instance
    /// </summary>
    public bool OwnsLogReaderState { get; }

    public BasicAnalyzerKind BasicAnalyzerKind { get; }
    public LogReaderState LogReaderState { get; }
    internal Metadata Metadata { get; }
    internal PathNormalizationUtil PathNormalizationUtil { get; }
    internal int Count => Metadata.Count;
    public int MetadataVersion => Metadata.MetadataVersion;
    public bool IsWindowsLog => Metadata.IsWindows;
    public bool IsDisposed => _zipArchiveCore is null;
    internal ZipArchive ZipArchive => !IsDisposed ? _zipArchiveCore : throw new ObjectDisposedException(nameof(CompilerLogReader));

    private CompilerLogReader(ZipArchive zipArchive, Metadata metadata, BasicAnalyzerKind? basicAnalyzerKind, LogReaderState? state)
    {
        _zipArchiveCore = zipArchive;
        BasicAnalyzerKind = basicAnalyzerKind ?? BasicAnalyzerHost.DefaultKind;
        OwnsLogReaderState = state is null;
        LogReaderState = state ?? new LogReaderState();
        Metadata = metadata;

        PathNormalizationUtil = (Metadata.IsWindows, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) switch
        {
            (true, true) => PathNormalizationUtil.Empty,
            (true, false) => PathNormalizationUtil.WindowsToUnix,
            (false, true) => PathNormalizationUtil.UnixToWindows,
            (false, false) => PathNormalizationUtil.Empty,
        };

        if (metadata.MetadataVersion == 2)
        {
            ReadAssemblyInfo();
        }
        else
        {
            ReadLogInfo();
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

        void ReadLogInfo()
        {
            using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(LogInfoFileName), ContentEncoding, leaveOpen: false);
            var hash = reader.ReadLine();
            var pack = GetContentPack<LogInfoPack>(hash!);
            foreach (var kvp in pack.MvidToReferenceInfoMap)
            {
                _mvidToRefInfoMap[kvp.Key] = (kvp.Value.FileName, new AssemblyName(kvp.Value.AssemblyName));
            }
            foreach (var tuple in pack.CompilerCallMvidList)
            {
                _mvidToCompilerCallIndexMap[tuple.Mvid] = tuple.CompilerCallIndex;
            }
        }
    }

    public static CompilerLogReader Create(
        Stream stream,
        BasicAnalyzerKind? basicAnalyzerKind,
        LogReaderState? state,
        bool leaveOpen)
    {
        try
        {
            var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            var metadata = ReadMetadata();
            return metadata.MetadataVersion switch {
                1 => throw new CompilerLogException("Version 1 compiler logs are no longer supported"),
                2 or 3 => new CompilerLogReader(zipArchive, metadata, basicAnalyzerKind, state),
                _ => throw new CompilerLogException($"Version {metadata.MetadataVersion} is higher than the max supported version {Metadata.LatestMetadataVersion}"),
            };

            Metadata ReadMetadata()
            {
                var entry = zipArchive.GetEntry(MetadataFileName) ?? throw GetInvalidCompilerLogFileException();
                using var reader = Polyfill.NewStreamReader(entry.Open(), ContentEncoding, leaveOpen: false);
                var metadata = Metadata.Read(reader);
                return metadata;
            }
        }
        catch (Exception ex)
        {
            if (!leaveOpen)
            {
                stream.Dispose();
            }

            // Happens when this is not a valid zip file
            if (ex is not CompilerLogException)
            {
                throw GetInvalidCompilerLogFileException();
            }

            throw;
        }

        static Exception GetInvalidCompilerLogFileException() => new CompilerLogException("Provided stream is not a compiler log file");
    }

    public static CompilerLogReader Create(
        Stream stream,
        BasicAnalyzerKind? basicAnalyzerKind = null,
        bool leaveOpen = true) =>
        Create(stream, basicAnalyzerKind, state: null, leaveOpen);

    public static CompilerLogReader Create(
        Stream stream,
        LogReaderState? state,
        bool leaveOpen = true) =>
        Create(stream, basicAnalyzerKind: null, state: state, leaveOpen);

    public static CompilerLogReader Create(
        string filePath,
        BasicAnalyzerKind? basicAnalyzerKind = null,
        LogReaderState? state = null)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return Create(stream, basicAnalyzerKind, state: state, leaveOpen: false);
    }

    private CompilerCall ReadCompilerCallCore(int index, CompilationInfoPack pack)
    {
        return new CompilerCall(
            NormalizePath(pack.ProjectFilePath),
            NormalizePath(pack.CompilerFilePath),
            pack.CompilerCallKind,
            pack.TargetFramework,
            pack.IsCSharp,
            new Lazy<IReadOnlyCollection<string>>(() => GetContentPack<string[]>(pack.CommandLineArgsHash)),
            new CompilerCallState(this, index));
    }

    public CompilerCallData ReadCompilerCallData(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var infoPack = GetOrReadCompilationInfoPack(index);
        var dataPack = GetOrReadCompilationDataPack(index);
        var tuple = ReadCompilerOptions(infoPack);
        return new CompilerCallData(
            compilerCall,
            assemblyFileName: dataPack.ValueMap["assemblyFileName"]!,
            outputDirectory: NormalizePath(dataPack.ValueMap["outputDirectory"]),
            tuple.ParseOptions,
            tuple.CompilationOptions,
            tuple.EmitOptions);
    }

    internal CompilationInfoPack GetOrReadCompilationInfoPack(int index)
    {
        if (!_compilationInfoPackMap.TryGetValue(index, out var info))
        {
            info = ReadCompilationInfo(index);
            _compilationInfoPackMap[index] = info;
        }

        return info;

        CompilationInfoPack ReadCompilationInfo(int index)
        {
            using var stream = ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index));
            return MessagePackSerializer.Deserialize<CompilationInfoPack>(stream);
        }
    }

    internal IEnumerable<RawContent> ReadAllRawContent(CompilerCall compilerCall, RawContentKind? kind = null) =>
        ReadAllRawContent(GetIndex(compilerCall), kind);

    internal IEnumerable<RawContent> ReadAllRawContent(int index, RawContentKind? kind = null)
    {
        var dataPack = GetOrReadCompilationDataPack(index);
        foreach (var tuple in dataPack.ContentList)
        {
            var currentKind = (RawContentKind)tuple.Item1;
            if (kind is not { } k || currentKind == k)
            {
                yield return new RawContent(tuple.Item2.FilePath, NormalizePath(tuple.Item2.FilePath), tuple.Item2.ContentHash, currentKind);
            }
        }
    }

    private CompilationDataPack GetOrReadCompilationDataPack(CompilerCall compilerCall) =>
        GetOrReadCompilationDataPack(GetIndex(compilerCall));

    private CompilationDataPack GetOrReadCompilationDataPack(int index)
    {
        if (!_compilationDataPackMap.TryGetValue(index, out var dataPack))
        {
            var infoPack = GetOrReadCompilationInfoPack(index);
            dataPack = GetContentPack<CompilationDataPack>(infoPack.CompilationDataPackHash);
            _compilationDataPackMap[index] = dataPack;
        }

        return dataPack;
    }

    public CompilerCall ReadCompilerCall(int index)
    {
        if (index < 0 || index >= Count)
            throw new ArgumentException("Invalid index", nameof(index));

        return ReadCompilerCallCore(index, GetOrReadCompilationInfoPack(index));
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

    public List<CompilerAssemblyData> ReadAllCompilerAssemblies()
    {
        var list = new List<(string CompilerFilePath, AssemblyName AssemblyName)>();
        var map = new Dictionary<string, (AssemblyName, string?)>(PathUtil.Comparer);
        for (int i = 0; i < Count; i++)
        {
            var pack = GetOrReadCompilationInfoPack(i);
            if (pack.CompilerFilePath is not null && 
                pack.CompilerAssemblyName is not null &&
                !map.ContainsKey(pack.CompilerFilePath))
            {
                var name = new AssemblyName(pack.CompilerAssemblyName);
                map[pack.CompilerFilePath] = (name, pack.CompilerCommitHash);
            }
        }

        return map
            .OrderBy(x => x.Key, PathUtil.Comparer)
            .Select(x => new CompilerAssemblyData(x.Key, x.Value.Item1, x.Value.Item2))
            .ToList();
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

    public SourceHashAlgorithm GetChecksumAlgorithm(CompilerCall compilerCall) =>
        GetOrReadCompilationDataPack(compilerCall).ChecksumAlgorithm;

    public CompilationData ReadCompilationData(int index) =>
        ReadCompilationData(ReadCompilerCall(index));

    public CompilationData ReadCompilationData(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var infoPack = GetOrReadCompilationInfoPack(index);
        var dataPack = GetOrReadCompilationDataPack(index);
        var (emitOptions, rawParseOptions, compilationOptions) = ReadCompilerOptions(infoPack);
        var referenceList = ReadMetadataReferences(dataPack.References);
        var resourceList = ReadResources(dataPack.Resources);
        var compilationName = dataPack.ValueMap["compilationName"];
        var assemblyFileName = dataPack.ValueMap["assemblyFileName"]!;
        var xmlFilePath = NormalizePath(dataPack.ValueMap["xmlFilePath"]);
        var hashAlgorithm = dataPack.ChecksumAlgorithm;
        var sourceTexts = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigs = new List<(SourceText SourceText, string Path)>();
        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();

        MemoryStream? win32ResourceStream = null;
        MemoryStream? sourceLinkStream = null;
        List<EmbeddedText>? embeddedTexts = null;

        foreach (var tuple in dataPack.ContentList)
        {
            var kind = (RawContentKind)tuple.Item1;
            var contentHash = tuple.Item2.ContentHash;
            var filePath = NormalizePath(tuple.Item2.FilePath);

            switch (kind)
            {
                case RawContentKind.SourceText:
                    sourceTexts.Add((GetSourceText(contentHash, hashAlgorithm), filePath));
                    break;
                case RawContentKind.GeneratedText:
                    // Nothing to do here as these are handled by the generation process.
                    break;
                case RawContentKind.AnalyzerConfig:
                    analyzerConfigs.Add((GetSourceText(contentHash, hashAlgorithm), filePath));
                    break;
                case RawContentKind.AdditionalText:
                    additionalTexts.Add(new BasicAdditionalTextFile(
                        filePath,
                        GetSourceText(contentHash, hashAlgorithm)));
                    break;
                case RawContentKind.CryptoKeyFile:
                    HandleCryptoKeyFile(contentHash);
                    break;
                case RawContentKind.SourceLink:
                    sourceLinkStream = GetContentBytes(contentHash).AsSimpleMemoryStream(writable: false);
                    break;
                case RawContentKind.Win32Resource:
                    win32ResourceStream = GetContentBytes(contentHash).AsSimpleMemoryStream(writable: false);
                    break;
                case RawContentKind.Embed:
                {
                    if (embeddedTexts is null)
                    {
                        embeddedTexts = new List<EmbeddedText>();
                    }

                    var sourceText = GetSourceText(contentHash, hashAlgorithm, canBeEmbedded: true);
                    var embeddedText = EmbeddedText.FromSource(filePath, sourceText);
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
            assemblyFileName,
            xmlFilePath,
            win32ResourceStream: win32ResourceStream,
            sourceLinkStream: sourceLinkStream,
            resources: resourceList,
            embeddedTexts: embeddedTexts);

        var basicAnalyzerHost = CreateBasicAnalyzerHost(index);

        return compilerCall.IsCSharp
            ? CreateCSharp()
            : CreateVisualBasic();

        void HandleCryptoKeyFile(string contentHash)
        {
            var dir = Path.Combine(LogReaderState.CryptoKeyFileDirectory, GetIndex(compilerCall).ToString());
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"{contentHash}.snk");
            File.WriteAllBytes(filePath, GetContentBytes(contentHash));
            compilationOptions = compilationOptions.WithCryptoKeyFile(filePath);
        }

        CSharpCompilationData CreateCSharp()
        {
            var csharpOptions = (CSharpCompilationOptions)compilationOptions;
            var parseOptions = (CSharpParseOptions)rawParseOptions;
            var syntaxTrees = RoslynUtil.ParseAllCSharp(sourceTexts, parseOptions);

            return RoslynUtil.CreateCSharpCompilationData(
                compilerCall,
                compilationName,
                (CSharpParseOptions)rawParseOptions,
                (CSharpCompilationOptions)compilationOptions,
                sourceTexts,
                referenceList,
                analyzerConfigs,
                additionalTexts.ToImmutableArray(),
                emitOptions,
                emitData,
                basicAnalyzerHost,
                PathNormalizationUtil);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            var basicOptions = (VisualBasicCompilationOptions)compilationOptions;
            var parseOptions = (VisualBasicParseOptions)rawParseOptions;
            var syntaxTrees = RoslynUtil.ParseAllVisualBasic(sourceTexts, parseOptions);

            return RoslynUtil.CreateVisualBasicCompilationData(
                compilerCall,
                compilationName,
                parseOptions,
                basicOptions,
                sourceTexts,
                referenceList,
                analyzerConfigs,
                additionalTexts.ToImmutableArray(),
                emitOptions,
                emitData,
                basicAnalyzerHost,
                PathNormalizationUtil);
        }

        List<MetadataReference> ReadMetadataReferences(List<ReferencePack> referencePacks)
        {
            var list = new List<MetadataReference>(capacity: referencePacks.Count);
            foreach (var referencePack in referencePacks)
            {
                var mdRef = ReadMetadataReference(referencePack.Mvid).With(referencePack.Aliases, referencePack.EmbedInteropTypes);
                list.Add(mdRef);
            }
            return list;
        }

        List<ResourceDescription>? ReadResources(List<ResourcePack> resourcePacks)
        {
            if (resourcePacks.Count == 0)
            {
                return null;
            }

            var list = new List<ResourceDescription>(capacity: resourcePacks.Count);
            foreach (var resourcePack in resourcePacks)
            {
                var desc = ReadResourceDescription(resourcePack.ContentHash, resourcePack.FileName, resourcePack.Name, resourcePack.IsPublic);
                list.Add(desc);
            }

            return list;
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

    internal int GetIndex(CompilerCall compilerCall) =>
        GetIndexCore(compilerCall.OwnerState);

    internal int GetIndexCore(object? ownerState)
    {
        if (ownerState is CompilerCallState state &&
            state.Index >= 0 &&
            state.Index < Count &&
            object.ReferenceEquals(this, state.Reader))
        {
            return state.Index;
        }

        throw new ArgumentException($"The provided {nameof(CompilerCall)} is not from this instance");
    }

    public List<ReferenceData> ReadAllReferenceData(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var dataPack = GetOrReadCompilationDataPack(index);
        var list = new List<ReferenceData>(dataPack.References.Count);
        foreach (var referencePack in dataPack.References)
        {
            var filePath = referencePack.FilePath is string fp
                ? NormalizePath(fp)
                : PathNormalizationUtil.RootFileName(GetMetadataReferenceFileName(referencePack.Mvid));
            var assemblyIdentityData = new AssemblyIdentityData(
                referencePack.Mvid,
                referencePack.AssemblyName,
                referencePack.AssemblyInformationalVersion);

            var data = new ReferenceData(assemblyIdentityData, filePath, referencePack.Aliases, referencePack.EmbedInteropTypes);
            list.Add(data);
        }

        return list;
    }

    public List<AnalyzerData> ReadAllAnalyzerData(CompilerCall compilerCall) =>
        ReadAllAnalyzerData(GetIndex(compilerCall));

    public List<AnalyzerData> ReadAllAnalyzerData(int index)
    {
        var dataPack = GetOrReadCompilationDataPack(index);
        var list = new List<AnalyzerData>(capacity: dataPack.Analyzers.Count);
        foreach (var analyzerPack in dataPack.Analyzers)
        {
            var assemblyIdentityData = new AssemblyIdentityData(
                analyzerPack.Mvid,
                analyzerPack.AssemblyName,
                analyzerPack.AssemblyInformationalVersion);
            list.Add(new AnalyzerData(assemblyIdentityData, NormalizePath(analyzerPack.FilePath)));
        }
        return list;
    }

    public List<ResourceData> ReadAllResourceData(CompilerCall compilerCall) =>
        ReadAllResourceData(GetIndex(compilerCall));

    public List<ResourceData> ReadAllResourceData(int index)
    {
        var dataPack = GetOrReadCompilationDataPack(index);
        var list = new List<ResourceData>(dataPack.Resources.Count);
        foreach (var pack in dataPack.Resources)
        {
            list.Add(new ResourceData(pack.ContentHash, pack.FileName, pack.Name, pack.IsPublic));
        }

        return list;
    }

    public List<SourceTextData> ReadAllSourceTextData(CompilerCall compilerCall)
    {
        // TODO: think about if this method is efficent or not
        var list = new List<SourceTextData>();
        var dataPack = GetOrReadCompilationDataPack(compilerCall);
        foreach (var rawContent in ReadAllRawContent(GetIndex(compilerCall)))
        {
            var kind = rawContent.Kind switch
            {
                RawContentKind.SourceText => SourceTextKind.SourceCode,
                RawContentKind.AnalyzerConfig => SourceTextKind.AnalyzerConfig,
                RawContentKind.AdditionalText => SourceTextKind.AdditionalText,
                _ => (SourceTextKind?)null,
            };

            if (kind is { } k)
            {
                var data = new SourceTextData(compilerCall, rawContent.NormalizedFilePath, dataPack.ChecksumAlgorithm, k);
                list.Add(data);
            }
        }

        return list;
    }

    public SourceText ReadSourceText(SourceTextData sourceTextData)
    {
        // TODO: this method is very inefficent, think about putting the checksum into the sourcetext data so it can
        // be read faster
        var index = GetIndex(sourceTextData.CompilerCall);
        foreach (var rawContent in ReadAllRawContent(index))
        {
            if (PathUtil.Comparer.Equals(rawContent.NormalizedFilePath, sourceTextData.FilePath))
            {
                return GetSourceText(rawContent.ContentHash, sourceTextData.ChecksumAlgorithm, canBeEmbedded: false);
            }
        }

        throw new InvalidOperationException();
    }

    /// <summary>
    /// Are all the generated files contained in the data?
    /// </summary>
    /// <remarks>
    /// Older versions of compiler log aren't guaranteed to have HasGeneratedFilesInPdb set
    /// </remarks>
    internal bool HasAllGeneratedFileContent(CompilerCall compilerCall) =>
        HasAllGeneratedFileContent(GetOrReadCompilationDataPack(compilerCall));

    private bool HasAllGeneratedFileContent(CompilationDataPack dataPack) =>
        dataPack.HasGeneratedFilesInPdb is true
            ? dataPack.IncludesGeneratedText
            : dataPack.IncludesGeneratedText;

    public BasicAnalyzerHost CreateBasicAnalyzerHost(CompilerCall compilerCall) =>
        CreateBasicAnalyzerHost(GetIndex(compilerCall));

    internal BasicAnalyzerHost CreateBasicAnalyzerHost(int index)
    {
        var dataPack = GetOrReadCompilationDataPack(index);

        return LogReaderState.GetOrCreate(
            BasicAnalyzerKind,
            ReadAllAnalyzerData(index),
            (kind, analyzers) => kind switch
            {
                BasicAnalyzerKind.OnDisk => new BasicAnalyzerHostOnDisk(this, analyzers),
                BasicAnalyzerKind.InMemory => new BasicAnalyzerHostInMemory(this, analyzers),
                BasicAnalyzerKind.None => HasAllGeneratedFileContent(dataPack)
                    ? new BasicAnalyzerHostNone(ReadGeneratedSourceTexts())
                    : new BasicAnalyzerHostNone("Generated files not available when compiler log created"),
                _ => throw new InvalidOperationException()
            });

        ImmutableArray<(SourceText SourceText, string FilePath)> ReadGeneratedSourceTexts()
        {
            var builder = ImmutableArray.CreateBuilder<(SourceText SourceText, string FilePath)>();
            foreach (var rawContent in ReadAllRawContent(index, RawContentKind.GeneratedText))
            {
                builder.Add((GetSourceText(rawContent.ContentHash, dataPack.ChecksumAlgorithm), rawContent.NormalizedFilePath));
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

    /// <summary>
    /// Reads a <see cref="MetadataReference"/> with the given <paramref name="mvid" />. This
    /// does not include extra metadata properties like alias, embedded interop types, etc ...
    /// </summary>
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

    public MetadataReference ReadMetadataReference(ReferenceData referenceData)
    {
        var mdRef = ReadMetadataReference(referenceData.Mvid);
        return mdRef.With(referenceData.Aliases, referenceData.EmbedInteropTypes);
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

    internal ResourceDescription ReadResourceDescription(ResourceData pack) =>
        ReadResourceDescription(pack.ContentHash, pack.FileName, pack.Name, pack.IsPublic);

    private ResourceDescription ReadResourceDescription(string contentHash, string? fileName, string name, bool isPublic)
    {
        var stream = GetContentBytes(contentHash).AsSimpleMemoryStream(writable: false);
        var dataProvider = () => stream;
        return string.IsNullOrEmpty(fileName)
            ? new ResourceDescription(name, dataProvider, isPublic)
            : new ResourceDescription(name, fileName, dataProvider, isPublic);
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

    public void CopyAssemblyBytes(AssemblyData assemblyData, Stream destination) =>
        CopyAssemblyBytes(assemblyData.Mvid, destination);

    internal void CopyAssemblyBytes(Guid mvid, Stream destination)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        stream.CopyTo(destination);
    }

    public bool TryGetCompilerCallIndex(Guid mvid, out int compilerCallIndex) =>
        _mvidToCompilerCallIndexMap.TryGetValue(mvid, out compilerCallIndex);

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

        if (OwnsLogReaderState)
        {
            LogReaderState.Dispose();
        }
    }

    void IBasicAnalyzerHostDataProvider.CopyAssemblyBytes(AssemblyData data, Stream stream) => CopyAssemblyBytes(data.Mvid, stream);
    byte[] IBasicAnalyzerHostDataProvider.GetAssemblyBytes(AssemblyData data) => GetAssemblyBytes(data.Mvid);
}
