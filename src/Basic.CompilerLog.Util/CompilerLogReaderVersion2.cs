using System.CodeDom.Compiler;
using System.IO.Compression;
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Serialize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogReaderVersion2 : CompilerLogReader
{
    internal CompilerLogReaderVersion2(ZipArchive zipArchive, Metadata metadata, BasicAnalyzerHostOptions? basicAnalyzersOptions, CompilerLogState? state)
        :base(zipArchive, metadata, basicAnalyzersOptions, state)
    {

    }

    private protected override object ReadCompilationInfo(int index)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index));
        return MessagePack.MessagePackSerializer.Deserialize<CompilationInfoPack>(stream);
    }

    private protected override CompilerCall ReadCompilerCallCore(int index, object rawInfo)
    {
        var pack = (CompilationInfoPack)rawInfo;
        return new CompilerCall(
            pack.ProjectFilePath,
            pack.CompilerCallKind,
            pack.TargetFramework,
            pack.IsCSharp,
            new Lazy<string[]>(() => GetContentPack<string[]>(pack.CommandLineArgsHash)),
            index);
    }

    private protected override RawCompilationData ReadRawCompilationDataCore(int index, object rawInfo)
    {
        var pack = (CompilationInfoPack)rawInfo;
        var dataPack = GetContentPack<CompilationDataPack>(pack.CompilationDataPackHash);

        var references = dataPack
            .References
            .Select(x => new RawReferenceData(x.Mvid, x.Aliases.ToArray(), x.EmbedInteropTypes))
            .ToList();
        var analyzers = dataPack
            .Analyzers
            .Select(x => new RawAnalyzerData(x.Mvid, x.FilePath))
            .ToList();
        var contents = dataPack
            .ContentList
            .Select(x => new RawContent(x.Item2.FilePath, x.Item2.ContentHash, (RawContentKind)x.Item1))
            .ToList();
        var resources = dataPack
            .Resources
            .Select(x => new RawResourceData(x.ContentHash, CreateResourceDescription(this, x)))
            .ToList();

        return new RawCompilationData(
            index,
            compilationName: dataPack.ValueMap["compilationName"],
            assemblyFileName: dataPack.ValueMap["assemblyFileName"]!,
            xmlFilePath: dataPack.ValueMap["xmlFilePath"],
            outputDirectory: dataPack.ValueMap["outputDirectory"],
            dataPack.ChecksumAlgorithm,
            references,
            analyzers,
            contents,
            resources,
            pack.IsCSharp,
            dataPack.IncludesGeneratedText);

        static ResourceDescription CreateResourceDescription(CompilerLogReader reader, ResourcePack pack)
        {
            var dataProvider = () =>
            {
                var bytes = reader.GetContentBytes(pack.ContentHash);
                return new MemoryStream(bytes);
            };

            return string.IsNullOrEmpty(pack.FileName)
                ? new ResourceDescription(pack.Name, dataProvider, pack.IsPublic)
                : new ResourceDescription(pack.Name, pack.FileName, dataProvider, pack.IsPublic);
        }
    }

    private protected override (EmitOptions, ParseOptions, CompilationOptions) ReadCompilerOptionsCore(int index, object rawInfo)
    {
        var pack = (CompilationInfoPack)rawInfo;
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
}