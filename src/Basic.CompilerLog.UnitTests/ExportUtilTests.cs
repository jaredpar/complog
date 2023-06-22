using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class ExportUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public ExportUtilTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(ExportUtilTests))
    {
        Fixture = fixture;
    }

    private void TestExport(int expectedCount, Action<string>? callback = null)
    {
        using var scratchDir = new TempDir();
        var binlogFilePath = Path.Combine(RootDirectory, "msbuild.binlog");
        var compilerLogFilePath = Path.Combine(scratchDir.DirectoryPath, "build.complog");
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, compilerLogFilePath);
        Assert.Empty(diagnosticList);

        // Now that we've converted to a compiler log delete all the original project code. This 
        // ensures our builds below don't succeed because old files are being referenced
        Root.EmptyDirectory();

        TestExport(compilerLogFilePath, expectedCount, callback: callback);
    }

    private void TestExport(string compilerLogFilePath, int expectedCount, bool includeAnalyzers = true, Action<string>? callback = null)
    {
        using var reader = CompilerLogReader.Create(compilerLogFilePath);
#if NETCOREAPP
        var sdkDirs = DotnetUtil.GetSdkDirectories();
#else
        var sdkDirs = DotnetUtil.GetSdkDirectories(@"c:\Program Files\dotnet");
#endif
        var exportUtil = new ExportUtil(reader, includeAnalyzers);
        var count = 0;
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            count++;
            TestOutputHelper.WriteLine($"Testing export for {compilerCall.ProjectFileName} - {compilerCall.TargetFramework}");
            using var tempDir = new TempDir();
            exportUtil.ExportRsp(compilerCall, tempDir.DirectoryPath, sdkDirs);

            // Now run the generated build.cmd and see if it succeeds;
            var buildResult = RunBuildCmd(tempDir.DirectoryPath);
            TestOutputHelper.WriteLine(buildResult.StandardOut);
            TestOutputHelper.WriteLine(buildResult.StandardError);
            Assert.True(buildResult.Succeeded);

            // Ensure that full paths aren't getting written out to the RSP file. That makes the 
            // build non-xcopyable. 
            foreach (var line in File.ReadAllLines(Path.Combine(tempDir.DirectoryPath, "build.rsp")))
            {
                Assert.False(line.Contains(tempDir.DirectoryPath, StringComparison.OrdinalIgnoreCase), $"Has full path: {line}");
            }

            callback?.Invoke(tempDir.DirectoryPath);
        }
        Assert.Equal(expectedCount, count);
    }

    [Fact]
    public void Console()
    {
        TestExport(Fixture.ConsoleComplogPath, 1);
    }

    [Fact]
    public void ClassLib()
    {
        TestExport(Fixture.ClassLibComplogPath, 1);
    }

    /// <summary>
    /// Make sure that generated files are put into the generated directory
    /// </summary>
    [Fact]
    public void GeneratedText()
    {
        TestExport(Fixture.ConsoleComplogPath, 1, callback: tempPath =>
        {
            var generatedPath = Path.Combine(tempPath, "generated");
            var files = Directory.GetFiles(generatedPath, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
        });
    }

    /// <summary>
    /// Make sure the rsp file has the expected structure when we exclude analyzers from the 
    /// export.
    /// </summary>
    [Fact]
    public void GeneratedTextExcludeAnalyzers()
    {
        TestExport(Fixture.ConsoleComplogPath, 1, includeAnalyzers: false, callback: tempPath =>
        {
            var rspPath = Path.Combine(tempPath, "build.rsp");
            var foundPath = false;
            foreach (var line in File.ReadAllLines(rspPath))
            {
                Assert.DoesNotContain("/analyzer:", line);
                if (line.Contains("RegexGenerator.g.cs") && !line.StartsWith("/"))
                {
                    foundPath = true;
                }
            }

            Assert.True(foundPath);

            var analyzers = Directory.GetFiles(Path.Combine(tempPath, "analyzers"), "*.dll", SearchOption.AllDirectories).ToList();
            Assert.Equal(7, analyzers.Count);
        });
    }

    [Fact]
    public void ConsoleMultiTarget()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFrameworks>net7.0;net6.0</TargetFrameworks>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);
        RunDotNet("build -bl");
        TestExport(2);
    }

    [Fact]
    public void ConsoleWithResource()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"),
            """
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
            """);
        File.WriteAllText(Path.Combine(RootDirectory, "resource.txt"), """
            This is an awesome resource
            """);
        RunDotNet("build -bl");
        TestExport(1);
    }

    [Fact]
    public void ContentWin32Elements()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <Win32Manifest>resource.txt</Win32Manifest>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(RootDirectory, "resource.txt"), """
            This is an awesome resource
            """);
        RunDotNet("build -bl");
        TestExport(1);
    }

    [Fact]
    public void StrongNameKey()
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
        TestExport(1);
    }
}
