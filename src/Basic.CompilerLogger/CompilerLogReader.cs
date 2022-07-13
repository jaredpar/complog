using Microsoft.Build.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Basic.CompilerLogger.CommonUtil;

namespace Basic.CompilerLogger;

internal sealed class CompilerLogReader : IDisposable
{
    private readonly Dictionary<Guid, MetadataReference> _refMap = new Dictionary<Guid, MetadataReference>();

    internal ZipArchive ZipArchive { get; set; }
    internal int CompilationCount { get; }

    public CompilerLogReader(Stream stream)
    {
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(MetadataFileName), ContentEncoding, leaveOpen: false);
        var line = reader.ReadLine();
        if (line is null)
            throw new InvalidOperationException();
        var items = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (items.Length != 2 || !int.TryParse(items[1], out var count))
            throw new InvalidOperationException();
        CompilationCount = count;
    }

    internal CompilationData ReadCompilation(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        var projectFile = reader.ReadLineOrThrow();
        var isCSharp = reader.ReadLineOrThrow() == "C#";
        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var metadataReferenceList = new List<MetadataReference>();
        var analyzerBuilder = ImmutableArray.CreateBuilder<AnalyzerReference>();
        var additionalTextBuilder = ImmutableArray.CreateBuilder<AdditionalText>();

        var done = false;
        while (!done && reader.ReadLine() is string line)
        {
            switch (line[0])
            {
                case 'm':
                    ParseMetadataReference(line);
                    break;
                case 'a':
                    ParseAnalyzer(line);
                    break;
                case 's':
                    ParseSourceText(line);
                    break;
                case 't':
                    ParseAdditionalText(line);
                    break;
                case '#':
                    done = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized line: {line}");
            }
        }

        var rawArgs = new List<string>();
        while (reader.ReadLine() is string line)
        {
            rawArgs.Add(line);
        }

        return isCSharp
            ? CreateCSharp()
            : CreateVisualBasic();

        void ParseMetadataReference(string line)
        {
            var items = line.Split(':');
            if (items.Length == 5 &&
                Guid.TryParse(items[1], out var mvid) &&
                int.TryParse(items[2], out var kind))
            {
                var reference = GetMetadataReference(mvid);
                if (items[3] == "1")
                    reference = reference.WithEmbedInteropTypes(true);

                if (!string.IsNullOrEmpty(items[4]))
                {
                    var aliases = items[4].Split(',');
                    reference = reference.WithAliases(aliases);
                }

                metadataReferenceList.Add(reference);
                return;
            }

            throw new InvalidOperationException();
        }

        void ParseSourceText(string line)
        {
            var items = line.Split(':', count: 3);
            if (items.Length == 3)
            {
                var sourceText = GetSourceText(items[1]);
                sourceTextList.Add((sourceText, items[2]));
                return;
            }

            throw new InvalidOperationException();
        }

        void ParseAdditionalText(string line)
        {
            var items = line.Split(':', count: 3);
            if (items.Length == 3)
            {
                var sourceText = GetSourceText(items[1]);
                var text = new BasicAdditionalTextFile(items[2], sourceText);
                additionalTextBuilder.Add(text);
                return;
            }

            throw new InvalidOperationException();
        }

        void ParseAnalyzer(string line)
        {
            // TODO: figure out how we want to represent this
        }

        CSharpCompilationData CreateCSharp()
        {
            var args = CSharpCommandLineParser.Default.Parse(rawArgs, Path.GetDirectoryName(projectFile), sdkDirectory: null, additionalReferenceDirectories: null);
            var parseOptions = args.ParseOptions;
            var syntaxTrees = sourceTextList
                .Select(t => CSharpSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path))
                .ToArray();

            // TODO: copy the code from rebuild to get the assembly name correct
            var compilation = CSharpCompilation.Create(
                "todo",
                syntaxTrees,
                metadataReferenceList,
                args.CompilationOptions);

            return new CSharpCompilationData(compilation, args);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            throw null!;
        }
    }

    internal MetadataReference GetMetadataReference(Guid mvid)
    {
        if (_refMap.TryGetValue(mvid, out var metadataReference))
        {
            return metadataReference;
        }

        using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        var bytes = entryStream.ReadAll();

        // TODO: should we include the file path in the image?
        metadataReference = MetadataReference.CreateFromImage(bytes);
        _refMap.Add(mvid, metadataReference);
        return metadataReference;
    }

    internal SourceText GetSourceText(string contentHash)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));

        // TODO: need to expose the real API for how the compiler reads source files. 
        // move this comment to the rehydration code when we write it.
        //
        // TODO: need to use the hash algorithm from the command line arguments. Burn it
        // into this line
        return SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
    }

    public void Dispose()
    {
        ZipArchive.Dispose();
        ZipArchive = null!;
    }
}
