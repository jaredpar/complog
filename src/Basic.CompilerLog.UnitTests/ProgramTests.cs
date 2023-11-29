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
        var (exitCode, _) = RunCompLogEx(args, currentDirectory);
        return exitCode;
    }

    public (int ExitCode, string Output) RunCompLogEx(string args, string? currentDirectory = null)
    {
        var writer = new System.IO.StringWriter();
        currentDirectory ??= RootDirectory;
        Constants.CurrentDirectory = currentDirectory;
        Constants.Out = writer;
        var assembly = typeof(FilterOptionSet).Assembly;
        var program = assembly.GetType("Program", throwOnError: true);
        var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(main);
        var ret = main!.Invoke(null, new[] { args.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
        return ((int)ret!, writer.ToString());
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

    [Fact]
    public void AnalyzersSimple()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.SolutionBinaryLogPath} -p console.csproj");
        Assert.Equal(0, exitCode);
        Assert.Contains("Microsoft.CodeAnalysis.NetAnalyzers.dll", output);
    }

    [Fact]
    public void AnalyzersHelp()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers -h");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("complog analyzers [OPTIONS]", output);
    }

    [Fact]
    public void AnalyzersError()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.RemovedBinaryLogPath}");
        Assert.NotEqual(0, exitCode);
        Assert.StartsWith("Unexpected error", output); 
    }

    [Fact]
    public void AnalyzerBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.RemovedBinaryLogPath} --not-an-option");
        Assert.NotEqual(0, exitCode);
        Assert.StartsWith("Extra arguments", output); 
    }

    [Fact]
    public void BadCommand()
    {
        var (exitCode, output) = RunCompLogEx("invalid");
        Assert.NotEqual(0, exitCode);
        Assert.Contains(@"""invalid"" is not a valid command", output);
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

    [Fact]
    public void CreateWithBuild()
    {
        RunCore(Fixture.SolutionPath);
        RunCore(Fixture.ConsoleProjectPath);
        void RunCore(string filePath)
        {
            Assert.Equal(0, RunCompLog($"create {filePath} -o msbuild.complog"));
            var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
            using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerHostOptions.None);
            Assert.NotEmpty(reader.ReadAllCompilerCalls());
        }
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

    [Theory]
    [InlineData("-h")]
    [InlineData("-help")]
    public void CreateHelp(string arg)
    {
        Assert.Equal(1, RunCompLog($"create {arg}"));
    }

    [Fact]
    public void CreateExistingComplog()
    {
        var complogPath = Path.Combine(RootDirectory, "file.complog");
        File.WriteAllText(complogPath, "");
        Assert.Equal(1, RunCompLog($"create {complogPath}"));
    }

    [Fact]
    public void CreateExtraArguments()
    {
        Assert.Equal(1, RunCompLog($"create {Fixture.SolutionBinaryLogPath} extra"));
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
    public void ReferencesHelp()
    {
        var (exitCode, output) = RunCompLogEx($"ref -h");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("complog ref [OPTIONS]", output);
    }

    [Fact]
    public void ReferencesBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"ref --not-an-option");
        Assert.Equal(1, exitCode);
        Assert.Contains("complog ref [OPTIONS]", output);
    }

    [Fact]
    public void ResponseSingle()
    {
        var exitCode = RunCompLog($"rsp {Fixture.SolutionBinaryLogPath} -p console.csproj");
        Assert.Equal(0, exitCode);
        var rsp = Path.Combine(RootDirectory, @".complog\console\build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseAll()
    {
        var exitCode = RunCompLog($"rsp {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(0, exitCode);
        var rsp = Path.Combine(RootDirectory, @".complog\console\build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseHelp()
    {
        var (exitCode, output) = RunCompLogEx($"rsp -h");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("complog rsp [OPTIONS]", output);
    }

    [Fact]
    public void ResponseBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"rsp --not-an-option");
        Assert.Equal(1, exitCode);
        Assert.Contains("complog rsp [OPTIONS]", output);
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

    [Fact]
    public void Help()
    {
        var (exitCode, output) = RunCompLogEx($"help");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("complog [command] [args]", output);
    }

    [Fact]
    public void HelpVerbose()
    {
        var (exitCode, output) = RunCompLogEx($"help -v");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("complog [command] [args]", output);
        Assert.Contains("Commands can be passed a .complog, ", output);
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

    [Fact]
    public void PrintAll()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(0, exitCode);
        Assert.Contains("console.csproj (net7.0)", output);
        Assert.Contains("classlib.csproj (net7.0)", output);
    }

    [Fact]
    public void PrintOne()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath} -p classlib.csproj");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("console.csproj (net7.0)", output);
        Assert.Contains("classlib.csproj (net7.0)", output);
    }

    [Fact]
    public void PrintHelp()
    {
        var (exitCode, output) = RunCompLogEx($"print -h");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("complog print [OPTIONS]", output);
    }

    [Fact]
    public void PrintError()
    {
        var (exitCode, output) = RunCompLogEx($"print --not-an-option");
        Assert.Equal(1, exitCode);
        Assert.Contains("complog print [OPTIONS]", output);
    }

}
#endif
