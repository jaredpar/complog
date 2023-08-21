using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class SolutionReaderTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public SolutionReaderTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(SolutionReader))
    {
        Fixture = fixture;
    }

    private void LoadAllCore(BasicAnalyzerHostOptions options)
    {
        foreach (var complogPath in Fixture.GetAllCompLogs())
        {
            using var reader = SolutionReader.Create(complogPath, options);
            var workspace = new AdhocWorkspace();
            var solution = workspace.AddSolution(reader.ReadSolutionInfo());
            Assert.NotEmpty(solution.Projects);
        }
    }

    [Fact]
    public void LoadAllWithAnalyzers() =>
        LoadAllCore(BasicAnalyzerHostOptions.Default);

    [Fact]
    public void LoadAllWithoutAnalyzers() =>
        LoadAllCore(BasicAnalyzerHostOptions.None);

    [Theory]
    [InlineData(BasicAnalyzerKind.Default)]
    [InlineData(BasicAnalyzerKind.None)]
    public async Task DocumentsHaveGeneratedTextWithAnalyzers(BasicAnalyzerKind kind)
    {
        var host = new BasicAnalyzerHostOptions(kind);
        using var reader = SolutionReader.Create(Fixture.ConsoleComplogPath.Value, host);
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(reader.ReadSolutionInfo());
        var project = solution.Projects.Single();
        Assert.NotEmpty(project.AnalyzerReferences);
        var docs = await project.GetSourceGeneratedDocumentsAsync();
        var doc = docs.First(x => x.Name == "RegexGenerator.g.cs");
        Assert.NotNull(doc);
    }
}
