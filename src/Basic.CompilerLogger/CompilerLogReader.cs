using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using static Basic.CompilerLogger.CommonUtil;

namespace Basic.CompilerLogger;

internal sealed class CompilerLogReader : IDisposable
{
    private readonly Dictionary<Guid, MetadataReference> _refMap = new Dictionary<Guid, MetadataReference>();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();

    internal ZipArchive ZipArchive { get; set; }
    internal int CompilationCount { get; }

    public CompilerLogReader(Stream stream)
    {
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        CompilationCount = ReadMetadata();
        ReadAssemblyInfo();

        int ReadMetadata()
        {
            using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(MetadataFileName), ContentEncoding, leaveOpen: false);
            var line = reader.ReadLineOrThrow();
            var items = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (items.Length != 2 || !int.TryParse(items[1], out var count))
                throw new InvalidOperationException();
            return count;
        }

        void ReadAssemblyInfo()
        {
            using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(AssemblyInfoFileName), ContentEncoding, leaveOpen: false);
            while (reader.ReadLine() is string line)
            {
                var items = line.Split(':', count: 3);
                var mvid = Guid.Parse(items[1]);
                var assemblyName = new AssemblyName(items[2]);
                _mvidToRefInfoMap[mvid] = (items[0], assemblyName);
            }
        }
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
        var analyzers = new List<Guid>();
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
            var sourceText = GetSourceText(items[1]);
            sourceTextList.Add((sourceText, items[2]));
        }

        void ParseAdditionalText(string line)
        {
            var items = line.Split(':', count: 3);
            var sourceText = GetSourceText(items[1]);
            var text = new BasicAdditionalTextFile(items[2], sourceText);
            additionalTextBuilder.Add(text);
        }

        void ParseAnalyzer(string line)
        {
            var items = line.Split(':', count: 2);
            var mvid = Guid.Parse(items[1]);
            analyzers.Add(mvid);
        }

        CompilerLogAssemblyLoadContext CreateAssemblyLoadContext()
        {
            var loadContext = new CompilerLogAssemblyLoadContext(projectFile);

            foreach (var mvid in analyzers)
            {
                using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
                var analyzerBytes = entryStream.ReadAllBytes();
                loadContext.LoadFromStream(new MemoryStream(analyzerBytes.ToArray()));
            }

            return loadContext;
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

            return new CSharpCompilationData(compilation, args, CreateAssemblyLoadContext());
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
        var bytes = entryStream.ReadAllBytes();

        // TODO: should we include the file path / name in the image?
        metadataReference = MetadataReference.CreateFromStream(new MemoryStream(bytes.ToArray()));
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
