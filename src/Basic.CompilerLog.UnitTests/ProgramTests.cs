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
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Microsoft.CodeAnalysis.NetAnalyzers.dll", output);
    }

    [Fact]
    public void AnalyzersHelp()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog analyzers [OPTIONS]", output);
    }

    [Fact]
    public void AnalyzersError()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.RemovedBinaryLogPath}");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("Unexpected error", output); 
    }

    [Fact]
    public void AnalyzerBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.RemovedBinaryLogPath} --not-an-option");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("Extra arguments", output); 
    }

    [Fact]
    public void BadCommand()
    {
        var (exitCode, output) = RunCompLogEx("invalid");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains(@"""invalid"" is not a valid command", output);
    }

    [Theory]
    [InlineData("", "msbuild.complog")]
    [InlineData("--out custom.complog", "custom.complog")]
    [InlineData("-o custom.complog", "custom.complog")]
    public void Create(string extra, string fileName)
    {
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"create {extra} -p {Fixture.ConsoleProjectName} {Fixture.SolutionBinaryLogPath}"));
        var complogPath = Path.Combine(RootDirectory, fileName);
        Assert.True(File.Exists(complogPath));
    }

    [Fact]
    public void CreateProjectFile()
    {
        RunDotNet("new console --name console -o .");
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"create console.csproj -o msbuild.complog"));
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
        Assert.Equal(Constants.ExitFailure, RunCompLog($"create console.csproj -o msbuild.complog -- -t:Build"));
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
            Assert.Equal(Constants.ExitSuccess, RunCompLog($"create {filePath} -o msbuild.complog"));
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
        Assert.NotEqual(Constants.ExitSuccess, result);
    }

    [Fact]
    public void CreateFullPath()
    {
        RunDotNet($"new console --name example --output .");
        RunDotNet("build -bl -nr:false");
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"create {GetBinaryLogFullPath()}", RootDirectory));
    }

    [Fact]
    public void CreateOverRemovedProject()
    {
        Assert.Equal(Constants.ExitFailure, RunCompLog($"create {Fixture.RemovedBinaryLogPath}"));
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-help")]
    public void CreateHelp(string arg)
    {
        Assert.Equal(Constants.ExitFailure, RunCompLog($"create {arg}"));
    }

    [Fact]
    public void CreateExistingComplog()
    {
        var complogPath = Path.Combine(RootDirectory, "file.complog");
        File.WriteAllText(complogPath, "");
        Assert.Equal(Constants.ExitFailure, RunCompLog($"create {complogPath}"));
    }

    [Fact]
    public void CreateExtraArguments()
    {
        Assert.Equal(Constants.ExitFailure, RunCompLog($"create {Fixture.SolutionBinaryLogPath} extra"));
    }

    [Fact]
    public void References()
    {
        RunWithBoth(logPath =>
        {
            Assert.Equal(Constants.ExitSuccess, RunCompLog($"ref -o {RootDirectory} {logPath}"));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(RootDirectory, "console", "refs"), "*.dll"));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(RootDirectory, "console", "analyzers"), "*.dll", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void ReferencesHelp()
    {
        var (exitCode, output) = RunCompLogEx($"ref -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog ref [OPTIONS]", output);
    }

    [Fact]
    public void ReferencesBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"ref --not-an-option");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("complog ref [OPTIONS]", output);
    }

    [Fact]
    public void ResponseSingleLine()
    {
        var exitCode = RunCompLog($"rsp {Fixture.SolutionBinaryLogPath} -p console.csproj -s");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var rsp = Path.Combine(RootDirectory, @".complog", "console", "build.rsp");
        Assert.True(File.Exists(rsp));

        var lines = File.ReadAllLines(rsp);
        Assert.Single(lines);
        Assert.Contains("Program.cs", lines[0]);
    }

    [Fact]
    public void ResponseOutputPath()
    {
        var dir = Root.NewDirectory("output");
        var exitCode = RunCompLog($"rsp {Fixture.SolutionBinaryLogPath} -p console.csproj -o {dir}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var rsp = Path.Combine(dir, "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseProjectFilter()
    {
        var exitCode = RunCompLog($"rsp {Fixture.SolutionBinaryLogPath} -p console.csproj");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var rsp = Path.Combine(RootDirectory, @".complog", "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseOnCompilerLog()
    {
        var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
        Assert.Empty(CompilerLogUtil.ConvertBinaryLog(Fixture.SolutionBinaryLogPath, complogPath));
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"rsp {complogPath} -p console.csproj"));
    }

    [Fact]
    public void ResponseOnInvalidFileType()
    {
        var (exitCode, output) = RunCompLogEx($"rsp data.txt -p console.csproj");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("Not a valid log", output);
    }

    [Fact]
    public void ResponseAll()
    {
        var exitCode = RunCompLog($"rsp {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var rsp = Path.Combine(RootDirectory, @".complog", "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseMultiTarget()
    {
        var exitCode = RunCompLog($"rsp {Fixture.ClassLibMultiProjectPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.True(File.Exists(Path.Combine(RootDirectory, @".complog", "classlibmulti-net6.0", "build.rsp")));
        Assert.True(File.Exists(Path.Combine(RootDirectory, @".complog", "classlibmulti-net7.0", "build.rsp")));
    }

    [Fact]
    public void ResponseNoLogArgument()
    {
        var (exitCode, output) = RunCompLogEx($"rsp -o {RootDirectory}", Path.GetDirectoryName(Fixture.ConsoleProjectPath)!);
        TestOutputHelper.WriteLine(output);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.True(File.Exists(Path.Combine(RootDirectory, "console", "build.rsp")));
    }

    [Fact]
    public void ResponseNoLogAvailable()
    {
        var dir = Root.NewDirectory("empty");
        var exitCode = RunCompLog($"rsp", dir);
        Assert.Equal(Constants.ExitFailure, exitCode);
    }

    [Fact]
    public void ResponseHelp()
    {
        var (exitCode, output) = RunCompLogEx($"rsp -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog rsp [OPTIONS]", output);
    }

    [Fact]
    public void ResponseBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"rsp --not-an-option");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("complog rsp [OPTIONS]", output);
    }

    [Fact]
    public void ResponseLinuxComplog()
    {
        var path = Path.Combine(RootDirectory, "console.complog");
        File.WriteAllBytes(path, ResourceLoader.GetResourceBlob("linux-console.complog"));
        var (exitCode, output) = RunCompLogEx($"rsp {path}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Contains("generated on different operating system", output);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("--exclude-analyzers")]
    public void ExportCompilerLog(string arg)
    {
        RunWithBoth(logPath =>
        {
            using var exportDir = new TempDir();

            Assert.Equal(Constants.ExitSuccess, RunCompLog($"export -o {exportDir.DirectoryPath} {arg} {logPath} ", RootDirectory));

            // Now run the generated build.cmd and see if it succeeds;
            var exportPath = Path.Combine(exportDir.DirectoryPath, "console", "export");
            var buildResult = RunBuildCmd(exportPath);
            Assert.True(buildResult.Succeeded);
        });
    }

    [Fact]
    public void ExportHelp()
    {
        var (exitCode, output) = RunCompLogEx($"export -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog export [OPTIONS]", output);
    }

    [Fact]
    public void ExportBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"export --not-an-option");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("complog export [OPTIONS]", output);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("")]
    public void Help(string arg)
    {
        var (exitCode, output) = RunCompLogEx(arg);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog [command] [args]", output);
    }

    [Fact]
    public void HelpVerbose()
    {
        var (exitCode, output) = RunCompLogEx($"help -v");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog [command] [args]", output);
        Assert.Contains("Commands can be passed a .complog, ", output);
    }

    [Theory]
    [InlineData("replay", "")]
    [InlineData("replay", "-none")]
    [InlineData("replay", "-analyzers")]
    [InlineData("replay", "-severity Error")]
    [InlineData("emit", "-none")]
    [InlineData("diagnostics", "-none")]
    public void ReplayWithArgs(string command, string arg)
    {
        using var emitDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"{command} {arg} {Fixture.SolutionBinaryLogPath}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-none")]
    public void ReplayConsoleWithEmit(string arg)
    {
        using var emitDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"replay {arg} -emit -o {emitDir.DirectoryPath} {Fixture.SolutionBinaryLogPath}"));

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
    public void ReplayHelp()
    {
        var (exitCode, output) = RunCompLogEx($"replay -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog replay [OPTIONS]", output);
    }

    [Fact]
    public void ReplayBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"replay --not-an-option");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("complog replay [OPTIONS]", output);
    }

    [Fact]
    public void RelpayBadOptionCombination()
    {
        var (exitCode, output) = RunCompLogEx($"replay -o example");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.StartsWith("Error: Specified a path", output);
    }

    [Fact]
    public void ReplayWithExport()
    {
        var (exitCode, output) = RunCompLogEx($"replay {Fixture.ConsoleWithDiagnosticsBinaryLogPath} -export -o {RootDirectory}");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("Exporting to", output);
        Assert.True(File.Exists(Path.Combine(RootDirectory, "console-with-diagnostics", "export", "build.rsp")));
        Assert.True(File.Exists(Path.Combine(RootDirectory, "console-with-diagnostics", "export", "ref", "netstandard.dll")));
    }

    [Fact]
    public void PrintAll()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("console.csproj (net7.0)", output);
        Assert.Contains("classlib.csproj (net7.0)", output);
    }

    [Fact]
    public void PrintOne()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath} -p classlib.csproj");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("console.csproj (net7.0)", output);
        Assert.Contains("classlib.csproj (net7.0)", output);
    }

    /// <summary>
    /// Engage the code to find files in the specidied directory
    /// </summary>
    [Fact]
    public void PrintDirectory()
    {
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"print {Path.GetDirectoryName(Fixture.SolutionBinaryLogPath)}"));

        // Make sure this works on a build that will fail!
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"print {Path.GetDirectoryName(Fixture.ConsoleWithDiagnosticsProjectPath)}"));
    }

    [Fact]
    public void PrintHelp()
    {
        var (exitCode, output) = RunCompLogEx($"print -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog print [OPTIONS]", output);
    }

    [Fact]
    public void PrintError()
    {
        var (exitCode, output) = RunCompLogEx($"print --not-an-option");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("complog print [OPTIONS]", output);
    }

    [WindowsFact]
    public void PrintKinds()
    {
        Debug.Assert(Fixture.WpfAppProjectPath is not null);
        var (exitCode, output) = RunCompLogEx($"print --include {Fixture.WpfAppProjectPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("WpfTemporaryCompile", output);

        (exitCode, output) = RunCompLogEx($"print {Fixture.WpfAppProjectPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("WpfTemporaryCompile", output);
    }

    [Fact]
    public void PrintFrameworks()
    {
        var (exitCode, output) = RunCompLogEx($"print --include {Fixture.ClassLibMultiProjectPath} --framework net7.0");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("(net7.0)", output);
    }

    [Fact]
    public void PrintBadFile()
    {
        var dir = Root.NewDirectory(Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "example.proj");
        var (exitCode, _) = RunCompLogEx($"print {file}");
        Assert.Equal(Constants.ExitFailure, exitCode);
    }

    [Fact]
    public void PrintOldMetadata()
    {
        var dir = Root.NewDirectory("metadata");
        var filePath = Path.Combine(dir, "old.complog");
        Create();

        var (exitCode, output) = RunCompLogEx($"print {filePath}");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("compiler logs are no longer supported", output);

        void Create()
        {
            using var binlogStream = new FileStream(Fixture.ConsoleWithDiagnosticsBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var complogStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var result = CompilerLogUtil.TryConvertBinaryLog(binlogStream, complogStream, predicate: null, metadataVersion: Basic.CompilerLog.Util.Metadata.LatestMetadataVersion - 1);
            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public void PrintEmptyDirectory()
    {
        var dir = Root.NewDirectory("empty");
        var (exitCode, output) = RunCompLogEx($"print {dir}");
        Assert.Equal(Constants.ExitFailure, exitCode);
    }
}
#endif
