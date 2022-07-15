using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogReader : IDisposable
{
    private readonly Dictionary<Guid, MetadataReference> _refMap = new Dictionary<Guid, MetadataReference>();
    private readonly Dictionary<Guid, (string FileName, AssemblyName AssemblyName)> _mvidToRefInfoMap = new();

    internal ZipArchive ZipArchive { get; set; }
    internal int CompilationCount { get; }

    public CompilerLogReader(Stream stream)
    {
        try
        {
            ZipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            // Happens when this is not a valid zip file
            throw GetInvalidCompilerLogFileException();
        }

        CompilationCount = ReadMetadata();
        ReadAssemblyInfo();

        int ReadMetadata()
        {
            var entry = ZipArchive.GetEntry(MetadataFileName);
            if (entry is null)
                throw GetInvalidCompilerLogFileException();

            using var reader = new StreamReader(entry.Open(), ContentEncoding, leaveOpen: false);
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

        static Exception GetInvalidCompilerLogFileException() => new ArgumentException("Provided stream is not a compiler log file");
    }

    internal CompilerCall ReadCompilerCall(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        return ReadCompilerCallCore(reader);
    }

    internal CompilationData ReadCompilation(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);
        var compilerCall = ReadCompilerCallCore(reader);

        CommandLineArguments args = compilerCall.IsCSharp 
            ? CSharpCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFile), sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(compilerCall.Arguments, Path.GetDirectoryName(compilerCall.ProjectFile), sdkDirectory: null, additionalReferenceDirectories: null);
        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigList = new List<(SourceText SourceText, string Path)>();
        var metadataReferenceList = new List<MetadataReference>();
        var analyzers = new List<Guid>();
        var additionalTextBuilder = ImmutableArray.CreateBuilder<AdditionalText>();

        while (reader.ReadLine() is string line)
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
                case 'c':
                    ParseAnalyzerConfig(line);
                    break;
                case 't':
                    ParseAdditionalText(line);
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized line: {line}");
            }
        }

        return compilerCall.IsCSharp
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
            var sourceText = GetSourceText(items[1], args.ChecksumAlgorithm);
            sourceTextList.Add((sourceText, items[2]));
        }

        void ParseAnalyzerConfig(string line)
        {
            var items = line.Split(':', count: 3);
            var sourceText = GetSourceText(items[1], args.ChecksumAlgorithm);
            analyzerConfigList.Add((sourceText, items[2]));
        }

        void ParseAdditionalText(string line)
        {
            var items = line.Split(':', count: 3);
            var sourceText = GetSourceText(items[1], args.ChecksumAlgorithm);
            var text = new BasicAdditionalTextFile(items[2], sourceText);
            additionalTextBuilder.Add(text);
        }

        void ParseAnalyzer(string line)
        {
            var items = line.Split(':', count: 2);
            var mvid = Guid.Parse(items[1]);
            analyzers.Add(mvid);
        }

        BasicAssemblyLoadContext CreateAssemblyLoadContext()
        {
            var loadContext = new BasicAssemblyLoadContext(compilerCall.ProjectFile);

            foreach (var mvid in analyzers)
            {
                using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
                var analyzerBytes = entryStream.ReadAllBytes();
                loadContext.LoadFromStream(new MemoryStream(analyzerBytes.ToArray()));
            }

            return loadContext;
        }

        (SyntaxTreeOptionsProvider, AnalyzerConfigOptionsProvider) CreateOptionsProviders(IEnumerable<SyntaxTree> syntaxTrees, IEnumerable<AdditionalText> additionalTexts)
        {
            AnalyzerConfigOptionsResult globalConfigOptions = default;
            AnalyzerConfigSet? analyzerConfigSet = null;
            var resultList = new List<(object, AnalyzerConfigOptionsResult)>();

            if (analyzerConfigList.Count > 0)
            {
                var list = new List<AnalyzerConfig>();
                foreach (var tuple in analyzerConfigList)
                {
                    list.Add(AnalyzerConfig.Parse(tuple.SourceText, tuple.Path));
                }

                analyzerConfigSet = AnalyzerConfigSet.Create(list);
                globalConfigOptions = analyzerConfigSet.GlobalConfigOptions;
            }

            foreach (var syntaxTree in syntaxTrees)
            {
                resultList.Add((syntaxTree, analyzerConfigSet?.GetOptionsForSourcePath(syntaxTree.FilePath) ?? default));
            }

            foreach (var additionalText in additionalTexts)
            {
                resultList.Add((additionalText, analyzerConfigSet?.GetOptionsForSourcePath(additionalText.Path) ?? default));
            }

            var syntaxOptionsProvider = new BasicSyntaxTreeOptionsProvider(
                isConfigEmpty: analyzerConfigList.Count == 0,
                globalConfigOptions,
                resultList);
            var analyzerConfigOptionsProvider = new BasicAnalyzerConfigOptionsProvider(
                isConfigEmpty: analyzerConfigList.Count == 0,
                globalConfigOptions,
                resultList);
            return (syntaxOptionsProvider, analyzerConfigOptionsProvider);
        }

        CSharpCompilationData CreateCSharp()
        {
            var csharpArgs = (CSharpCommandLineArguments)args;
            var parseOptions = csharpArgs.ParseOptions;

            var syntaxTrees = new SyntaxTree[sourceTextList.Count];
            Parallel.For(
                0,
                sourceTextList.Count,
                i =>
                {
                    var t = sourceTextList[i];
                    syntaxTrees[i] = CSharpSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
                });
            var additionalTexts = additionalTextBuilder.ToImmutable();

            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextBuilder);
            var compilation = CSharpCompilation.Create(
                args.CompilationName,
                syntaxTrees,
                metadataReferenceList,
                csharpArgs.CompilationOptions.WithSyntaxTreeOptionsProvider(syntaxProvider));

            return new CSharpCompilationData(
                compilerCall,
                compilation,
                csharpArgs,
                additionalTextBuilder.ToImmutable(),
                CreateAssemblyLoadContext(),
                analyzerProvider);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            var basicArgs = (VisualBasicCommandLineArguments)args;
            var parseOptions = basicArgs.ParseOptions;
            var syntaxTrees = new SyntaxTree[sourceTextList.Count];
            Parallel.For(
                0,
                sourceTextList.Count,
                i =>
                {
                    var t = sourceTextList[i];
                    syntaxTrees[i] = VisualBasicSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
                });
            var additionalTexts = additionalTextBuilder.ToImmutable();

            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextBuilder);

            var compilation = VisualBasicCompilation.Create(
                args.CompilationName,
                syntaxTrees,
                metadataReferenceList,
                basicArgs.CompilationOptions.WithSyntaxTreeOptionsProvider(syntaxProvider));

            return new VisualBasicCompilationData(
                compilerCall,
                compilation,
                basicArgs,
                additionalTextBuilder.ToImmutable(),
                CreateAssemblyLoadContext(),
                analyzerProvider);
        }
    }

    private CompilerCall ReadCompilerCallCore(StreamReader reader)
    {
        var projectFile = reader.ReadLineOrThrow();
        var isCSharp = reader.ReadLineOrThrow() == "C#";
        var targetFramework = reader.ReadLineOrThrow();
        if (string.IsNullOrEmpty(targetFramework))
        {
            targetFramework = null;
        }

        var kind = Enum.Parse<CompilerCallKind>(reader.ReadLineOrThrow());
        var count = int.Parse(reader.ReadLineOrThrow());
        var arguments = new string[count];
        for (int i = 0; i < count; i++)
        {
            arguments[i] = reader.ReadLineOrThrow();
        }

        return new CompilerCall(projectFile, kind, targetFramework, isCSharp, arguments);
    }

    internal MetadataReference GetMetadataReference(Guid mvid)
    {
        if (_refMap.TryGetValue(mvid, out var metadataReference))
        {
            return metadataReference;
        }

        using var entryStream = ZipArchive.OpenEntryOrThrow(GetAssemblyEntryName(mvid));
        var bytes = entryStream.ReadAllBytes();

        var tuple = _mvidToRefInfoMap[mvid];
        metadataReference = MetadataReference.CreateFromStream(new MemoryStream(bytes.ToArray()), filePath: tuple.FileName);
        _refMap.Add(mvid, metadataReference);
        return metadataReference;
    }

    internal SourceText GetSourceText(string contentHash, SourceHashAlgorithm checksumAlgorithm)
    {
        using var stream = ZipArchive.OpenEntryOrThrow(GetContentEntryName(contentHash));

        // TODO: need to expose the real API for how the compiler reads source files. 
        // move this comment to the rehydration code when we write it.
        return SourceText.From(stream, checksumAlgorithm: checksumAlgorithm);
    }

    public void Dispose()
    {
        ZipArchive.Dispose();
        ZipArchive = null!;
    }
}
