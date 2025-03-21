﻿using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class SolutionReaderTests : TestBase
{
    public List<SolutionReader> ReaderList { get; } = new();
    public CompilerLogFixture Fixture { get; }

    public SolutionReaderTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(SolutionReader))
    {
        Fixture = fixture;
    }

    public override void Dispose()
    {
        foreach (var reader in ReaderList)
        {
            reader.Dispose();
        }
        ReaderList.Clear();

#if NET
        // The underlying solution structure holds lots of references that root our contexts 
        // so there is no way to fully free here.
        OnDiskLoader.ClearActiveAssemblyLoadContext();
#endif

        base.Dispose();
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
    [MemberData(nameof(GetSimpleBasicAnalyzerKinds))]
    public async Task DocumentsGeneratedDefaultHost(BasicAnalyzerKind basicAnalyzerKind)
    {
        await Run(Fixture.Console.Value.BinaryLogPath!);
        await Run(Fixture.Console.Value.CompilerLogPath);

        async Task Run(string filePath)
        {
            var solution = GetSolution(filePath, basicAnalyzerKind);
            var project = solution.Projects.Single();
            Assert.NotEmpty(project.AnalyzerReferences);
            var docs = project.Documents.ToList();
            var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync(CancellationToken)).ToList();
            Assert.Null(docs.FirstOrDefault(x => x.Name == "RegexGenerator.g.cs"));
            Assert.Single(generatedDocs);
            Assert.NotNull(generatedDocs.First(x => x.Name == "RegexGenerator.g.cs"));
        }
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
        await Run(Fixture.ConsoleWithReference.Value.BinaryLogPath!);
        await Run(Fixture.ConsoleWithReference.Value.CompilerLogPath);

        async Task Run(string filePath)
        {
            var solution = GetSolution(filePath, BasicAnalyzerKind.None);
            var consoleProject = solution.Projects
                .Where(x => x.Name == "console-with-reference.csproj")
                .Single();
            var projectReference = consoleProject.ProjectReferences.Single();
            var utilProject = solution.GetProject(projectReference.ProjectId);
            Assert.NotNull(utilProject);
            Assert.Equal("util.csproj", utilProject.Name);
            var compilation = await consoleProject.GetCompilationAsync(CancellationToken);
            Assert.NotNull(compilation);
            var result = compilation.EmitToMemory(cancellationToken: CancellationToken);
            Assert.True(result.Success);
        }
    }
}
