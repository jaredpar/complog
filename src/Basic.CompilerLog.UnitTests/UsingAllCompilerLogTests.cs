
using System.Configuration;
using System.Runtime.InteropServices;
using System.Windows.Markup;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

#if NET
using Basic.CompilerLog.App;
#endif

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class UsingAllCompilerLogTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public UsingAllCompilerLogTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(UsingAllCompilerLogTests))
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

    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task EmitToDisk(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        var complogPath = logData.CompilerLogPath;
        using var reader = CompilerLogReader.Create(complogPath, basicAnalyzerKind: BasicAnalyzerKind.None);
        foreach (var data in reader.ReadAllCompilationData())
        {
            if (!reader.HasAllGeneratedFileContent(data.CompilerCall))
            {
                continue;
            }

            using var testDir = new TempDir();
            TestOutputHelper.WriteLine($"{Path.GetFileName(complogPath)}: {data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
            var emitResult = data.EmitToDisk(testDir.DirectoryPath, cancellationToken: CancellationToken);
            AssertEx.Success(TestOutputHelper, emitResult);
            Assert.NotEmpty(emitResult.Directory);
            Assert.NotEmpty(emitResult.AssemblyFileName);
            Assert.NotEmpty(emitResult.AssemblyFilePath);

            var emitFlags = data.EmitFlags;
            if ((emitFlags & EmitFlags.IncludePdbStream) != 0 && data.EmitOptions.DebugInformationFormat != DebugInformationFormat.Embedded)
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
    }

    /// <summary>
    /// Make sure paths for generated files don't have illegal characters when using the none host
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task GeneratedFilePathsNoneHost(string logDataName)
    {
        char[] illegalChars = ['<', '>'];
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        using var reader = CompilerCallReaderUtil.Create(logData.CompilerLogPath, BasicAnalyzerKind.None);
        foreach (var data in reader.ReadAllCompilationData(reader.HasAllGeneratedFileContent))
        {
            TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
            var generatedTrees = data.GetGeneratedSyntaxTrees(CancellationToken);
            foreach (var tree in generatedTrees)
            {
                foreach (var c in illegalChars)
                {
                    Assert.DoesNotContain(c, tree.FilePath);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetSimpleBasicAnalyzerKindsAndLogDataNames))]
    public void EmitToMemory(BasicAnalyzerKind basicAnalyzerKind, string logDataName)
    {
        TestOutputHelper.WriteLine($"BasicAnalyzerKind: {basicAnalyzerKind}, LogDataName: {logDataName}");
        var logData = Fixture.GetLogDataByName(logDataName).Value;
        using var reader = CompilerCallReaderUtil.Create(logData.CompilerLogPath, basicAnalyzerKind);
        foreach (var data in reader.ReadAllCompilationData(cc => basicAnalyzerKind != BasicAnalyzerKind.None || reader.HasAllGeneratedFileContent(cc)))
        {
            TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
            var emitResult = data.EmitToMemory(cancellationToken: CancellationToken);
            AssertEx.Success(TestOutputHelper, emitResult);
        }
    }

    /// <summary>
    /// Use a <see cref="Util.LogReaderState"/> and dispose the reader before using the <see cref="CompilationData"/>.
    /// This helps prove that we don't maintain accidental references into the reader when doing Emit
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task EmitToMemoryCompilerLogWithSeparateState(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        if (!logData.SupportsNoneHost)
        {
            return;
        }

        using var state = new Util.LogReaderState(baseDir: Root.NewDirectory());
        foreach (var data in ReadAll(logData.CompilerLogPath, state))
        {
            // Cannot replay metadata only with a None host. There is no PDB from which generated source files
            // can be read.
            if (data.EmitOptions.EmitMetadataOnly)
            {
                continue;
            }

            TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
            var emitResult = data.EmitToMemory(cancellationToken: CancellationToken);
            TestOutputHelper.WriteLine(string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString())));
            AssertEx.Success(TestOutputHelper, emitResult);
        }

        static List<CompilationData> ReadAll(string complogPath, Util.LogReaderState state)
        {
            using var reader = CompilerLogReader.Create(complogPath, basicAnalyzerKind: BasicAnalyzerKind.None, state: state);
            return reader.ReadAllCompilationData();
        }
    }

    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task CommandLineArguments(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        using var reader = CompilerLogReader.Create(logData.CompilerLogPath, basicAnalyzerKind: BasicAnalyzerKind.None);
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            var fileName = Path.GetFileName(logData.CompilerLogPath);
            if (fileName is "windows-console.complog" ||
                fileName is "linux-console.complog")
            {
                Assert.Null(compilerCall.CompilerFilePath);
            }
            else
            {
                Assert.NotNull(compilerCall.CompilerFilePath);
            }

            Assert.NotEmpty(reader.ReadArguments(compilerCall));
        }
    }

    /// <summary>
    /// Make sure that there is a consistent view of content bytes with or without path
    /// normalization. This helps catch any place where there is an inconsistency in the
    /// low level code or an incomplete optimization
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task ContentBytesAndStream(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        using var reader = CompilerLogReader.Create(logData.CompilerLogPath, basicAnalyzerKind: BasicAnalyzerKind.None);
        VerifyCore();
        reader.PathNormalizationUtil = new IdentityPathNormalizationUtil();
        VerifyCore();

        void VerifyCore()
        {
            foreach (var compilerCall in reader.ReadAllCompilerCalls())
            {
                foreach (var rawContent in reader.ReadAllRawContent(compilerCall))
                {
                    if (rawContent.ContentHash is null)
                    {
                        continue;
                    }

                    var bytes = reader.GetContentBytes(rawContent.Kind, rawContent.ContentHash);
                    var stream = reader.GetContentStream(rawContent.Kind, rawContent.ContentHash);

                    var streamBytes = stream.ReadAllBytes();
                    Assert.True(bytes.SequenceEqual(streamBytes));
                }
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
                    var text = await document.GetTextAsync(CancellationToken);
                    var textSpan = new TextSpan(0, text.Length);
                    _ = await Classifier.GetClassifiedSpansAsync(document, textSpan, CancellationToken);
                }
            }
        }
    }

    public static IEnumerable<object[]> GetExportAndBuildData()
    {
        foreach (var logData in CompilerLogFixture.GetAllLogDataNames())
        {
            yield return [true, logData];
            yield return [false, logData];
        }
    }

    [Theory]
    [MemberData(nameof(GetExportAndBuildData))]
    public async Task ExportRspAndBuild(bool excludeAnalyzers, string logDataName)
    {
        var list = new List<Task>();
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        if (excludeAnalyzers && !logData.SupportsNoneHost)
        {
            return;
        }

        ExportUtilTests.TestExportRsp(TestOutputHelper, logData.CompilerLogPath, expectedCount: null, excludeAnalyzers, runBuild: true);
    }

    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task ExportSolutionAndBuild(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        if (!logData.SupportsNoneHost)
        {
            return;
        }

        // This is a log file created with an old format that doesn't have the necessary information to
        // do an export to a solution
        if (logData.BinaryLogPath is null)
        {
            return;
        }

        using var reader = CompilerLogReader.Create(logData.CompilerLogPath);
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: true);

        using var tempDir = new TempDir();
        exportUtil.ExportSolution(tempDir.DirectoryPath);

        // Verify solution file exists
        var solutionFile = Path.Combine(tempDir.DirectoryPath, "export.slnx");
        Assert.True(File.Exists(solutionFile), $"Solution file should exist for {logDataName}");

        // Verify at least one project directory exists
        var projectDirs = Directory.GetDirectories(tempDir.DirectoryPath)
            .Where(d => !Path.GetFileName(d).Equals("references", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(projectDirs);

        // Build the solution
        TestOutputHelper.WriteLine($"Building solution for {logDataName}");
        var result = ProcessUtil.Run("dotnet", $"build \"{solutionFile}\"");
        TestOutputHelper.WriteLine(result.StandardOut);
        TestOutputHelper.WriteLine(result.StandardError);
        Assert.True(result.Succeeded, $"Build failed for {logDataName}: {result.StandardOut}");
    }

    public static IEnumerable<object[]> GetLoadAllCoreData()
    {
        foreach (var logData in CompilerLogFixture.GetAllLogDataNames())
        {
            yield return [true, logData];
            yield return [false, logData];
        }
    }

    [Theory]
    [MemberData(nameof(GetLoadAllCoreData))]
    public async Task LoadAllCore(bool none, string logDataName)
    {
        var kind = none ? BasicAnalyzerKind.None : BasicAnalyzerHost.DefaultKind;
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        var complogPath = logData.CompilerLogPath;
        using var reader = SolutionReader.Create(complogPath, kind);
        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(reader.ReadSolutionInfo());
        Assert.NotEmpty(solution.Projects);
    }

#if NET

    /// <summary>
    /// Ensure that the in memory loader finds the same set of analyzers that the
    /// on disk loader. The in memory loader has a lot of custom logic and need to
    /// verify it stays in sync with the disk versions
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task VerifyAnalyzerConsistency(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        using var diskReader = CompilerLogReader.Create(logData.CompilerLogPath, BasicAnalyzerKind.OnDisk);
        var diskDataList = diskReader.ReadAllCompilationData();
        using var memoryReader = CompilerLogReader.Create(logData.CompilerLogPath, BasicAnalyzerKind.InMemory);
        var memoryDataList = memoryReader.ReadAllCompilationData();
        Assert.Equal(diskDataList.Count, memoryDataList.Count);
        for (int i = 0; i < diskDataList.Count; i++)
        {
            var diskData = diskDataList[i];
            var memoryData = memoryDataList[i];

            var diskAnalyzers = diskData.AnalyzerReferences;
            var memoryAnalyzers = memoryData.AnalyzerReferences;
            Assert.Equal(diskAnalyzers.Length, memoryAnalyzers.Length);
            for (int j = 0; j < diskAnalyzers.Length; j++)
            {
                AssertCore(diskAnalyzers[j], memoryAnalyzers[j]);
            }
        }

        void AssertCore(AnalyzerReference expected, AnalyzerReference actual)
        {
            var expectedAnalyzers = expected.GetAnalyzersForAllLanguages();
            var actualAnalyzers = actual.GetAnalyzersForAllLanguages();

            AssertEx.SequenceEqual(
                expectedAnalyzers.Select(x => x.GetType().FullName).OrderBy(x => x),
                actualAnalyzers.Select(x => x.GetType().FullName).OrderBy(x => x));

            // Cannot test GetGeneratorsForAllLanguages here as it has a bug. The
            // de-dupe method not taking into account IncrementalGeneratorWrapper
            //
            // https://github.com/dotnet/roslyn/blob/99d8eeb69f19385838bf4e15dbe9bfcb50edc0eb/src/Compilers/Core/Portable/DiagnosticAnalyzer/AnalyzerFileReference.cs#L420
            var expectedGenerators = expected.GetGenerators(LanguageNames.CSharp);
            var actualGenerators = actual.GetGenerators(LanguageNames.CSharp);
            AssertEx.SequenceEqual(
                expectedGenerators.Select(x => TestUtil.GetGeneratorType(x).FullName).OrderBy(x => x),
                actualGenerators.Select(x => TestUtil.GetGeneratorType(x).FullName).OrderBy(x => x));
        }
    }

#endif

    /// <summary>
    /// Ensure that our options round tripping code is correct and produces the same result as
    /// argument parsing. This will also catch cases where new values are added to the options
    /// that are not being set by our code base.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllLogDataNames))]
    public async Task VerifyConsistentOptions(string logDataName)
    {
        var logData = await Fixture.GetLogDataByNameAsync(logDataName, TestOutputHelper);
        if (logData.BinaryLogPath is null)
        {
            return;
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
