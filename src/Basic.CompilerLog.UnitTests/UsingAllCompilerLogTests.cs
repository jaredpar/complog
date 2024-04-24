
using System.Configuration;
using System.Windows.Markup;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
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
                    AssertEx.Success(TestOutputHelper, emitResult);
                    Assert.NotEmpty(emitResult.Directory);
                    Assert.NotEmpty(emitResult.AssemblyFileName);
                    Assert.NotEmpty(emitResult.AssemblyFilePath);

                    var emitFlags = data.EmitFlags;
                    if ((emitFlags & EmitFlags.IncludePdbStream) != 0)
                    {
                        Assert.NotNull(emitResult.PdbFilePath);
                    }

                    if ((emitFlags & EmitFlags.IncludeXmlStream) != 0)
                    {
                        Assert.NotNull(emitResult.XmlFilePath);
                    }

                    if ((emitFlags & EmitFlags.IncludeMetadataStream) != 0)
                    {
                        Assert.NotNull(emitResult.MetadataFilePath);
                    }
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
            using var reader = CompilerLogReader.Create(complogPath, options: CompilerLogReaderOptions.None);
            foreach (var data in reader.ReadAllCompilationData())
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                AssertEx.Success(TestOutputHelper, emitResult);
            }
        }
    }

    [Fact]
    public async Task EmitToMemoryWithSeparateState()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var state = new CompilerLogState(baseDir: Root.NewDirectory());
            foreach (var data in ReadAll(complogPath, state))
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                AssertEx.Success(TestOutputHelper, emitResult);
            }
        }

        static List<CompilationData> ReadAll(string complogPath, CompilerLogState state)
        {
            using var reader = CompilerLogReader.Create(complogPath, options: CompilerLogReaderOptions.None, state: state);
            return reader.ReadAllCompilationData();
        }
    }

    [Fact]
    public async Task CommandLineArguments()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath, options: CompilerLogReaderOptions.None);
            foreach (var data in reader.ReadAllCompilerCalls())
            {
                var fileName = Path.GetFileName(complogPath);
                if (fileName is "windows-console.complog" ||
                    fileName is "linux-console.complog")
                {
                    Assert.Null(data.CompilerFilePath);
                }
                else
                {
                    Assert.NotNull(data.CompilerFilePath);
                }
                
                Assert.NotEmpty(data.GetArguments());
            }
        }
    }

    /// <summary>
    /// The classification path exercises a lot of items like analyzer options. 
    /// </summary>
    [Fact]
    public async Task ClassifyAll()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            using var reader = SolutionReader.Create(complogPath, CompilerLogReaderOptions.None);
            using var workspace = new AdhocWorkspace();
            workspace.AddSolution(reader.ReadSolutionInfo());
            foreach (var project in workspace.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var text = await document.GetTextAsync();
                    var textSpan = new TextSpan(0, text.Length);
                    _ = await Classifier.GetClassifiedSpansAsync(document, textSpan);
                }
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
        var options = none ? CompilerLogReaderOptions.None : CompilerLogReaderOptions.Default;
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
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
    public async Task VerifyConsistentOptions()
    {
        await foreach (var logData in Fixture.GetAllLogs(TestOutputHelper))
        {
            if (logData.BinaryLogPath is null)
            {
                continue;
            }

            using var complogReader = CompilerLogReader.Create(logData.CompilerLogPath);
            using var binlogReader = CompilerLogReader.Create(logData.BinaryLogPath);
            var complogDataList = complogReader.ReadAllCompilationData();
            var binlogDataList = binlogReader.ReadAllCompilationData();
            Assert.Equal(complogDataList.Count, binlogDataList.Count);
            for (int i = 0; i < complogDataList.Count; i++)
            {
                Assert.Equal(complogDataList[i].EmitOptions, binlogDataList[i].EmitOptions);
                Assert.Equal(complogDataList[i].ParseOptions, binlogDataList[i].ParseOptions);

                var complogOptions = Normalize(complogDataList[i].CompilationOptions);
                var binlogOptions = Normalize(binlogDataList[i].CompilationOptions);
                Assert.Equal(complogOptions, binlogOptions);

                CompilationOptions Normalize(CompilationOptions options) => options
                    .WithCryptoKeyFile(null)
                    .WithStrongNameProvider(null)
                    .WithSyntaxTreeOptionsProvider(null);
            }
        }
    }
}
