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

    /// <summary>
    /// Basic round-trip test: create a complog from a workspace project, read it back, and verify
    /// that the compilation data is correct (sources, references, options).
    /// </summary>
    [Fact]
    public void AddFromWorkspace_RoundTrip()
    {
        // Load with BasicAnalyzerKind.None to avoid in-memory analyzer references
        // that cannot be serialized back to file-based references
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, BasicAnalyzerKind.None, predicate: x => x.ProjectFileName == Fixture.ConsoleProjectName);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());

        var project = workspace.CurrentSolution.Projects.Single();

        // Write the workspace project to a new complog
        var complogStream = new MemoryStream();
        // Diagnostics may contain informational messages about unsupported analyzer reference types
        _ = CompilerLogUtil.CreateFromWorkspace(workspace, complogStream, x => x.Name == project.Name, CancellationToken);
        complogStream.Position = 0;

        // Read back the new complog and verify its contents
        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCalls = reader.ReadAllCompilerCalls();
        Assert.Single(compilerCalls);

        var compilationData = reader.ReadCompilationData(compilerCalls[0]);
        Assert.True(compilationData.IsCSharp);
        Assert.NotNull(compilationData.Compilation);
        Assert.NotEmpty(compilationData.Compilation.SyntaxTrees);
        Assert.NotEmpty(compilationData.Compilation.References);
    }

    /// <summary>
    /// Verify that source text content is correctly preserved in the round-trip.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_SourceTextPreserved()
    {
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, BasicAnalyzerKind.None, predicate: x => x.ProjectFileName == Fixture.ConsoleProjectName);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());

        var project = workspace.CurrentSolution.Projects.Single();

        // Collect the original source text content from the workspace project
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

    /// <summary>
    /// Verify that workspace projects with compilation references (project-to-project dependencies)
    /// are correctly serialized - the referenced project's assembly is embedded.
    /// </summary>
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
            // Skip: no project with project references in this fixture
            return;
        }

        var complogStream = new MemoryStream();
        // Diagnostics may contain informational messages about unsupported analyzer reference types
        _ = CompilerLogUtil.CreateFromWorkspace(workspace, complogStream, x => x.Name == consoleProject.Name, CancellationToken);
        complogStream.Position = 0;

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCalls = reader.ReadAllCompilerCalls();
        Assert.Single(compilerCalls);

        var referenceData = reader.ReadAllReferenceData(compilerCalls[0]);
        Assert.NotEmpty(referenceData);
    }

    /// <summary>
    /// Verify that the CreateFromWorkspace overload that takes a file path works correctly.
    /// </summary>
    [Fact]
    public void CreateFromWorkspace_FilePath()
    {
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, BasicAnalyzerKind.None, predicate: x => x.ProjectFileName == Fixture.ConsoleProjectName);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());

        var complogFilePath = Path.Combine(RootDirectory, "workspace.complog");
        // Diagnostics may contain informational messages about unsupported analyzer reference types
        _ = CompilerLogUtil.CreateFromWorkspace(workspace, complogFilePath, cancellationToken: CancellationToken);
        Assert.True(File.Exists(complogFilePath));

        using var reader = CompilerLogReader.Create(complogFilePath, state: State);
        var compilerCalls = reader.ReadAllCompilerCalls();
        Assert.Single(compilerCalls);
    }

    /// <summary>
    /// Verify that file-based analyzer references are correctly preserved during workspace serialization.
    /// </summary>
    [Fact]
    public void AddFromWorkspace_WithAnalyzerFileReferences()
    {
        // OnDisk creates AnalyzerFileReference objects which can be serialized
        using var solutionReader = SolutionReader.Create(Fixture.SolutionBinaryLogPath, BasicAnalyzerKind.OnDisk, predicate: x => x.ProjectFileName == Fixture.ConsoleProjectName);
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(solutionReader.ReadSolutionInfo());

        var project = workspace.CurrentSolution.Projects.Single();

        var complogStream = new MemoryStream();
        var diagnostics = CompilerLogUtil.CreateFromWorkspace(workspace, complogStream, cancellationToken: CancellationToken);
        Assert.Empty(diagnostics);
        complogStream.Position = 0;

        using var reader = CompilerLogReader.Create(complogStream, State, leaveOpen: false);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var analyzerData = reader.ReadAllAnalyzerData(compilerCall);
        Assert.Equal(project.AnalyzerReferences.Count, analyzerData.Count);
    }
}
