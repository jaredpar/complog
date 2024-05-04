using System.Collections.Immutable;
using Basic.CompilerLog.Util.Impl;
using MessagePack.Formatters;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLog.Util;

public sealed class BinaryLogReader : ICompilerCallReader, IBasicAnalyzerHostDataProvider
{
    private Stream _stream;
    private readonly bool _leaveOpen;

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

        return BinaryLogUtil.ReadAllCompilerCalls(_stream, predicate, ownerState: this);
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
        return BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall);
    }

    public CompilationData ReadCompilationData(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        var args = ReadCommandLineArguments(compilerCall);

        var references = GetReferences();
        var sourceTexts = GetSourceTexts();
        var additionalTexts = GetAdditionalTexts();
        var analyzerConfigs = GetAnalyzerConfigs();
        var emitData = GetEmitData();
        var basicAnalyzerHost = CreateAnalyzerHost();

        if (basicAnalyzerHost is BasicAnalyzerHostNone none)
        {
            // Generated source code should appear last to match the compiler behavior.
            sourceTexts.AddRange(none.GeneratedSourceTexts);
        }

        return compilerCall.IsCSharp ? GetCSharp() : GetVisualBasic();

        CSharpCompilationData GetCSharp()
        {
            return RoslynUtil.CreateCSharpCompilationData(
                compilerCall,
                args.CompilationName,
                (CSharpParseOptions)args.ParseOptions,
                (CSharpCompilationOptions)args.CompilationOptions,
                sourceTexts,
                references,
                analyzerConfigs,
                additionalTexts,
                args.EmitOptions,
                emitData,
                basicAnalyzerHost,
                PathNormalizationUtil.Empty);
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
                analyzerConfigs,
                additionalTexts,
                args.EmitOptions,
                emitData,
                basicAnalyzerHost,
                PathNormalizationUtil.Empty);
        }

        BasicAnalyzerHost CreateAnalyzerHost()
        {
            var list = ReadAllRawAnalyzerData(args);
            return LogReaderState.GetOrCreate(
                BasicAnalyzerKind,
                list,
                (kind, analyzers) => kind switch
                {
                    BasicAnalyzerKind.None => CreateNoneHost(),
                    BasicAnalyzerKind.OnDisk => new BasicAnalyzerHostOnDisk(this, analyzers),
                    BasicAnalyzerKind.InMemory => new BasicAnalyzerHostInMemory(this, analyzers),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                });

            BasicAnalyzerHostNone CreateNoneHost()
            {
                if (!RoslynUtil.HasGeneratedFilesInPdb(args))
                {
                    return new BasicAnalyzerHostNone("Compilation does not have a PDB compatible with generated files");
                }

                try
                {
                    var generatedFiles = RoslynUtil.ReadGeneratedFiles(compilerCall, args);
                    var builder = ImmutableArray.CreateBuilder<(SourceText SourceText, string Path)>(generatedFiles.Count);
                    foreach (var tuple in generatedFiles)
                    {
                        var sourceText = RoslynUtil.GetSourceText(tuple.Stream, args.ChecksumAlgorithm, canBeEmbedded: false);
                        builder.Add((sourceText, tuple.FilePath));
                    }

                    return new BasicAnalyzerHostNone(builder.MoveToImmutable());
                }
                catch (Exception ex)
                {
                    return new BasicAnalyzerHostNone(ex.Message);
                }
            }
        }

        List<(SourceText SourceText, string Path)> GetAnalyzerConfigs() => 
            GetSourceTextsFromPaths(args.AnalyzerConfigPaths, args.AnalyzerConfigPaths.Length, args.ChecksumAlgorithm);

        List<MetadataReference> GetReferences()
        {
            var list = new List<MetadataReference>(capacity: args.MetadataReferences.Length);
            foreach (var reference in args.MetadataReferences)
            {
                var mdref = MetadataReference.CreateFromFile(reference.Reference, reference.Properties);
                list.Add(mdref);
            }
            return list;
        }

        List<(SourceText SourceText, string Path)> GetSourceTexts() =>
            GetSourceTextsFromPaths(args.SourceFiles.Select(x => x.Path), args.SourceFiles.Length, args.ChecksumAlgorithm);

        static List<(SourceText SourceText, string Path)> GetSourceTextsFromPaths(IEnumerable<string> filePaths, int count, SourceHashAlgorithm checksumAlgorithm)
        {
            var list = new List<(SourceText, string)>(capacity: count);
            foreach (var filePath in filePaths)
            {
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
                var sourceText = RoslynUtil.GetSourceText(additionalFile.Path, args.ChecksumAlgorithm, canBeEmbedded: false);
                var additionalText = new BasicAdditionalTextFile(
                    additionalFile.Path,
                    sourceText);
                builder.Add(additionalText);
            }
            return builder.MoveToImmutable();
        }

        EmitData GetEmitData()
        {
            return new EmitData(
                RoslynUtil.GetAssemblyFileName(args),
                args.DocumentationPath,
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
                var sourceText = RoslynUtil.GetSourceText(item.Path, args.ChecksumAlgorithm, canBeEmbedded: true);
                var embeddedText = EmbeddedText.FromSource(item.Path, sourceText);
                list.Add(embeddedText);
            }

            return list;
        }
    }

    public List<ReferenceData> ReadAllAnalyzerData(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        var args = ReadCommandLineArguments(compilerCall);
        return ReadAllReferenceDataCore(args.AnalyzerReferences.Select(x => x.FilePath), args.AnalyzerReferences.Length);
    }

    public List<ReferenceData> ReadAllReferenceData(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        var args = ReadCommandLineArguments(compilerCall);
        return ReadAllReferenceDataCore(args.MetadataReferences.Select(x => x.Reference), args.MetadataReferences.Length);
    }

    /// <summary>
    /// Attempt to add all the generated files from generators. When successful the generators
    /// don't need to be run when re-hydrating the compilation.
    /// </summary>
    /// <remarks>
    /// This method will throw if the compilation does not have a PDB compatible with generated files
    /// available to read
    /// </remarks>
    public List<(string FilePath, MemoryStream Stream)> ReadGeneratedFiles(CompilerCall compilerCall)
    {
        var args = ReadCommandLineArguments(compilerCall);
        return RoslynUtil.ReadGeneratedFiles(compilerCall, args);
    }

    private List<ReferenceData> ReadAllReferenceDataCore(IEnumerable<string> filePaths, int count)
    {
        var list = new List<ReferenceData>(capacity: count);
        foreach (var filePath in filePaths)
        {
            var data = new ReferenceData(filePath, RoslynUtil.GetMvid(filePath), File.ReadAllBytes(filePath));
            list.Add(data);
        }
        return list;
    }

    private List<RawAnalyzerData> ReadAllRawAnalyzerData(CommandLineArguments args)
    {
        var list = new List<RawAnalyzerData>(args.AnalyzerReferences.Length);
        foreach (var analyzer in args.AnalyzerReferences)
        {
            var data = new RawAnalyzerData(RoslynUtil.GetMvid(analyzer.FilePath), analyzer.FilePath);
            list.Add(data);
        }
        return list;
    }

    private void CheckOwnership(CompilerCall compilerCall)
    {
        if (compilerCall.OwnerState is BinaryLogReader reader && object.ReferenceEquals(reader, this))
        {
            return;
        }

        throw new ArgumentException($"The provided {nameof(CompilerCall)} is not from this instance");
    }

    void IBasicAnalyzerHostDataProvider.CopyAssemblyBytes(RawAnalyzerData data, Stream stream)
    {
        using var fileStream = RoslynUtil.OpenBuildFileForRead(data.FilePath);
        fileStream.CopyTo(stream);
    }

    byte[] IBasicAnalyzerHostDataProvider.GetAssemblyBytes(RawAnalyzerData data) =>
        File.ReadAllBytes(data.FilePath);
}