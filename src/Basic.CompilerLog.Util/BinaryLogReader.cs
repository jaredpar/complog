using System.Collections.Immutable;
using Basic.CompilerLog.Util.Impl;
using MessagePack.Formatters;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

public sealed class BinaryLogReader(List<CompilerCall> compilerCalls) : ICompilerCallReader, IBasicAnalyzerHostDataProvider
{
    private List<CompilerCall> _compilerCalls = compilerCalls;

    // TODO: figure out lifetime and init of this
    private CompilerLogState _state = new CompilerLogState();

    public CompilerLogState CompilerLogState => _state;

    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        return _compilerCalls.Where(predicate).ToList();
    }

    // TODO: implement
    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null)
    {
        // TODO: this is a hack, need to do the filter
        return _compilerCalls.Select(Convert).ToList();
    }

    // TODO:
    //  - CompilationName isn't a perfect match for CompilerLog. Need to fix that.
    public CompilationData Convert(CompilerCall compilerCall)
    {
        var args = compilerCall.ParseArguments();

        var references = GetReferences();
        var sourceTexts = GetSourceTexts();
        var additionalTexts = GetAdditionalTexts();
        var analyzerConfigs = GetAnalyzerConfigs();
        var emitData = GetEmitData();
        var basicAnalyzerHost = CreateAnalyzerHost();

        // TODO: handle visual basic
        return GetCSharp();

        CompilationData GetCSharp()
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

    void IBasicAnalyzerHostDataProvider.CopyAssemblyBytes(RawAnalyzerData data, Stream stream)
    {
        using var fileStream = new FileStream(data.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fileStream.CopyTo(stream);
    }
}