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
    public List<SolutionReader> ReaderList { get; } = new();
    public CompilerLogFixture Fixture { get; }

    public SolutionReaderTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(SolutionReader))
    {
        Fixture = fixture;
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var reader in ReaderList)
        {
            reader.Dispose();
        }
    }

    private Solution GetSolution(string compilerLogFilePath, BasicAnalyzerKind basicAnalyzerKind)
    {
        var reader = SolutionReader.Create(compilerLogFilePath, basicAnalyzerKind);
        ReaderList.Add(reader);
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(reader.ReadSolutionInfo());
        return solution;
    }

    [Theory]
    [MemberData(nameof(GetSupportedBasicAnalyzerKinds))]
    public async Task DocumentsGeneratedDefaultHost(BasicAnalyzerKind basicAnalyzerKind)
    {
        var solution = GetSolution(Fixture.Console.Value.CompilerLogPath, basicAnalyzerKind);
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

    [Fact]
    public async Task ProjectReference_Simple()
    {
        var solution = GetSolution(Fixture.ConsoleWithReference.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var consoleProject = solution.Projects
            .Where(x => x.Name == "console-with-reference.csproj")
            .Single();
        var projectReference = consoleProject.ProjectReferences.Single();
        var utilProject = solution.GetProject(projectReference.ProjectId);
        Assert.NotNull(utilProject);
        Assert.Equal("util.csproj", utilProject.Name);
        var compilation = await consoleProject.GetCompilationAsync();
        Assert.NotNull(compilation);
        var result = compilation.EmitToMemory();
        Assert.True(result.Success);
    }
}
