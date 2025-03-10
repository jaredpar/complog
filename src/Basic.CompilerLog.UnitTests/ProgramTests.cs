#if NET
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(SolutionFixtureCollection.Name)]
public sealed class ProgramTests : TestBase
{
    private Action<ICompilerCallReader>? _assertCompilerCallReader;

    public SolutionFixture Fixture { get; }

    public ProgramTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, SolutionFixture fixture) 
        : base(testOutputHelper, testContextAccessor, nameof(ProgramTests))
    {
        Fixture = fixture;
    }

    public override void Dispose()
    {
        base.Dispose();
        Assert.Null(_assertCompilerCallReader);
    }

    public int RunCompLog(string args, string? currentDirectory = null)
    {
        var (exitCode, _) = RunCompLogEx(args, currentDirectory);
        return exitCode;
    }

    private void OnCompilerCallReader(ICompilerCallReader reader)
    {
        if (_assertCompilerCallReader is { })
        {
            try
            {
                _assertCompilerCallReader(reader);
            }
            finally
            {
                _assertCompilerCallReader = null;
            }
        }
    }

    private void AssertCompilerCallReader(Action<ICompilerCallReader> action)
    {
        _assertCompilerCallReader = action;
    }

    private void AssertCorrectReader(ICompilerCallReader reader, string logFilePath)
    {
        var isBinlog = Path.GetExtension(logFilePath) == ".binlog";
        if (isBinlog)
        {
            Assert.IsType<BinaryLogReader>(reader);
        }
        else
        {
            Assert.IsType<CompilerLogReader>(reader);
        }
    }

    public (int ExitCode, string Output) RunCompLogEx(string args, string? currentDirectory = null)
    {
        var savedCurrentDirectory = Constants.CurrentDirectory;
        var savedLocalAppDataDirectory = Constants.LocalAppDataDirectory;

        try
        {
            var writer = new System.IO.StringWriter();
            currentDirectory ??= RootDirectory;
            Constants.CurrentDirectory = currentDirectory;
            Constants.LocalAppDataDirectory = Path.Combine(currentDirectory, "localappdata");
            Constants.Out = writer;
            Constants.OnCompilerCallReader = OnCompilerCallReader;
            var assembly = typeof(FilterOptionSet).Assembly;
            var program = assembly.GetType("Program", throwOnError: true);
            var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(main);
            var ret = main!.Invoke(null, new[] { args.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
            if (Directory.Exists(Constants.LocalAppDataDirectory))
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(Constants.LocalAppDataDirectory));
            }
            return ((int)ret!, writer.ToString());
        }
        finally
        {
            Constants.CurrentDirectory = savedCurrentDirectory;
            Constants.LocalAppDataDirectory = savedLocalAppDataDirectory;
            Constants.Out = Console.Out;
            Constants.OnCompilerCallReader = _ => { };
        }
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
    public void AnalyzersBoth()
    {
        RunWithBoth(void (string logPath) =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logPath));
            var (exitCode, output) = RunCompLogEx($"analyzers {logPath} -p console.csproj");
            Assert.Equal(Constants.ExitSuccess, exitCode);
            Assert.Contains("Microsoft.CodeAnalysis.NetAnalyzers.dll", output);
        });
    }

    [Fact]
    public void AnalyzersHelp()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog analyzers [OPTIONS]", output);
    }

    /// <summary>
    /// The analyzers can still be listed if the project file is deleted as long as the 
    /// analyzers are still on disk
    /// </summary>
    [Fact]
    public void AnalyzersProjectFilesDeleted()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.RemovedBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("CSharp.NetAnalyzers.dll", output);
    }

    [Fact]
    public void AnalyzersBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.RemovedBinaryLogPath} --not-an-option");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("Extra arguments", output); 
    }

    [Fact]
    public void AnalyzersSimple()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("Analyzers:", output); 
        Assert.DoesNotContain("Generators:", output); 

        (exitCode, output) = RunCompLogEx($"analyzers {Fixture.SolutionBinaryLogPath} -t");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Analyzers:", output); 
        Assert.Contains("Generators:", output); 
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
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
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
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
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
            using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
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
        var (exitCode, output) = RunCompLogEx($"create {Fixture.RemovedBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains(RoslynUtil.GetMissingFileDiagnosticMessage(""), output);
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
    public void CreateFilePathOutput()
    {
        var complogFilePath = Path.Combine(RootDirectory, "file.complog");
        var (exitCode, output) = RunCompLogEx($"create {Fixture.ClassLibProjectPath} -o {complogFilePath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains($"Wrote {complogFilePath}", output);
    }

    [Fact]
    public void CreateMultipleFiles()
    {
        File.Copy(Fixture.ConsoleWithDiagnosticsBinaryLogPath, Path.Combine(RootDirectory, "console1.binlog"));
        File.Copy(Fixture.ConsoleWithDiagnosticsBinaryLogPath, Path.Combine(RootDirectory, "console2.binlog"));
        var (exitCode, output) = RunCompLogEx($"create");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains($"Found multiple log files in {RootDirectory}", output);
    }

    [Fact]
    public void Id()
    {
        var (exitCode, output) = RunCompLogEx($"id {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var dir = Path.Combine(RootDirectory, ".complog");
        var files = Directory.EnumerateFiles(dir, "build-id.txt", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        files = Directory.EnumerateFiles(dir, "build-id.content.txt", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
    }

    [Fact]
    public void IdHelp()
    {
        var (exitCode, output) = RunCompLogEx($"id -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog id [OPTIONS]", output);
    }

    [Fact]
    public void IdInline()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);
        var (exitCode, output) = RunCompLogEx($"id -i", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Generating id files inline", output);

        var contentFilePath = Path.Combine(dir, "build-id.content.txt");
        try
        {
            var idFilePath = Path.Combine(dir, "build-id.txt");
            Assert.True(File.Exists(idFilePath));
            var id = File.ReadAllText(idFilePath);

            var expectedId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
                ? ""
                : "D339B2B333F7C2344D6AFD47135FF7BF6F7DF64FB9E5E5E76690B093FC302BF9";
            Assert.Equal(expectedId, id);
        }
        catch (Exception)
        {
            AddFileToTestArtifacts(contentFilePath);
            throw;
        }
    }

    [Fact]
    public void IdPrint()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);
        var (exitCode, output) = RunCompLogEx($"id --print", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("example.csproj", output);
    }

    [Fact]
    public void IdInlineAndOutput()
    {
        var dir = Root.NewDirectory();
        var (exitCode, output) = RunCompLogEx($"id -i -o blah");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
    }

    [Fact]
    public void IdBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"id -blah");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
    }

    [Fact]
    public void References()
    {
        RunWithBoth(logPath =>
        {
            AssertCompilerCallReader(reader => AssertCorrectReader(reader, logPath));
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
        var rsp = Path.Combine(RootDirectory, @".complog", "rsp", "console", "build.rsp");
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
        var rspBaseDir = Path.Combine(RootDirectory, ".complog", "rsp");
        var rsp = Path.Combine(rspBaseDir, "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
        Assert.Single(Directory.EnumerateDirectories(rspBaseDir));
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
        var rsp = Path.Combine(RootDirectory, @".complog", "rsp", "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseMultiTarget()
    {
        var exitCode = RunCompLog($"rsp {Fixture.ClassLibMultiProjectPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.True(File.Exists(Path.Combine(RootDirectory, @".complog", "rsp", "classlibmulti-net6.0", "build.rsp")));
        Assert.True(File.Exists(Path.Combine(RootDirectory, @".complog", "rsp", "classlibmulti-net8.0", "build.rsp")));
    }

    [Fact]
    public void ResponseNoLogArgument()
    {
        var consoleDir = Path.GetDirectoryName(Fixture.ConsoleProjectPath)!;
        var (exitCode, output) = RunCompLogEx($"rsp -o {RootDirectory}", consoleDir);
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
    public void ResponseInline()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);
        var (exitCode, output) = RunCompLogEx($"rsp -i", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Generating response files inline", output);
        Assert.True(File.Exists(Path.Combine(dir, "build.rsp")));
    }

    [Fact]
    public void ResponseInlineMultiTarget()
    {
        var dir = Root.CopyDirectory(Path.GetDirectoryName(Fixture.ClassLibMultiProjectPath)!);
        RunDotNet($"new console --name example --output .", dir);
        var (exitCode, output) = RunCompLogEx($"rsp -i", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Generating response files inline", output);
        Assert.True(File.Exists(Path.Combine(dir, "build-net6.0.rsp")));
        Assert.True(File.Exists(Path.Combine(dir, "build-net8.0.rsp")));
    }

    [Fact]
    public void ResponseInlineWithOutput()
    {
        var (exitCode, _) = RunCompLogEx($"rsp -i -o {RootDirectory}");
        Assert.Equal(Constants.ExitFailure, exitCode);
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
    [InlineData("", null)]
    [InlineData("-a none", BasicAnalyzerKind.None)]
    [InlineData("-a ondisk", BasicAnalyzerKind.OnDisk)]
    public void ExportCompilerLog(string arg, BasicAnalyzerKind? expectedKind)
    {
        expectedKind ??= BasicAnalyzerHost.DefaultKind;
        RunWithBoth(logPath =>
        {
            using var exportDir = new TempDir();

            AssertCompilerCallReader(reader => Assert.Equal(expectedKind.Value, reader.BasicAnalyzerKind));
            Assert.Equal(Constants.ExitSuccess, RunCompLog($"export -o {exportDir.DirectoryPath} {arg} {logPath} ", RootDirectory));

            // Now run the generated build.cmd and see if it succeeds;
            var exportPath = Path.Combine(exportDir.DirectoryPath, "console");
            var buildResult = TestUtil.RunBuildCmd(exportPath);
            Assert.True(buildResult.Succeeded);

            // Check that the RSP file matches the analyzer intent
            var rspPath = Path.Combine(exportPath, "build.rsp");
            var anyAnalyzers = File.ReadAllLines(rspPath)
                .Any(x => x.StartsWith("/analyzer:", StringComparison.Ordinal));
            var expectAnalyzers = expectedKind.Value != BasicAnalyzerKind.None;
            Assert.Equal(expectAnalyzers, anyAnalyzers);
        });
    }

    /// <summary>
    /// The --all option should force all the compilations, including satellite ones, to be exported.
    /// </summary>
    [Fact]
    public void ExportSatellite()
    {
        using var exportDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"export -o {exportDir.DirectoryPath} --all {Fixture.ClassLibWithResourceLibs} ", RootDirectory));
        Assert.Equal(3, Directory.EnumerateDirectories(exportDir.DirectoryPath).Count());
    }

    /// <summary>
    /// Lacking the --all option only the core assemblies should be generated
    /// </summary>
    [Fact]
    public void ExportSatelliteDefault()
    {
        using var exportDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"export -o {exportDir.DirectoryPath} {Fixture.ClassLibWithResourceLibs} ", RootDirectory));
        Assert.Single(Directory.EnumerateDirectories(exportDir.DirectoryPath));
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
    [InlineData("replay", "", null)]
    [InlineData("replay", "--none", BasicAnalyzerKind.None)]
    [InlineData("replay", "--analyzers inmemory", BasicAnalyzerKind.InMemory)]
    [InlineData("replay", "--analyzers ondisk", BasicAnalyzerKind.OnDisk)]
    [InlineData("replay", "--analyzers none", BasicAnalyzerKind.None)]
    [InlineData("replay", "--severity Error", null)]
    [InlineData("emit", "--none", BasicAnalyzerKind.None)]
    [InlineData("diagnostics", "--none", BasicAnalyzerKind.None)]
    public void ReplayWithArgs(string command, string arg, BasicAnalyzerKind? kind)
    {
        kind ??= BasicAnalyzerHost.DefaultKind;
        using var emitDir = new TempDir();
        AssertCompilerCallReader(reader => 
        {
            Assert.Equal(kind.Value, reader.BasicAnalyzerKind);
        });
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"{command} {arg} {Fixture.SolutionBinaryLogPath}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--none")]
    public void ReplayConsoleWithEmit(string arg)
    {
        using var emitDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"replay {arg} -o {emitDir.DirectoryPath} {Fixture.SolutionBinaryLogPath}"));

        AssertOutput(@"console\console.dll");
        AssertOutput(@"console\console.pdb");
        AssertOutput(@"console\ref\console.dll");

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
    public void ReplayMissingOutput()
    {
        using var emitDir = new TempDir();
        var (extiCode, output) = RunCompLogEx($"replay --out");
        Assert.Equal(Constants.ExitFailure, extiCode);
        Assert.Contains("Missing required value", output);
    }

    [Fact]
    public void ReplayWithBadProject()
    {
        using var emitDir = new TempDir();
        var (extiCode, output) = RunCompLogEx($"replay --severity Info --project console-with-diagnostics.csproj {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(Constants.ExitFailure, extiCode);
        Assert.Contains("No compilations found", output);
    }

    [Fact]
    public void ReplayWithDiagnostics()
    {
        using var emitDir = new TempDir();
        var (exitCode, output) = RunCompLogEx($"replay --severity Info {Fixture.ConsoleWithDiagnosticsBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("CS0219", output);
    }

    [Fact]
    public void ReplayHelp()
    {
        var (exitCode, output) = RunCompLogEx($"replay -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog replay [OPTIONS]", output);
    }

    [Fact]
    public void ReplayNewCompiler()
    {
        string logFilePath = CreateBadLog();
        var (exitCode, output) = RunCompLogEx($"replay {logFilePath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Compiler in log is newer than complog: 99.99.99.99 >", output);

        string CreateBadLog()
        {
            var logFilePath = Path.Combine(RootDirectory, "mutated.complog");
            CompilerLogUtil.ConvertBinaryLog(
                Fixture.SolutionBinaryLogPath,
                logFilePath,
                cc => cc.ProjectFileName == "console.csproj");
            MutateArchive(logFilePath, CancellationToken);
            return logFilePath;
        }

        static void MutateArchive(string complogFilePath, CancellationToken cancellationToken)
        {
            using var fileStream = new FileStream(complogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: true);
            using var entryStream = zipArchive.OpenEntryOrThrow(CommonUtil.GetCompilerEntryName(0));
            var infoPack = MessagePackSerializer.Deserialize<CompilationInfoPack>(entryStream, CommonUtil.SerializerOptions, cancellationToken);
            infoPack.CompilerAssemblyName = Regex.Replace(
                infoPack.CompilerAssemblyName!,
                @"\d+\.\d+\.\d+\.\d+",
                "99.99.99.99");
            entryStream.Position = 0;
            MessagePackSerializer.Serialize(entryStream, infoPack, CommonUtil.SerializerOptions, cancellationToken);
        }
    }

    [Fact]
    public void ReplayBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"replay --not-an-option");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("complog replay [OPTIONS]", output);
    }

    [Fact]
    public void ReplayWithBothLogs()
    {
        RunWithBoth(void (string logFilePath) =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logFilePath));
            RunCompLog($"replay {logFilePath}");
        });
    }

    [Fact]
    public void ReplayWithProject()
    {
        AssertCompilerCallReader(void (ICompilerCallReader reader) => Assert.IsType<BinaryLogReader>(reader));
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"replay {Fixture.ConsoleProjectPath}"));
    }

    [Theory]
    [MemberData(nameof(GetBasicAnalyzerKinds))]
    public void GeneratedBoth(BasicAnalyzerKind basicAnalyzerKind)
    {
        RunWithBoth(logPath =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logPath));
            var dir = Root.NewDirectory("generated");
            var (exitCode, output) = RunCompLogEx($"generated {logPath} -p console.csproj -a {basicAnalyzerKind} -o {dir}");
            Assert.Equal(Constants.ExitSuccess, exitCode);
            Assert.Single(Directory.EnumerateFiles(dir, "RegexGenerator.g.cs", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void GeneratedBadFilter()
    {
        RunWithBoth(logPath =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logPath));
            var (exitCode, _) = RunCompLogEx($"generated {logPath} -p console-does-not-exist.csproj");
            Assert.Equal(Constants.ExitFailure, exitCode);
        });
    }

    [Fact]
    public void GeneratePdbMissing()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);
        RunDotNet("build -bl -nr:false", dir);

        // Delete the PDB
        Directory.EnumerateFiles(dir, "*.pdb", SearchOption.AllDirectories).ForEach(File.Delete);

        var (exitCode, output) = RunCompLogEx($"generated {dir} -a None");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("BCLA0001", output);
    }

    [Fact]
    public void GeneratedHelp()
    {
        var (exitCode, output) = RunCompLogEx($"generated -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog generated [OPTIONS]", output);
    }

    [Fact]
    public void GeneratedBadArg()
    {
        var (exitCode, _) = RunCompLogEx($"generated -o");
        Assert.Equal(Constants.ExitFailure, exitCode);
    }

    [Fact]
    public void PrintAll()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("console.csproj (net9.0)", output);
        Assert.Contains("classlib.csproj (net9.0)", output);
    }

    [Fact]
    public void PrintOne()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath} -p classlib.csproj");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("console.csproj (net9.0)", output);
        Assert.Contains("classlib.csproj (net9.0)", output);
    }

    [Fact]
    public void PrintCompilers()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath} -c");
        Assert.Equal(Constants.ExitSuccess, exitCode);

        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithDiagnosticsBinaryLogPath);
        var tuple = reader.ReadAllCompilerAssemblies().Single();
        Assert.Contains($"""
            Compilers
            {'\t'}File Path: {tuple.FilePath}
            {'\t'}Assembly Name: {tuple.AssemblyName}
            {'\t'}Commit Hash: {tuple.CommitHash}
            """, output);
    }

    /// <summary>
    /// Ensure that print can run without the code being present
    /// </summary>
    [Fact]
    public void PrintWithoutProject()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.RemovedBinaryLogPath} -c");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("Projects", output);
    }

    /// <summary>
    /// Engage the code to find files in the specified directory
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
        var (exitCode, output) = RunCompLogEx($"print --all {Fixture.WpfAppProjectPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("WpfTemporaryCompile", output);

        (exitCode, output) = RunCompLogEx($"print {Fixture.WpfAppProjectPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("WpfTemporaryCompile", output);
    }

    [Fact]
    public void PrintFrameworks()
    {
        var (exitCode, output) = RunCompLogEx($"print --all {Fixture.ClassLibMultiProjectPath} --framework net8.0");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("(net8.0)", output);
    }

    [Fact]
    public void PrintBadFile()
    {
        var dir = Root.NewDirectory(Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "example.proj");
        var (exitCode, _) = RunCompLogEx($"print {file}");
        Assert.Equal(Constants.ExitFailure, exitCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void PrintDiffMetadata(int metadataVersion)
    {
        var dir = Root.NewDirectory("metadata");
        var filePath = Path.Combine(dir, "old.complog");
        Create();

        var (exitCode, output) = RunCompLogEx($"print {filePath}");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("supported", output);

        void Create()
        {
            using var binlogStream = new FileStream(Fixture.ConsoleWithDiagnosticsBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var complogStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var result = CompilerLogUtil.TryConvertBinaryLog(binlogStream, complogStream, predicate: null, metadataVersion: metadataVersion);
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

    [Fact]
    public void PrintZipWithComplog()
    {
        var dir = Root.NewDirectory("empty");
        var zipFilePath = Path.Combine(dir, "example.zip");
        var exitCode = RunCompLog($"create -p {Fixture.ConsoleProjectName} {Fixture.SolutionBinaryLogPath} -o build.complog", RootDirectory);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        CreateZip();
        exitCode = RunCompLog($"print {zipFilePath}", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);

        void CreateZip()
        {
            using var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create);
            zipArchive.CreateEntryFromFile(Path.Combine(RootDirectory, "build.complog"), "build.complog");
        }
    }

    [Fact]
    public void PrintZipWithBinlog()
    {
        var dir = Root.NewDirectory("empty");
        var zipFilePath = Path.Combine(dir, "example.zip");
        CreateZip();
        var exitCode = RunCompLog($"print {zipFilePath}", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);

        void CreateZip()
        {
            using var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create);
            zipArchive.CreateEntryFromFile(Fixture.SolutionBinaryLogPath, "build.binlog");
        }
    }


}
#endif
