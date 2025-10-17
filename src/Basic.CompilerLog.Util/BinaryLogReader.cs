using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Basic.CompilerLog.Util.Impl;
using MessagePack.Formatters;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Basic.CompilerLog.Util;

public sealed class BinaryLogReader : ICompilerCallReader, IBasicAnalyzerHostDataProvider
{
    private Stream _stream;
    private readonly bool _leaveOpen;

    private readonly Dictionary<string, PortableExecutableReference> _metadataReferenceMap = new(PathUtil.Comparer);
    private readonly Dictionary<string, AssemblyIdentityData> _assemblyIdentityDataMap = new(PathUtil.Comparer);
    private readonly Dictionary<CompilerCall, CommandLineArguments> _argumentsMap = new();
    private readonly Lazy<List<CompilerCall>> _lazyCompilerCalls;
    private readonly Lazy<Dictionary<Guid, int>> _lazyMvidToCompilerCallIndexMap;

    public bool OwnsLogReaderState { get; }
    public LogReaderState LogReaderState { get; }
    public BasicAnalyzerKind BasicAnalyzerKind { get; }
    public bool IsDisposed => _stream is null;

    private BinaryLogReader(Stream stream, bool leaveOpen, BasicAnalyzerKind? basicAnalyzerKind, LogReaderState? state)
    {
        _stream = stream;
        BasicAnalyzerKind = basicAnalyzerKind ?? BasicAnalyzerHost.DefaultKind;
        OwnsLogReaderState = state is null;
        LogReaderState = state ?? new LogReaderState();
        _leaveOpen = leaveOpen;
        _lazyCompilerCalls = new(() =>
        {
            _stream.Position = 0;
            return BinaryLogUtil.ReadAllCompilerCalls(_stream, ownerState: this);
        });
        _lazyMvidToCompilerCallIndexMap = new(() => BuildMvidToCompilerCallIndexMap());
    }

    public static BinaryLogReader Create(
        Stream stream,
        BasicAnalyzerKind? basicAnalyzerKind,
        LogReaderState? state,
        bool leaveOpen) =>
        new BinaryLogReader(stream, leaveOpen, basicAnalyzerKind, state);

    public static BinaryLogReader Create(
        Stream stream,
        BasicAnalyzerKind? basicAnalyzerKind = null,
        bool leaveOpen = true) =>
        Create(stream, basicAnalyzerKind, state: null, leaveOpen);

    public static BinaryLogReader Create(
        Stream stream,
        LogReaderState? state,
        bool leaveOpen = true) =>
        Create(stream, basicAnalyzerKind: null, state: state, leaveOpen);

    public static BinaryLogReader Create(
        string filePath,
        BasicAnalyzerKind? basicAnalyzerKind = null,
        LogReaderState? state = null)
    {
        var stream = RoslynUtil.OpenBuildFileForRead(filePath);
        return Create(stream, basicAnalyzerKind, state: state, leaveOpen: false);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (OwnsLogReaderState)
        {
            LogReaderState.Dispose();
        }

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        _stream = null!;
    }

    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        return _lazyCompilerCalls.Value.Where(predicate).ToList();
    }

    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null)
    {
        var list = new List<CompilationData>();
        foreach (var compilerCall in ReadAllCompilerCalls(predicate))
        {
            list.Add(ReadCompilationData(compilerCall));
        }
        return list;
    }

    public CompilerCall ReadCompilerCall(int index) =>
        _lazyCompilerCalls.Value[index];

    public CompilerCallData ReadCompilerCallData(CompilerCall compilerCall) =>
            ReadCompilerCallDataCore(compilerCall).CompilerCallData;

    private (CompilerCallData CompilerCallData, CommandLineArguments Arguments) ReadCompilerCallDataCore(CompilerCall compilerCall)
    {
        var args = ReadCommandLineArguments(compilerCall);
        var assemblyFileName = RoslynUtil.GetAssemblyFileName(args);
        var compilationName = Path.GetFileNameWithoutExtension(assemblyFileName);
        var data = new CompilerCallData(
            compilerCall,
            compilationName,
            args.OutputDirectory,
            args.ParseOptions,
            args.CompilationOptions,
            args.EmitOptions);
        return (data, args);
    }

    /// <summary>
    /// Reads the <see cref="CommandLineArguments"/> for the given <see cref="CompilerCall"/>.
    /// </summary>
    /// <remarks>
    /// !!!WARNING!!!
    ///
    /// This method is only valid when this instance represents a compilation on the disk of the
    /// current machine. In any other scenario this will lead to mostly correct but potentially
    /// incorrect results.
    ///
    /// This method is on <see cref="BinaryLogReader"/> as its presence is a stronger indicator
    /// that the necessary data is on disk.
    /// </remarks>
    public CommandLineArguments ReadCommandLineArguments(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        if (!_argumentsMap.TryGetValue(compilerCall, out var args))
        {
            args = BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall);
            _argumentsMap[compilerCall] = args;
        }
        return args;
    }

    public CompilationData ReadCompilationData(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        var (compilerCallData, args) = ReadCompilerCallDataCore(compilerCall);

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var references = GetReferences();
        var sourceTexts = GetSourceTexts();
        var additionalTexts = GetAdditionalTexts();
        var analyzerConfigSet = GetAnalyzerConfigSet();
        var emitData = GetEmitData();
        var basicAnalyzerHost = CreateBasicAnalyzerHost(compilerCall);

        return compilerCall.IsCSharp ? GetCSharp() : GetVisualBasic();

        CSharpCompilationData GetCSharp()
        {
            return RoslynUtil.CreateCSharpCompilationData(
                compilerCall,
                compilerCallData.AssemblyFileName,
                (CSharpParseOptions)args.ParseOptions,
                (CSharpCompilationOptions)args.CompilationOptions,
                sourceTexts,
                references,
                analyzerConfigSet,
                additionalTexts,
                args.EmitOptions,
                emitData,
                basicAnalyzerHost,
                diagnostics.ToImmutable());
        }

        VisualBasicCompilationData GetVisualBasic()
        {
            return RoslynUtil.CreateVisualBasicCompilationData(
                compilerCall,
                args.CompilationName,
                (VisualBasicParseOptions)args.ParseOptions,
                (VisualBasicCompilationOptions)args.CompilationOptions,
                sourceTexts,
                references,
                analyzerConfigSet,
                additionalTexts,
                args.EmitOptions,
                emitData,
                basicAnalyzerHost,
                diagnostics.ToImmutable());
        }

        AnalyzerConfigSet? GetAnalyzerConfigSet()
        {
            var list = GetSourceTextsFromPaths(args.AnalyzerConfigPaths, args.AnalyzerConfigPaths.Length, args.ChecksumAlgorithm);
            if (list.Count == 0)
            {
                return null;
            }

            return AnalyzerConfigSet.Create(list.Select(x => AnalyzerConfig.Parse(x.SourceText, x.Path)).ToList());
        }

        List<MetadataReference> GetReferences()
        {
            var list = new List<MetadataReference>(capacity: args.MetadataReferences.Length);
            foreach (var reference in args.MetadataReferences)
            {
                var mdRef = ReadMetadataReference(reference.Reference).With(reference.Properties.Aliases, reference.Properties.EmbedInteropTypes);
                list.Add(mdRef);
            }
            return list;
        }

        List<(SourceText SourceText, string Path)> GetSourceTexts() =>
            GetSourceTextsFromPaths(args.SourceFiles.Select(x => x.Path), args.SourceFiles.Length, args.ChecksumAlgorithm);

        List<(SourceText SourceText, string Path)> GetSourceTextsFromPaths(IEnumerable<string> filePaths, int count, SourceHashAlgorithm checksumAlgorithm)
        {
            var list = new List<(SourceText, string)>(capacity: count);
            foreach (var filePath in filePaths)
            {
                if (!File.Exists(filePath))
                {
                    diagnostics.Add(Diagnostic.Create(
                        RoslynUtil.CannotReadFileDiagnosticDescriptor,
                        Location.None,
                        filePath));
                    continue;
                }

                var sourceText = RoslynUtil.GetSourceText(filePath, checksumAlgorithm, canBeEmbedded: false);
                list.Add((sourceText, filePath));
            }
            return list;
        }

        ImmutableArray<AdditionalText> GetAdditionalTexts()
        {
            var builder = ImmutableArray.CreateBuilder<AdditionalText>(args.AdditionalFiles.Length);
            foreach (var additionalFile in args.AdditionalFiles)
            {
                var additionalText = new BasicAdditionalTextFile(additionalFile.Path, args.ChecksumAlgorithm);
                builder.Add(additionalText);
            }
            return builder.MoveToImmutable();
        }

        EmitData GetEmitData()
        {
            return new EmitData(
                RoslynUtil.GetAssemblyFileName(args),
                args.DocumentationPath,
                args.EmitPdb,
                win32ResourceStream: ReadFileAsMemoryStream(args.Win32ResourceFile),
                sourceLinkStream: ReadFileAsMemoryStream(args.SourceLink),
                resources: args.ManifestResources,
                embeddedTexts: GetEmbeddedTexts());
        }

        MemoryStream? ReadFileAsMemoryStream(string? filePath)
        {
            if (filePath is null)
            {
                return null;
            }

            if (!File.Exists(filePath))
            {
                diagnostics.Add(Diagnostic.Create(
                    RoslynUtil.CannotReadFileDiagnosticDescriptor,
                    Location.None,
                    filePath));
                return null;
            }

            var bytes = File.ReadAllBytes(filePath);
            return new MemoryStream(bytes);
        }

        IEnumerable<EmbeddedText>? GetEmbeddedTexts()
        {
            if (args.EmbeddedFiles.Length == 0)
            {
                return null;
            }

            var list = new List<EmbeddedText>(args.EmbeddedFiles.Length);
            foreach (var item in args.EmbeddedFiles)
            {
                if (!File.Exists(item.Path))
                {
                    diagnostics.Add(Diagnostic.Create(
                        RoslynUtil.CannotReadFileDiagnosticDescriptor,
                        Location.None,
                        item.Path));
                    continue;
                }

                var sourceText = RoslynUtil.GetSourceText(item.Path, args.ChecksumAlgorithm, canBeEmbedded: true);
                var embeddedText = EmbeddedText.FromSource(item.Path, sourceText);
                list.Add(embeddedText);
            }

            return list;

        }
    }

    public BasicAnalyzerHost CreateBasicAnalyzerHost(CompilerCall compilerCall) =>
        LogReaderState.GetOrCreateBasicAnalyzerHost(this, BasicAnalyzerKind, compilerCall);

    public SourceText ReadSourceText(SourceTextData sourceTextData) =>
        RoslynUtil.GetSourceText(sourceTextData.FilePath, sourceTextData.ChecksumAlgorithm, canBeEmbedded: false);

    public List<ReferenceData> ReadAllReferenceData(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        var args = ReadCommandLineArguments(compilerCall);
        return ReadAllReferenceDataCore(args.MetadataReferences, args.MetadataReferences.Length);
    }

    public List<CompilerAssemblyData> ReadAllCompilerAssemblies()
    {
        var list = new List<(string CompilerFilePath, AssemblyName AssemblyName)>();
        var map = new Dictionary<string, (AssemblyName, string?)>(PathUtil.Comparer);
        foreach (var compilerCall in ReadAllCompilerCalls())
        {
            if (compilerCall.CompilerFilePath is string compilerFilePath &&
                !map.ContainsKey(compilerFilePath))
            {
                AssemblyName name;
                string? commitHash;
                try
                {
                    name = AssemblyName.GetAssemblyName(compilerFilePath);
                    commitHash = RoslynUtil.ReadCompilerCommitHash(compilerFilePath);
                }
                catch
                {
                    name = new AssemblyName(Path.GetFileName(compilerFilePath));
                    commitHash = null;
                }

                map[compilerCall.CompilerFilePath] = (name, commitHash);
            }
        }

        return map
            .OrderBy(x => x.Key, PathUtil.Comparer)
            .Select(x => new CompilerAssemblyData(x.Key, x.Value.Item1, x.Value.Item2))
            .ToList();
    }

    /// <inheritdoc cref="ICompilerCallReader.HasAllGeneratedFileContent(CompilerCall)"/>
    public bool HasAllGeneratedFileContent(CompilerCall compilerCall)
    {
        var args = ReadCommandLineArguments(compilerCall);
        if (args.AnalyzerReferences.IsDefaultOrEmpty)
        {
            return true;
        }

        return RoslynUtil.HasGeneratedFilesInPdb(args);
    }

    /// <inheritdoc cref="ICompilerCallReader.ReadAllGeneratedSourceTexts(CompilerCall)"/>
    public List<(SourceText SourceText, string FilePath)> ReadAllGeneratedSourceTexts(CompilerCall compilerCall)
    {
        var args = ReadCommandLineArguments(compilerCall);
        if (args.AnalyzerReferences.IsDefaultOrEmpty)
        {
            return [];
        }

        var generatedFiles = RoslynUtil.ReadGeneratedFilesFromPdb(compilerCall, args);
        var list = new List<(SourceText SourceText, string Path)>(generatedFiles.Count);
        foreach (var tuple in generatedFiles)
        {
            var sourceText = RoslynUtil.GetSourceText(tuple.Stream, args.ChecksumAlgorithm, canBeEmbedded: false);
            list.Add((sourceText, tuple.FilePath));
        }
        return list;
    }

    public List<SourceTextData> ReadAllSourceTextData(CompilerCall compilerCall)
    {
        var args = ReadCommandLineArguments(compilerCall);
        var list = new List<SourceTextData>(args.SourceFiles.Length + args.AnalyzerConfigPaths.Length + args.AdditionalFiles.Length);
        list.AddRange(args.SourceFiles.Select(x => new SourceTextData(this, x.Path, args.ChecksumAlgorithm, SourceTextKind.SourceCode)));
        list.AddRange(args.AnalyzerConfigPaths.Select(x => new SourceTextData(this, x, args.ChecksumAlgorithm, SourceTextKind.AnalyzerConfig)));
        list.AddRange(args.AdditionalFiles.Select(x => new SourceTextData(this, x.Path, args.ChecksumAlgorithm, SourceTextKind.AnalyzerConfig)));
        return list;
    }

    private AssemblyIdentityData GetOrReadAssemblyIdentityData(string filePath)
    {
        if (!_assemblyIdentityDataMap.TryGetValue(filePath, out var assemblyIdentityData))
        {
            assemblyIdentityData = RoslynUtil.ReadAssemblyIdentityData(filePath);
            _assemblyIdentityDataMap[filePath] = assemblyIdentityData;
        }

        return assemblyIdentityData;
    }

    private List<ReferenceData> ReadAllReferenceDataCore(IEnumerable<CommandLineReference> commandLineReferences, int count)
    {
        var list = new List<ReferenceData>(capacity: count);
        foreach (var commandLineReference in commandLineReferences)
        {
            var identityData = GetOrReadAssemblyIdentityData(commandLineReference.Reference);
            var data = new ReferenceData(
                identityData,
                commandLineReference.Reference,
                commandLineReference.Properties.Aliases,
                commandLineReference.Properties.EmbedInteropTypes);

            list.Add(data);
        }

        return list;
    }

    public List<AnalyzerData> ReadAllAnalyzerData(CompilerCall compilerCall)
    {
        var args = ReadCommandLineArguments(compilerCall);
        var list = new List<AnalyzerData>(args.AnalyzerReferences.Length);
        foreach (var analyzer in args.AnalyzerReferences)
        {
            var identityData = GetOrReadAssemblyIdentityData(analyzer.FilePath);
            var data = new AnalyzerData(identityData, analyzer.FilePath);
            list.Add(data);
        }
        return list;
    }

    public IReadOnlyCollection<string> ReadCommandLineArgumentText(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        // TODO: temp part of refactoring
        return compilerCall.GetArguments();
    }

    private void CheckOwnership(CompilerCall compilerCall)
    {
        if (compilerCall.OwnerState is BinaryLogReader reader && object.ReferenceEquals(reader, this))
        {
            return;
        }

        throw new ArgumentException($"The provided {nameof(CompilerCall)} is not from this instance");
    }

    public MetadataReference ReadMetadataReference(ReferenceData referenceData)
    {
        var mdRef = ReadMetadataReference(referenceData.FilePath);
        return mdRef.With(referenceData.Aliases, referenceData.EmbedInteropTypes);
    }

    public MetadataReference ReadMetadataReference(string filePath)
    {
        if (!_metadataReferenceMap.TryGetValue(filePath, out var mdRef))
        {
            mdRef = MetadataReference.CreateFromFile(filePath);
            _metadataReferenceMap[filePath] = mdRef;
        }

        return mdRef;
    }

    public void CopyAssemblyBytes(AssemblyData data, Stream stream)
    {
        using var fileStream = RoslynUtil.OpenBuildFileForRead(data.FilePath);
        fileStream.CopyTo(stream);
    }

    byte[] IBasicAnalyzerHostDataProvider.GetAssemblyBytes(AssemblyData data) =>
        File.ReadAllBytes(data.FilePath);

    public bool TryGetCompilerCallIndex(Guid mvid, out int compilerCallIndex)
    {
        var map = _lazyMvidToCompilerCallIndexMap.Value;
        return map.TryGetValue(mvid, out compilerCallIndex);
    }

    private Dictionary<Guid, int> BuildMvidToCompilerCallIndexMap()
    {
        var map =  new Dictionary<Guid, int>();
        var compilerCalls = _lazyCompilerCalls.Value;
        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var args = ReadCommandLineArguments(compilerCall);
            var assemblyName = RoslynUtil.GetAssemblyFileName(args);
            if (args.OutputDirectory is not null &&
                RoslynUtil.TryReadMvid(Path.Combine(args.OutputDirectory, assemblyName)) is Guid assemblyMvid)
            {
                map[assemblyMvid] = i;
            }

            if (args.OutputRefFilePath is not null &&
                RoslynUtil.TryReadMvid(args.OutputRefFilePath) is Guid refAssemblyMvid)
            {
                map[refAssemblyMvid] = i;
            }
        }

        return map;
    }
}
