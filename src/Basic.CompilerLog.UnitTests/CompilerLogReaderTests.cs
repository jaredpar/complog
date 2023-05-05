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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeyFile(bool redirect)
    {
        RunDotNet($"new console --name example --output .");
        var projectFileContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <PublicSign>true</PublicSign>
                <KeyOriginatorFile>key.snk</KeyOriginatorFile>
              </PropertyGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"), projectFileContent, DefaultEncoding);

        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        File.WriteAllBytes(Path.Combine(RootDirectory, "key.snk"), keyBytes);
        RunDotNet("build -bl");

        using var cryptoDir = new TempDir("cryptodir");
        using var reader = GetReader(cryptoKeyFileDirectory: redirect ? cryptoDir.DirectoryPath : null);
        var data = reader.ReadCompilationData(0);

        if (redirect)
        {
            Assert.Equal(cryptoDir.DirectoryPath, Path.GetDirectoryName(data.CompilationOptions.CryptoKeyFile));
            Assert.True(keyBytes.SequenceEqual(File.ReadAllBytes(data.CompilationOptions.CryptoKeyFile!)));
        }
        else
        {
            Assert.Equal(RootDirectory, Path.GetDirectoryName(data.CompilationOptions.CryptoKeyFile));
            Assert.False(File.Exists(data.CompilationOptions.CryptoKeyFile));
            Assert.Empty(Directory.EnumerateFiles(cryptoDir.DirectoryPath));
        }
    }

    [Fact]
    public void AnalyzerLoadOptions()
    {
        foreach (var kind in Enum.GetValues<BasicAnalyzersKind>())
        {
            var options = new BasicAnalyzersOptions(kind);
            using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath, options: options);
            var data = reader.ReadCompilationData(0);
            var compilation = data.GetCompilationAfterGenerators();
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
            data.BasicAnalyzers.Dispose();
        }
    }

    [Theory]
    [InlineData(BasicAnalyzersKind.InMemory)]
    [InlineData(BasicAnalyzersKind.OnDisk)]
    public void AnalyzerLoadCaching(BasicAnalyzersKind kind)
    {
        var options = new BasicAnalyzersOptions(kind, cacheable: true);
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath, options: options);
        var key = reader.ReadRawCompilationData(0).Item2.Analyzers;

        var analyzers1 = reader.ReadAnalyzers(key);
        var analyzers2 = reader.ReadAnalyzers(key);
        Assert.Same(analyzers1, analyzers2);
        analyzers1.Dispose();
        Assert.False(analyzers1.IsDisposed);
        Assert.False(analyzers2.IsDisposed);
        analyzers2.Dispose();
        Assert.True(analyzers1.IsDisposed);
        Assert.True(analyzers2.IsDisposed);
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
