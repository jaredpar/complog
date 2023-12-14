
using System.Configuration;
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
        var list = new List<Task>();
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            var task = Task.Run(() => 
            {
                using var reader = CompilerLogReader.Create(complogPath);
                foreach (var data in reader.ReadAllCompilationData())
                {
                    using var testDir = new TempDir();
                    TestOutputHelper.WriteLine($"{Path.GetFileName(complogPath)}: {data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                    var emitResult = data.EmitToDisk(testDir.DirectoryPath);
                    Assert.True(emitResult.Success);
                }
            });

            list.Add(task);
        }

        Assert.NotEmpty(list);
        await Task.WhenAll(list);
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

    [Fact]
    public async Task EmitToMemoryWithSeparateState()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var state = new CompilerLogState(cryptoKeyFileDirectoryBase: Root.NewDirectory());
            foreach (var data in ReadAll(complogPath, state))
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                Assert.True(emitResult.Success);
            }
        }

        static List<CompilationData> ReadAll(string complogPath, CompilerLogState state)
        {
            using var reader = CompilerLogReader.Create(complogPath, options: BasicAnalyzerHostOptions.None, state: state);
            return reader.ReadAllCompilationData();
        }
    }

    [Fact]
    public async Task CommandLineArguments()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath, options: BasicAnalyzerHostOptions.None);
            foreach (var data in reader.ReadAllCompilerCalls())
            {
                Assert.NotEmpty(data.GetArguments());
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportAndBuild(bool includeAnalyzers)
    {
        var list = new List<Task>();
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            var task = Task.Run(() => ExportUtilTests.TestExport(TestOutputHelper, complogPath, expectedCount: null, includeAnalyzers, runBuild: true));
            list.Add(task);
        }

        await Task.WhenAll(list);
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
