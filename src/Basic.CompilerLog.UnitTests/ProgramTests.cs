#if NETCOREAPP
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.Build.Logging.StructuredLogger;
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
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(SolutionFixtureCollection.Name)]
public sealed class ProgramTests : TestBase
{
    private Action<ICompilerCallReader>? _assertCompilerCallReader;

    public SolutionFixture Fixture { get; }

    public ProgramTests(ITestOutputHelper testOutputHelper, SolutionFixture fixture) 
        : base(testOutputHelper, nameof(ProgramTests))
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
        try
        {
            var writer = new System.IO.StringWriter();
            currentDirectory ??= RootDirectory;
            Constants.CurrentDirectory = currentDirectory;
            Constants.Out = writer;
            Constants.OnCompilerCallReader = OnCompilerCallReader;
            var assembly = typeof(FilterOptionSet).Assembly;
            var program = assembly.GetType("Program", throwOnError: true);
            var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(main);
            var ret = main!.Invoke(null, new[] { args.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
            return ((int)ret!, writer.ToString());
        }
        finally
        {
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
        var rsp = Path.Combine(RootDirectory, @".complog", "rsp", "console", "build.rsp");
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
        Assert.True(File.Exists(Path.Combine(RootDirectory, @".complog", "rsp", "classlibmulti-net7.0", "build.rsp")));
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
            var buildResult = RunBuildCmd(exportPath);
            Assert.True(buildResult.Succeeded);

            // Check that the RSP file matches the analyzer intent
            var rspPath = Path.Combine(exportPath, "build.rsp");
            var anyAnalyzers = File.ReadAllLines(rspPath)
                .Any(x => x.StartsWith("/analyzer:", StringComparison.Ordinal));
            var expectAnalyzers = expectedKind.Value != BasicAnalyzerKind.None;
            Assert.Equal(expectAnalyzers, anyAnalyzers);
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
            MutateArchive(logFilePath);
            return logFilePath;
        }

        static void MutateArchive(string complogFilePath)
        {
            using var fileStream = new FileStream(complogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: true);
            using var entryStream = zipArchive.OpenEntryOrThrow(CommonUtil.GetCompilerEntryName(0));
            var infoPack = MessagePackSerializer.Deserialize<CompilationInfoPack>(entryStream, CommonUtil.SerializerOptions);
            infoPack.CompilerAssemblyName = Regex.Replace(
                infoPack.CompilerAssemblyName!,
                @"\d+\.\d+\.\d+\.\d+",
                "99.99.99.99");
            entryStream.Position = 0;
            MessagePackSerializer.Serialize(entryStream, infoPack, CommonUtil.SerializerOptions);
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
    [CombinatorialData]
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

    [Fact]
    public void PrintCompilers()
    {
        var (exitCode, output) = RunCompLogEx($"print {Fixture.SolutionBinaryLogPath} -c");
        Assert.Equal(Constants.ExitSuccess, exitCode);

        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithDiagnosticsBinaryLogPath);
        var tuple = reader.ReadAllCompilerAssemblies().Single();
        Assert.Contains($"""
            Compilers
            {tuple.CompilerFilePath}
            {tuple.AssemblyName}
            """, output);
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
}
#endif
