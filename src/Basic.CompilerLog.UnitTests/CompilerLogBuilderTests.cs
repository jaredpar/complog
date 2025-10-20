using Basic.CompilerLog.Util;
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
}
