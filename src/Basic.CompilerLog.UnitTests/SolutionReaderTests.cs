using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

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

    private static string GetLogFilePath(LogData logData, bool useBinaryLog) =>
        useBinaryLog ? logData.BinaryLogPath! : logData.CompilerLogPath;

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
                .Where(x => Path.GetFileName(x.FilePath) == "console-with-reference.csproj")
                .Single();
            Assert.Equal("console-with-reference", consoleProject.Name);
            var projectReference = consoleProject.ProjectReferences.Single();
            var utilProject = solution.GetProject(projectReference.ProjectId);
            Assert.NotNull(utilProject);
            Assert.Equal("util", utilProject.Name);
            var compilation = await consoleProject.GetCompilationAsync(CancellationToken);
            Assert.NotNull(compilation);
            var result = compilation.EmitToMemory(cancellationToken: CancellationToken);
            Assert.True(result.Success);
        }
    }

    [Fact]
    public async Task CryptoKeyFile()
    {
        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        var reader = SolutionReader.Create(Fixture.ConsoleSigned.Value.CompilerLogPath, BasicAnalyzerKind.None);
        ReaderList.Add(reader);

        var workspace = new AdhocWorkspace();
        var project = workspace.AddSolution(reader.ReadSolutionInfo()).Projects.Single();
        project = project.WithCompilationOptions(
            ((CSharpCompilationOptions)project.CompilationOptions!).WithPublicSign(true));
        var cryptoKeyFile = project.CompilationOptions!.CryptoKeyFile;

        Assert.NotNull(cryptoKeyFile);
        Assert.Equal(
            reader.Reader.LogReaderState.CryptoKeyFileDirectory,
            Path.GetDirectoryName(cryptoKeyFile));
        Assert.True(File.Exists(cryptoKeyFile));
        Assert.Equal(keyBytes, File.ReadAllBytes(cryptoKeyFile));

        var compilation = await project.GetCompilationAsync(CancellationToken);
        Assert.NotNull(compilation);
        var diagnostics = compilation.GetDiagnostics(CancellationToken);
        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleTargetProjectNameUsesProjectFileBaseName(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.Console.Value, useBinaryLog);
        var project = GetSolution(filePath, BasicAnalyzerKind.None).Projects.Single();

        Assert.Equal("console", project.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiTargetProjectNamesIncludeTargetFramework(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ClassLibMulti.Value, useBinaryLog);
        var projects = GetSolution(filePath, BasicAnalyzerKind.None).Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [$"classlibmulti ({TestUtil.TestTargetFramework})", "classlibmulti (net6.0)"],
            projects.Select(project => project.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssemblyNameExcludesOutputExtension(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ClassLibMulti.Value, useBinaryLog);
        var projects = GetSolution(filePath, BasicAnalyzerKind.None).Projects.ToList();

        Assert.All(projects, project => Assert.Equal("classlibmulti", project.AssemblyName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocumentPropertiesMatchWorkspaceConventions(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ConsoleComplex.Value, useBinaryLog);
        var project = GetSolution(filePath, BasicAnalyzerKind.None).Projects.Single();
        var document = project.Documents.Single(document => Path.GetFileName(document.FilePath) == "Nested.cs");

        Assert.Equal("Nested.cs", document.Name);
        Assert.Equal(["Features"], document.Folders);
        Assert.True(Path.IsPathRooted(document.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditionalDocumentPropertiesMatchWorkspaceConventions(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ConsoleComplex.Value, useBinaryLog);
        var project = GetSolution(filePath, BasicAnalyzerKind.None).Projects.Single();
        var document = Assert.Single(project.AdditionalDocuments);

        Assert.Equal("additional.txt", document.Name);
        Assert.Equal(["Assets"], document.Folders);
        Assert.True(Path.IsPathRooted(document.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnalyzerConfigDocumentPropertiesMatchWorkspaceConventions(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ConsoleComplex.Value, useBinaryLog);
        var project = GetSolution(filePath, BasicAnalyzerKind.None).Projects.Single();
        var document = project.AnalyzerConfigDocuments.Single(
            document => document.FilePath!.EndsWith(
                Path.Combine("Features", ".editorconfig"),
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(".editorconfig", document.Name);
        Assert.Equal(["Features"], document.Folders);
        Assert.True(Path.IsPathRooted(document.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataReferenceDisplayUsesReferenceFileName(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.Console.Value, useBinaryLog);
        var reader = SolutionReader.Create(filePath, BasicAnalyzerKind.None);
        ReaderList.Add(reader);
        var compilerCall = reader.Reader.ReadAllCompilerCalls().Single();
        var expectedReference = reader.Reader.ReadAllReferenceData(compilerCall)
            .First(reference => Path.GetFileName(reference.FilePath) == "System.Runtime.dll");
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddSolution(reader.ReadSolutionInfo()).Projects.Single();
        var actualReference = project.MetadataReferences.Single(
            reference => Path.GetFileName(reference.Display) == "System.Runtime.dll");

        var expectedDisplay = useBinaryLog ? expectedReference.FilePath : expectedReference.FileName;
        Assert.Equal(expectedDisplay, actualReference.Display);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnalyzerReferencePathsPointToExtractedFiles(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.Console.Value, useBinaryLog);
        var reader = SolutionReader.Create(filePath, BasicAnalyzerKind.OnDisk);
        ReaderList.Add(reader);
        var compilerCall = reader.Reader.ReadAllCompilerCalls().Single();
        var expectedReferenceNames = reader.Reader.ReadAllAnalyzerData(compilerCall)
            .Select(reference => reference.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddSolution(reader.ReadSolutionInfo()).Projects.Single();

        Assert.NotEmpty(expectedReferenceNames);
        Assert.Equal(expectedReferenceNames.Count, project.AnalyzerReferences.Count);
        foreach (var reference in project.AnalyzerReferences)
        {
            Assert.Contains(Path.GetFileName(reference.FullPath!), expectedReferenceNames);
            Assert.Equal(Path.GetFileNameWithoutExtension(reference.FullPath), reference.Display);
            Assert.True(Path.IsPathRooted(reference.FullPath));
            Assert.True(File.Exists(reference.FullPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputFilePathUsesCompilerOutput(bool useBinaryLog)
    {
        var logData = Fixture.Console.Value;
        var filePath = GetLogFilePath(logData, useBinaryLog);
        var reader = SolutionReader.Create(filePath, BasicAnalyzerKind.None);
        ReaderList.Add(reader);
        var compilerCall = reader.Reader.ReadAllCompilerCalls().Single();
        var compilerCallData = reader.Reader.ReadCompilerCallData(compilerCall);
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddSolution(reader.ReadSolutionInfo()).Projects.Single();
        var expectedPath = Path.Combine(compilerCallData.OutputDirectory!, compilerCallData.AssemblyFileName);

        Assert.Equal(expectedPath, project.OutputFilePath);
        Assert.True(Path.IsPathRooted(project.OutputFilePath));
        Assert.True(File.Exists(project.OutputFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiTargetProjectReferenceMapsToMatchingTargetFramework(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ConsoleWithMultiTargetReference.Value, useBinaryLog);
        var reader = SolutionReader.Create(filePath, BasicAnalyzerKind.None);
        ReaderList.Add(reader);
        var firstSolutionInfo = reader.ReadSolutionInfo();
        var secondSolutionInfo = reader.ReadSolutionInfo();
        Assert.Equal(
            firstSolutionInfo.Projects.Select(project => (project.FilePath, project.Id)),
            secondSolutionInfo.Projects.Select(project => (project.FilePath, project.Id)));

        using var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(firstSolutionInfo);
        var appProject = solution.Projects.Single(
            project => Path.GetFileName(project.FilePath) == "console-with-reference.csproj");
        var referencedProject = solution.GetProject(Assert.Single(appProject.ProjectReferences).ProjectId);
        var utilProjects = solution.Projects
            .Where(project => project.FilePath!.EndsWith("util.csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var targetFrameworkSymbol = TestUtil.TestTargetFramework
            .Replace('.', '_')
            .Replace('-', '_')
            .ToUpperInvariant();
        var matchingProject = utilProjects.Single(
            project => ((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames.Contains(targetFrameworkSymbol));
        var otherProject = utilProjects.Single(project => project.Id != matchingProject.Id);

        Assert.Equal(2, utilProjects.Count);
        Assert.Equal(matchingProject.Id, referencedProject!.Id);
        Assert.NotEqual(otherProject.Id, referencedProject.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PredicateDoesNotInventUncompiledTargetFrameworkVariants(bool useBinaryLog)
    {
        var filePath = GetLogFilePath(Fixture.ClassLibMulti.Value, useBinaryLog);
        var reader = SolutionReader.Create(
            filePath,
            BasicAnalyzerKind.None,
            predicate: compilerCall => compilerCall.TargetFramework == TestUtil.TestTargetFramework);
        ReaderList.Add(reader);
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddSolution(reader.ReadSolutionInfo()).Projects.Single();

        Assert.Equal(1, reader.ProjectCount);
        Assert.Equal($"classlibmulti ({TestUtil.TestTargetFramework})", project.Name);
    }

}
