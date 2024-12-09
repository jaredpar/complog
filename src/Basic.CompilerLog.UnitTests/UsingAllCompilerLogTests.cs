
using System.Configuration;
using System.Runtime.InteropServices;
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
    public async Task GetAllLogData()
    {
        var count = 0;
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper, BasicAnalyzerKind.OnDisk))
        {
            count++;
        }
        Assert.Equal(Fixture.AllLogs.Length, count);

        count = 0;
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper, BasicAnalyzerKind.None))
        {
            count++;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(Fixture.AllLogs.Length - 1, count);
        }
        else
        {
            Assert.Equal(Fixture.AllLogs.Length, count);
        }
    }

    [Fact]
    public async Task EmitToDisk()
    {
        var list = new List<Task>();
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            var task = Task.Run(() => 
            {
                using var reader = CompilerLogReader.Create(complogPath, basicAnalyzerKind: BasicAnalyzerKind.None);
                foreach (var data in reader.ReadAllCompilationData())
                {
                    if (!reader.HasAllGeneratedFileContent(data.CompilerCall))
                    {
                        continue;
                    }

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

    /// <summary>
    /// Make sure paths for generated files don't have illegal characters when using the none host
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task GeneratedFilePathsNoneHost()
    {
        char[] illegalChars = ['<', '>'];
        await foreach (var logPath in Fixture.GetAllLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(logPath);
            using var reader = CompilerCallReaderUtil.Create(logPath, BasicAnalyzerKind.None);
            foreach (var data in reader.ReadAllCompilationData(reader.HasAllGeneratedFileContent))
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var generatedTrees = data.GetGeneratedSyntaxTrees();
                foreach (var tree in generatedTrees)
                {
                    foreach (var c in illegalChars)
                    {
                        Assert.DoesNotContain(c, tree.FilePath);
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetSimpleBasicAnalyzerKinds))]
    public async Task EmitToMemory(BasicAnalyzerKind basicAnalyzerKind)
    {
        TestOutputHelper.WriteLine($"BasicAnalyzerKind: {basicAnalyzerKind}");
        await foreach (var logPath in Fixture.GetAllLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(logPath);
            using var reader = CompilerCallReaderUtil.Create(logPath, basicAnalyzerKind);
            foreach (var data in reader.ReadAllCompilationData(cc => basicAnalyzerKind != BasicAnalyzerKind.None || reader.HasAllGeneratedFileContent(cc)))
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                AssertEx.Success(TestOutputHelper, emitResult);
            }
        }
    }

    /// <summary>
    /// Use a <see cref="Util.LogReaderState"/> and dispose the reader before using the <see cref="CompilationData"/>.
    /// This helps prove that we don't maintain accidental references into the reader when doing Emit
    /// </summary>
    [Fact]
    public async Task EmitToMemoryCompilerLogWithSeparateState()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper, BasicAnalyzerKind.None))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var state = new Util.LogReaderState(baseDir: Root.NewDirectory());
            foreach (var data in ReadAll(complogPath, state))
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                AssertEx.Success(TestOutputHelper, emitResult);
            }
        }

        static List<CompilationData> ReadAll(string complogPath, Util.LogReaderState state)
        {
            using var reader = CompilerLogReader.Create(complogPath, basicAnalyzerKind: BasicAnalyzerKind.None, state: state);
            return reader.ReadAllCompilationData();
        }
    }

    [Fact]
    public async Task CommandLineArguments()
    {
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper))
        {
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath, basicAnalyzerKind: BasicAnalyzerKind.None);
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
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper, BasicAnalyzerKind.None))
        {
            using var reader = SolutionReader.Create(complogPath, BasicAnalyzerKind.None);
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
        using var fileLock = Fixture.LockScratchDirectory();
        var list = new List<Task>();
        await foreach (var logData in Fixture.GetAllLogData(TestOutputHelper))
        {
            if (!includeAnalyzers && !logData.SupportsNoneHost)
            {
                continue;
            }

            var task = Task.Run(() => ExportUtilTests.TestExport(TestOutputHelper, logData.CompilerLogPath, expectedCount: null, includeAnalyzers, runBuild: true));
            list.Add(task);
        }

        await Task.WhenAll(list);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAllCore(bool none)
    {
        var kind = none ? BasicAnalyzerKind.None : BasicAnalyzerHost.DefaultKind;
        await foreach (var complogPath in Fixture.GetAllCompilerLogs(TestOutputHelper, kind))
        {
            using var reader = SolutionReader.Create(complogPath, kind);
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
        await foreach (var logData in Fixture.GetAllLogData(TestOutputHelper))
        {
            if (logData.BinaryLogPath is null)
            {
                continue;
            }

            using var complogReader = CompilerLogReader.Create(logData.CompilerLogPath);
            using var binlogReader = BinaryLogReader.Create(logData.BinaryLogPath);
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
