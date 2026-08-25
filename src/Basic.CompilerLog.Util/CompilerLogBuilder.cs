using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
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
using System.Xml;
using static Basic.CompilerLog.Util.CommonUtil;
using BuilderAssemblyData = (System.Guid Mvid, string? AssemblyName, string? AssemblyInformationalVersion, System.Collections.Immutable.ImmutableArray<string> NetModuleNames);

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
    private readonly Dictionary<string, BuilderAssemblyData> _assemblyPathToMvidMap = new(PathUtil.Comparer);
    private readonly Dictionary<ProjectId, BuilderAssemblyData> _emittedProjectMap = new();
    private readonly HashSet<string> _contentHashMap = new(PathUtil.Comparer);
    private readonly Dictionary<string, (AssemblyName AssemblyName, string? CommitHash)> _compilerInfoMap = new(PathUtil.Comparer);
    private readonly List<(int CompilerCallIndex, bool IsRefAssembly, Guid Mvid)> _compilerCallMvidList = new();
    private readonly DefaultObjectPool<MemoryStream> _memoryStreamPool = new(new MemoryStreamPoolPolicy(), maximumRetained: 5);

    /// <summary>
    /// The earliest timestamp the zip format can represent. Values before 1980 are rejected by
    /// <see cref="ZipArchiveEntry.LastWriteTime"/>.
    /// </summary>
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private int _compilationCount;
    private bool _closed;

    internal int MetadataVersion { get; }
    internal List<string> Diagnostics { get; }
    internal ZipArchive ZipArchive { get; private set; }
    internal MSBuildData? MSBuildData { get; set; }

    internal bool IsOpen => !_closed;
    internal bool IsClosed => _closed;

    internal CompilerLogBuilder(Stream stream, List<string> diagnostics, int? metadataVersion = null)
    {
        MetadataVersion = metadataVersion ?? Metadata.LatestMetadataVersion;
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Creates a zip entry with a fixed modification time, so that two logs built from the same
    /// inputs are byte-identical.
    /// </summary>
    /// <remarks>
    /// <see cref="ZipArchive.CreateEntry(string, CompressionLevel)"/> defaults the entry's
    /// modification time to the moment it is created, which makes every log unique even when its
    /// contents are not. Nothing reads these timestamps — entries are addressed by name, and their
    /// names are already content-derived — but they defeat storage layers that deduplicate by
    /// comparing bytes, because the header preceding each entry differs between two logs that hold
    /// the identical entry. The value is the DOS epoch, the earliest a zip can represent.
    /// </remarks>
    private ZipArchiveEntry CreateEntry(string entryName)
    {
        var entry = ZipArchive.CreateEntry(entryName, CompressionLevel.Fastest);
        entry.LastWriteTime = ZipEpoch;
        return entry;
    }

    /// <summary>
    /// Adds a compilation into the builder and returns the index of the entry
    /// </summary>
    internal void AddFromDisk(CompilerCall compilerCall, IReadOnlyCollection<string> arguments)
    {
        var commandLineArguments = BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall, arguments);
        var infoPack = new CompilationInfoPack()
        {
            CompilerFilePath = compilerCall.CompilerFilePath,
            ProjectFilePath = compilerCall.ProjectFilePath,
            IsCSharp = compilerCall.IsCSharp,
            TargetFramework = compilerCall.TargetFramework,
            CompilerCallKind = compilerCall.Kind,
            CommandLineArgsHash = WriteContentMessagePack(arguments),
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
            AddRulesets(dataPack, commandLineArguments);
            AddContentIf(dataPack, RawContentKind.SourceLink, commandLineArguments.SourceLink);
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
                compilerInfo = RoslynUtil.GetCompilerInfo(compilerCall.CompilerFilePath, compilerCall.IsCSharp);
                if (compilerInfo.CommitHash is null)
                {
                    Diagnostics.Add(RoslynUtil.GetDiagnosticMissingCommitHash(compilerCall.CompilerFilePath));
                }

                _compilerInfoMap[compilerCall.CompilerFilePath] = compilerInfo;
            }

            infoPack.CompilerAssemblyName = compilerInfo.AssemblyName.ToString();
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
    /// Adds a compilation built from a Roslyn workspace <see cref="Project"/>. Returns the
    /// synthesized <see cref="CompilerCall"/> on success, or <see langword="null"/> when the
    /// project's compilation could not be obtained or one of its project references could not
    /// be captured (a diagnostic is recorded in either case).
    /// </summary>
    internal async Task<CompilerCall?> AddFromWorkspaceAsync(Project project, CancellationToken cancellationToken = default)
    {
        var isCSharp = project.Language == LanguageNames.CSharp;
        var projectFilePath = project.FilePath ?? $"{project.Name}{(isCSharp ? ".csproj" : ".vbproj")}";
        var targetFramework = GetTargetFrameworkFromProject(project);

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            Diagnostics.Add($"Cannot get compilation for {project.Name}");
            return null;
        }

        var allProjectReferencesAdded = true;

        // Roslyn's Workspace API doesn't surface emit-time inputs (embedded resources,
        // Win32 manifest/icon/resource, source link, app.config) — only items needed for
        // semantic analysis. Synthesizing a partial command line from compilation.Options
        // would produce an rsp that compiles but silently drops those inputs, so we leave
        // the args empty: replay/rsp/export will fail visibly rather than misleadingly.
        var compilationDataPackHash = await CreateCompilationDataPackAsync().ConfigureAwait(false);
        if (!allProjectReferencesAdded)
        {
            // A dropped project reference means the stored compilation would silently differ
            // from the workspace one, so the project is excluded rather than recorded broken.
            return null;
        }

        var infoPack = new CompilationInfoPack()
        {
            ProjectFilePath = projectFilePath,
            IsCSharp = isCSharp,
            TargetFramework = targetFramework,
            CompilerCallKind = CompilerCallKind.Regular,
            CommandLineArgsHash = WriteContentMessagePack(Array.Empty<string>()),
            CompilationDataPackHash = compilationDataPackHash,
        };

        AddWorkspaceCompilationOptions(infoPack, compilation, isCSharp);
        AddCore(infoPack);

        return new CompilerCall(
            projectFilePath: projectFilePath,
            kind: CompilerCallKind.Regular,
            targetFramework: targetFramework,
            isCSharp: isCSharp);

        async Task<string> CreateCompilationDataPackAsync()
        {
            var firstSourceText = project.Documents.FirstOrDefault() is { } firstDocument
                ? await firstDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var dataPack = new CompilationDataPack()
            {
                ContentList = new(),
                ValueMap = new(),
                References = new(),
                Analyzers = new(),
                Resources = new(),
                ChecksumAlgorithm = firstSourceText?.ChecksumAlgorithm ?? SourceHashAlgorithm.Sha256,
                EmitPdb = false,
                HasGeneratedFilesInPdb = false,
                IncludesGeneratedText = true,
            };

            var outputFilePath = project.OutputFilePath;
            var assemblyFileName = outputFilePath is not null
                ? Path.GetFileName(outputFilePath)
                : GetWorkspaceAssemblyFileName(project.AssemblyName, compilation.Options.OutputKind);
            var outputDirectory = outputFilePath is not null
                ? Path.GetDirectoryName(outputFilePath)
                : null;

            dataPack.ValueMap["assemblyFileName"] = assemblyFileName;
            dataPack.ValueMap["outputDirectory"] = outputDirectory;
            dataPack.ValueMap["xmlFilePath"] = null;
            dataPack.ValueMap["compilationName"] = project.AssemblyName;

            foreach (var document in project.Documents)
            {
                var filePath = document.FilePath ?? document.Name;
                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                AddSourceText(dataPack, RawContentKind.SourceText, filePath, sourceText);
            }

            foreach (var document in project.AdditionalDocuments)
            {
                var filePath = document.FilePath ?? document.Name;
                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                AddSourceText(dataPack, RawContentKind.AdditionalText, filePath, sourceText);
            }

            foreach (var document in project.AnalyzerConfigDocuments)
            {
                var filePath = document.FilePath ?? document.Name;
                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                AddSourceText(dataPack, RawContentKind.AnalyzerConfig, filePath, sourceText);
            }

            var generatedDocs = await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var document in generatedDocs)
            {
                var filePath = document.FilePath ?? document.HintName;
                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                AddSourceText(dataPack, RawContentKind.GeneratedText, filePath, sourceText);
            }

            foreach (var reference in project.MetadataReferences)
            {
                if (reference is PortableExecutableReference peRef)
                {
                    if (peRef.FilePath is not null)
                    {
                        var refInfo = AddAssembly(peRef.FilePath);
                        dataPack.References.Add(new ReferencePack()
                        {
                            Mvid = refInfo.Mvid,
                            Kind = peRef.Properties.Kind,
                            EmbedInteropTypes = peRef.Properties.EmbedInteropTypes,
                            Aliases = peRef.Properties.Aliases,
                            FilePath = peRef.FilePath,
                            AssemblyName = refInfo.AssemblyName,
                            AssemblyInformationalVersion = refInfo.AssemblyInformationalVersion,
                            NetModuleMvids = ImmutableArray<Guid>.Empty,
                        });
                    }
                    else
                    {
                        Diagnostics.Add($"Skipping in-memory metadata reference in {project.Name}: {reference.Display}");
                    }
                }
                else
                {
                    Diagnostics.Add($"Skipping metadata reference of unsupported type {reference.GetType().Name} in {project.Name}: {reference.Display}");
                }
            }

            foreach (var projectRef in project.ProjectReferences)
            {
                if (!await TryAddProjectReferenceToDataPackAsync(dataPack, project, projectRef, cancellationToken).ConfigureAwait(false))
                {
                    allProjectReferencesAdded = false;
                }
            }

            foreach (var analyzerReference in project.AnalyzerReferences)
            {
                if (analyzerReference is AnalyzerFileReference fileRef)
                {
                    var refInfo = AddAssembly(fileRef.FullPath);
                    dataPack.Analyzers.Add(new AnalyzerPack()
                    {
                        Mvid = refInfo.Mvid,
                        FilePath = fileRef.FullPath,
                        AssemblyName = refInfo.AssemblyName,
                        AssemblyInformationalVersion = refInfo.AssemblyInformationalVersion,
                    });
                }
                else
                {
                    Diagnostics.Add($"Skipping analyzer reference of unsupported type {analyzerReference.GetType().Name} in {project.Name}: {analyzerReference.Display}. Reload the workspace with BasicAnalyzerKind.OnDisk or BasicAnalyzerKind.None to capture analyzers via file paths.");
                }
            }

            if (compilation.Options.CryptoKeyFile is { Length: > 0 } keyFile
                && ResolveProjectRelativePath(project, keyFile) is { } resolvedKeyFile
                && File.Exists(resolvedKeyFile))
            {
                AddContentFromDisk(dataPack, RawContentKind.CryptoKeyFile, resolvedKeyFile);
            }

            return WriteContentMessagePack(dataPack);
        }

        void AddWorkspaceCompilationOptions(CompilationInfoPack infoPack, Compilation compilation, bool isCSharp)
        {
            infoPack.EmitOptionsHash = WriteContentMessagePack(MessagePackUtil.CreateEmitOptionsPack(new EmitOptions()));

            if (isCSharp)
            {
                var parseOptions = (project.ParseOptions as CSharpParseOptions) ?? CSharpParseOptions.Default;
                infoPack.ParseOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateCSharpParseOptionsPack(parseOptions));
                infoPack.CompilationOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateCSharpCompilationOptionsPack((CSharpCompilationOptions)compilation.Options));
            }
            else
            {
                var parseOptions = (project.ParseOptions as VisualBasicParseOptions) ?? VisualBasicParseOptions.Default;
                infoPack.ParseOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateVisualBasicParseOptionsPack(parseOptions));
                infoPack.CompilationOptionsHash = WriteContentMessagePack(
                    MessagePackUtil.CreateVisualBasicCompilationOptionsPack((VisualBasicCompilationOptions)compilation.Options));
            }
        }
    }

    private void AddSourceText(CompilationDataPack dataPack, RawContentKind kind, string filePath, SourceText sourceText)
    {
        var encoding = sourceText.Encoding ?? ContentEncoding;
        using var stream = new MemoryStream();
        using (var writer = Polyfill.NewStreamWriter(stream, encoding, leaveOpen: true))
        {
            sourceText.Write(writer);
        }

        stream.Position = 0;
        AddContent(dataPack, kind, filePath, stream);
    }

    /// <summary>
    /// Serialize a project-to-project reference. Prefers the dependency's on-disk
    /// <see cref="Project.OutputFilePath"/> (which matches the consumer's TargetFramework), and only
    /// falls back to emitting the dependency's in-memory <see cref="Compilation"/> when no compiled
    /// output is available on disk. Returns <see langword="false"/> when the reference could not be
    /// captured (a diagnostic is recorded in that case).
    /// </summary>
    private async Task<bool> TryAddProjectReferenceToDataPackAsync(CompilationDataPack dataPack, Project parentProject, ProjectReference projectRef, CancellationToken cancellationToken)
    {
        var dep = parentProject.Solution.GetProject(projectRef.ProjectId);
        if (dep is null)
        {
            Diagnostics.Add($"Cannot resolve project reference {projectRef.ProjectId} in {parentProject.Name}");
            return false;
        }

        if (dep.OutputFilePath is not null && File.Exists(dep.OutputFilePath))
        {
            AddReferencePack(AddAssembly(dep.OutputFilePath), dep.OutputFilePath);
            return true;
        }

        // The emit cache is keyed by ProjectId: assembly names are not unique across a
        // workspace (each TargetFramework flavor of a multi-targeted project shares one).
        var displayPath = $"{dep.AssemblyName}.dll";
        if (_emittedProjectMap.TryGetValue(dep.Id, out var cachedInfo))
        {
            AddReferencePack(cachedInfo, displayPath);
            return true;
        }

        var depCompilation = await dep.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (depCompilation is null)
        {
            Diagnostics.Add($"Cannot get compilation for project reference {dep.Name} in {parentProject.Name}");
            return false;
        }

        var memStream = new MemoryStream();
        var emitResult = depCompilation.Emit(memStream, cancellationToken: cancellationToken);
        if (!emitResult.Success)
        {
            var errors = string.Join(", ", emitResult.Diagnostics
                .Where(static x => x.Severity == DiagnosticSeverity.Error)
                .Select(static x => x.GetMessage()));
            Diagnostics.Add($"Cannot emit compilation reference {depCompilation.AssemblyName} in {parentProject.Name}: {errors}");
            return false;
        }

        var emittedInfo = AddEmittedAssembly(displayPath, memStream);
        _emittedProjectMap[dep.Id] = emittedInfo;
        AddReferencePack(emittedInfo, displayPath);
        return true;

        void AddReferencePack(BuilderAssemblyData info, string filePath) =>
            dataPack.References.Add(new ReferencePack()
            {
                Mvid = info.Mvid,
                Kind = MetadataImageKind.Assembly,
                EmbedInteropTypes = projectRef.EmbedInteropTypes,
                Aliases = projectRef.Aliases,
                FilePath = filePath,
                AssemblyName = info.AssemblyName,
                AssemblyInformationalVersion = info.AssemblyInformationalVersion,
                NetModuleMvids = ImmutableArray<Guid>.Empty,
            });
    }

    private BuilderAssemblyData AddEmittedAssembly(string displayPath, MemoryStream stream)
    {
        stream.Position = 0;
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var metadataReader = peReader.GetMetadataReader();
        var identityData = RoslynUtil.ReadAssemblyIdentityData(metadataReader);
        BuilderAssemblyData info = (identityData.Mvid, identityData.AssemblyName, identityData.AssemblyInformationalVersion, ImmutableArray<string>.Empty);

        if (!_mvidToRefInfoMap.ContainsKey(info.Mvid))
        {
            var fullAssemblyName = metadataReader.GetAssemblyDefinition().GetAssemblyName().ToString();
            stream.Position = 0;
            WriteAssemblyEntry(info.Mvid, Path.GetFileName(displayPath), fullAssemblyName, stream);
        }

        return info;
    }

    /// <summary>
    /// Extract a TargetFramework moniker from a workspace <see cref="Project"/>. MSBuildWorkspace
    /// names multi-targeted projects "AssemblyName(tfm)" — that's the most reliable signal and is
    /// preferred. Falls back to the parent directory of <see cref="Project.OutputFilePath"/>, which
    /// for default-layout SDK projects is the TFM (but for projects using <c>artifacts/</c> output
    /// the directory is <c>{config}_{tfm}</c>, so this fallback is best-effort only).
    /// </summary>
    private static string? GetTargetFrameworkFromProject(Project project)
    {
        var name = project.Name;
        var open = name.LastIndexOf('(');
        var close = name.LastIndexOf(')');
        if (open > 0 && close == name.Length - 1 && close > open + 1)
        {
            return name.Substring(open + 1, close - open - 1);
        }

        if (project.OutputFilePath is { } outputPath)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(outputPath));
            if (!string.IsNullOrEmpty(parent))
            {
                return parent;
            }
        }

        return null;
    }

    private static string? ResolveProjectRelativePath(Project project, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var projectDir = project.FilePath is { } projectFilePath
            ? Path.GetDirectoryName(projectFilePath)
            : null;
        return projectDir is not null
            ? Path.Combine(projectDir, path)
            : null;
    }

    private static string GetWorkspaceAssemblyFileName(string assemblyName, OutputKind outputKind) =>
        outputKind switch
        {
            OutputKind.NetModule => $"{assemblyName}.netmodule",
            OutputKind.ConsoleApplication => $"{assemblyName}.exe",
            OutputKind.WindowsApplication => $"{assemblyName}.exe",
            _ => $"{assemblyName}.dll",
        };

    private void AddCore(CompilationInfoPack infoPack)
    {
        var index = _compilationCount;
        var entry = CreateEntry(GetCompilerEntryName(index));
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
            var entry = CreateEntry(MetadataFileName);
            using var writer = Polyfill.NewStreamWriter(entry.Open(), ContentEncoding, leaveOpen: false);
            Metadata.Create(_compilationCount, MetadataVersion).Write(writer);
        }

        void WriteLogInfo()
        {
            var pack = new LogInfoPack()
            {
                CompilerCallMvidList = _compilerCallMvidList,
                MvidToReferenceInfoMap = _mvidToRefInfoMap,
                MSBuildData = MSBuildData is { } d
                    ? new MSBuildDataPack
                    {
                        ProcessPath = d.ProcessPath,
                        MSBuildPath = d.MSBuildPath,
                        CommandLine = d.CommandLine,
                        MSBuildVersion = d.MSBuildVersion,
                    }
                    : null,
            };
            var contentHash = WriteContentMessagePack(pack);
            var entry = CreateEntry(LogInfoFileName);
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

    private void AddRulesets(CompilationDataPack dataPack, CommandLineArguments args)
    {
        if (args.RuleSetPath is null)
        {
            return;
        }

        AddContentFromDisk(dataPack, RawContentKind.RuleSet, args.RuleSetPath);

        var queue = new Queue<string>();
        queue.Enqueue(args.RuleSetPath);
        do
        {
            var filePath = queue.Dequeue();
            if (!File.Exists(filePath))
            {
                Diagnostics.Add(RoslynUtil.GetDiagnosticMissingFile(filePath));
                continue;
            }

            try
            {
                var doc = new XmlDocument();
                doc.Load(filePath);
                foreach (var i in RoslynUtil.GetRuleSetIncludes(doc))
                {
                    var includePath = Path.IsPathRooted(i) ? i : Path.Combine(Path.GetDirectoryName(filePath)!, i);
                    if (AddContentFromDisk(dataPack, RawContentKind.RuleSetInclude, includePath))
                    {
                        queue.Enqueue(includePath);
                    }
                }
            }
            catch (Exception)
            {
                Diagnostics.Add(RoslynUtil.GetDiagnosticCannotReadRulset(filePath));
            }
        } while (queue.Count > 0);
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
            Diagnostics.Add(RoslynUtil.GetDiagnosticMissingFile(filePath));
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
        var hashText = hash.AsHexString();

        if (_contentHashMap.Add(hashText))
        {
            var entry = CreateEntry(GetContentEntryName(hashText));
            using var entryStream = entry.Open();
            stream.Position = 0;
            stream.CopyTo(entryStream);
        }

        return hashText;
    }

    /// <summary>
    /// The command line parser does not resolve analyzer or metadata reference paths against the
    /// base directory, that happens later when the compiler loads them. Do the same here so a
    /// relative path on the command line isn't resolved against the process working directory.
    /// </summary>
    private static string ResolveOnDiskPath(string filePath, CommandLineArguments args) =>
        !Path.IsPathRooted(filePath) && args.BaseDirectory is { } baseDirectory
            ? Path.Combine(baseDirectory, filePath)
            : filePath;

    private void AddReferences(CompilationDataPack dataPack, CommandLineArguments args)
    {
        var explicitModuleSet = new HashSet<Guid>();
        var implicitModuleList = new List<(string, Guid)>();

        foreach (var reference in args.MetadataReferences)
        {
            var referencePath = ResolveOnDiskPath(reference.Reference, args);
            if (reference.Properties.Kind == MetadataImageKind.Assembly)
            {
                var (mvid, assemblyName, assemblyInformationalVersion, netModuleNames) = AddAssembly(referencePath);

                var netModuleMvids = ImmutableArray<Guid>.Empty;
                if (netModuleNames.Length > 0)
                {
                    var mvidBuilder = ImmutableArray.CreateBuilder<Guid>(netModuleNames.Length);
                    var assemblyDir = Path.GetDirectoryName(referencePath)!;
                    foreach (var netModuleName in netModuleNames)
                    {
                        var netModulePath = Path.Combine(assemblyDir, netModuleName);
                        if (!File.Exists(netModulePath))
                        {
                            Diagnostics.Add(RoslynUtil.GetDiagnosticMissingFile(netModulePath));
                            continue;
                        }

                        var netModuleMvid = AddNetModule(netModulePath);
                        mvidBuilder.Add(netModuleMvid);
                        implicitModuleList.Add((netModulePath, netModuleMvid));
                    }
                    netModuleMvids = mvidBuilder.ToImmutable();
                }

                var pack = new ReferencePack()
                {
                    Mvid = mvid,
                    Kind = reference.Properties.Kind,
                    EmbedInteropTypes = reference.Properties.EmbedInteropTypes,
                    Aliases = reference.Properties.Aliases,
                    FilePath = referencePath,
                    AssemblyName = assemblyName,
                    AssemblyInformationalVersion = assemblyInformationalVersion,
                    NetModuleMvids = netModuleMvids,
                };
                dataPack.References.Add(pack);
            }
            else
            {
                Debug.Assert(reference.Properties.Kind == MetadataImageKind.Module);
                Debug.Assert(reference.Properties.Aliases.IsEmpty);
                Debug.Assert(!reference.Properties.EmbedInteropTypes);

                 var mvid = AddNetModule(referencePath);
                 var pack = new ReferencePack()
                 {
                     Mvid = mvid,
                     Kind = MetadataImageKind.Module,
                     Aliases = [],
                     FilePath = referencePath,
                 };
                 dataPack.References.Add(pack);
                 explicitModuleSet.Add(mvid);
            }
        }

        // Now that all the explicit items are added and in the proper order, lets go back
        // and add any implicit netmodules that weren't already added as explicit references.
        foreach (var (netModulePath, netModuleMvid) in implicitModuleList)
        {
            if (explicitModuleSet.Add(netModuleMvid))
            {
                var pack = new ReferencePack()
                {
                    Mvid = netModuleMvid,
                    Kind = MetadataImageKind.Module,
                    FilePath = netModulePath,
                    Aliases = [],
                    IsImplicit = true,
                };
                dataPack.References.Add(pack);
            }
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
            var analyzerPath = ResolveOnDiskPath(analyzer.FilePath, args);
            var (mvid, assemblyName, assemblyInformationalVersion, _) = AddAssembly(analyzerPath);
            var pack = new AnalyzerPack()
            {
                Mvid = mvid,
                FilePath = analyzerPath,
                AssemblyName = assemblyName,
                AssemblyInformationalVersion = assemblyInformationalVersion
            };
            dataPack.Analyzers.Add(pack);
        }
    }

    /// <summary>
    /// Add the assembly into the storage and return tis MVID
    /// </summary>
    private BuilderAssemblyData AddAssembly(string filePath)
    {
        if (_assemblyPathToMvidMap.TryGetValue(filePath, out var info))
        {
            Debug.Assert(_mvidToRefInfoMap.ContainsKey(info.Mvid));
            return info;
        }

        info = ReadBuilderAssemblyData(filePath);
        _assemblyPathToMvidMap[filePath] = info;

        // If the assembly was already loaded from a different path then no more
        // work is needed here
        if (_mvidToRefInfoMap.ContainsKey(info.Mvid))
        {
            return info;
        }

        // There are some assemblies for which MetadataReader will return an AssemblyName which
        // fails ToString calls which is why we use AssemblyName.GetAssemblyName here.
        //
        // Example: .nuget\packages\microsoft.visualstudio.interop\17.2.32505.113\lib\net472\Microsoft.VisualStudio.Interop.dll
        var assemblyName = AssemblyName.GetAssemblyName(filePath);
        using var fileStream = RoslynUtil.OpenBuildFileForRead(filePath);
        WriteAssemblyEntry(info.Mvid, Path.GetFileName(filePath), assemblyName.ToString(), fileStream);
        return info;

        BuilderAssemblyData ReadBuilderAssemblyData(string filePath)
        {
            using var stream = RoslynUtil.OpenBuildFileForRead(filePath);
            using var peReader = new PEReader(stream);
            var metadataReader = peReader.GetMetadataReader();
            var identityData = RoslynUtil.ReadAssemblyIdentityData(metadataReader);
            var netModuleNames = RoslynUtil.GetNetModuleFileNames(metadataReader);
            return (identityData.Mvid, identityData.AssemblyName, identityData.AssemblyInformationalVersion, netModuleNames);
        }
    }

    private void WriteAssemblyEntry(Guid mvid, string fileName, string fullAssemblyName, Stream stream)
    {
        var entry = CreateEntry(GetAssemblyEntryName(mvid));
        using var entryStream = entry.Open();
        stream.CopyTo(entryStream);
        _mvidToRefInfoMap[mvid] = (fileName, fullAssemblyName);
    }

    /// <summary>
    /// Add a netmodule into storage and return its MVID. Netmodules don't have an assembly
    /// manifest so they need different handling than assemblies.
    /// </summary>
    private Guid AddNetModule(string filePath)
    {
        if (_assemblyPathToMvidMap.TryGetValue(filePath, out var info))
        {
            return info.Mvid;
        }

        var mvid = RoslynUtil.ReadMvid(filePath);
        info.Mvid = mvid;
        _assemblyPathToMvidMap[filePath] = info;

        if (_mvidToRefInfoMap.ContainsKey(mvid))
        {
            return mvid;
        }

        var entry = CreateEntry(GetAssemblyEntryName(mvid));
        using var entryStream = entry.Open();
        using var fileStream = RoslynUtil.OpenBuildFileForRead(filePath);
        fileStream.CopyTo(entryStream);

        _mvidToRefInfoMap[mvid] = (Path.GetFileName(filePath), Path.GetFileNameWithoutExtension(filePath));
        return mvid;
    }

    public void Dispose()
    {
        if (IsOpen)
        {
            Close();
        }
    }
}
