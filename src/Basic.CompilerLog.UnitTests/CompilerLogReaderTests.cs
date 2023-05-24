using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Linq;
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
        using var reader = CompilerLogReader.Create(Fixture.ClassLibSignedComplogPath);
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
        using var reader = CompilerLogReader.Create(Fixture.ClassLibSignedComplogPath, state: state);
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
            using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath, options: options);
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
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath, options: options);
        var key = reader.ReadRawCompilationData(0).Item2.Analyzers;

        var host1 = reader.ReadAnalyzers(key);
        var host2 = reader.ReadAnalyzers(key);
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
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath, options: options);
        var data = reader.ReadCompilationData(0);
        Assert.False(data.BasicAnalyzerHost.IsDisposed);
        reader.Dispose();
        Assert.True(data.BasicAnalyzerHost.IsDisposed);
    }

    [Fact]
    public void ProjectSingleTarget()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath);
        var list = reader.ReadAllCompilationData();
        Assert.Single(list);
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net7.0"));
    }

    [Fact]
    public void ProjectMultiTarget()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMultiComplogPath);
        var list = reader.ReadAllCompilationData();
        Assert.Equal(2, list.Count);
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net6.0"));
        Assert.NotNull(list.Single(x => x.CompilerCall.TargetFramework == "net7.0"));
    }
}
