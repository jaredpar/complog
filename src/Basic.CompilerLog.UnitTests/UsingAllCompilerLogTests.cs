
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class UsingAllCompilerLogTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public UsingAllCompilerLogTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(UsingAllCompilerLogTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public async Task EmitToDisk()
    {
        var any = false;
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            any = true;
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath);
            foreach (var data in reader.ReadAllCompilationData())
            {
                using var testDir = new TempDir();
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToDisk(testDir.DirectoryPath);
                Assert.True(emitResult.Success);
            }
        }

        Assert.True(any);
    }

    [Fact]
    public async Task EmitToMemory()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath, options: BasicAnalyzerHostOptions.None);
            foreach (var data in reader.ReadAllCompilationData())
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                Assert.True(emitResult.Success);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportAndBuild(bool includeAnalyzers)
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            ExportUtilTests.TestExport(TestOutputHelper, complogPath, expectedCount: null, includeAnalyzers, runBuild: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAllCore(bool none)
    {
        var options = none ? BasicAnalyzerHostOptions.None : BasicAnalyzerHostOptions.Default;
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            using var reader = SolutionReader.Create(complogPath, options);
            var workspace = new AdhocWorkspace();
            var solution = workspace.AddSolution(reader.ReadSolutionInfo());
            Assert.NotEmpty(solution.Projects);
        }
    }
}
