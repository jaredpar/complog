using System.IO.Compression;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using CompilationInfo = System.Tuple<Basic.CompilerLog.Util.CompilerCall, Microsoft.CodeAnalysis.CommandLineArguments>;
using static Basic.CompilerLog.Util.CommonUtil;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.Emit;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogReaderVersion1 : CompilerLogReader
{
    internal CompilerLogReaderVersion1(ZipArchive zipArchive, Metadata metadata, BasicAnalyzerHostOptions? basicAnalyzersOptions, CompilerLogState? state)
        :base(zipArchive, metadata, basicAnalyzersOptions, state)
    {

    }

    private protected override object ReadCompilationInfo(int index)
    {
        using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        var compilerCall = ReadCompilerCallCore(reader, index);
        var commandLineArguments = compilerCall.ParseArguments();
        return new CompilationInfo(compilerCall, commandLineArguments);
    }

    private protected override CompilerCall ReadCompilerCallCore(int index, object rawInfo)
    {
        var info = (CompilationInfo)rawInfo;
        return info.Item1;
    }

    private CompilerCall ReadCompilerCallCore(StreamReader reader, int index)
    {
        var projectFile = reader.ReadLineOrThrow();
        var isCSharp = reader.ReadLineOrThrow() == "C#";
        var targetFramework = reader.ReadLineOrThrow();
        if (string.IsNullOrEmpty(targetFramework))
        {
            targetFramework = null;
        }

        var kind = (CompilerCallKind)Enum.Parse(typeof(CompilerCallKind), reader.ReadLineOrThrow());
        var count = int.Parse(reader.ReadLineOrThrow());
        var arguments = new string[count];
        for (int i = 0; i < count; i++)
        {
            arguments[i] = reader.ReadLineOrThrow();
        }

        return new CompilerCall(projectFile, kind, targetFramework, isCSharp, arguments, index);
    }

    private protected override RawCompilationData ReadRawCompilationDataCore(int index, object rawInfo)
    {
        var info = (CompilationInfo)rawInfo;
        var args = info.Item2;
        using var reader = Polyfill.NewStreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        var compilerCall = ReadCompilerCallCore(reader, index);

        var references = new List<RawReferenceData>();
        var analyzers = new List<RawAnalyzerData>();
        var contents = new List<RawContent>();
        var resources = new List<RawResourceData>();
        var readGeneratedFiles = false;

        while (reader.ReadLine() is string line)
        {
            var colonIndex = line.IndexOf(':');
            switch (line.AsSpan().Slice(0, colonIndex))
            {
                case "m":
                    ParseMetadataReference(line);
                    break;
                case "a":
                    ParseAnalyzer(line);
                    break;
                case "source":
                    ParseContent(line, RawContentKind.SourceText);
                    break;
                case "generated":
                    ParseContent(line, RawContentKind.GeneratedText);
                    break;
                case "generatedResult":
                    readGeneratedFiles = ParseBool();
                    break;
                case "config":
                    ParseContent(line, RawContentKind.AnalyzerConfig);
                    break;
                case "text":
                    ParseContent(line, RawContentKind.AdditionalText);
                    break;
                case "embed":
                    ParseContent(line, RawContentKind.Embed);
                    break;
                case "embedline":
                    ParseContent(line, RawContentKind.EmbedLine);
                    break;
                case "link":
                    ParseContent(line, RawContentKind.SourceLink);
                    break;
                case "ruleset":
                    ParseContent(line, RawContentKind.RuleSet);
                    break;
                case "appconfig":
                    ParseContent(line, RawContentKind.AppConfig);
                    break;
                case "win32manifest":
                    ParseContent(line, RawContentKind.Win32Manifest);
                    break;
                case "win32resource":
                    ParseContent(line, RawContentKind.Win32Resource);
                    break;
                case "cryptokeyfile":
                    ParseContent(line, RawContentKind.CryptoKeyFile);
                    break;
                case "r":
                    ParseResource(line);
                    break;
                case "win32icon":
                    ParseContent(line, RawContentKind.Win32Icon);
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized line: {line}");
            }

            bool ParseBool() =>
                colonIndex + 1 < line.Length &&
                line[colonIndex + 1] == '1';
        }

        var assemblyFileName = Path.GetFileNameWithoutExtension(compilerCall.ProjectFileName);
        var data = new RawCompilationData(
            index,
            args.CompilationName,
            assemblyFileName,
            args.DocumentationPath,
            args.OutputDirectory,
            args.ChecksumAlgorithm,
            references,
            analyzers,
            contents,
            resources,
            isCSharp: compilerCall.IsCSharp,
            readGeneratedFiles);

        return data;

        void ParseMetadataReference(string line)
        {
            var items = line.Split(':');
            if (items.Length == 5 &&
                Guid.TryParse(items[1], out var mvid) &&
                int.TryParse(items[2], out var kind))
            {
                var embedInteropTypes = items[3] == "1";

                string[]? aliases = null;
                if (!string.IsNullOrEmpty(items[4]))
                {
                    aliases = items[4].Split(',');
                }

                references.Add(new RawReferenceData(
                    mvid,
                    aliases,
                    embedInteropTypes));
                return;
            }

            throw new InvalidOperationException();
        }

        void ParseContent(string line, RawContentKind kind)
        {
            var items = line.Split(':', count: 3);
            contents.Add(new(items[2], items[1], kind));
        }

        void ParseResource(string line)
        {
            var items = line.Split(':', count: 5);
            var fileName = items[4];
            var isPublic = bool.Parse(items[3]);
            var contentHash = items[1];
            var dataProvider = () =>
            {
                var bytes = GetContentBytes(contentHash);
                return new MemoryStream(bytes);
            };

            var d = string.IsNullOrEmpty(fileName)
                ? new ResourceDescription(items[2], dataProvider, isPublic)
                : new ResourceDescription(items[2], fileName, dataProvider, isPublic);
            resources.Add(new(contentHash, d));
        }

        void ParseAnalyzer(string line)
        {
            var items = line.Split(':', count: 3);
            var mvid = Guid.Parse(items[1]);
            analyzers.Add(new RawAnalyzerData(mvid, items[2]));
        }
    }

    private protected override (EmitOptions, ParseOptions, CompilationOptions) ReadCompilerOptionsCore(int index, object rawInfo)
    {
        var info = (CompilationInfo)rawInfo;
        var args = info.Item2;
        return (args.EmitOptions, args.ParseOptions, args.CompilationOptions);
    }
}