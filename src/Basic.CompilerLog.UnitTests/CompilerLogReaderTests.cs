using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilerLogReaderTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilerLogReaderTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Can we process an extra file in the major templates. The file name should not impact
    /// the content of the file.
    /// </summary>
    /// <param name="template"></param>
    [Theory]
    [InlineData("file1.cs")]
    [InlineData("file2.cs")]
    public void ContentExtraSourceFile(string fileName)
    {
        RunDotNet($"new console --name example --output .");
        var content = """
            // Example content
            """;
        File.WriteAllText(Path.Combine(RootDirectory, fileName), content, DefaultEncoding);
        RunDotNet("build -bl -nr:false");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"));
        var extraData = reader.ReadAllRawContent(0).Single(x => Path.GetFileName(x.FilePath) == fileName);
        Assert.Equal("84C9FAFCF8C92F347B96D26B149295128B08B07A3C4385789FE4758A2B520FDE", extraData.ContentHash);
        var contentBytes = reader.GetContentBytes(extraData.Kind, extraData.ContentHash!);
        Assert.Equal(content, DefaultEncoding.GetString(contentBytes));
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory, true)]
    [InlineData(BasicAnalyzerKind.OnDisk, true)]
    [InlineData(BasicAnalyzerKind.InMemory, false)]
    public void CreateStream1(BasicAnalyzerKind basicAnalyzerKind, bool leaveOpen)
    {
        var stream = new FileStream(Fixture.Console.Value.CompilerLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = CompilerLogReader.Create(stream, basicAnalyzerKind, leaveOpen);
        Assert.Equal(basicAnalyzerKind, reader.BasicAnalyzerKind);
        reader.Dispose();
        // CanRead is the best approximation we have for checking if the stream is disposed
        Assert.Equal(leaveOpen, stream.CanRead);
        stream.Dispose();
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory, true)]
    [InlineData(BasicAnalyzerKind.OnDisk, true)]
    [InlineData(BasicAnalyzerKind.InMemory, false)]
    public void CreateStream2(BasicAnalyzerKind basicAnalyzerKind, bool leaveOpen)
    {
        var state = new LogReaderState();
        var stream = new FileStream(Fixture.Console.Value.CompilerLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = CompilerLogReader.Create(stream, basicAnalyzerKind, leaveOpen);
        Assert.Equal(basicAnalyzerKind, reader.BasicAnalyzerKind);
        reader.Dispose();
        // CanRead is the best approximation we have for checking if the stream is disposed
        Assert.Equal(leaveOpen, stream.CanRead);
        stream.Dispose();
        Assert.False(state.IsDisposed);
        state.Dispose();
    }

    [Fact]
    public void CreateFromZipEmpty()
    {
        var zipFilePath = Path.Combine(RootDirectory, "empty.zip");
        {
            using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Create);
        }
        Assert.Throws<Exception>(() => CompilerLogReader.Create(zipFilePath));
    }

    [Fact]
    public void CreateFromZipInvalid()
    {
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3, 4, 5], 0, 0);
        stream.Position = 0;
        Assert.Throws<CompilerLogException>(() => CompilerLogReader.Create(stream, BasicAnalyzerKind.None, leaveOpen: true));
    }

    [Fact]
    public void CreateFromZipOfLogfile()
    {
        var logData = Fixture.Console.Value;
        Core(logData.BinaryLogPath!);
        Core(logData.CompilerLogPath);

        void Core(string logFilePath)
        {
            var zipFilePath = Path.Combine(RootDirectory, "file.zip");
            ZipFile(zipFilePath, logFilePath);
            var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(zipFilePath);
            using var reader = CompilerLogReader.Create(stream, BasicAnalyzerKind.None, leaveOpen: false);
            Assert.NotEmpty(reader.ReadAllCompilerCalls());

            void ZipFile(string zipFilePath, string filePath)
            {
                using var zipArchive = new ZipArchive(File.Open(zipFilePath, FileMode.Create), ZipArchiveMode.Create);
                var entry = zipArchive.CreateEntry(Path.GetFileName(filePath));
                using var entryStream = entry.Open();
                using var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fileStream.CopyTo(entryStream);
            }
        }
    }

    [Fact]
    public void CreateFromInvalidExtension()
    {
        var filePath = Root.NewFile("file.txt", "this is some content");
        Assert.Throws<ArgumentException>(() => CompilerLogReader.Create(filePath));
    }

    [Fact]
    public void GetContentBytes()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        reader.PathNormalizationUtil = new IdentityPathNormalizationUtil();
        var compilerCall = reader.ReadCompilerCall(0);
        var any = false;
        foreach (var rawContent in reader.ReadAllRawContent(compilerCall, RawContentKind.AnalyzerConfig))
        {
            var bytes = reader.GetContentBytes(rawContent.Kind, rawContent.ContentHash!);
            Assert.NotEmpty(bytes);
            any = true;
        }
        Assert.True(any);
    }

    [Fact]
    public void GlobalConfigPathMap()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);
        var provider = (BasicAnalyzerConfigOptionsProvider)data.AnalyzerConfigOptionsProvider;
        var additionalText = data.AdditionalTexts.Single(x => x.Path.Contains("additional.txt"));
        var options = provider.GetOptions(additionalText);
        Assert.True(options.TryGetValue("build_metadata.AdditionalFiles.FixtureKey", out var value));
        Assert.Equal("true", value);
    }

    [Fact]
    public void MetadataVersion()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        Assert.Equal(Util.Metadata.LatestMetadataVersion, reader.MetadataVersion);
    }

    [Fact]
    public void ResourceSimpleEmbedded()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var d = reader.ReadAllResourceData(0).Single();
        Assert.Equal("console-complex.resource.txt", reader.ReadResourceDescription(d).GetResourceName());
    }

    [Fact]
    public void KeyFileDefault()
    {
        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        using var reader = CompilerLogReader.Create(Fixture.ConsoleSigned.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);

        Assert.NotNull(data.CompilationOptions.CryptoKeyFile);
        Assert.StartsWith(reader.LogReaderState.CryptoKeyFileDirectory, data.CompilationOptions.CryptoKeyFile);
        Assert.True(keyBytes.SequenceEqual(File.ReadAllBytes(data.CompilationOptions.CryptoKeyFile)));
        reader.Dispose();
        Assert.False(File.Exists(data.CompilationOptions.CryptoKeyFile));
    }

    [Fact]
    public void KeyFileCustomState()
    {
        using var tempDir = new TempDir("keyfiledir");
        using var state = new Util.LogReaderState(tempDir.DirectoryPath);

        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath, state: state);
        var data = reader.ReadCompilationData(0);

        Assert.NotNull(data.CompilationOptions.CryptoKeyFile);
        Assert.StartsWith(reader.LogReaderState.CryptoKeyFileDirectory, data.CompilationOptions.CryptoKeyFile);
        Assert.True(keyBytes.SequenceEqual(File.ReadAllBytes(data.CompilationOptions.CryptoKeyFile)));

        // Reader does not own the state now and it should not clean up resources
        reader.Dispose();
        Assert.True(File.Exists(data.CompilationOptions.CryptoKeyFile));

        // State does own and it should cleanup
        state.Dispose();
        Assert.False(File.Exists(data.CompilationOptions.CryptoKeyFile));
    }

    [Fact]
    public void KeyFileAssemblyName()
    {
        Go(Fixture.ConsoleComplex.Value.BinaryLogPath!);
        Go(Fixture.ConsoleComplex.Value.CompilerLogPath);

        void Go(string filePath)
        {
            using var reader = CompilerCallReaderUtil.Create(filePath, BasicAnalyzerKind.None);
            var compilerCall = reader.ReadCompilerCall(0);
            var data = reader.ReadCompilationData(compilerCall);
            var compilation = data.GetCompilationAfterGenerators(cancellationToken: CancellationToken);
            Assert.Equal("console-complex", compilation.AssemblyName);
        }
    }

    [Fact]
    public void AdditionalFiles()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);
        Assert.Single(data.AdditionalTexts);
        Assert.Equal("additional.txt", Path.GetFileName(data.AdditionalTexts[0].Path));

        var additionalText = data.AdditionalTexts[0]!;
        var text = additionalText.GetText(CancellationToken)!;
        Assert.Contains("This is an additional file", text.ToString());

        var options = data.AnalyzerConfigOptionsProvider.GetOptions(additionalText);
        Assert.NotNull(options);
    }

    [Theory]
    [MemberData(nameof(GetSupportedBasicAnalyzerKinds))]
    public void AnalyzerLoadOptions(BasicAnalyzerKind basicAnalyzerKind)
    {
        RunInContext((FilePath: Fixture.Console.Value.CompilerLogPath, Kind: basicAnalyzerKind), static (testOutputHelper, state, cancellationToken) =>
        {
            using var reader = CompilerLogReader.Create(state.FilePath, state.Kind);
            var data = reader.ReadCompilationData(0);
            var compilation = data.GetCompilationAfterGenerators(out var diagnostics, cancellationToken);
            Assert.Empty(diagnostics);
            var found = false;
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (tree.ToString().Contains("REGEX_DEFAULT_MATCH_TIMEOUT"))
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found);
            data.BasicAnalyzerHost.Dispose();
        });
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory)]
    [InlineData(BasicAnalyzerKind.OnDisk)]
    public void AnalyzerLoadCaching(BasicAnalyzerKind kind)
    {
        if (!BasicAnalyzerHost.IsSupported(kind))
        {
            return;
        }

        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, kind);
        var compilerCall = reader.ReadCompilerCall(0);
        var host1 = reader.CreateBasicAnalyzerHost(compilerCall);
        var host2 = reader.CreateBasicAnalyzerHost(compilerCall);
        Assert.Same(host1, host2);
        host1.Dispose();
        Assert.True(host1.IsDisposed);
        Assert.True(host2.IsDisposed);
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory)]
    [InlineData(BasicAnalyzerKind.OnDisk)]
    public void AnalyzerLoadDispose(BasicAnalyzerKind kind)
    {
        if (!BasicAnalyzerHost.IsSupported(kind))
        {
            return;
        }

        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, kind);
        var data = reader.ReadCompilationData(0);
        Assert.False(data.BasicAnalyzerHost.IsDisposed);
        reader.Dispose();
        Assert.True(data.BasicAnalyzerHost.IsDisposed);
    }

#if NET

    /// <summary>
    /// Ensure that diagnostics are raised when the analyzer can't properly load all of the types
    /// </summary>
    [Fact]
    public void AnalyzerDiagnostics()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.InMemory);
        var data = reader.ReadAllAnalyzerData(0);
        var analyzers = data
            .Where(x => x.FileName != "Microsoft.CodeAnalysis.NetAnalyzers.dll")
            .ToList();
        var host = new BasicAnalyzerHostInMemory(reader, analyzers);
        var list = new List<Diagnostic>();
        foreach (var analyzer in host.AnalyzerReferences)
        {
            analyzer.AsBasicAnalyzerReference().GetAnalyzers(LanguageNames.CSharp, list);
        }
        Assert.NotEmpty(list);
    }

#endif

    [Fact]
    public void ProjectSingleTarget()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLib.Value.CompilerLogPath);
        var list = reader.ReadAllCompilationData();
        Assert.Single(list);
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net8.0"));
    }

    [Fact]
    public void ProjectMultiTarget()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMulti.Value.CompilerLogPath);
        var list = reader.ReadAllCompilationData();
        Assert.Equal(2, list.Count);
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net6.0"));
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == TestUtil.TestTargetFramework));
    }

    [Fact]
    public void NoneHostGeneratedFilesInRaw()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        Assert.Single(reader.ReadAllRawContent(0, RawContentKind.GeneratedText));
    }

    [Fact]
    public void NoneHostGeneratedFilesShouldBeLast()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var tree = data.GetCompilationAfterGenerators(CancellationToken).SyntaxTrees.Last();
        var decls = tree.GetRoot(CancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        Assert.True(decls.Count >= 2);
        Assert.Equal("Util", decls[0].Identifier.Text);
        Assert.Equal("GetRegex_0", decls[1].Identifier.Text);
    }

    [Fact]
    public void NoneHostHasSingelGenerator()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var compilation1 = data.Compilation;
        var compilation2 = data.GetCompilationAfterGenerators(CancellationToken);
        Assert.NotSame(compilation1, compilation2);
        Assert.Single(data.AnalyzerReferences);
    }

    [Fact]
    public void NoneHostAddsNoGeneratorIfNoGeneratedSource()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleNoGenerator.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var compilation1 = data.Compilation;
        var compilation2 = data.GetCompilationAfterGenerators(CancellationToken);
        Assert.Same(compilation1, compilation2);
        Assert.Single(data.AnalyzerReferences);
    }

    [WindowsFact]
    public void HasAllGeneratedFileContent()
    {
        Run(Fixture.Console.Value.CompilerLogPath, true);
        Run(Fixture.ConsoleWithNativePdb!.Value.CompilerLogPath, false);

        void Run(string complogFilePath, bool expected)
        {
            using var reader = CompilerLogReader.Create(complogFilePath, BasicAnalyzerKind.None);
            var compilerCall = reader.ReadCompilerCall(0);
            Assert.Equal(expected, reader.HasAllGeneratedFileContent(compilerCall));
        }
    }

    [Fact]
    public void NoneHostNativePdb()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        RunDotNet($"new console --name example --output .");
        var projectFileContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <DebugType>Full</DebugType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"), projectFileContent, DefaultEncoding);
        RunDotNet("build -bl -nr:false");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"), BasicAnalyzerKind.None);
        var compilerCall = reader.ReadCompilerCall(0);
        Assert.False(reader.HasAllGeneratedFileContent(compilerCall));
        var data = reader.ReadCompilationData(0);
        var compilation = data.GetCompilationAfterGenerators(out var diagnostics, CancellationToken);
        Assert.Single(diagnostics);
        Assert.Equal(RoslynUtil.ErrorReadingGeneratedFilesDiagnosticDescriptor.Id, diagnostics[0].Id);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void ReadCompilerCallBadIndex(int index)
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        Assert.Throws<ArgumentException>(() => reader.ReadCompilerCall(index));
    }

    [Fact]
    public void ReadCompilerCallWrongOwner()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        var compilerCall = reader.ReadCompilerCall(0);
        compilerCall = compilerCall.WithOwner(null);
        Assert.Throws<ArgumentException>(() => reader.ReadCompilationData(compilerCall));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void ReadCompilationDataBadIndex(int index)
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        Assert.Throws<ArgumentException>(() => reader.ReadCompilationData(index));
    }

    [Fact]
    public async Task ReadCompilationDataMissingAdditionalFiles()
    {
        var dir = Root.NewDirectory("missing-additional-files");
        RunDotNet("new classlib --name example -o .", dir);
        SetProjectFileContent("""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <NoWarn>$(NoWarn);CS8933</NoWarn>
                </PropertyGroup>
                <ItemGroup>
                    <AdditionalFiles Include="additional.txt" />
                </ItemGroup>
            </Project>
            """, dir);
        RunDotNet("build -bl:build.binlog -nr:false", dir);

        var binlogFilePath = Path.Combine(dir, "build.binlog");
        using var binlogReader = BinaryLogReader.Create(binlogFilePath, BasicAnalyzerKind.None);
        await Core(binlogReader, CancellationToken);
        binlogReader.Dispose();

        var complogFilePath = Path.Combine(dir, "build.complog");
        CompilerLogUtil.ConvertBinaryLog(binlogFilePath, complogFilePath);
        using var complogReader = CompilerLogReader.Create(complogFilePath, BasicAnalyzerKind.None);
        await Core(complogReader, CancellationToken);

        static async Task Core(ICompilerCallReader reader, CancellationToken cancellationToken)
        {
            var compilerCall = reader.ReadAllCompilerCalls().Single();
            var compilationData = reader.ReadCompilationData(compilerCall);
            var diagnostics = compilationData.GetDiagnostics();

            // This may seem counter intuitive but the compiler does not issue an error on a missing
            // additional file. The error only happens if something tries to read the file
            Assert.Empty(diagnostics);

            diagnostics = await compilationData.GetAllDiagnosticsAsync(cancellationToken);
            Assert.Empty(diagnostics);

            var additionalText = (BasicAdditionalText)compilationData.AdditionalTexts.Single();
            Assert.Null(additionalText.GetText());
            Assert.Single(additionalText.Diagnostics);
            Assert.Equal(RoslynUtil.CannotReadFileDiagnosticDescriptor, additionalText.Diagnostics[0].Descriptor);

            // Now that the text is observed to be empty the diagnostic should show up
            diagnostics = await compilationData.GetAllDiagnosticsAsync(cancellationToken);
            Assert.Single(diagnostics);
            Assert.Equal(RoslynUtil.CannotReadFileDiagnosticDescriptor, additionalText.Diagnostics[0].Descriptor);
        }
    }

    [Fact]
    public void Satellite()
    {
        Go(Fixture.ClassLibWithResourceLibs.Value.BinaryLogPath!);
        Go(Fixture.ClassLibWithResourceLibs.Value.CompilerLogPath);

        void Go(string logFilePath)
        {
            using var reader = CompilerCallReaderUtil.Create(logFilePath, BasicAnalyzerKind.None);
            var compilerCalls = reader.ReadAllCompilerCalls();
            Assert.Equal(3, compilerCalls.Count);
            Assert.Equal(2, compilerCalls.Count(x => x.Kind == CompilerCallKind.Satellite));
        }
    }

    [Fact]
    public void SatellitePdb()
    {
        Go(Fixture.ClassLibWithResourceLibs.Value.BinaryLogPath!);
        Go(Fixture.ClassLibWithResourceLibs.Value.CompilerLogPath);

        void Go(string logFilePath)
        {
            using var reader = CompilerCallReaderUtil.Create(logFilePath, BasicAnalyzerKind.None);
            var compilerCall = reader
                .ReadAllCompilerCalls()
                .First(x => x.Kind == CompilerCallKind.Satellite);
            var data = reader.ReadCompilationData(compilerCall);
            Assert.False(data.EmitData.EmitPdb);
        }
    }

    [WindowsFact]
    public void KindWpf()
    {
        Assert.NotNull(Fixture.WpfApp);
        using var reader = CompilerLogReader.Create(Fixture.WpfApp.Value.CompilerLogPath);
        var list = reader.ReadAllCompilationData();
        Assert.Equal(2, list.Count);
        Assert.Equal(CompilerCallKind.WpfTemporaryCompile, list[0].Kind);
        Assert.Contains(nameof(CompilerCallKind.WpfTemporaryCompile), list[0].CompilerCall.GetDiagnosticName());
        Assert.Equal(CompilerCallKind.Regular, list[1].Kind);
    }

    /// <summary>
    /// Ensure diagnostics are issued for the cases where a #line refers to
    /// a target that can't be exported to another computer correctly.
    /// </summary>
    [Fact]
    public void EmbedLineIssues()
    {
        // Outside project full path
        {
            using var temp = new TempDir();
            Core(temp.NewFile("content.txt", "this is some content"));
        }

        // Inside project full path
        {
            Core(Root.NewFile("content.txt", "this is some content"));
        }

        void Core(string contentFilePath)
        {
            RunDotNet($"new console --name example --output .");
            AddProjectProperty("<EmbedAllSources>true</EmbedAllSources>");
            File.WriteAllText(Path.Combine(RootDirectory, "Util.cs"),
                $"""
            #line 42 "{contentFilePath}"
            """);
            RunDotNet("build -bl -nr:false");
            var diagnostics = CompilerLogUtil.ConvertBinaryLog(
                Path.Combine(RootDirectory, "msbuild.binlog"),
                Path.Combine(RootDirectory, "msbuild.complog"));
            Assert.Single(diagnostics);
            Root.EmptyDirectory();
        }
    }

    [Theory]
    [InlineData("MetadataVersion1.console.complog")]
    public void MetadataCompatV1(string resourceName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var stream = ResourceLoader.GetResourceStream(resourceName);
            Assert.Throws<CompilerLogException>(() => CompilerLogReader.Create(stream, BasicAnalyzerKind.None, leaveOpen: true));
        }
    }

    [Theory]
    [InlineData("MetadataVersion2.console.complog")]
    public void MetadataCompatV2(string resourceName)
    {
        using var stream = ResourceLoader.GetResourceStream(resourceName);
        using var reader = CompilerLogReader.Create(stream);
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            Assert.NotNull(compilerCall.ProjectFileName);
        }
    }

    [Fact]
    public void Disposed()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadCompilationData(0));
    }

    [Fact]
    public void VisualBasic()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleVisualBasic.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);
        Assert.True(data.IsVisualBasic);
        Assert.True(data.CompilerCall.IsVisualBasic);
    }

    [Fact]
    public void ProjectReferences_Simple()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        var compilerCall = reader.ReadCompilerCall(0);
        var arguments = BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall, reader.ReadArguments(compilerCall));
        var (assemblyFilePath, refAssemblyFilePath) = RoslynUtil.GetAssemblyOutputFilePaths(arguments);
        AssertProjectRef(assemblyFilePath);
        AssertProjectRef(refAssemblyFilePath);

        void AssertProjectRef(string? filePath)
        {
            Assert.NotNull(filePath);
            var mvid = RoslynUtil.ReadMvid(filePath);
            Assert.True(reader.TryGetCompilerCallIndex(mvid, out var callIndex));
            Assert.Equal(reader.GetIndex(compilerCall), callIndex);
        }
    }

    [Fact]
    public void ProjectReferences_ReadReference()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithReference.Value.CompilerLogPath);
        var classLibCompilerCall = reader
            .ReadAllCompilerCalls(cc => cc.ProjectFileName == "util.csproj")
            .Single();
        var consoleCompilerCall = reader
            .ReadAllCompilerCalls(cc => cc.ProjectFileName == "console-with-reference.csproj")
            .Single();
        var count = 0;
        foreach (var rawReferenceData in reader.ReadAllReferenceData(consoleCompilerCall))
        {
            if (reader.TryGetCompilerCallIndex(rawReferenceData.Mvid, out var callIndex))
            {
                Assert.Equal(reader.GetIndex(classLibCompilerCall), callIndex);
                count++;
            }
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public void ProjectReferences_Alias()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithAliasReference.Value.CompilerLogPath);
        var consoleCompilerCall = reader
            .ReadAllCompilerCalls(cc => cc.ProjectFileName == "console-with-alias-reference.csproj")
            .Single();
        var referenceData = reader
            .ReadAllReferenceData(consoleCompilerCall)
            .Single(x => x.Aliases.Length == 1);
        Assert.Equal("Util", referenceData.Aliases.Single());
    }

    [Fact]
    public void ProjectReferences_Corrupted()
    {
        RunDotNet($"new console --name example --output .", Root.DirectoryPath);
        RunDotNet("build -bl -nr:false", Root.DirectoryPath);
        var binlogFilePath = Path.Combine(Root.DirectoryPath, "msbuild.binlog");

        var mvidList = CorruptAssemblies();
        Assert.NotEmpty(mvidList);
        using var reader = CompilerLogReader.Create(binlogFilePath);
        foreach (var mvid in mvidList)
        {
            Assert.False(reader.TryGetCompilerCallIndex(mvid, out _));
        }

        List<Guid> CorruptAssemblies()
        {
            var list = new List<Guid>();
            using var binlogReader = BinaryLogReader.Create(binlogFilePath);
            foreach (var compilerCall in binlogReader.ReadAllCompilerCalls())
            {
                var arguments = binlogReader.ReadArguments(compilerCall);
                var commandLineArguments = BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall, arguments);
                var (assemblyPath, refAssemblyPath) = RoslynUtil.GetAssemblyOutputFilePaths(commandLineArguments);
                Assert.NotNull(assemblyPath);
                Assert.NotNull(refAssemblyPath);
                list.Add(RoslynUtil.ReadMvid(assemblyPath));
                list.Add(RoslynUtil.ReadMvid(refAssemblyPath));

                File.WriteAllText(assemblyPath, "hello");
                File.WriteAllText(refAssemblyPath, "hello ref");

            }

            return list;
        }
    }

    /// <summary>
    /// Make sure the result of the ruleset is correctly encoded into the <see cref="CompilationOptions"/>. This
    /// is calculated on the machine where the compilation occurs but must be replicated through the log
    /// </summary>
    [Fact]
    public void RulesetPresentInOptions()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);
        Assert.NotNull(data.CompilationOptions);
        Assert.Equal(ReportDiagnostic.Warn, data.CompilationOptions.SpecificDiagnosticOptions["CA1001"]);
        Assert.Equal(ReportDiagnostic.Error, data.CompilationOptions.SpecificDiagnosticOptions["CA1802"]);
    }
}
