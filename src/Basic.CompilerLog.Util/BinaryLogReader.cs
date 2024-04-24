using System.Collections.Immutable;
using Basic.CompilerLog.Util.Impl;
using MessagePack.Formatters;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLog.Util;

public sealed class BinaryLogReader : IDisposable, ICompilerCallReader, IBasicAnalyzerHostDataProvider
{
    private Stream _stream;
    private readonly bool _leaveOpen;

    // TODO: figure out lifetime and init of this
    private CompilerLogState _state = new CompilerLogState();

    public CompilerLogState CompilerLogState => _state;
    public bool IsDisposed => _stream is null;

    private BinaryLogReader(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }   

    public static BinaryLogReader Create(
        Stream stream,
        bool leaveOpen) =>
        new BinaryLogReader(stream, leaveOpen);

    public static BinaryLogReader Create(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Create(stream, leaveOpen: false);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
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

        // TODO: need to remove this and consider just throwing exceptions here instead. Look inside
        // compiler log to see what it does for exception during read and get some symetry with it
        var diagnosticList = new List<string>();
        return BinaryLogUtil.ReadAllCompilerCalls(_stream, diagnosticList, predicate, ownerState: this);
    }

    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null)
    {
        var list = new List<CompilationData>();
        foreach (var compilerCall in ReadAllCompilerCalls(predicate))
        {
            list.Add(Convert(compilerCall));
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

    public CompilationData Convert(CompilerCall compilerCall)
    {
        CheckOwnership(compilerCall);
        var args = ReadCommandLineArguments(compilerCall);

        var references = GetReferences();
        var sourceTexts = GetSourceTexts();
        var additionalTexts = GetAdditionalTexts();
        var analyzerConfigs = GetAnalyzerConfigs();
        var emitData = GetEmitData();
        var basicAnalyzerHost = CreateAnalyzerHost();

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

        // TODO: this is tough. Existing hosts are way to tied to CompilerLogReader. Have to break that apart.
        BasicAnalyzerHost CreateAnalyzerHost()
        {
            var list = new List<RawAnalyzerData>(args.AnalyzerReferences.Length);
            foreach (var analyzer in args.AnalyzerReferences)
            {
                var data = new RawAnalyzerData(RoslynUtil.GetMvid(analyzer.FilePath), analyzer.FilePath);
                list.Add(data);
            }
            
            return new BasicAnalyzerHostOnDisk(this, list);
        }

        List<(SourceText SourceText, string Path)> GetAnalyzerConfigs() => 
            GetSourceTextsFromPaths(args.AnalyzerConfigPaths, args.AnalyzerConfigPaths.Length, args.ChecksumAlgorithm);

        List<MetadataReference> GetReferences()
        {
            var list = new List<MetadataReference>(capacity: args.MetadataReferences.Length);
            foreach (var reference in args.MetadataReferences)
            {
                // TODO: should cache this
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
        using var fileStream = new FileStream(data.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fileStream.CopyTo(stream);
    }
}