using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

public sealed class ExportUtilTests : TestBase
{
    public ExportUtilTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper, nameof(ExportUtilTests))
    {


    }

    private void TestExport(int expectedCount)
    {
        var binlogFilePath = Path.Combine(RootDirectory, "msbuild.binlog");
        var compilerLogFilePath = Path.Combine(RootDirectory, "build.compilerlog");
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, compilerLogFilePath);
        Assert.Empty(diagnosticList);

        using var reader = CompilerLogReader.Create(compilerLogFilePath);
        var sdkDirs = DotnetUtil.GetSdkDirectories();
        var exportUtil = new ExportUtil(reader);
        var count = 0;
        foreach (var compilerCall in reader.ReadCompilerCalls())
        {
            count++;
            TestOutputHelper.WriteLine($"Testing export for {compilerCall.ProjectFileName} - {compilerCall.TargetFramework}");
            using var tempDir = new TempDir();
            exportUtil.ExportRsp(compilerCall, tempDir.DirectoryPath, sdkDirs);

            // Now run the generated build.cmd and see if it succeeds;
            var buildResult = ProcessUtil.RunBatchFile(
                Path.Combine(tempDir.DirectoryPath, "build.cmd"),
                args: "",
                workingDirectory: tempDir.DirectoryPath);
            TestOutputHelper.WriteLine(buildResult.StandardOut);
            TestOutputHelper.WriteLine(buildResult.StandardError);
            Assert.True(buildResult.Succeeded);
        }
        Assert.Equal(expectedCount, count);
    }

    [Fact]
    public void Console()
    {
        RunDotNet($"new console --name example --output .");
        RunDotNet("build -bl");
        TestExport(1);
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
}
