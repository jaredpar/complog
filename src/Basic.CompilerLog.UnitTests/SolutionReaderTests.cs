using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

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

    [Theory]
    [CombinatorialData]
    public async Task DocumentsGeneratedDefaultHost(BasicAnalyzerKind basicAnalyzerKind)
    {
        using var reader = SolutionReader.Create(Fixture.Console.Value.CompilerLogPath, basicAnalyzerKind);
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(reader.ReadSolutionInfo());
        var project = solution.Projects.Single();
        Assert.NotEmpty(project.AnalyzerReferences);
        var docs = project.Documents.ToList();
        var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync()).ToList();
        Assert.Null(docs.FirstOrDefault(x => x.Name == "RegexGenerator.g.cs"));
        Assert.Single(generatedDocs);
        Assert.NotNull(generatedDocs.First(x => x.Name == "RegexGenerator.g.cs"));
    }

    [Fact]
    public void CreateRespectLeaveOpen()
    {
        using var stream = new FileStream(Fixture.ConsoleComplex.Value.CompilerLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = SolutionReader.Create(stream, leaveOpen: true);
        reader.Dispose();

        // Throws if the underlying stream is disposed
        stream.Seek(0, SeekOrigin.Begin);
    }
}
