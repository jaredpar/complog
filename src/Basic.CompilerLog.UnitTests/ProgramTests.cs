#if NETCOREAPP
using Basic.CompilerLog.Util;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(SolutionFixtureCollection.Name)]
public sealed class ProgramTests : TestBase
{
    public SolutionFixture Fixture { get; }

    public ProgramTests(ITestOutputHelper testOutputHelper, SolutionFixture fixture) 
        : base(testOutputHelper, nameof(ProgramTests))
    {
        Fixture = fixture;
    }

    public int RunCompLog(string args, string? currentDirectory = null)
    {
        currentDirectory ??= RootDirectory;
        Constants.CurrentDirectory = currentDirectory;
        var assembly = typeof(FilterOptionSet).Assembly;
        var program = assembly.GetType("Program", throwOnError: true);
        var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(main);
        var ret = main!.Invoke(null, new[] { args.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
        return (int)ret!;
    }

    private void RunWithBoth(Action<string> action)
    {
        // Run with the binary log
        action(Fixture.SolutionBinaryLogPath);

        // Now create a compiler log 
        var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
        var diagnostics = CompilerLogUtil.ConvertBinaryLog(Fixture.SolutionBinaryLogPath, complogPath);
        Assert.Empty(diagnostics);
        action(complogPath);
    }

    [Theory]
    [InlineData("", "msbuild.complog")]
    [InlineData("--out custom.complog", "custom.complog")]
    [InlineData("-o custom.complog", "custom.complog")]
    public void Create(string extra, string fileName)
    {
        Assert.Equal(0, RunCompLog($"create {extra} -p {Fixture.ConsoleProjectName} {Fixture.SolutionBinaryLogPath}"));
        var complogPath = Path.Combine(RootDirectory, fileName);
        Assert.True(File.Exists(complogPath));
    }

    [Fact]
    public void CreateProjectFile()
    {
        RunDotNet("new console --name console -o .");
        Assert.Equal(0, RunCompLog($"create console.csproj -o msbuild.complog"));
        var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerHostOptions.None);
        Assert.Single(reader.ReadAllCompilerCalls());
    }

    /// <summary>
    /// Explicit build target overrides the implicit -t:Rebuild
    /// </summary>
    [Fact]
    public void CreateNoopBuild()
    {
        RunDotNet("new console --name console -o .");
        RunDotNet("build");
        Assert.Equal(1, RunCompLog($"create console.csproj -o msbuild.complog -- -t:Build"));
        var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerHostOptions.None);
        Assert.Empty(reader.ReadAllCompilerCalls());
    }

    [Theory]
    [InlineData("console.sln")]
    [InlineData("console.csproj")]
    [InlineData("")]
    public void CreateSolution(string target)
    {
        RunDotNet("new console --name console -o .");
        RunDotNet("new sln --name console");
        RunDotNet("sln add console.csproj");
        Assert.Equal(0, RunCompLog($"create {target} -o msbuild.complog"));
        var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerHostOptions.None);
        Assert.Single(reader.ReadAllCompilerCalls());
    }

    /// <summary>
    /// When the resulting compiler log is empty an error should be returned cause clearly 
    /// there was a mistake somewhere on the command line.
    /// </summary>
    [Fact]
    public void CreateEmpty()
    {
        var result = RunCompLog($"create -p does-not-exist.csproj {Fixture.SolutionBinaryLogPath}");
        Assert.NotEqual(0, result);
    }

    [Fact]
    public void CreateFullPath()
    {
        RunDotNet($"new console --name example --output .");
        RunDotNet("build -bl -nr:false");
        Assert.Equal(0, RunCompLog($"create {GetBinaryLogFullPath()}", RootDirectory));
    }

    [Fact]
    public void CreateOverRemovedProject()
    {
        Assert.Equal(1, RunCompLog($"create {Fixture.RemovedBinaryLogPath}"));
    }

    /// <summary>
    /// Don't search for complogs when an explicit log source isn't provided.
    /// </summary>
    [Fact]
    public void CreateOtherComplogExists()
    {
        RunDotNet($"new console --name example --output .");
        RunDotNet("build -bl -nr:false");
        Root.NewFile("other.complog", "");
        Assert.Equal(0, RunCompLog($"create", RootDirectory));
    }

    [Fact]
    public void References()
    {
        RunWithBoth(logPath =>
        {
            Assert.Equal(0, RunCompLog($"ref -o {RootDirectory} {logPath}"));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(RootDirectory, "console", "refs"), "*.dll"));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(RootDirectory, "console", "analyzers"), "*.dll", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void ExportCompilerLog()
    {
        RunWithBoth(logPath =>
        {
            using var exportDir = new TempDir();

            Assert.Equal(0, RunCompLog($"export -o {exportDir.DirectoryPath} {logPath} ", RootDirectory));

            // Now run the generated build.cmd and see if it succeeds;
            var exportPath = Path.Combine(exportDir.DirectoryPath, "console", "export");
            var buildResult = RunBuildCmd(exportPath);
            Assert.True(buildResult.Succeeded);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("-none")]
    public void ReplayConsoleWithEmit(string arg)
    {
        using var emitDir = new TempDir();
        RunCompLog($"replay {arg} -emit -o {emitDir.DirectoryPath} {Fixture.SolutionBinaryLogPath}");

        AssertOutput(@"console\emit\console.dll");
        AssertOutput(@"console\emit\console.pdb");
        AssertOutput(@"console\emit\ref\console.dll");

        void AssertOutput(string relativePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                relativePath = relativePath.Replace('\\', '/');
            }

            var filePath = Path.Combine(emitDir.DirectoryPath, relativePath);
            Assert.True(File.Exists(filePath));
        }
    }
}
#endif
