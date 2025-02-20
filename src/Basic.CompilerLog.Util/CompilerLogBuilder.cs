using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Microsoft.Extensions.ObjectPool;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Basic.CompilerLog.Util.CommonUtil;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogBuilder : IDisposable
{
    private sealed class MemoryStreamPoolPolicy : IPooledObjectPolicy<MemoryStream>
    {
        public MemoryStream Create() => new MemoryStream();
        public bool Return(MemoryStream stream)
        { 
            stream.Position = 0;
            return true;
        }
    }

    private readonly Dictionary<Guid, (string FileName, string AssemblyName)> _mvidToRefInfoMap = new();
    private readonly Dictionary<string, (Guid Mvid, string? AssemblyName, string? AssemblyInformationVersion)> _assemblyPathToMvidMap = new(PathUtil.Comparer);
    private readonly HashSet<string> _contentHashMap = new(PathUtil.Comparer);
    private readonly Dictionary<string, (string AssemblyName, string? CommitHash)> _compilerInfoMap = new(PathUtil.Comparer);
    private readonly List<(int CompilerCallIndex, bool IsRefAssembly, Guid Mvid)> _compilerCallMvidList = new();
    private readonly DefaultObjectPool<MemoryStream> _memoryStreamPool = new(new MemoryStreamPoolPolicy(), maximumRetained: 5);

    private int _compilationCount;
    private bool _closed;

    internal int MetadataVersion { get; }
    internal List<string> Diagnostics { get; }
    internal ZipArchive ZipArchive { get; private set; }

    internal bool IsOpen => !_closed;
    internal bool IsClosed => _closed;

    internal CompilerLogBuilder(Stream stream, List<string> diagnostics, int? metadataVersion = null)
    {
        MetadataVersion = metadataVersion ?? Metadata.LatestMetadataVersion;
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Adds a compilation into the builder and returns the index of the entry
    /// </summary>
    internal void AddFromDisk(CompilerCall compilerCall, CommandLineArguments commandLineArguments)
    {
        var infoPack = new CompilationInfoPack()
        {
            CompilerFilePath = compilerCall.CompilerFilePath,
            ProjectFilePath = compilerCall.ProjectFilePath,
            IsCSharp = compilerCall.IsCSharp,
            TargetFramework = compilerCall.TargetFramework,
            CompilerCallKind = compilerCall.Kind,
            CommandLineArgsHash = WriteContentMessagePack(compilerCall.GetArguments()),
            CompilationDataPackHash = AddCompilationDataPack(commandLineArguments),
        };

        AddCompilerInfo(infoPack, compilerCall);
        AddCompilationOptions(infoPack, commandLineArguments, compilerCall);
        AddCore(infoPack);

        string AddCompilationDataPack(CommandLineArguments commandLineArguments)
        {
            var dataPack = new CompilationDataPack()
            {
                ContentList = new(),
                ValueMap = new(),
                References = new(),
                Analyzers = new(),
                Resources = new(),
            };
            AddCommandLineArgumentValues(dataPack, commandLineArguments);
            AddReferences(dataPack, commandLineArguments);
            AddAnalyzers(dataPack, commandLineArguments);
            AddAnalyzerConfigs(dataPack, commandLineArguments);
            AddGeneratedFiles(dataPack, commandLineArguments, compilerCall);
            AddSources(dataPack, commandLineArguments);
            AddAdditionalTexts(dataPack, commandLineArguments);
            AddResources(dataPack, commandLineArguments);
            AddEmbeds(dataPack, compilerCall, commandLineArguments);
            AddContentIf(dataPack, RawContentKind.SourceLink, commandLineArguments.SourceLink);
            AddContentIf(dataPack, RawContentKind.RuleSet, commandLineArguments.RuleSetPath);
            AddContentIf(dataPack, RawContentKind.AppConfig, commandLineArguments.AppConfigPath);
            AddContentIf(dataPack, RawContentKind.Win32Resource, commandLineArguments.Win32ResourceFile);
            AddContentIf(dataPack, RawContentKind.Win32Icon, commandLineArguments.Win32Icon);
            AddContentIf(dataPack, RawContentKind.Win32Manifest, commandLineArguments.Win32Manifest);
            AddContentIf(dataPack, RawContentKind.CryptoKeyFile, commandLineArguments.CompilationOptions.CryptoKeyFile);
            AddAssemblyMvid(commandLineArguments);
            return WriteContentMessagePack(dataPack);
        }

        void AddContentIf(CompilationDataPack dataPack, RawContentKind kind, string? filePath)
        {
            if (Resolve(filePath) is { } resolvedFilePath)
            {
                AddContentFromDisk(dataPack, kind, resolvedFilePath);
            }
        }

        [return: NotNullIfNotNull("filePath")]
        string? Resolve(string? filePath)
        {
            if (filePath is null)
            {
                return null;
            }

            if (Path.IsPathRooted(filePath))
            {
                return filePath;
            }

            return Path.Combine(compilerCall.ProjectDirectory, filePath);
        }

        void AddCompilerInfo(CompilationInfoPack infoPack, CompilerCall compilerCall)
        {
            if (compilerCall.CompilerFilePath is null)
            {
                Diagnostics.Add($"Cannot find compiler for {compilerCall.GetDiagnosticName()}");
                return;
            }

            if (!_compilerInfoMap.TryGetValue(compilerCall.CompilerFilePath, out var compilerInfo))
            {
                var name = AssemblyName.GetAssemblyName(compilerCall.CompilerFilePath);
                compilerInfo.AssemblyName = name.ToString();
                compilerInfo.CommitHash = RoslynUtil.ReadCompilerCommitHash(compilerCall.CompilerFilePath);
                if (compilerInfo.CommitHash is null)
                {
                    Diagnostics.Add($"Cannot find commit hash for {compilerCall.CompilerFilePath}");
                }

                _compilerInfoMap[compilerCall.CompilerFilePath] = compilerInfo;
            }

            infoPack.CompilerAssemblyName = compilerInfo.AssemblyName;
            infoPack.CompilerCommitHash = compilerInfo.CommitHash;
        }

        void AddCompilationOptions(CompilationInfoPack infoPack, CommandLineArguments args, CompilerCall compilerCall)
        {
            infoPack.EmitOptionsHash = WriteContentMessagePack(MessagePackUtil.CreateEmitOptionsPack(args.EmitOptions));

            if (compilerCall.IsCSharp)
            {
                infoPack.ParseOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateCSharpParseOptionsPack((CSharpParseOptions)args.ParseOptions));
                infoPack.CompilationOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateCSharpCompilationOptionsPack((CSharpCompilationOptions)args.CompilationOptions));
            }
            else
            {
                infoPack.ParseOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateVisualBasicParseOptionsPack((VisualBasicParseOptions)args.ParseOptions));
                infoPack.CompilationOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateVisualBasicCompilationOptionsPack((VisualBasicCompilationOptions)args.CompilationOptions));
            }
        }

        void AddAssemblyMvid(CommandLineArguments args)
        {
            var (assemblyFilePath, refAssemblyFilePath) = RoslynUtil.GetAssemblyOutputFilePaths(args);
            AddIf(assemblyFilePath, false);
            AddIf(refAssemblyFilePath, false);
            void AddIf(string? filePath, bool isRefAssembly)
            {
                if (filePath is not null && File.Exists(filePath))
                {
                    try
                    {
                        var mvid = RoslynUtil.ReadMvid(filePath);
                        _compilerCallMvidList.Add((_compilationCount, isRefAssembly, mvid));
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Add($"Could not read emit assembly MVID for {filePath}: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Add the compiler call content into the builder.
    /// </summary>
    /// <remarks>
    /// This is useful for building up compilations on the fly for testing.
    /// </remarks>
    internal void AddContent(
        CompilerCall compilerCall,
        string[] sources,
        CommandLineArguments? commandLineArguments = null)
    {
        commandLineArguments ??= GetEmptyCommandLineArguments();

        var dataPack = new CompilationDataPack()
        {
            ContentList = new(),
            ValueMap = new(),
            References = new(),
            Analyzers = new(),
            Resources = new(),
        };

        AddCommandLineArgumentValues(dataPack, commandLineArguments);

        foreach (var (i, source) in sources.Index())
        {
            AddContent(dataPack, RawContentKind.SourceText, filePath: $"source{i}.cs", content: source);
        }

        var infoPack = new CompilationInfoPack()
        {
            CompilerFilePath = compilerCall.CompilerFilePath,
            ProjectFilePath = compilerCall.ProjectFilePath,
            IsCSharp = compilerCall.IsCSharp,
            TargetFramework = compilerCall.TargetFramework,
            CompilerCallKind = compilerCall.Kind,
            CommandLineArgsHash = WriteContentMessagePack(compilerCall.GetArguments()),
            CompilationDataPackHash = WriteContentMessagePack(dataPack)
        };
        
        AddCore(infoPack);

        CommandLineArguments GetEmptyCommandLineArguments() => compilerCall.IsCSharp
            ? CSharpCommandLineParser.Default.Parse([], baseDirectory: null, sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse([], baseDirectory: null, sdkDirectory: null, additionalReferenceDirectories: null);
    }

    private void AddCore(CompilationInfoPack infoPack)
    {
        var index = _compilationCount;
        var entry = ZipArchive.CreateEntry(GetCompilerEntryName(index), CompressionLevel.Fastest);
        using (var entryStream = entry.Open())
        {
            MessagePackSerializer.Serialize(entryStream, infoPack, SerializerOptions);
        }

        _compilationCount++;
    }

    public void Close()
    {
        if (IsClosed)
            throw new InvalidOperationException();

        try
        {
            WriteMetadata();
            WriteLogInfo();
            ZipArchive.Dispose();
            ZipArchive = null!;
        }
        finally
        {
            _closed = true;
        }

        void WriteMetadata()
        {
            var entry = ZipArchive.CreateEntry(MetadataFileName, CompressionLevel.Fastest);
            using var writer = Polyfill.NewStreamWriter(entry.Open(), ContentEncoding, leaveOpen: false);
            Metadata.Create(_compilationCount, MetadataVersion).Write(writer);
        }

        void WriteLogInfo()
        {
            var pack = new LogInfoPack()
            {
                CompilerCallMvidList = _compilerCallMvidList,
                MvidToReferenceInfoMap = _mvidToRefInfoMap
            };
            var contentHash = WriteContentMessagePack(pack);;
            var entry = ZipArchive.CreateEntry(LogInfoFileName, CompressionLevel.Fastest);
            using var writer = Polyfill.NewStreamWriter(entry.Open(), ContentEncoding, leaveOpen: false);
            writer.WriteLine(contentHash);
        }
    }

    private void AddContent(CompilationDataPack dataPack, RawContentKind kind, string filePath, Stream stream)
    {
        var contentHash = WriteContent(stream);

        dataPack.ContentList.Add(((int)kind, new ContentPack()
        {
            ContentHash = contentHash,
            FilePath = filePath
        }));
    }

    private bool AddContentFromDisk(CompilationDataPack dataPack, RawContentKind kind, string filePath)
    {
        var contentHash = WriteContentFromDisk(filePath);

        dataPack.ContentList.Add(((int)kind, new ContentPack()
        {
            ContentHash = contentHash,
            FilePath = filePath
        }));

        return contentHash is not null;
    }

    private void AddContent(CompilationDataPack dataPack, RawContentKind kind, string filePath, string content)
    {
        using var stream = new StringStream(content, ContentEncoding);
        dataPack.ContentList.Add(((int)kind, new ContentPack()
        {
            ContentHash = WriteContent(stream),
            FilePath = filePath
        }));
    }

    private void AddAnalyzerConfigs(CompilationDataPack dataPack, CommandLineArguments args)
    {
        foreach (var filePath in args.AnalyzerConfigPaths)
        {
            AddContentFromDisk(dataPack, RawContentKind.AnalyzerConfig, filePath);
        }
    }

    private void AddCommandLineArgumentValues(CompilationDataPack dataPack, CommandLineArguments args)
    {
        dataPack.ValueMap.Add("assemblyFileName", RoslynUtil.GetAssemblyFileName(args));
        dataPack.ValueMap.Add("xmlFilePath", args.DocumentationPath);
        dataPack.ValueMap.Add("outputDirectory", args.OutputDirectory);
        dataPack.ValueMap.Add("compilationName", args.CompilationName);
        dataPack.ChecksumAlgorithm = args.ChecksumAlgorithm;
        dataPack.EmitPdb = args.EmitPdb;
    }

    private void AddSources(CompilationDataPack dataPack, CommandLineArguments args)
    {
        foreach (var commandLineFile in args.SourceFiles)
        {
            AddContentFromDisk(dataPack, RawContentKind.SourceText, commandLineFile.Path);
        }
    }

    /// <summary>
    /// Attempt to add all the generated files from generators. When successful the generators
    /// don't need to be run when re-hydrating the compilation.
    /// </summary>
    private void AddGeneratedFiles(CompilationDataPack dataPack, CommandLineArguments args, CompilerCall compilerCall)
    {
        if (!RoslynUtil.HasGeneratedFilesInPdb(args))
        {
            dataPack.HasGeneratedFilesInPdb = false;
            dataPack.IncludesGeneratedText = false;
            return;
        }

        dataPack.HasGeneratedFilesInPdb = true;
        try
        {
            var generatedFiles = RoslynUtil.ReadGeneratedFilesFromPdb(compilerCall, args);
            foreach (var tuple in generatedFiles)
            {
                AddContent(dataPack, RawContentKind.GeneratedText, tuple.FilePath, tuple.Stream);
            }
            dataPack.IncludesGeneratedText = true;
        }
        catch (Exception ex)
        {
            dataPack.IncludesGeneratedText = false;
            Diagnostics.Add(ex.Message);
        }
    }

    /// <summary>
    /// Add the <paramref name="value"/> as content using message pack serialization
    /// </summary>
    private string WriteContentMessagePack<T>(T value)
    {
        var stream = _memoryStreamPool.Get();
        try
        {
            MessagePackSerializer.Serialize(stream, value, SerializerOptions);
            stream.Position = 0;
            return WriteContent(stream);
        }
        finally
        {
            _memoryStreamPool.Return(stream);
        }
    }

    private string? WriteContentFromDisk(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Diagnostics.Add(RoslynUtil.GetMissingFileDiagnosticMessage(filePath));
            return null;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return WriteContent(stream);
    }

    /// <summary>
    /// Add a source file to the storage and return the stored name of the content in our 
    /// storage. This will be a checksum of the content itself
    /// </summary>
    private string WriteContent(Stream stream)
    {
        Debug.Assert(stream.Position == 0);
        var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        var hashText = GetHashText();

        if (_contentHashMap.Add(hashText))
        {
            var entry = ZipArchive.CreateEntry(GetContentEntryName(hashText), CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            stream.Position = 0;
            stream.CopyTo(entryStream);
        }

        return hashText;

        string GetHashText()
        {
            var builder = new StringBuilder();
            foreach (var b in hash)
            {
                builder.Append($"{b:X2}");
            }

            return builder.ToString();
        }
    }

    private void AddReferences(CompilationDataPack dataPack, CommandLineArguments args)
    {
        foreach (var reference in args.MetadataReferences)
        {
            var (mvid, assemblyName, assemblyInformationalVersion) = AddAssembly(reference.Reference);
            var pack = new ReferencePack()
            {
                Mvid = mvid,
                Kind = reference.Properties.Kind,
                EmbedInteropTypes = reference.Properties.EmbedInteropTypes,
                Aliases = reference.Properties.Aliases,
                AssemblyName = assemblyName,
                AssemblyInformationalVersion = assemblyInformationalVersion,
            };
            dataPack.References.Add(pack);
        }
    }

    private void AddAdditionalTexts(CompilationDataPack dataPack, CommandLineArguments args)
    {
        foreach (var additionalText in args.AdditionalFiles)
        {
            AddContentFromDisk(dataPack, RawContentKind.AdditionalText, additionalText.Path);
        }
    }

    private void AddResources(CompilationDataPack dataPack, CommandLineArguments args)
    {
        foreach (var r in args.ManifestResources)
        {
            var name = r.GetResourceName();
            var fileName = r.GetFileName();
            var isPublic = r.IsPublic();
            var dataProvider = r.GetDataProvider();

            using var stream = dataProvider();
            var pack = new ResourcePack()
            {
                ContentHash = WriteContent(stream),
                FileName = fileName,
                Name = name,
                IsPublic = isPublic,
            };
            dataPack.Resources.Add(pack);
        }
    }

    private void AddEmbeds(CompilationDataPack dataPack, CompilerCall compilerCall, CommandLineArguments args)
    {
        if (args.EmbeddedFiles.Length == 0)
        {
            return;
        }

        // Embedded files is one place where the compiler requires strict ordinal matching
        var baseDirectory = Path.GetDirectoryName(compilerCall.ProjectFilePath)!;
        var sourceFileSet = new HashSet<string>(args.SourceFiles.Select(static x => x.Path), StringComparer.Ordinal);
        var lineSet = new HashSet<string>(StringComparer.Ordinal);
        var resolver = new SourceFileResolver(ImmutableArray<string>.Empty, args.BaseDirectory, args.PathMap);
        foreach (var e in args.EmbeddedFiles)
        {
            if (!AddContentFromDisk(dataPack, RawContentKind.Embed, e.Path))
            {
                continue;
            }

            // When the compiler embeds a source file it will also embed the targets of any 
            // #line directives in the code
            if (sourceFileSet.Contains(e.Path))
            {
                foreach (string rawTarget in GetLineTargets())
                {
                    var resolvedTarget = resolver.ResolveReference(rawTarget, e.Path);
                    if (resolvedTarget is not null)
                    {
                        AddContentFromDisk(dataPack, RawContentKind.EmbedLine, resolvedTarget);

                        // Presently the compiler does not use /pathhmap when attempting to resolve
                        // #line targets for embedded files. That means if the path is a full one here, or
                        // resolved outside the cone of the project then it can't be exported later so 
                        // issue a diagnostic.
                        //
                        // The original project directory from a compiler point of view is arbitrary as
                        // compilers don't know about projects. Compiler logs center some operations,
                        // like export, around the project directory.For export anything under the
                        // original project directory will maintain the same relative relationship to
                        // each other. Outside that though there is no relative relationship.
                        //
                        // https://github.com/dotnet/roslyn/issues/69659
                        if (Path.IsPathRooted(rawTarget) ||
                            !resolvedTarget.StartsWith(baseDirectory, PathUtil.Comparison))
                        {
                            Diagnostics.Add($"Cannot embed #line target {rawTarget} in {compilerCall.GetDiagnosticName()}");
                        }
                    }
                }

                IEnumerable<string> GetLineTargets()
                {
                    var sourceText = RoslynUtil.GetSourceText(e.Path, args.ChecksumAlgorithm, canBeEmbedded: false);
                    if (args.ParseOptions is CSharpParseOptions csharpParseOptions)
                    {
                        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, csharpParseOptions);
                        foreach (var line in syntaxTree.GetRoot().DescendantNodes(descendIntoTrivia: true).OfType<LineDirectiveTriviaSyntax>())
                        {
                            yield return line.File.Text.Trim('"');
                        }
                    }
                    else
                    {
                        var basicParseOptions = (VisualBasicParseOptions)args.ParseOptions;
                        var syntaxTree = VisualBasicSyntaxTree.ParseText(sourceText, basicParseOptions);
                        foreach (var line in syntaxTree.GetRoot().GetDirectives(static x => x.Kind() == Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.ExternalSourceDirectiveTrivia).OfType<ExternalSourceDirectiveTriviaSyntax>())
                        {
                            yield return line.ExternalSource.Text.Trim('"');
                        }
                    }
                }
            }
        }
    }

    private void AddAnalyzers(CompilationDataPack dataPack, CommandLineArguments args)
    {
        foreach (var analyzer in args.AnalyzerReferences)
        {
            var (mvid, assemblyName, assemblyInformationalVersion) = AddAssembly(analyzer.FilePath);
            var pack = new AnalyzerPack()
            {
                Mvid = mvid,
                FilePath = analyzer.FilePath,
                AssemblyName = assemblyName,
                AssemblyInformationalVersion = assemblyInformationalVersion
            };
            dataPack.Analyzers.Add(pack);
        }
    }

    /// <summary>
    /// Add the assembly into the storage and return tis MVID
    /// </summary>
    private (Guid mvid, string? assemblyName, string? assemblyInformationalVersion) AddAssembly(string filePath)
    {
        if (_assemblyPathToMvidMap.TryGetValue(filePath, out var info))
        {
            Debug.Assert(_mvidToRefInfoMap.ContainsKey(info.Mvid));
            return info;
        }

        var identityData = RoslynUtil.ReadAssemblyIdentityData(filePath);
        info.Mvid = identityData.Mvid;
        info.AssemblyName = identityData.AssemblyName;
        info.AssemblyInformationVersion = identityData.AssemblyInformationalVersion;

        _assemblyPathToMvidMap[filePath] = info;

        // If the assembly was already loaded from a different path then no more
        // work is needed here
        if (_mvidToRefInfoMap.ContainsKey(info.Mvid))
        {
            return info;
        }

        var entry = ZipArchive.CreateEntry(GetAssemblyEntryName(info.Mvid), CompressionLevel.Fastest);
        using var entryStream = entry.Open();
        using var fileStream = RoslynUtil.OpenBuildFileForRead(filePath);
        fileStream.CopyTo(entryStream);

        // There are some assemblies for which MetadataReader will return an AssemblyName which 
        // fails ToString calls which is why we use AssemblyName.GetAssemblyName here.
        //
        // Example: .nuget\packages\microsoft.visualstudio.interop\17.2.32505.113\lib\net472\Microsoft.VisualStudio.Interop.dll
        var assemblyName = AssemblyName.GetAssemblyName(filePath);
        _mvidToRefInfoMap[info.Mvid] = (Path.GetFileName(filePath), assemblyName.ToString());
        return info;
    }

    public void Dispose()
    {
        if (IsOpen)
        {
            Close();
        }
    }
}
