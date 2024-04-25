using Basic.CompilerLog.Util.Impl;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
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

public sealed class CompilerLogReader : IDisposable, ICompilerCallReader, IBasicAnalyzerHostDataProvider
{
    private readonly struct CompilerCallState(CompilerLogReader reader, int index)
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
    private readonly Dictionary<int, CompilationInfoPack> _compilationInfoMap = new();

    /// <summary>
    /// Is this reader responsible for disposing the <see cref="CompilerLogState"/> instance
    /// </summary>
    public bool OwnsCompilerLogState { get; }

    public BasicAnalyzerKind BasicAnalyzerKind { get; }
    public CompilerLogState CompilerLogState { get; }
    internal Metadata Metadata { get; }
    internal PathNormalizationUtil PathNormalizationUtil { get; }
    internal int Count => Metadata.Count;
    public int MetadataVersion => Metadata.MetadataVersion;
    public bool IsWindowsLog => Metadata.IsWindows;
    public bool IsDisposed => _zipArchiveCore is null;
    internal ZipArchive ZipArchive => !IsDisposed ? _zipArchiveCore : throw new ObjectDisposedException(nameof(CompilerLogReader));

    private CompilerLogReader(ZipArchive zipArchive, Metadata metadata, BasicAnalyzerKind? basicAnalyzerKind, CompilerLogState? state)
    {
        _zipArchiveCore = zipArchive;
        BasicAnalyzerKind = basicAnalyzerKind ?? BasicAnalyzerHost.DefaultKind;
        OwnsCompilerLogState = state is null;
        CompilerLogState = state ?? new CompilerLogState();
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
        BasicAnalyzerKind? basicAnalyzerKind,
        CompilerLogState? state,
        bool leaveOpen)
    {
        try
        {
            var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            var metadata = ReadMetadata();
            return metadata.MetadataVersion switch {
                1 => throw new CompilerLogException("Version 1 compiler logs are no longer supported"),
                2 => new CompilerLogReader(zipArchive, metadata, basicAnalyzerKind, state),
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
        catch (InvalidDataException)
        {
            // Happens when this is not a valid zip file
            throw GetInvalidCompilerLogFileException();
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
        CompilerLogState? state,
        bool leaveOpen = true) =>
        Create(stream, basicAnalyzerKind: null, state: state, leaveOpen);

    public static CompilerLogReader Create(
        string filePath,
        BasicAnalyzerKind? basicAnalyzerKind = null,
        CompilerLogState? state = null)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return Create(stream, basicAnalyzerKind, state: state, leaveOpen: false);
    }

    private CompilationInfoPack ReadCompilationInfo(int index)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index));
        return MessagePackSerializer.Deserialize<CompilationInfoPack>(stream);
    }

    private CompilerCall ReadCompilerCallCore(int index, CompilationInfoPack pack)
    {
        return new CompilerCall(
            NormalizePath(pack.CompilerFilePath),
            NormalizePath(pack.ProjectFilePath),
            pack.CompilerCallKind,
            pack.TargetFramework,
            pack.IsCSharp,
            new Lazy<IReadOnlyCollection<string>>(() => GetContentPack<string[]>(pack.CommandLineArgsHash)),
            new CompilerCallState(this, index));
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

    public List<(string CompilerFilePath, AssemblyName AssemblyName)> ReadAllCompilerAssemblies()
    {
        var list = new List<(string CompilerFilePath, AssemblyName AssemblyName)>();
        var map = new Dictionary<string, AssemblyName>(PathUtil.Comparer);
        for (int i = 0; i < Count; i++)
        {
            var pack = GetOrReadCompilationInfo(i);
            if (pack.CompilerFilePath is not null && 
                pack.CompilerAssemblyName is not null &&
                !map.ContainsKey(pack.CompilerFilePath))
            {
                var name = new AssemblyName(pack.CompilerAssemblyName);
                map[pack.CompilerFilePath] = name;
            }
        }

        return map
            .OrderBy(x => x.Key, PathUtil.Comparer)
            .Select(x => (x.Key, x.Value))
            .ToList();
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
        var sourceTexts = new List<(SourceText SourceText, string Path)>();
        var generatedTexts = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigs = new List<(SourceText SourceText, string Path)>();
        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();

        MemoryStream? win32ResourceStream = null;
        MemoryStream? sourceLinkStream = null;
        List<ResourceDescription>? resources = rawCompilationData.Resources.Count == 0
            ? null
            : rawCompilationData.Resources.Select(x => ReadResourceDescription(x)).ToList();
        List<EmbeddedText>? embeddedTexts = null;

        foreach (var rawContent in rawCompilationData.Contents)
        {
            switch (rawContent.Kind)
            {
                case RawContentKind.SourceText:
                    sourceTexts.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    break;
                case RawContentKind.GeneratedText:
                    if (BasicAnalyzerKind == BasicAnalyzerKind.None)
                    {
                        generatedTexts.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    }
                    break;
                case RawContentKind.AnalyzerConfig:
                    analyzerConfigs.Add((GetSourceText(rawContent.ContentHash, hashAlgorithm), rawContent.FilePath));
                    break;
                case RawContentKind.AdditionalText:
                    additionalTexts.Add(new BasicAdditionalTextFile(
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
        sourceTexts.AddRange(generatedTexts);

        var emitData = new EmitData(
            rawCompilationData.AssemblyFileName,
            rawCompilationData.XmlFilePath,
            win32ResourceStream: win32ResourceStream,
            sourceLinkStream: sourceLinkStream,
            resources: resources,
            embeddedTexts: embeddedTexts);

        var basicAnalyzerHost = ReadAnalyzers(rawCompilationData);

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

        CSharpCompilationData CreateCSharp()
        {
            var csharpOptions = (CSharpCompilationOptions)compilationOptions;
            var parseOptions = (CSharpParseOptions)rawParseOptions;
            var syntaxTrees = RoslynUtil.ParseAllCSharp(sourceTexts, parseOptions);

            return RoslynUtil.CreateCSharpCompilationData(
                compilerCall,
                rawCompilationData.CompilationName,
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
                rawCompilationData.CompilationName,
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
        if (compilerCall.OwnerState is CompilerCallState state &&
            state.Index >= 0 &&
            state.Index < Count &&
            object.ReferenceEquals(this, state.Reader))
        {
            return state.Index;
        }

        throw new ArgumentException($"The provided {nameof(CompilerCall)} is not from this instance");
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
        return CompilerLogState.GetOrCreate(
            BasicAnalyzerKind,
            rawCompilationData.Analyzers,
            (kind, analyzers) => kind switch
            {
                BasicAnalyzerKind.OnDisk => new BasicAnalyzerHostOnDisk(this, analyzers),
                BasicAnalyzerKind.InMemory => new BasicAnalyzerHostInMemory(this, analyzers),
                BasicAnalyzerKind.None => new BasicAnalyzerHostNone(rawCompilationData.ReadGeneratedFiles, ReadGeneratedSourceTexts()),
                _ => throw new InvalidOperationException()
            });

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

    void IBasicAnalyzerHostDataProvider.CopyAssemblyBytes(RawAnalyzerData data, Stream stream) => CopyAssemblyBytes(data.Mvid, stream);
}
