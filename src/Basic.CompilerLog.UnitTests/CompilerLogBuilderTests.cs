using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(SolutionFixtureCollection.Name)]
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

    [Fact]
    public void AddFromWorkspace_RoundTrip()
    {
        using var workspace = LoadConsoleWorkspace();
        var project = workspace.CurrentSolution.Projects.Single();

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, x => x.Name == project.Name, CancellationToken);
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
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, BasicAnalyzerKind.None);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());

        var consoleProject = workspace.CurrentSolution.Projects
            .FirstOrDefault(p => p.Language == LanguageNames.CSharp && p.ProjectReferences.Any());
        if (consoleProject is null)
        {
            Assert.Skip("Fixture has no C# project with ProjectReferences");
            return;
        }

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, x => x.Name == consoleProject.Name, CancellationToken);
        complogStream.Position = 0;

        Assert.True(result.Succeeded);
        Assert.Single(result.CompilerCalls);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        Assert.NotEmpty(reader.ReadAllReferenceData(compilerCall));
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

    [Fact]
    public void AddFromWorkspace_WithAnalyzerFileReferences()
    {
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, BasicAnalyzerKind.OnDisk, predicate: x => x.ProjectFileName == Fixture.ConsoleProjectName);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());
        var project = workspace.CurrentSolution.Projects.Single();

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;

        Assert.True(result.Succeeded, $"Diagnostics: {string.Join("; ", result.Diagnostics)}");
        Assert.Empty(result.Diagnostics);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var analyzerData = reader.ReadAllAnalyzerData(compilerCall);
        Assert.Equal(project.AnalyzerReferences.Count, analyzerData.Count);
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
    public void TryCreateFromWorkspace_LeavesArgsEmpty()
    {
        // Workspace-derived complogs deliberately omit a synthesized command line because the
        // Roslyn workspace API does not surface emit-time inputs (resources, manifests, etc.);
        // a partial rsp would mislead replay/export. Lock in that the args are empty so we
        // notice if anyone wires the synthesizer back in by default.
        using var workspace = LoadConsoleWorkspace();

        var complogStream = new MemoryStream();
        var result = CompilerLogUtil.TryCreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        complogStream.Position = 0;
        Assert.True(result.Succeeded);

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        Assert.Empty(reader.ReadArguments(compilerCall));
    }
}
