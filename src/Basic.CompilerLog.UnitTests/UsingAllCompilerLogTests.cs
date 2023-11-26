
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
    public void EmitToDisk()
    {
        var all = Fixture.GetAllCompilerLogs(TestOutputHelper);
        Assert.NotEmpty(all);
        foreach (var complogPath in all)
        {
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
    }

    [Fact]
    public void EmitToMemory()
    {
        var all = Fixture.GetAllCompilerLogs(TestOutputHelper);
        Assert.NotEmpty(all);
        foreach (var complogPath in all)
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
    public void AllCompilerLogs(bool includeAnalyzers)
    {
        foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            ExportUtilTests.TestExport(TestOutputHelper, complogPath, expectedCount: null, includeAnalyzers, runBuild: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadAllCore(bool none)
    {
        var options = none ? BasicAnalyzerHostOptions.None : BasicAnalyzerHostOptions.Default;
        foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            using var reader = SolutionReader.Create(complogPath, options);
            var workspace = new AdhocWorkspace();
            var solution = workspace.AddSolution(reader.ReadSolutionInfo());
            Assert.NotEmpty(solution.Projects);
        }
    }

    /// <summary>
    /// Ensure that our options round tripping code is correct and produces the same result as 
    /// argument parsing. This will also catch cases where new values are added to the options 
    /// that are not being set by our code base.
    /// </summary>
    [Fact]
    public void OptionsCorrectness()
    {
        var all = Fixture.GetAllCompilerLogs(TestOutputHelper);
        Assert.NotEmpty(all);
        foreach (var complogPath in all)
        {
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath);
            foreach (var data in reader.ReadAllCompilationData())
            {
                var args = data.CompilerCall.ParseArguments();
                Assert.Equal(args.EmitOptions, data.EmitOptions);
                Assert.Equal(args.ParseOptions, data.ParseOptions);

                // TODO: can't round trip ruleset options yet because it isn't 
                // handled in specific diagnostic potions
                if (complogPath != Fixture.ConsoleComplexComplogPath.Value)
                {
                    var expectedCompilationOptions = args.CompilationOptions
                        .WithCryptoKeyFile(null);
                    var actualCompilationOptions = data.CompilationOptions
                        .WithSyntaxTreeOptionsProvider(null)
                        .WithStrongNameProvider(null)
                        .WithCryptoKeyFile(null);
                    Assert.Equal(expectedCompilationOptions, actualCompilationOptions);
                }
            }
        }
    }
}
