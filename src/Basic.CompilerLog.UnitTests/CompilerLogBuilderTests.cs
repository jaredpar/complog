using System.IO.Compression;
using System.Security.Cryptography;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogBuilderTests : TestBase
{
    public SolutionFixture Fixture { get; }

    public CompilerLogBuilderTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, SolutionFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogBuilderTests))
    {
        Fixture = fixture;
    }

    private void WithCompilerCall(Action<CompilerLogBuilder, CompilerCall, IReadOnlyCollection<string>> action)
    {
        using var stream = new MemoryStream();
        using var builder = new CompilerLogBuilder(stream, new());
        using var binlogReader = BinaryLogReader.Create(Fixture.SolutionBinaryLogPath);

        var compilerCall = binlogReader
            .ReadAllCompilerCalls(x => x.ProjectFileName == Fixture.ConsoleProjectName)
            .Single();
        action(builder, compilerCall, binlogReader.ReadArguments(compilerCall));
    }

    /// <summary>
    /// We should be able to create log files that are resilient to artifacts missing on disk. Basically we can create
    /// a <see cref="CompilationData"/> for this scenario, it will have diagnostics.
    /// </summary>
    [Fact]
    public void MissingFileSourceLink()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            // Add a source link that doesn't exist
            builder.AddFromDisk(compilerCall, ["/sourcelink:does-not-exist.txt"]);
            Assert.NotEmpty(builder.Diagnostics);
        });
    }

    /// <summary>
    /// Analyzer and metadata reference paths are not resolved by the command line parser (that
    /// happens later when the compiler loads them) so a relative path has to be resolved against
    /// the base directory here, not the process working directory.
    /// </summary>
    [Fact]
    public void AddWithRelativeAnalyzerAndReferencePaths()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            var projectDirectory = Path.Combine(RootDirectory, "relative");
            Directory.CreateDirectory(projectDirectory);
            var fileName = "relative-analyzer.dll";
            File.Copy(typeof(CompilerLogBuilder).Assembly.Location, Path.Combine(projectDirectory, fileName));
            compilerCall = new CompilerCall(
                Path.Combine(projectDirectory, "relative.csproj"),
                compilerFilePath: compilerCall.CompilerFilePath);
            builder.AddFromDisk(compilerCall, [$"/analyzer:{fileName}", $"/reference:{fileName}"]);
            Assert.Empty(builder.Diagnostics);
        });
    }

    [Fact]
    public void RulesetMissing()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            // Add a ruleset that doesn't exist
            builder.AddFromDisk(compilerCall, ["/ruleset:does-not-exist.ruleset"]);
            Assert.NotEmpty(builder.Diagnostics);
        });
    }

    [Fact]
    public void RulesetInvalidXml()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            // Add a ruleset with invalid XML
            var filePath = Path.Combine(RootDirectory, "invalid.ruleset");
            File.WriteAllText(filePath, "not valid xml");
            builder.AddFromDisk(compilerCall, [$"/ruleset:{filePath}"]);
            Assert.Equal([RoslynUtil.GetDiagnosticCannotReadRulset(filePath)], builder.Diagnostics);
        });
    }

    [Fact]
    public void RulesetMissingInclude()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            var filePath = Path.Combine(RootDirectory, "example.ruleset");
            File.WriteAllText(filePath, """
                <RuleSet Name="Rules for Hello World project" Description="These rules focus on critical issues for the Hello World app." ToolsVersion="10.0">
                    <Localization ResourceAssembly="Microsoft.VisualStudio.CodeAnalysis.RuleSets.Strings.dll" ResourceBaseName="Microsoft.VisualStudio.CodeAnalysis.RuleSets.Strings.Localized">
                        <Name Resource="HelloWorldRules_Name" />
                        <Description Resource="HelloWorldRules_Description" />
                    </Localization>
                    <Rules AnalyzerId="Microsoft.Analyzers.ManagedCodeAnalysis" RuleNamespace="Microsoft.Rules.Managed">
                        <Rule Id="CA1001" Action="Warning" />
                        <Rule Id="CA1009" Action="Warning" />
                        <Rule Id="CA1016" Action="Warning" />
                        <Rule Id="CA1033" Action="Warning" />
                    </Rules>
                    <Include Path="nested.ruleset" Action="Default" />
                </RuleSet>
                """);

            // Add a ruleset that doesn't exist
            builder.AddFromDisk(compilerCall, [$"/ruleset:{filePath}"]);
            Assert.Equal([RoslynUtil.GetDiagnosticMissingFile(Path.Combine(RootDirectory, "nested.ruleset"))], builder.Diagnostics);
        });
    }

    [Fact]
    public void PortablePdbMissing()
    {
        RunDotNet("new console -o .");
        RunDotNet("build -bl:msbuild.binlog");

        Directory
            .EnumerateFiles(RootDirectory, "*.pdb", SearchOption.AllDirectories)
            .ForEach(File.Delete);

        using var complogStream = new MemoryStream();
        using var binlogStream = new FileStream(Path.Combine(RootDirectory, "msbuild.binlog"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogStream, complogStream);
        Assert.Contains(diagnostics, x => x.Contains("Can't find portable pdb"));
    }

    [Fact]
    public void CloseTwice()
    {
        var builder = new CompilerLogBuilder(new MemoryStream(), []);
        builder.Close();
        Assert.Throws<InvalidOperationException>(() => builder.Close());
    }

    [Fact]
    public void CompilerFilePathMissingCommitHash()
    {
        WithCompilerCall((builder, compilerCall, arguments) =>
        {
            compilerCall = new CompilerCall(
                compilerCall.ProjectFilePath,
                compilerFilePath: typeof(CompilerLogBuilderTests).Assembly.Location);
            builder.AddFromDisk(compilerCall, arguments);
            Assert.Equal([RoslynUtil.GetDiagnosticMissingCommitHash(compilerCall.CompilerFilePath!)], builder.Diagnostics);
        });
    }

    private Workspace LoadConsoleWorkspace(BasicAnalyzerKind analyzerKind = BasicAnalyzerKind.None)
    {
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, analyzerKind, predicate: x => x.ProjectFileName == Fixture.ConsoleProjectName);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());
        return workspace;
    }

    /// <summary>
    /// Build a simple C# class library project inside <paramref name="workspace"/>. The project has
    /// no on-disk output so project references to it exercise the in-memory emit path.
    /// </summary>
    private static Project AddAdhocProject(AdhocWorkspace workspace, string projectName, string assemblyName, string source, params ProjectId[] projectReferences)
    {
        var projectId = ProjectId.CreateNewId(projectName);
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(projectId),
            $"{projectName}.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source, Encoding.UTF8), VersionStamp.Default)));
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: projectName,
            assemblyName: assemblyName,
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents: [documentInfo],
            projectReferences: projectReferences.Select(x => new ProjectReference(x)),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        return workspace.AddProject(projectInfo);
    }

    [Fact]
    public async Task AddFromWorkspace_RoundTrip()
    {
        using var workspace = LoadConsoleWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();

        var complogStream = new MemoryStream();
        var result = await CompilerLogUtil.TryCreateFromWorkspaceAsync(workspace, complogStream, x => x.Name == project.Name, CancellationToken);
        complogStream.Position = 0;

        Assert.True(result.Succeeded);
        Assert.Single(result.CompilerCalls);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCalls = reader.ReadAllCompilerCalls();
        Assert.Single(compilerCalls);

        var compilationData = reader.ReadCompilationData(compilerCalls[0]);
        Assert.True(compilationData.IsCSharp);
        Assert.NotNull(compilationData.Compilation);
        Assert.NotEmpty(compilationData.Compilation.SyntaxTrees);
        Assert.NotEmpty(compilationData.Compilation.References);
        Assert.All(compilerCalls, x => Assert.True(x.IsWorkspace));
        Assert.All(result.CompilerCalls, x => Assert.True(x.IsWorkspace));
    }

    /// <summary>
    /// The workspace origin flag must be scoped to workspace-created compilations:
    /// build-derived compiler calls report false.
    /// </summary>
    [Fact]
    public void ConvertBinaryLog_IsNotWorkspaceLog()
    {
        using var complogStream = new MemoryStream();
        using (var binlogStream = new FileStream(Fixture.SolutionBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = CompilerLogUtil.ConvertBinaryLog(binlogStream, complogStream);
        }

        complogStream.Position = 0;
        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        Assert.All(reader.ReadAllCompilerCalls(), x => Assert.False(x.IsWorkspace));

        using var binlogReader = BinaryLogReader.Create(Fixture.SolutionBinaryLogPath);
        Assert.All(binlogReader.ReadAllCompilerCalls(), x => Assert.False(x.IsWorkspace));
    }

    [Fact]
    public void AddFromWorkspace_SourceTextPreserved()
    {
        using var workspace = LoadConsoleWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();

        var originalSources = project.Documents
            .Select(d => d.GetTextAsync(CancellationToken).GetAwaiter().GetResult().ToString())
            .OrderBy(x => x)
            .ToList();

        var complogStream = new MemoryStream();
        CompilerLogUtil.CreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var sourceTextData = reader.ReadAllSourceTextData(compilerCall)
            .Where(x => x.SourceTextKind == SourceTextKind.SourceCode)
            .ToList();

        Assert.Equal(originalSources.Count, sourceTextData.Count);

        var roundTripSources = sourceTextData
            .Select(x => reader.ReadSourceText(x).ToString())
            .OrderBy(x => x)
            .ToList();

        Assert.Equal(originalSources, roundTripSources);
    }

    [Fact]
    public void AddFromWorkspace_WithProjectReference()
    {
        using var workspace = new AdhocWorkspace();
        var lib = AddAdhocProject(workspace, "RefLib", "RefLib", "public class RefLib { public const int Version = 1; }");
        _ = AddAdhocProject(workspace, "Consumer", "Consumer", "public class Consumer { public int Version => RefLib.Version; }", lib.Id);

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, x => x.Name == "Consumer", CancellationToken);
        complogStream.Position = 0;

        Assert.True(result.Succeeded);
        Assert.Single(result.CompilerCalls);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var referenceData = reader.ReadAllReferenceData(compilerCall);
        Assert.Contains(referenceData, x => x.AssemblyIdentityData.AssemblyName == "RefLib");

        // The embedded reference must actually resolve: the consumer must re-compile without
        // errors using only assemblies stored in the log.
        var compilationData = reader.ReadCompilationData(compilerCall);
        Assert.Empty(compilationData.GetDiagnostics(CancellationToken).Where(x => x.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Workspace projects can share an assembly name (a multi-targeted project loads as one
    /// project per TargetFramework). The emitted-reference cache is keyed by ProjectId so the
    /// flavors must not be conflated.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_ProjectReferencesSharingAssemblyName()
    {
        using var workspace = new AdhocWorkspace();
        var lib1 = AddAdhocProject(workspace, "RefLib(net9.0)", "RefLib", "public class RefLib { public const int Version = 9; }");
        var lib2 = AddAdhocProject(workspace, "RefLib(net10.0)", "RefLib", "public class RefLib { public const int Version = 10; }");
        _ = AddAdhocProject(workspace, "Consumer1", "Consumer1", "public class Consumer1 { public int Version => RefLib.Version; }", lib1.Id);
        _ = AddAdhocProject(workspace, "Consumer2", "Consumer2", "public class Consumer2 { public int Version => RefLib.Version; }", lib2.Id);

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, x => x.Name.StartsWith("Consumer", StringComparison.Ordinal), CancellationToken);
        complogStream.Position = 0;
        Assert.True(result.Succeeded);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var mvids = reader.ReadAllCompilerCalls()
            .Select(x => reader.ReadAllReferenceData(x).Single(r => r.AssemblyIdentityData.AssemblyName == "RefLib").Mvid)
            .ToList();
        Assert.Equal(2, mvids.Count);
        Assert.NotEqual(mvids[0], mvids[1]);
    }

    /// <summary>
    /// When a referenced project has no on-disk output and its in-memory emit fails, the
    /// referencing project must be reported as failed rather than recorded with the
    /// reference silently dropped.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_ProjectReferenceEmitFailure()
    {
        using var workspace = new AdhocWorkspace();

        // ConsoleApplication with no entry point: in-memory emit fails with CS5001
        var depId = ProjectId.CreateNewId("Dep");
        workspace.AddProject(ProjectInfo.Create(
            depId,
            VersionStamp.Default,
            name: "Dep",
            assemblyName: "Dep",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        _ = AddAdhocProject(workspace, "Consumer", "Consumer", "public class Consumer { }", depId);

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, x => x.Contains("Cannot emit compilation reference Dep in Consumer"));

        // The dependency project itself serialized fine; only the consumer is excluded.
        var compilerCall = Assert.Single(result.CompilerCalls);
        Assert.Equal("Dep.csproj", Path.GetFileName(compilerCall.ProjectFilePath));

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        Assert.Single(reader.ReadAllCompilerCalls());
    }

    /// <summary>
    /// The synthesized command line must reflect the compilation options that survive the
    /// workspace API. This exercises the option-rich paths that the fixture projects don't hit.
    /// </summary>
    [Fact]
    public async Task SynthesizeCommandLine_Options()
    {
        using var workspace = new AdhocWorkspace();
        var libRef = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var aliasedRef = MetadataReference.CreateFromFile(typeof(Uri).Assembly.Location)
            .WithAliases(["MyAlias"]);
        var interopRef = MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
            .WithEmbedInteropTypes(true);

        var projectId = ProjectId.CreateNewId("Options");
        var project = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Options",
            assemblyName: "Options",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                mainTypeName: "Program",
                platform: Platform.X64,
                checkOverflow: true,
                allowUnsafe: true,
                generalDiagnosticOption: ReportDiagnostic.Error,
                cryptoKeyContainer: "MyContainer",
                delaySign: true,
                nullableContextOptions: NullableContextOptions.Warnings,
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>()
                {
                    ["CS0169"] = ReportDiagnostic.Suppress,
                    ["CS0219"] = ReportDiagnostic.Error,
                    ["CS0414"] = ReportDiagnostic.Warn,
                }),
            parseOptions: new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview, preprocessorSymbols: ["FIRST", "SECOND"]),
            documents: [DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                "Program.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class Program { static void Main() { } }", Encoding.UTF8), VersionStamp.Default)))],
            metadataReferences: [libRef, aliasedRef, interopRef],
            outputFilePath: Path.Combine(RootDirectory, "Options.exe")));

        var compilation = (await project.GetCompilationAsync(CancellationToken))!;
        var args = WorkspaceCommandLineSynthesizer.Synthesize(project, compilation);

        Assert.Contains("/target:exe", args);
        Assert.Contains("/main:Program", args);
        Assert.Contains("/platform:x64", args);
        Assert.Contains("/checked+", args);
        Assert.Contains("/unsafe+", args);
        Assert.Contains("/warnaserror+", args);
        Assert.Contains("/keycontainer:MyContainer", args);
        Assert.Contains("/delaysign+", args);
        Assert.Contains("/nullable:warnings", args);
        Assert.Contains("/langversion:preview", args);
        Assert.Contains("/define:FIRST;SECOND", args);
        Assert.Contains("/nowarn:CS0169", args);
        Assert.Contains("/warnaserror+:CS0219", args);
        Assert.Contains("/warnaserror-:CS0414", args);
        Assert.Contains(args, x => x.StartsWith("/reference:MyAlias=", StringComparison.Ordinal));
        Assert.Contains(args, x => x.StartsWith("/link:", StringComparison.Ordinal));
        Assert.Contains(args, x => x.StartsWith("/out:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SynthesizeCommandLine_SuppressAndPublicSign()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Quiet");
        var project = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Quiet",
            assemblyName: "Quiet",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Suppress,
                publicSign: true),
            parseOptions: new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var compilation = (await project.GetCompilationAsync(CancellationToken))!;
        var args = WorkspaceCommandLineSynthesizer.Synthesize(project, compilation);

        Assert.Contains("/nowarn", args);
        Assert.Contains("/publicsign+", args);
        Assert.Contains("/langversion:latest", args);
    }

    [Fact]
    public async Task SynthesizeCommandLine_WinExeAndModule()
    {
        using var workspace = new AdhocWorkspace();

        // An in-memory reference has no file path, so nothing useful can go into the rsp for it.
        var imageReference = MetadataReference.CreateFromImage(File.ReadAllBytes(typeof(object).Assembly.Location));
        var winExeId = ProjectId.CreateNewId("WinExe");
        var winExe = workspace.AddProject(ProjectInfo.Create(
            winExeId,
            VersionStamp.Default,
            name: "WinExe",
            assemblyName: "WinExe",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.WindowsApplication, nullableContextOptions: NullableContextOptions.Annotations),
            parseOptions: new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.LatestMajor),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), imageReference]));

        var winExeArgs = WorkspaceCommandLineSynthesizer.Synthesize(winExe, (await winExe.GetCompilationAsync(CancellationToken))!);
        Assert.Contains("/target:winexe", winExeArgs);
        Assert.Contains("/nullable:annotations", winExeArgs);
        Assert.Contains("/langversion:latestmajor", winExeArgs);
        Assert.Single(winExeArgs, x => x.StartsWith("/reference:", StringComparison.Ordinal));

        var moduleId = ProjectId.CreateNewId("ModuleProject");
        var moduleProject = workspace.AddProject(ProjectInfo.Create(
            moduleId,
            VersionStamp.Default,
            name: "ModuleProject",
            assemblyName: "ModuleProject",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.NetModule),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var moduleArgs = WorkspaceCommandLineSynthesizer.Synthesize(moduleProject, (await moduleProject.GetCompilationAsync(CancellationToken))!);
        Assert.Contains("/target:module", moduleArgs);
    }

    [Theory]
    [InlineData(OutputKind.WindowsRuntimeMetadata, "winmdobj")]
    [InlineData(OutputKind.WindowsRuntimeApplication, "appcontainerexe")]
    public async Task SynthesizeCommandLine_WindowsRuntimeTargets(OutputKind outputKind, string expectedTarget)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("WinRt");
        var project = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "WinRt",
            assemblyName: "WinRt",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(outputKind),
            parseOptions: new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp10),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var args = WorkspaceCommandLineSynthesizer.Synthesize(project, (await project.GetCompilationAsync(CancellationToken))!);
        Assert.Contains($"/target:{expectedTarget}", args);
        Assert.Contains("/langversion:10.0", args);
    }

    /// <summary>
    /// The Visual Basic synthesizer path with non-default options: nothing else exercises the
    /// "On"/"text" sides of the option flags or a VB root namespace.
    /// </summary>
    [Fact]
    public async Task SynthesizeCommandLine_VisualBasicOptions()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("VbLib");
        var project = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "VbLib",
            assemblyName: "VbLib",
            language: LanguageNames.VisualBasic,
            compilationOptions: new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                rootNamespace: "My.Root",
                optionStrict: Microsoft.CodeAnalysis.VisualBasic.OptionStrict.On,
                optionExplicit: false,
                optionInfer: false,
                optionCompareText: true),
            parseOptions: new Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions(
                preprocessorSymbols: [new KeyValuePair<string, object>("DEBUG", true)]),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var args = WorkspaceCommandLineSynthesizer.Synthesize(project, (await project.GetCompilationAsync(CancellationToken))!);
        Assert.Contains("/rootnamespace:My.Root", args);
        Assert.Contains("/optionstrict+", args);
        Assert.Contains("/optionexplicit-", args);
        Assert.Contains("/optioninfer-", args);
        Assert.Contains("/optioncompare:text", args);
        Assert.Contains(args, x => x.StartsWith("/define:DEBUG=", StringComparison.Ordinal));
    }

    /// <summary>
    /// A reference of kind <see cref="MetadataImageKind.Module"/> must be materialized as
    /// <see cref="ModuleMetadata"/> (the compiler casts to it) and captured as a netmodule,
    /// not an assembly (its image has no assembly manifest).
    /// </summary>
    [Fact]
    public void AddFromWorkspace_ExplicitNetModuleReference()
    {
        var moduleCompilation = CSharpCompilation.Create(
            "ModuleLib",
            [CSharpSyntaxTree.ParseText("public class ModuleClass { public static string GetMessage() => \"Hello\"; }", cancellationToken: CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.NetModule));
        var moduleStream = new MemoryStream();
        Assert.True(moduleCompilation.Emit(moduleStream, cancellationToken: CancellationToken).Success);
        var moduleImage = moduleStream.ToArray();

        Guid moduleMvid;
        using (var peReader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(moduleImage)))
        {
            var metadataReader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(peReader);
            moduleMvid = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);
        }

        var moduleReference = BasicMetadataReference.Create(
            [(moduleMvid, moduleImage)],
            new MetadataReferenceProperties(MetadataImageKind.Module),
            "ModuleLib.netmodule");

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Consumer");
        workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Consumer",
            assemblyName: "Consumer",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents: [DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                "Consumer.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("public class Consumer { public string M() => ModuleClass.GetMessage(); }", Encoding.UTF8), VersionStamp.Default)))],
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), moduleReference]));

        // A second consumer of the same module exercises the already-stored dedupe path.
        var secondProjectId = ProjectId.CreateNewId("Consumer2");
        workspace.AddProject(ProjectInfo.Create(
            secondProjectId,
            VersionStamp.Default,
            name: "Consumer2",
            assemblyName: "Consumer2",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents: [DocumentInfo.Create(
                DocumentId.CreateNewId(secondProjectId),
                "Consumer2.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("public class Consumer2 { public string M() => ModuleClass.GetMessage(); }", Encoding.UTF8), VersionStamp.Default)))],
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), moduleReference]));

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;
        Assert.True(result.Succeeded, $"Diagnostics: {string.Join("; ", result.Diagnostics)}");

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCalls = reader.ReadAllCompilerCalls();
        Assert.Equal(2, compilerCalls.Count);
        foreach (var compilerCall in compilerCalls)
        {
            Assert.Contains(reader.ReadAllReferenceData(compilerCall), x => x.Kind == MetadataImageKind.Module);
            var compilationData = reader.ReadCompilationData(compilerCall);
            Assert.Empty(compilationData.GetDiagnostics(CancellationToken).Where(x => x.Severity == DiagnosticSeverity.Error));
        }
    }

    /// <summary>
    /// The reader decodes stored content assuming UTF-8 unless the bytes carry a BOM, so a
    /// BOM-less non-UTF-8 source text has to be re-encoded to survive the round trip.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_NonUtf8SourceTextRoundTrips()
    {
        const string source = "public class Latin1Café { }";
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Latin");
        _ = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Latin",
            assemblyName: "Latin",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents: [DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                "Latin.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source, Encoding.GetEncoding("ISO-8859-1")), VersionStamp.Default)))],
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;
        Assert.True(result.Succeeded);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var sourceTextData = reader.ReadAllSourceTextData(compilerCall).Single(x => x.SourceTextKind == SourceTextKind.SourceCode);
        Assert.Equal(source, reader.ReadSourceText(sourceTextData).ToString());
    }

    /// <summary>
    /// Two projects referencing the same in-memory dependency must reuse the emitted assembly
    /// through the ProjectId-keyed cache rather than emitting it twice.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_SharedProjectReferenceUsesCache()
    {
        using var workspace = new AdhocWorkspace();
        var lib = AddAdhocProject(workspace, "SharedLib", "SharedLib", "public class SharedLib { }");
        _ = AddAdhocProject(workspace, "ConsumerA", "ConsumerA", "public class ConsumerA : SharedLib { }", lib.Id);
        _ = AddAdhocProject(workspace, "ConsumerB", "ConsumerB", "public class ConsumerB : SharedLib { }", lib.Id);

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, x => x.Name.StartsWith("Consumer", StringComparison.Ordinal), CancellationToken);
        complogStream.Position = 0;
        Assert.True(result.Succeeded);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var mvids = reader.ReadAllCompilerCalls()
            .Select(x => reader.ReadAllReferenceData(x).Single(r => r.AssemblyIdentityData.AssemblyName == "SharedLib").Mvid)
            .ToList();
        Assert.Equal(2, mvids.Count);
        Assert.Equal(mvids[0], mvids[1]);
    }

    /// <summary>
    /// When the project name has no "(tfm)" suffix the TargetFramework falls back to the parent
    /// directory of the output path, which for default-layout SDK projects is the TFM.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_TargetFrameworkFromOutputPath()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("TfmLib");
        _ = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "TfmLib",
            assemblyName: "TfmLib",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            outputFilePath: Path.Combine(RootDirectory, "bin", "Debug", "net9.0", "TfmLib.dll")));

        // MSBuildWorkspace names multi-targeted projects "AssemblyName(tfm)"; that takes priority.
        var parenId = ProjectId.CreateNewId("ParenLib(net8.0)");
        _ = workspace.AddProject(ProjectInfo.Create(
            parenId,
            VersionStamp.Default,
            name: "ParenLib(net8.0)",
            assemblyName: "ParenLib",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal("net9.0", result.CompilerCalls.Single(x => x.ProjectFileName.StartsWith("TfmLib", StringComparison.Ordinal)).TargetFramework);
        Assert.Equal("net8.0", result.CompilerCalls.Single(x => x.ProjectFileName.StartsWith("ParenLib", StringComparison.Ordinal)).TargetFramework);
    }

    private sealed class ThrowingTextLoader(Exception exception) : TextLoader
    {
        public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static AdhocWorkspace CreateWorkspaceWithThrowingDocument(Exception exception)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Broken");
        _ = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Broken",
            assemblyName: "Broken",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents: [DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                "Broken.cs",
                loader: new ThrowingTextLoader(exception))],
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        return workspace;
    }

    /// <summary>
    /// Build a workspace whose single project has a metadata reference whose backing file no
    /// longer exists on disk: the compilation itself is fine (the reference bytes were read
    /// eagerly) but serializing it throws when the builder goes back to the file.
    /// </summary>
    private AdhocWorkspace CreateWorkspaceWithMissingReferenceFile()
    {
        var referencePath = Path.Combine(RootDirectory, "missing-reference.dll");
        File.Copy(typeof(object).Assembly.Location, referencePath);
        var reference = MetadataReference.CreateFromFile(referencePath);
        File.Delete(referencePath);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Broken");
        _ = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Broken",
            assemblyName: "Broken",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [reference]));
        return workspace;
    }

    /// <summary>
    /// A project that throws while serializing is recorded as a diagnostic and fails the
    /// result rather than propagating.
    /// </summary>
    [Fact]
    public void TryCreateFromWorkspace_ProjectThrows()
    {
        using var workspace = CreateWorkspaceWithMissingReferenceFile();

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, x => x.Contains("Error adding Broken"));
    }

    [Fact]
    public void TryCreateFromWorkspace_CancellationDuringSerialization()
    {
        using var workspace = CreateWorkspaceWithThrowingDocument(new OperationCanceledException());

        var complogStream = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task CreateFromWorkspaceAsync_FilePath()
    {
        using var workspace = LoadConsoleWorkspace();
        var complogFilePath = Path.Combine(RootDirectory, "workspace-async.complog");

        _ = await CompilerLogUtil.CreateFromWorkspaceAsync(workspace, complogFilePath, cancellationToken: CancellationToken);

        using var reader = CompilerLogReader.Create(complogFilePath, state: State);
        Assert.Single(reader.ReadAllCompilerCalls());
    }

    [Fact]
    public void CreateFromWorkspace_SyncFilePath()
    {
        using var workspace = LoadConsoleWorkspace();
        var complogFilePath = Path.Combine(RootDirectory, "workspace-sync.complog");

        _ = CompilerLogUtil.CreateFromWorkspace(workspace, complogFilePath, cancellationToken: CancellationToken);

        Assert.True(File.Exists(complogFilePath));
    }

    /// <summary>
    /// The throwing variant surfaces per-project failures as a <see cref="CompilerLogException"/>.
    /// </summary>
    [Fact]
    public void CreateFromWorkspace_ThrowsOnFailure()
    {
        using var workspace = CreateWorkspaceWithMissingReferenceFile();

        var complogStream = new MemoryStream();
        var ex = Assert.Throws<CompilerLogException>(() =>
            CompilerLogUtil.CreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken));
        Assert.Contains("Error adding Broken", ex.Message);
    }

    [Fact]
    public void CreateFromWorkspace_FilePath()
    {
        using var workspace = LoadConsoleWorkspace();
        var complogFilePath = Path.Combine(RootDirectory, "workspace.complog");

        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogFilePath, cancellationToken: CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(complogFilePath));

        using var reader = CompilerLogReader.Create(complogFilePath, state: State);
        Assert.Single(reader.ReadAllCompilerCalls());
    }

    /// <summary>
    /// Capturing a workspace runs its generators, which loads the analyzer assemblies. On .NET
    /// Framework <see cref="BasicAnalyzerKind.OnDisk"/> loads them into the current
    /// <see cref="AppDomain"/>, so this runs in a child domain to keep those loads out of the test
    /// process where they would trip the assembly load check in every concurrently running test.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_WithAnalyzerFileReferences()
    {
        RunInContext((BinaryLogPath: Fixture.SolutionBinaryLogPath, ProjectName: Fixture.ConsoleProjectName), static (testOutputHelper, state, cancellationToken) =>
        {
            using var solutionReader = SolutionReader.Create(state.BinaryLogPath, BasicAnalyzerKind.OnDisk, predicate: x => x.ProjectFileName == state.ProjectName);
            var workspace = new AdhocWorkspace();
            workspace.AddSolution(solutionReader.ReadSolutionInfo());
            var project = workspace.CurrentSolution.Projects.Single();

            var complogStream = new MemoryStream();
            var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: cancellationToken);
            complogStream.Position = 0;

            Assert.True(result.Succeeded, $"Diagnostics: {string.Join("; ", result.Diagnostics)}");
            Assert.Empty(result.Diagnostics);

            using var reader = CompilerLogReader.Create(complogStream, leaveOpen: false);
            var compilerCall = reader.ReadAllCompilerCalls().Single();
            var analyzerData = reader.ReadAllAnalyzerData(compilerCall);
            Assert.Equal(project.AnalyzerReferences.Count, analyzerData.Count);
        });
    }

    [Fact]
    public async Task AddFromWorkspace_GeneratedTextRoundTrip()
    {
        using var workspace = LoadConsoleWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();
        var generated = (await project.GetSourceGeneratedDocumentsAsync(CancellationToken)).ToList();

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;

        Assert.True(result.Succeeded);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        Assert.Equal(generated.Count, reader.ReadAllGeneratedSourceTexts(compilerCall).Count);
    }

    [Fact]
    public void TryCreateFromWorkspace_PropagatesCancellation()
    {
        using var workspace = LoadConsoleWorkspace();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var complogStream = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() =>
            CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: cts.Token));
    }

    [Fact]
    public void TryCreateFromWorkspace_SynthesizesCommandLine()
    {
        // The Roslyn workspace API does not surface emit-time inputs (resources, manifests,
        // etc.) so the stored command line is synthesized on a best-effort basis: it keeps
        // rsp / replay / export functional while accepting fidelity gaps around emit.
        using var workspace = LoadConsoleWorkspace();

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;
        Assert.True(result.Succeeded);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var arguments = reader.ReadArguments(compilerCall);
        Assert.NotEmpty(arguments);
        Assert.Contains("/noconfig", arguments);
        Assert.Contains(arguments, x => x.EndsWith(".cs", StringComparison.Ordinal));
        Assert.Contains(arguments, x => x.StartsWith("/reference:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same binary log should convert to the same bytes every time, so that storage which
    /// deduplicates by content sees one blob rather than one per conversion.
    /// </summary>
    [Fact]
    public void ConvertBinaryLogIsByteIdentical()
    {
        var hashes = Enumerable
            .Range(0, 3)
            .Select(_ => GetHash(CreateComplog()))
            .Distinct()
            .ToList();
        Assert.Single(hashes);

        byte[] CreateComplog()
        {
            using var complogStream = new MemoryStream();
            using var binlogStream = new FileStream(Fixture.SolutionBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Empty(CompilerLogUtil.ConvertBinaryLog(binlogStream, complogStream));
            return complogStream.ToArray();
        }

        static string GetHash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes));
        }
    }

    /// <summary>
    /// Zip entries default their modification time to the moment the entry is created. That is not
    /// read back anywhere, but it does put a different value in the header in front of every entry,
    /// so it has to be pinned for <see cref="ConvertBinaryLogIsByteIdentical"/> to hold. Checked
    /// separately because a zip timestamp only has two second resolution, so three conversions in a
    /// row can land on the same value by luck.
    /// </summary>
    [Fact]
    public void ZipEntryTimestampsAreFixed()
    {
        using var complogStream = new MemoryStream();
        using (var binlogStream = new FileStream(Fixture.SolutionBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Empty(CompilerLogUtil.ConvertBinaryLog(binlogStream, complogStream));
        }

        complogStream.Position = 0;
        using var zip = new ZipArchive(complogStream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.NotEmpty(zip.Entries);
        Assert.All(zip.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));
    }
}
