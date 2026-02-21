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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
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

    /// <summary>
    /// This is the default path normalization util that was created based on the log metadata. It cannot
    /// be changed after creation.
    /// </summary>
    internal PathNormalizationUtil DefaultPathNormalizationUtil { get; }

    /// <summary>
    /// This is used to normalize paths within the log. This will be used to both map the file paths
    /// in the Roslyn API as well as to map paths within content that is understood by the compiler. For
    /// example: this will be used to map section paths within a global editorconfig file.
    /// </summary>
    internal PathNormalizationUtil PathNormalizationUtil { get; set; }

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

        DefaultPathNormalizationUtil = (Metadata.IsWindows, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) switch
        {
            (true, true) => PathNormalizationUtil.Empty,
            (true, false) => PathNormalizationUtil.WindowsToUnix,
            (false, true) => PathNormalizationUtil.UnixToWindows,
            (false, false) => PathNormalizationUtil.Empty,
        };

        PathNormalizationUtil = DefaultPathNormalizationUtil;

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
            pack.CompilerCallKind,
            pack.TargetFramework,
            pack.IsCSharp,
            NormalizePath(pack.CompilerFilePath),
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
                // This is deliberately using the original file path (not a normalized one) because
                // RawContent is meant to represent the original content of the log.
                yield return new RawContent(
                    tuple.Item2.FilePath,
                    tuple.Item2.ContentHash,
                    currentKind);
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

    public IReadOnlyCollection<string> ReadRawArguments(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var infoPack = GetOrReadCompilationInfoPack(index);
        return GetContentPack<string[]>(infoPack.CommandLineArgsHash);
    }

    public IReadOnlyCollection<string> ReadArguments(CompilerCall compilerCall)
    {
        var rawArgs = ReadRawArguments(compilerCall);
        if (PathNormalizationUtil.IsEmpty)
        {
            return rawArgs;
        }

        var normalizedArgs = new string[rawArgs.Count];
        var index = 0;
        foreach (var arg in rawArgs)
        {
            normalizedArgs[index++] = CompilerCommandLineUtil.NormalizeArgument(arg, PathNormalizationUtil.NormalizePath);
        }

        return normalizedArgs;
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
        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var emitPdb = dataPack.EmitPdb ?? !emitOptions.EmitMetadataOnly;

        MemoryStream? win32ResourceStream = null;
        MemoryStream? sourceLinkStream = null;
        List<EmbeddedText>? embeddedTexts = null;
        List<AnalyzerConfig>? analyzerConfigs = null;

        foreach (var tuple in dataPack.ContentList)
        {
            var kind = (RawContentKind)tuple.Item1;
            var contentHash = tuple.Item2.ContentHash;
            var filePath = PathNormalizationUtil.NormalizePath(tuple.Item2.FilePath, kind);

            switch (kind)
            {
                case RawContentKind.SourceText:
                    if (contentHash is null)
                    {
                        AddMissingFileDiagnostic(filePath);
                        continue;
                    }

                    sourceTexts.Add((ReadSourceText(kind, contentHash, hashAlgorithm), filePath));
                    break;
                case RawContentKind.GeneratedText:
                    // Nothing to do here as these are handled by the generation process.
                    break;
                case RawContentKind.AnalyzerConfig:
                {
                    if (contentHash is null)
                    {
                        AddMissingFileDiagnostic(filePath);
                        continue;
                    }

                    var sourceText = ReadSourceText(kind, contentHash, hashAlgorithm);
                    var analyzerConfig = AnalyzerConfig.Parse(sourceText, filePath);
                    analyzerConfigs ??= new List<AnalyzerConfig>();
                    analyzerConfigs.Add(analyzerConfig);
                    break;
                }
                case RawContentKind.AdditionalText:
                    additionalTexts.Add(new BasicAdditionalSourceText(
                        filePath,
                         contentHash is not null ? ReadSourceText(kind, contentHash, hashAlgorithm) : null));
                    break;
                case RawContentKind.CryptoKeyFile:
                    HandleCryptoKeyFile(contentHash, filePath);
                    break;
                case RawContentKind.SourceLink:
                    sourceLinkStream = TryGetContentAsStream(contentHash, filePath);
                    break;
                case RawContentKind.Win32Resource:
                    win32ResourceStream = TryGetContentAsStream(contentHash, filePath);
                    break;
                case RawContentKind.Embed:
                {
                    if (embeddedTexts is null)
                    {
                        embeddedTexts = new List<EmbeddedText>();
                    }

                    if (contentHash is null)
                    {
                        AddMissingFileDiagnostic(filePath);
                        break;
                    }

                    var sourceText = ReadSourceText(kind, contentHash, hashAlgorithm, canBeEmbedded: true);
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
                case RawContentKind.RuleSetInclude:
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
            emitPdb,
            win32ResourceStream: win32ResourceStream,
            sourceLinkStream: sourceLinkStream,
            resources: resourceList,
            embeddedTexts: embeddedTexts);

        var basicAnalyzerHost = CreateBasicAnalyzerHost(compilerCall);
        var analyzerConfigSet = analyzerConfigs is null
            ? null
            : AnalyzerConfigSet.Create(analyzerConfigs);

        return compilerCall.IsCSharp
            ? CreateCSharp()
            : CreateVisualBasic();

        void HandleCryptoKeyFile(string? contentHash, string originalFilePath)
        {
            var dir = Path.Combine(LogReaderState.CryptoKeyFileDirectory, GetIndex(compilerCall).ToString());
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, Path.GetFileName(originalFilePath));

            if (contentHash is not null)
            {
                File.WriteAllBytes(filePath, GetRawContentBytes(contentHash));
            }

            compilationOptions = compilationOptions.WithCryptoKeyFile(filePath);
        }

        CSharpCompilationData CreateCSharp() =>
            RoslynUtil.CreateCSharpCompilationData(
                compilerCall,
                compilationName,
                (CSharpParseOptions)rawParseOptions,
                (CSharpCompilationOptions)compilationOptions,
                sourceTexts,
                referenceList,
                analyzerConfigSet,
                additionalTexts.ToImmutableArray(),
                emitOptions,
                emitData,
                basicAnalyzerHost,
                diagnostics.ToImmutable());

        VisualBasicCompilationData CreateVisualBasic() =>
            RoslynUtil.CreateVisualBasicCompilationData(
                compilerCall,
                compilationName,
                (VisualBasicParseOptions)rawParseOptions,
                (VisualBasicCompilationOptions)compilationOptions,
                sourceTexts,
                referenceList,
                analyzerConfigSet,
                additionalTexts.ToImmutableArray(),
                emitOptions,
                emitData,
                basicAnalyzerHost,
                diagnostics.ToImmutable());

        List<MetadataReference> ReadMetadataReferences(List<ReferencePack> referencePacks)
        {
            var list = new List<MetadataReference>(capacity: referencePacks.Count);
            foreach (var referencePack in referencePacks)
            {
                if (referencePack.IsImplicit)
                {
                    continue;
                }

                MetadataReference mdRef = ReadMetadataReference(
                        referencePack.Mvid,
                        referencePack.NetModuleMvids.IsDefault ? [] : referencePack.NetModuleMvids);
                mdRef = mdRef.With(referencePack.Aliases, referencePack.EmbedInteropTypes);
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

        MemoryStream? TryGetContentAsStream(string? contentHash, string filePath)
        {
            if (contentHash is null)
            {
                AddMissingFileDiagnostic(filePath);
                return null;
            }

            return GetRawContentBytes(contentHash).AsSimpleMemoryStream(writable: false);
        }

        void AddMissingFileDiagnostic(string name)
        {
            diagnostics.Add(Diagnostic.Create(RoslynUtil.CannotReadFileDiagnosticDescriptor, Location.None, name));
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
        var list = new List<ReferenceData>(capacity: dataPack.References.Count);

        foreach (var referencePack in dataPack.References)
        {
            var filePath = referencePack.FilePath is string fp
                ? NormalizePath(fp)
                : GetMetadataReferenceFileName(referencePack.Mvid);
            var assemblyIdentityData = new AssemblyIdentityData(
                referencePack.Mvid,
                referencePack.AssemblyName,
                referencePack.AssemblyInformationalVersion);
            var data = new ReferenceData(assemblyIdentityData, filePath, referencePack.Kind, referencePack.Aliases, referencePack.EmbedInteropTypes, referencePack.IsImplicit, referencePack.NetModuleMvids);
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
        var index = GetIndex(compilerCall);
        var dataPack = GetOrReadCompilationDataPack(index);

        var list = new List<SourceTextData>();
        foreach (var rawContent in ReadAllRawContent(index))
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
                object id = rawContent.ContentHash ?? rawContent.FilePath;
                var filePath = PathNormalizationUtil.NormalizePath(rawContent.FilePath, rawContent.Kind);
                var data = new SourceTextData(id, filePath, dataPack.ChecksumAlgorithm, k);
                list.Add(data);
            }
        }

        return list;
    }

    /// <inheritdoc cref="ICompilerCallReader.HasAllGeneratedFileContent(CompilerCall)"/>
    public bool HasAllGeneratedFileContent(CompilerCall compilerCall) =>
        HasAllGeneratedFileContent(GetOrReadCompilationDataPack(compilerCall));

    private bool HasAllGeneratedFileContent(CompilationDataPack dataPack)
    {
        if (dataPack.Analyzers.Count == 0)
        {
            return true;
        }

        return dataPack.HasGeneratedFilesInPdb is true
            ? dataPack.IncludesGeneratedText
            : dataPack.IncludesGeneratedText;
    }

    /// <inheritdoc cref="ICompilerCallReader.ReadAllGeneratedSourceTexts(CompilerCall)"/>
    public List<(SourceText SourceText, string FilePath)> ReadAllGeneratedSourceTexts(CompilerCall compilerCall)
    {
        var index = GetIndex(compilerCall);
        var dataPack = GetOrReadCompilationDataPack(index);
        var list = new List<(SourceText SourceText, string FilePath)>();
        foreach (var rawContent in ReadAllRawContent(index, RawContentKind.GeneratedText))
        {
            list.Add((ReadSourceText(rawContent.Kind, rawContent.ContentHash!, dataPack.ChecksumAlgorithm), rawContent.FilePath));
        }
        return list;
    }

    public BasicAnalyzerHost CreateBasicAnalyzerHost(CompilerCall compilerCall) =>
        LogReaderState.GetOrCreateBasicAnalyzerHost(this, BasicAnalyzerKind, compilerCall);

    internal string GetMetadataReferenceFileName(Guid mvid)
    {
        if (_mvidToRefInfoMap.TryGetValue(mvid, out var tuple))
        {
            return tuple.FileName;
        }

        throw new ArgumentException($"{mvid} is not a valid MVID");
    }

    public MetadataReference ReadMetadataReference(ReferenceData referenceData)
    {
        var mdRef = ReadMetadataReference(referenceData.Mvid, referenceData.NetModules);
        return mdRef.With(referenceData.Aliases, referenceData.EmbedInteropTypes);
    }

    /// <summary>
    /// Reads a <see cref="MetadataReference"/> for the given <paramref name="mvid"/>
    /// and <paramref name="netModuleMvids"/>. This does not include extra metadata properties
    /// like alias, embedded interop types, etc ...
    ///
    /// The same set of <paramref name="netModuleMvids"/> must be provided for the same
    /// <paramref name="mvid"/> across calls in order to get the same reference instance back.
    /// This is because references with netmodules are cached together. Really it's a single key.
    /// </summary>
    private MetadataReference ReadMetadataReference(Guid mvid, ImmutableArray<Guid> netModuleMvids)
    {
        Debug.Assert(!netModuleMvids.IsDefault);
        if (!_refMap.TryGetValue(mvid, out var metadataReference))
        {
            metadataReference = CreateMetadataReference(mvid, netModuleMvids);
            _refMap.Add(mvid, metadataReference);
        }

        return metadataReference;

        MetadataReference CreateMetadataReference(Guid mvid, ImmutableArray<Guid> netModuleMvids)
        {
            var bytes = GetAssemblyBytes(mvid);
            var tuple = _mvidToRefInfoMap[mvid];
            if (netModuleMvids.Length == 0)
            {
                return MetadataReference.CreateFromStream(new MemoryStream(bytes), filePath: tuple.FileName);
            }

            var modules = new ModuleMetadata[1 + netModuleMvids.Length];
            modules[0] = ModuleMetadata.CreateFromImage(bytes);
            for (int i = 0; i < netModuleMvids.Length; i++)
            {
                modules[i + 1] = ModuleMetadata.CreateFromImage(GetAssemblyBytes(netModuleMvids[i]));
            }

            var assemblyMetadata = AssemblyMetadata.Create(modules);
            return assemblyMetadata.GetReference(filePath: tuple.FileName);
        }
    }

    internal T GetContentPack<T>(string contentHash)
    {
        var stream = GetRawContentStream(contentHash);
        return MessagePackSerializer.Deserialize<T>(stream, SerializerOptions);
    }

    private byte[] GetRawContentBytes(string contentHash) =>
        ZipArchive.ReadAllBytes(GetContentEntryName(contentHash));

    private Stream GetRawContentStream(string contentHash) =>
        ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));

    /// <summary>
    /// Get the normalized content stream for the given <paramref name="kind"/> and <paramref name="contentHash"/>.
    /// </summary>
    internal Stream GetContentStream(RawContentKind kind, string contentHash)
    {
        if (GetNormalizedContentStream(kind, contentHash) is { } normalizedStream)
        {
            return normalizedStream;
        }

        return GetRawContentStream(contentHash);
    }

    /// <summary>
    /// Get the normalized content bytes for the given <paramref name="kind"/> and <paramref name="contentHash"/>.
    /// </summary>
    internal byte[] GetContentBytes(RawContentKind kind, string contentHash)
    {
        if (GetNormalizedContentStream(kind, contentHash) is { } stream)
        {
            return stream.ReadAllBytes();
        }

        return GetRawContentBytes(contentHash);
    }

    internal byte[] GetContentBytes(ResourceData resourceData) =>
        GetRawContentBytes(resourceData.ContentHash);

    private MemoryStream? GetNormalizedContentStream(RawContentKind kind, string contentHash)
    {
        if (PathNormalizationUtil.IsEmpty)
        {
            return null;
        }

        if (kind == RawContentKind.AnalyzerConfig && GetNormalizedAnalyzerConfing(contentHash) is { } newSourceText)
        {
            return newSourceText.ToMemoryStream();
        }

        if ((kind == RawContentKind.RuleSet || kind == RawContentKind.RuleSetInclude) && GetNormalizedRuleset(contentHash) is { } newStream)
        {
            return newStream;
        }

        return null;

        // If paths are being mapped then we need to map the paths inside of global config
        // files as they can impact diagnostics in the compilation
        SourceText? GetNormalizedAnalyzerConfing(string contentHash)
        {
            using var stream = GetRawContentStream(contentHash);
            var sourceText = RoslynUtil.GetSourceText(stream, checksumAlgorithm: SourceHashAlgorithm.Sha1, canBeEmbedded: false);
            if (RoslynUtil.IsGlobalEditorConfigWithSection(sourceText))
            {
                var newText = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, PathNormalizationUtil.NormalizePath);
                return SourceText.From(newText, checksumAlgorithm: sourceText.ChecksumAlgorithm);
            }

            return null;
        }

        // If paths are being mapped then we need to map the paths inside of rulesets as they
        // can impact diagnostics in the compilation
        MemoryStream? GetNormalizedRuleset(string contentHash)
        {
            using var stream = GetRawContentStream(contentHash);
            var xmlDocument = new XmlDocument();
            xmlDocument.Load(stream);
            var includes = RoslynUtil.GetRuleSetIncludes(xmlDocument);
            if (includes.Count == 0)
            {
                return null;
            }

            RoslynUtil.RewriteRuleSetIncludes(xmlDocument, PathNormalizationUtil.NormalizePath);
            var newStream = new MemoryStream();
            xmlDocument.Save(newStream);
            newStream.Position = 0;
            return newStream;
        }
    }

    internal ResourceDescription ReadResourceDescription(ResourceData pack) =>
        ReadResourceDescription(pack.ContentHash, pack.FileName, pack.Name, pack.IsPublic);

    private ResourceDescription ReadResourceDescription(string contentHash, string? fileName, string name, bool isPublic)
    {
        var stream = GetRawContentBytes(contentHash).AsSimpleMemoryStream(writable: false);
        var dataProvider = () => stream;
        return string.IsNullOrEmpty(fileName)
            ? new ResourceDescription(name, dataProvider, isPublic)
            : new ResourceDescription(name, fileName, dataProvider, isPublic);
    }

    public SourceText ReadSourceText(SourceTextData sourceTextData)
    {
        var contentHash = (string)sourceTextData.Id;
        return ReadSourceText(sourceTextData.RawContentKind, contentHash, sourceTextData.ChecksumAlgorithm, canBeEmbedded: false);
    }

    /// <summary>
    /// Read the content represented by <paramref name="contentHash"/> as a <see cref="SourceText"/>.
    /// </summary>
    internal SourceText ReadSourceText(RawContentKind kind, string contentHash, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded = false)
    {
        Stream? stream = null;
        try
        {
            if (canBeEmbedded)
            {
                // Zip streams don't have length so we have to go the byte[] route
                var bytes = GetContentBytes(kind, contentHash);
                stream = bytes.AsSimpleMemoryStream();
            }
            else
            {
                stream = GetContentStream(kind, contentHash);
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
