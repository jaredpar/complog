using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilerLogReaderTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilerLogReaderTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilerLogReaderTests))
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
        var rawData = reader.ReadRawCompilationData(0).Item2;
        var extraData = rawData.Contents.Single(x => Path.GetFileName(x.FilePath) == fileName);
        Assert.Equal("84C9FAFCF8C92F347B96D26B149295128B08B07A3C4385789FE4758A2B520FDE", extraData.ContentHash);
        var contentBytes = reader.GetContentBytes(extraData.ContentHash);
        Assert.Equal(content, DefaultEncoding.GetString(contentBytes));
    }

    [Fact]
    public void CreateInvalidZipFile()
    {
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3, 4, 5], 0, 0);
        stream.Position = 0;
        Assert.Throws<CompilerLogException>(() => CompilerLogReader.Create(stream, BasicAnalyzerKind.None, leaveOpen: true));
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
    public void GlobalConfigPathMap()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);
        var provider = (BasicAnalyzerConfigOptionsProvider)data.AnalyzerConfigOptionsProvider;
        var additonalText = data.AdditionalTexts.Single(x => x.Path.Contains("additional.txt"));
        var options = provider.GetOptions(additonalText);
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
        var rawData = reader.ReadRawCompilationData(0).Item2;
        var d = rawData.Resources.Single();
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
    public void AdditionalFiles()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var data = reader.ReadCompilationData(0);
        Assert.Single(data.AdditionalTexts);
        Assert.Equal("additional.txt", Path.GetFileName(data.AdditionalTexts[0].Path));

        var additionalText = data.AdditionalTexts[0]!;
        var text = additionalText.GetText()!;
        Assert.Contains("This is an additional file", text.ToString());

        var options = data.AnalyzerConfigOptionsProvider.GetOptions(additionalText);
        Assert.NotNull(options);
    }

    [Fact]
    public void AnalyzerLoadOptions()
    {
        var any = false;
        foreach (BasicAnalyzerKind kind in Enum.GetValues(typeof(BasicAnalyzerKind)))
        {
            if (!BasicAnalyzerHost.IsSupported(kind))
            {
                continue;
            }
            any = true;

            using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, kind);
            var data = reader.ReadCompilationData(0);
            var compilation = data.GetCompilationAfterGenerators(out var diagnostics);
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
        }

        Assert.True(any);
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
        var (compilerCall, data) = reader.ReadRawCompilationData(0);

        var host1 = reader.ReadAnalyzers(data);
        var host2 = reader.ReadAnalyzers(data);
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
        var data = reader.ReadRawCompilationData(0).Item2;
        var analyzers = data.Analyzers
            .Where(x => x.FileName != "Microsoft.CodeAnalysis.NetAnalyzers.dll")
            .ToList();
        var host = new BasicAnalyzerHostInMemory(reader, analyzers);
        foreach (var analyzer in host.AnalyzerReferences)
        {
            analyzer.GetAnalyzersForAllLanguages();
        }
        Assert.NotEmpty(host.GetDiagnostics());
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
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net8.0"));
    }

    [Fact]
    public void NoneHostGeneratedFilesInRaw()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var (_, data) = reader.ReadRawCompilationData(0);
        Assert.Equal(1, data.Contents.Count(x => x.Kind == RawContentKind.GeneratedText));
    }

    [Fact]
    public void NoneHostGeneratedFilesShouldBeLast()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var tree = data.GetCompilationAfterGenerators().SyntaxTrees.Last();
        var decls = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
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
        var compilation2 = data.GetCompilationAfterGenerators();
        Assert.NotSame(compilation1, compilation2);
        Assert.Single(data.AnalyzerReferences);
    }

    [Fact]
    public void NoneHostAddsNoGeneratorIfNoGeneratedSource()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleNoGenerator.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var compilation1 = data.Compilation;
        var compilation2 = data.GetCompilationAfterGenerators();
        Assert.Same(compilation1, compilation2);
        Assert.Single(data.AnalyzerReferences);
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
        var rawData = reader.ReadRawCompilationData(0).Item2;
        Assert.False(rawData.HasAllGeneratedFileContent);
        var data = reader.ReadCompilationData(0);
        var compilation = data.GetCompilationAfterGenerators(out var diagnostics);
        Assert.Single(diagnostics);
        Assert.Equal(BasicAnalyzerHostNone.CannotReadGeneratedFiles.Id, diagnostics[0].Id);
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
    public void MetadataCompat(string resourceName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var stream = ResourceLoader.GetResourceStream(resourceName);
            Assert.Throws<CompilerLogException>(() => CompilerLogReader.Create(stream, BasicAnalyzerKind.None, leaveOpen: true));
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
}
