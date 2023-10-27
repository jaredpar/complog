using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
        : base(testOutputHelper, nameof(CompilerLogReader))
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Ensure that we can process the contents of all the major templates
    /// </summary>
    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    public void ReadDifferentTemplates(string template)
    {
        RunDotNet($"new {template} --name example --output .");
        RunDotNet("build -bl");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"));
        var compilerCall = reader.ReadCompilerCall(0);
        Assert.True(compilerCall.IsCSharp);

        var compilationData = reader.ReadCompilationData(compilerCall);
        Assert.NotNull(compilationData);
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
        RunDotNet("build -bl");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"));
        var rawData = reader.ReadRawCompilationData(0).Item2;
        var extraData = rawData.Contents.Single(x => Path.GetFileName(x.FilePath) == fileName);
        Assert.Equal("84C9FAFCF8C92F347B96D26B149295128B08B07A3C4385789FE4758A2B520FDE", extraData.ContentHash);
        var contentBytes = reader.GetContentBytes(extraData.ContentHash);
        Assert.Equal(content, DefaultEncoding.GetString(contentBytes));
    }

    [Fact]
    public void ResourceSimpleEmbedded()
    {
        RunDotNet($"new console --name example --output .");
        var projectFileContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <EmbeddedResource Include="resource.txt" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"), projectFileContent, DefaultEncoding);
        var resourceContent = """
            // This is an amazing resource
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "resource.txt"), resourceContent, DefaultEncoding);
        RunDotNet("build -bl");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"));
        var rawData = reader.ReadRawCompilationData(0).Item2;
        var d = rawData.Resources.Single();
        Assert.Equal("example.resource.txt", d.ResourceDescription.GetResourceName());
    }

    [Fact]
    public void KeyFileDefault()
    {
        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        using var reader = CompilerLogReader.Create(Fixture.ClassLibSignedComplogPath.Value);
        var data = reader.ReadCompilationData(0);

        Assert.NotNull(data.CompilationOptions.CryptoKeyFile);
        Assert.StartsWith(reader.CompilerLogState.CryptoKeyFileDirectory, data.CompilationOptions.CryptoKeyFile);
        Assert.True(keyBytes.SequenceEqual(File.ReadAllBytes(data.CompilationOptions.CryptoKeyFile)));
        reader.Dispose();
        Assert.False(File.Exists(data.CompilationOptions.CryptoKeyFile));
    }

    [Fact]
    public void KeyFileCustomState()
    {
        using var tempDir = new TempDir("keyfiledir");
        using var state = new CompilerLogState(tempDir.DirectoryPath);

        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        using var reader = CompilerLogReader.Create(Fixture.ClassLibSignedComplogPath.Value, state: state);
        var data = reader.ReadCompilationData(0);

        Assert.NotNull(data.CompilationOptions.CryptoKeyFile);
        Assert.StartsWith(reader.CompilerLogState.CryptoKeyFileDirectory, data.CompilationOptions.CryptoKeyFile);
        Assert.True(keyBytes.SequenceEqual(File.ReadAllBytes(data.CompilationOptions.CryptoKeyFile)));

        // Reader does not own the state now and it should not clean up resources
        reader.Dispose();
        Assert.True(File.Exists(data.CompilationOptions.CryptoKeyFile));

        // State does own and it should cleanup
        state.Dispose();
        Assert.False(File.Exists(data.CompilationOptions.CryptoKeyFile));
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

            var options = new BasicAnalyzerHostOptions(kind);
            using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, options: options);
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

        var options = new BasicAnalyzerHostOptions(kind, cacheable: true);
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, options: options);
        var data = reader.ReadRawCompilationData(0).Item2;

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

        var options = new BasicAnalyzerHostOptions(kind, cacheable: true);
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, options: options);
        var data = reader.ReadCompilationData(0);
        Assert.False(data.BasicAnalyzerHost.IsDisposed);
        reader.Dispose();
        Assert.True(data.BasicAnalyzerHost.IsDisposed);
    }

    [Fact]
    public void ProjectSingleTarget()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath.Value);
        var list = reader.ReadAllCompilationData();
        Assert.Single(list);
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net7.0"));
    }

    [Fact]
    public void ProjectMultiTarget()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMultiComplogPath.Value);
        var list = reader.ReadAllCompilationData();
        Assert.Equal(2, list.Count);
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net6.0"));
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net7.0"));
    }

    [Fact]
    public void EmitToDisk()
    {
        var all = Fixture.GetAllCompLogs();
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
        var all = Fixture.GetAllCompLogs();
        Assert.NotEmpty(all);
        foreach (var complogPath in all)
        {
            TestOutputHelper.WriteLine(complogPath);
            using var reader = CompilerLogReader.Create(complogPath);
            foreach (var data in reader.ReadAllCompilationData())
            {
                TestOutputHelper.WriteLine($"\t{data.CompilerCall.ProjectFileName} ({data.CompilerCall.TargetFramework})");
                var emitResult = data.EmitToMemory();
                Assert.True(emitResult.Success);
            }
        }
    }

    [Fact]
    public void NoneHostGeneratedFilesInRaw()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, BasicAnalyzerHostOptions.None);
        var (_, data) = reader.ReadRawCompilationData(0);
        Assert.Equal(1, data.Contents.Count(x => x.Kind == RawContentKind.GeneratedText));
    }

    [Fact]
    public void NoneHostGeneratedFilesShouldBeLast()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, BasicAnalyzerHostOptions.None);
        var data = reader.ReadCompilationData(0);
        var tree = data.GetCompilationAfterGenerators().SyntaxTrees.Last();
        var decls = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        Assert.True(decls.Count >= 2);
        Assert.Equal("Util", decls[0].Identifier.Text);
        Assert.Equal("GetRegex_0", decls[1].Identifier.Text);
    }

    [Fact]
    public void NoneHostAddsFakeGeneratorForGeneratedSource()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, BasicAnalyzerHostOptions.None);
        var data = reader.ReadCompilationData(0);
        var compilation1 = data.Compilation;
        var compilation2 = data.GetCompilationAfterGenerators();
        Assert.NotSame(compilation1, compilation2);
        Assert.Single(data.AnalyzerReferences);
    }

    [Fact]
    public void NoneHostAddsNoGeneratorIfNoGeneratedSource()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleNoGeneratorComplogPath.Value, BasicAnalyzerHostOptions.None);
        var data = reader.ReadCompilationData(0);
        var compilation1 = data.Compilation;
        var compilation2 = data.GetCompilationAfterGenerators();
        Assert.Same(compilation1, compilation2);
        Assert.Empty(data.AnalyzerReferences);
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
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"), projectFileContent, DefaultEncoding);
        RunDotNet("build -bl");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"), BasicAnalyzerHostOptions.None);
        var rawData = reader.ReadRawCompilationData(0).Item2;
        Assert.False(rawData.ReadGeneratedFiles);
        var data = reader.ReadCompilationData(0);
        var compilation = data.GetCompilationAfterGenerators(out var diagnostics);
        Assert.Single(diagnostics);
        Assert.Equal(BasicAnalyzerHostNone.CannotReadGeneratedFiles.Id, diagnostics[0].Id);
    }

    [Fact]
    public void KindWpf()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.NotNull(Fixture.WpfAppComplogPath);
            using var reader = CompilerLogReader.Create(Fixture.WpfAppComplogPath.Value);
            var list = reader.ReadAllCompilationData();
            Assert.Equal(2, list.Count);
            Assert.Equal(CompilerCallKind.WpfTemporaryCompile, list[0].Kind);
            Assert.Equal(CompilerCallKind.Regular, list[1].Kind);
        }
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
            RunDotNet("build -bl");
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
        using var stream = ResourceLoader.GetResourceStream(resourceName);
        using var reader = CompilerLogReader.Create(stream, leaveOpen: true, BasicAnalyzerHostOptions.None);
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            var data = reader.ReadCompilationData(compilerCall);
            var result = data.EmitToMemory();
            Assert.True(result.Success);
        }
    }
}
