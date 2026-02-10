#if NET
using Basic.CompilerLog.App;
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
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilerLogAppTests : TestBase, IClassFixture<CompilerLogAppTests.CompilerLogAppTestsFixture>
{
    public sealed class CompilerLogAppTestsFixture: FixtureBase, IDisposable
    {
        private TempDir Storage { get; } = new TempDir();

        public string StorageDirectory => Storage.DirectoryPath;
        public string BinlogDirectory { get; }
        public Lazy<(string ProjectFilePath, string BinaryLogPath)> RemovedConsoleProject { get; }
        public Lazy<(string ProjectFilePath, string BinaryLogPath)> ConsoleWithDiagnostics { get; }

        public CompilerLogAppTestsFixture(IMessageSink messageSink)
            : base(messageSink)
        {
            BinlogDirectory = Storage.NewDirectory("binlogs");
            RemovedConsoleProject = new Lazy<(string, string)>(() => CreateRemovedProject());
            ConsoleWithDiagnostics = new Lazy<(string ProjectFilePath, string BinaryLogPath)>(() => CreateConsoleWithDiagnosticsProject());

            (string, string) CreateRemovedProject()
            {
                var dir = Path.Combine(StorageDirectory, "removed");
                Directory.CreateDirectory(dir);
                RunDotnetCommand("new console --name removed-console -o .", dir);
                var projectPath = Path.Combine(dir, "removed-console.csproj");
                var binlogFilePath = Path.Combine(BinlogDirectory, "removed-console.binlog");

                RunDotnetCommand($"build -bl:{binlogFilePath} -nr:false", dir);
                Directory.Delete(dir, recursive: true);
                return (projectPath, binlogFilePath);
            }

            (string, string) CreateConsoleWithDiagnosticsProject()
            {
                var dir = Path.Combine(StorageDirectory, "diagnostics");
                Directory.CreateDirectory(dir);
                RunDotnetCommand("new console --name console-with-diagnostics -o .", dir);
                File.WriteAllText(Path.Combine(dir, "Diagnostic.cs"), """
                    using System;
                    class C
                    {
                        void Method1()
                        {
                            // Warning CS0219
                            int i = 42;
                        }

                        void Method2()
                        {
                            // Error CS0029
                            string s = 13;
                            Console.WriteLine(s);
                        }
                    }
                    """, TestBase.DefaultEncoding);
                var projectPath = Path.Combine(dir, "console-with-diagnostics.csproj");
                var binlogFilePath = Path.Combine(BinlogDirectory, "console-with-diagnostics.binlog");
                var result = DotnetUtil.Command($"dotnet build -bl:{binlogFilePath} -nr:false", dir);
                Assert.False(result.Succeeded);
                return (projectPath, binlogFilePath);
            };
        }

        public void Dispose()
        {
            Storage.Dispose();
        }
    }

    private Action<ICompilerCallReader>? _assertCompilerCallReader;

    public CompilerLogAppTestsFixture ClassFixture { get; }
    public CompilerLogFixture Fixture { get; }

    public CompilerLogAppTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture, CompilerLogAppTestsFixture classFixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogAppTests))
    {
        ClassFixture = classFixture;
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
        currentDirectory ??= RootDirectory;
        var appDataDirectory = Path.Combine(currentDirectory, "localappdata");
        var writer = new System.IO.StringWriter();
        var app = new CompilerLogApp(
            currentDirectory,
            appDataDirectory,
            writer,
            OnCompilerCallReader);
        var ret = app.Run(TestUtil.ParseCommandLine(args).ToArray());
        if (Directory.Exists(appDataDirectory))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(appDataDirectory));
        }

        var output = writer.ToString();
        TestOutputHelper.WriteLine(output);
        return (ret, output);
    }

    private void RunWithBoth(LogData logData, Action<string> action)
    {
        Assert.NotNull(logData.BinaryLogPath);
        action(logData.BinaryLogPath);
        action(logData.CompilerLogPath);
    }

    [Fact]
    public void AnalyzersBoth()
    {
        RunWithBoth(Fixture.Console.Value, void (string logPath) =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logPath));
            var (exitCode, output) = RunCompLogEx($@"analyzers ""{logPath}""");
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
        var (exitCode, output) = RunCompLogEx($"analyzers {ClassFixture.RemovedConsoleProject.Value.BinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("CSharp.NetAnalyzers.dll", output);
    }

    [Fact]
    public void AnalyzersBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"analyzers {ClassFixture.RemovedConsoleProject.Value.BinaryLogPath} --not-an-option");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("Extra arguments", output);
    }

    [Fact]
    public void AnalyzersSimple()
    {
        var (exitCode, output) = RunCompLogEx($@"analyzers ""{Fixture.Console.Value.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("Analyzers:", output);
        Assert.DoesNotContain("Generators:", output);

        (exitCode, output) = RunCompLogEx($@"analyzers ""{Fixture.Console.Value.BinaryLogPath}"" -t");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Analyzers:", output);
        Assert.Contains("Generators:", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnalyzersPath(bool includePath)
    {
        var projectFilePath = Fixture.GetWritableCopy(Fixture.Console.Value, Root);
        var arg = includePath ? "--path" : "";
        var (exitCode, output) = RunCompLogEx($@"analyzers ""{projectFilePath}"" {arg}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Microsoft.CodeAnalysis.NetAnalyzers.dll", output);
        if (includePath)
        {
            Assert.Contains($"{Path.DirectorySeparatorChar}Microsoft.CodeAnalysis.NetAnalyzers.dll", output);
        }
        else
        {
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}Microsoft.CodeAnalysis.NetAnalyzers.dll", output);
        }
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
        var logData = Fixture.ClassLibMulti.Value;
        Assert.Equal(Constants.ExitSuccess, RunCompLog($@"create {extra} -f {TestUtil.TestTargetFramework} -p {Path.GetFileName(logData.ProjectFilePath)} ""{logData.BinaryLogPath}"""));
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

    /// <summary>
    /// Verify that create will work given a project file and do an implicit build to get the
    /// compiler log information.
    /// </summary>
    [Fact]
    public void CreateWithBuild()
    {
        RunCore(Fixture.Console.Value);
        RunCore(Fixture.ClassLib.Value);
        void RunCore(LogData logData)
        {
            var projectFilePath = Fixture.GetWritableCopy(logData, Root);
            var directory = Path.GetDirectoryName(projectFilePath)!;
            var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
            Assert.Equal(Constants.ExitSuccess, RunCompLog($@"create ""{projectFilePath}"" -o ""{complogPath}"""));
            var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
            Assert.NotEmpty(reader.ReadAllCompilerCalls());
            reader.Dispose();
            File.Delete(complogPath);
        }
    }

    [Fact]
    public void CreateFromResponseFile()
    {
        Core(Fixture.Console.Value, true);
        Core(Fixture.ConsoleVisualBasic.Value, false);
        void Core(LogData logData, bool isCSharp)
        {
            using var tempDir = new TempDir($"rsp-{(isCSharp ? "cs" : "vb")}");
            TestUtil.CopyDirectory(Path.GetDirectoryName(logData.ProjectFilePath)!, tempDir.DirectoryPath);
            var rspPath = WriteResponseFile(Fixture.Console.Value, tempDir);
            var complogPath = Path.Combine(tempDir.DirectoryPath, "rsp.complog");
            var (exitCode, output) = RunCompLogEx($@"create ""{rspPath}"" -o ""{complogPath}""");
            Assert.Equal(Constants.ExitSuccess, exitCode);
            Assert.Contains("Wrote", output);

            using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
            var compilerCall = Assert.Single(reader.ReadAllCompilerCalls());
            Assert.Equal(isCSharp, compilerCall.IsCSharp);
        }

        static string WriteResponseFile(LogData logData, TempDir tempDir)
        {
            using var reader = CompilerLogReader.Create(logData.CompilerLogPath, BasicAnalyzerKind.None);
            var compilerCall = Assert.Single(reader.ReadAllCompilerCalls());
            var exportUtil = new ExportUtil(reader);
            return exportUtil.Export(compilerCall, tempDir.DirectoryPath, SdkUtil.GetSdkCompilerDirectories().Take(1).ToList());
        }
    }

    [WindowsFact]
    public void CreateCapturesWpfTemporaryCompile()
    {
        Debug.Assert(Fixture.WpfApp is not null);
        var logData = Fixture.WpfApp.Value;

        // Create a compiler log from the solution binary log that contains the WPF project
        var complogPath = Path.Combine(RootDirectory, "wpf-test.complog");
        var (exitCode, output) = RunCompLogEx($@"create ""{logData.BinaryLogPath}"" -o {complogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.True(File.Exists(complogPath));

        // Verify that the created compiler log contains WPF temporary compile
        using var reader = CompilerLogReader.Create(complogPath);
        var compilerCalls = reader.ReadAllCompilerCalls();

        // Should have WPF-related compiler calls (WpfTemporaryCompile should be captured by default)
        var wpfTempCompile = compilerCalls.FirstOrDefault(c => c.Kind == CompilerCallKind.WpfTemporaryCompile);
        Assert.NotNull(wpfTempCompile);

        // Should also have the regular compile for the WPF project
        var regularCompile = compilerCalls.FirstOrDefault(c => c.Kind == CompilerCallKind.Regular && c.ProjectFilePath == logData.ProjectFilePath);
        Assert.NotNull(regularCompile);
    }

    /// <summary>
    /// When the resulting compiler log is empty an error should be returned cause clearly
    /// there was a mistake somewhere on the command line.
    /// </summary>
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void CreateEmpty(bool complog)
    {
        var logData = Fixture.Console.Value;
        var logFilePath = complog ? logData.CompilerLogPath : logData.BinaryLogPath;
        Assert.NotNull(logFilePath);
        var result = RunCompLog($"create -p does-not-exist.csproj {logFilePath}");
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
        var (exitCode, output) = RunCompLogEx($"create {ClassFixture.RemovedConsoleProject.Value.BinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains(RoslynUtil.GetDiagnosticMissingFile(""), output);
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
        Assert.Equal(Constants.ExitFailure, RunCompLog($"create {Fixture.Console.Value.BinaryLogPath} extra"));
    }

    [Fact]
    public void CreateFilePathOutput()
    {
        var projectFilePath = Fixture.GetWritableCopy(Fixture.ClassLib.Value, Root);
        var complogFilePath = Path.Combine(Root.DirectoryPath, "file.complog");
        var (exitCode, output) = RunCompLogEx($@"create ""{projectFilePath}"" -o {complogFilePath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains($"Wrote {complogFilePath}", output);
    }

    [Fact]
    public void CreateMultipleFiles()
    {
        File.Copy(ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath, Path.Combine(RootDirectory, "console1.binlog"));
        File.Copy(ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath, Path.Combine(RootDirectory, "console2.binlog"));
        var (exitCode, output) = RunCompLogEx($"create");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains($"Found multiple log files in {RootDirectory}", output);
    }

    [Fact]
    public void HashBadCommand()
    {
        var (exitCode, output) = RunCompLogEx($"hash move {Fixture.Console.Value.BinaryLogPath}");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains("move is not a valid command", output);
    }

    [Fact]
    public void HashNoCommand()
    {
        var (exitCode, output) = RunCompLogEx("hash");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains("Need a subcommand", output);
    }

    [Fact]
    public void HashHelp()
    {
        var (exitCode, output) = RunCompLogEx("hash help");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("complog hash [command] [args]", output);
    }

    [Fact]
    public void HashPrintSimple()
    {
        var logData = Fixture.Console.Value;
        Assert.NotNull(logData.BinaryLogPath);
        AddContentHashToTestArtifacts();

        var (exitCode, output) = RunCompLogEx($@"hash print ""{logData.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Matches($"console [0-9A-F]+", output);

        // Save the full content to test artifacts so we can compare it to what is
        // seen locally.
        void AddContentHashToTestArtifacts()
        {
            var reader = CompilerLogReader.Create(logData.BinaryLogPath, BasicAnalyzerKind.None);
            var compilerCall = reader.ReadAllCompilerCalls().Single();
            var compilationData = reader.ReadCompilationData(compilerCall);
            var contentHash = compilationData.GetContentHash();
            AddContentToTestArtifacts("console-hash.txt", contentHash);
        }
    }

    [Fact]
    public void HashPrintAll()
    {
        var logData = Fixture.ClassLibMulti.Value;
        Assert.NotNull(logData.BinaryLogPath);

        AddContentHashToTestArtifacts();
        var (exitCode, output) = RunCompLogEx($@"hash print ""{logData.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Matches($"classlibmulti-{TestUtil.TestTargetFramework} [0-9A-F]+", output);

        // Save the full content to test artifacts so we can compare it to what is
        // seen locally.
        void AddContentHashToTestArtifacts()
        {
            var reader = CompilerLogReader.Create(logData.BinaryLogPath, BasicAnalyzerKind.None);
            foreach (var compilerCall in reader.ReadAllCompilerCalls())
            {
                var compilationData = reader.ReadCompilationData(compilerCall);
                var contentHash = compilationData.GetContentHash();
                AddContentToTestArtifacts($"{compilerCall.GetDiagnosticName()}.txt", contentHash);
            }
        }
    }

    [Fact]
    public void HashPrintHelp()
    {
        var (exitCode, output) = RunCompLogEx("hash print -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("complog hash print [OPTIONS]", output);
    }

    [Fact]
    public void HashPrintBadOption()
    {
        var (exitCode, output) = RunCompLogEx("hash print --does-not-exist");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains("complog hash print [OPTIONS]", output);
    }

    [Fact]
    public void HashExportInline()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);
        var (exitCode, output) = RunCompLogEx($"hash export -i", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Generating hash files inline", output);

        var identityFilePath = Path.Combine(dir, "build-identity-hash.txt");
        var contentFilePath = Path.Combine(dir, "build-content-hash.txt");
        AddFileToTestArtifacts(contentFilePath);

        Assert.True(File.Exists(identityFilePath));
        Assert.True(File.Exists(contentFilePath));
        var actualContentHash = File.ReadAllText(contentFilePath);
        Assert.Contains(@"""outputKind"": ""ConsoleApplication""", actualContentHash);
        Assert.Contains(@"""moduleName"": ""example.dll""", actualContentHash);
    }

    [Fact]
    public void HashExportFull()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);

        var outDir = Root.NewDirectory();
        var (exitCode, output) = RunCompLogEx($"hash export -o {outDir} ", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);

        var identityFilePath = Path.Combine(outDir, "example", "build-identity-hash.txt");
        Assert.True(File.Exists(identityFilePath));
        var contentFilePath = Path.Combine(outDir, "example", "build-content-hash.txt");
        Assert.True(File.Exists(identityFilePath));
        Assert.Contains("""
                    "outputKind": "ConsoleApplication",
                    "moduleName": "example.dll",
              """, File.ReadAllText(contentFilePath));
    }

    [Fact]
    public void HashExportBadOption()
    {
        var (exitCode, output) = RunCompLogEx("hash export --does-not-exist");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains("complog hash export [OPTIONS]", output);
    }

    [Fact]
    public void HashExportInlineAndOutput()
    {
        var (exitCode, output) = RunCompLogEx("hash export --inline --out example");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains("complog hash export [OPTIONS]", output);
    }

    [Fact]
    public void HashExportHelp()
    {
        var (exitCode, output) = RunCompLogEx("hash export -h");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("complog hash export [OPTIONS]", output);
    }

    [Fact]
    public void HashInlineAndOutput()
    {
        var dir = Root.NewDirectory();
        var (exitCode, output) = RunCompLogEx($"id -i -o blah");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
    }

    [Fact]
    public void HashBadOption()
    {
        var (exitCode, output) = RunCompLogEx($"id -blah");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
    }

    [Fact]
    public void References()
    {
        RunWithBoth(Fixture.Console.Value, logPath =>
        {
            AssertCompilerCallReader(reader => AssertCorrectReader(reader, logPath));
            Assert.Equal(Constants.ExitSuccess, RunCompLog($@"ref -o {RootDirectory} ""{logPath}"""));
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
        var exitCode = RunCompLog($@"rsp ""{Fixture.Console.Value.BinaryLogPath}"" -s");
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
        var exitCode = RunCompLog($@"rsp ""{Fixture.Console.Value.BinaryLogPath}"" -o {dir}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var rsp = Path.Combine(dir, "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseProjectFilter()
    {
        var exitCode = RunCompLog($@"rsp ""{Fixture.Console.Value.BinaryLogPath}""");
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
        var logData = Fixture.ClassLibMulti.Value;
        Assert.NotNull(logData.BinaryLogPath);

        var complogPath = Path.Combine(RootDirectory, "msbuild.complog");
        Assert.Empty(CompilerLogUtil.ConvertBinaryLog(logData.BinaryLogPath, complogPath));
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"rsp {complogPath}"));
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
        var exitCode = RunCompLog($@"rsp ""{Fixture.Console.Value.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        var rsp = Path.Combine(RootDirectory, @".complog", "rsp", "console", "build.rsp");
        Assert.True(File.Exists(rsp));
        Assert.Contains("Program.cs", File.ReadAllLines(rsp));
    }

    [Fact]
    public void ResponseMultiTarget()
    {
        var exitCode = RunCompLog($@"rsp ""{Fixture.ClassLibMulti.Value.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.True(File.Exists(Path.Combine(RootDirectory, ".complog", "rsp", "classlibmulti-net6.0", "build.rsp")));
        Assert.True(File.Exists(Path.Combine(RootDirectory, ".complog", "rsp", $"classlibmulti-{TestUtil.TestTargetFramework}", "build.rsp")));
    }

    [Fact]
    public void ResponseNoLogArgument()
    {
        var consoleDir = Path.GetDirectoryName(Fixture.Console.Value.ProjectFilePath)!;
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
        var logData = Fixture.ClassLibMulti.Value;
        var dir = Root.CopyDirectory(Path.GetDirectoryName(logData.ProjectFilePath)!);
        var fileName = Path.GetFileName(logData.ProjectFilePath);
        var (exitCode, output) = RunCompLogEx($"rsp -i {fileName}", dir);
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("Generating response files inline", output);
        Assert.True(File.Exists(Path.Combine(dir, "build-net6.0.rsp")));
        Assert.True(File.Exists(Path.Combine(dir, $"build-{TestUtil.TestTargetFramework}.rsp")));
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
    [InlineData("", false)]
    [InlineData("-n", true)]
    [InlineData("--no-analyzers", true)]
    public void ExportCompilerLog(string arg, bool excludeAnalyzers)
    {
        RunWithBoth(Fixture.Console.Value, logPath =>
        {
            using var exportDir = new TempDir();

            AssertCompilerCallReader(reader => Assert.Equal(BasicAnalyzerKind.None, reader.BasicAnalyzerKind));
            Assert.Equal(Constants.ExitSuccess, RunCompLog($@"export -o {exportDir.DirectoryPath} {arg} ""{logPath}"" ", RootDirectory));

            // Now run the generated build.cmd and see if it succeeds;
            var exportPath = Path.Combine(exportDir.DirectoryPath, "console");
            var buildResult = TestUtil.RunBuildCmd(exportPath);
            Assert.True(buildResult.Succeeded);

            // Check that the RSP file matches the analyzer intent
            var rspPath = Path.Combine(exportPath, "build.rsp");
            var anyAnalyzers = File.ReadAllLines(rspPath)
                .Any(x => x.StartsWith("/analyzer:", StringComparison.Ordinal));
            Assert.Equal(!excludeAnalyzers, anyAnalyzers);
        });
    }

    [WindowsFact]
    public void ExportCompilerLogVs()
    {
        RunWithBoth(Fixture.Console.Value, logPath =>
        {
            using var exportDir = new TempDir();
            var (exitCode, output) = RunCompLogEx($@"export --vs -o {exportDir.DirectoryPath} ""{logPath}""");

            var compilers = VisualStudioUtil.GetInstallations();
            if (compilers.Count == 0)
            {
                Assert.Equal(Constants.ExitFailure, exitCode);
                Assert.Contains("No Visual Studio installations with csc.exe were found.", output);
                return;
            }

            Assert.Equal(Constants.ExitSuccess, exitCode);

            var exportPath = Path.Combine(exportDir.DirectoryPath, "console");
            var buildScripts = Directory.EnumerateFiles(exportPath, "build*.cmd").ToList();
            Assert.NotEmpty(buildScripts);

            var buildResult = TestUtil.RunBuildCmd(exportPath);
            TestOutputHelper.WriteLine(buildResult.StandardOut);
            TestOutputHelper.WriteLine(buildResult.StandardError);
            Assert.True(buildResult.Succeeded);
        });
    }

    [UnixFact]
    public void ExportCompilerLogVsUnsupported()
    {
        var logPath = Fixture.Console.Value.CompilerLogPath;
        var (exitCode, output) = RunCompLogEx($@"export --vs ""{logPath}""");
        Assert.Equal(Constants.ExitFailure, exitCode);
        Assert.Contains("The --vs option is only supported on Windows.", output);
    }

    /// <summary>
    /// The --all option should force all the compilations, including satellite ones, to be exported.
    /// </summary>
    [Fact]
    public void ExportSatellite()
    {
        using var exportDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($@"export -o {exportDir.DirectoryPath} --all ""{Fixture.ClassLibWithResourceLibs.Value.BinaryLogPath}""", RootDirectory));
        Assert.Equal(3, Directory.EnumerateDirectories(exportDir.DirectoryPath).Count());
    }

    /// <summary>
    /// Lacking the --all option only the core assemblies should be generated
    /// </summary>
    [Fact]
    public void ExportSatelliteDefault()
    {
        using var exportDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($@"export -o {exportDir.DirectoryPath} ""{Fixture.ClassLibWithResourceLibs.Value.BinaryLogPath}""", RootDirectory));
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

    [Fact]
    public void ExportBadProjectFilter()
    {
        var tuple = ClassFixture.ConsoleWithDiagnostics.Value;
        var (exitCode, output) = RunCompLogEx($"export -p does-not-exist.csproj {tuple.BinaryLogPath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("No compilations found", output);
    }

    [Fact]
    public void ExportEmptyLog()
    {
        var logFilePath = CreateEmptyLog(RootDirectory);
        var (exitCode, output) = RunCompLogEx($"export {logFilePath}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("No compilations found in the log", output);

        static string CreateEmptyLog(string dir)
        {
            var logFilePath = Path.Combine(dir, "empty.complog");
            using var fileStream = new FileStream(logFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var builder = new CompilerLogBuilder(fileStream, []);
            builder.Close();
            return logFilePath;
        }
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
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"{command} {arg} {Fixture.Console.Value.CompilerLogPath}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--none")]
    public void ReplayConsoleWithEmit(string arg)
    {
        using var emitDir = new TempDir();
        Assert.Equal(Constants.ExitSuccess, RunCompLog($@"replay {arg} -o {emitDir.DirectoryPath} ""{Fixture.Console.Value.BinaryLogPath}"""));

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
    public void ReplayWithBadProjectFilter()
    {
        using var emitDir = new TempDir();
        var (extiCode, output) = RunCompLogEx($@"replay -p does-not-exist.csproj --severity Info ""{ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, extiCode);
        Assert.Contains("No compilations found", output);
    }

    [Fact]
    public void ReplayWithDiagnostics()
    {
        using var emitDir = new TempDir();
        var (exitCode, output) = RunCompLogEx($@"replay --severity Info ""{ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath}""");
        Assert.Equal(Constants.ExitFailure, exitCode);
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
                Fixture.Console.Value.BinaryLogPath!,
                logFilePath);
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
        RunWithBoth(Fixture.Console.Value, void (string logFilePath) =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logFilePath));
            RunCompLog($"replay {logFilePath}");
        });
    }

    /// <summary>
    /// When replaying with a project file, the binary log reader should be used
    /// </summary>
    [Fact]
    public void ReplayWithProject()
    {
        AssertCompilerCallReader(void (ICompilerCallReader reader) => Assert.IsType<BinaryLogReader>(reader));
        Assert.Equal(Constants.ExitSuccess, RunCompLog($@"replay ""{Fixture.Console.Value.ProjectFilePath}"""));
    }

    [Theory]
    [MemberData(nameof(GetCustomCompilerArgument))]
    public void ReplayConsoleCustomCompiler(string customCompilerArgument, bool isOlderCompiler)
    {
        var exitCode = RunCompLog($@"replay {customCompilerArgument} ""{Fixture.Console.Value.BinaryLogPath}""");
        if (exitCode != Constants.ExitSuccess)
        {
            Assert.True(isOlderCompiler);
        }
    }

    [Fact]
    public void ReplayWithCustomCompilerInvalid()
    {
        var exitCode = RunCompLog($"replay --compiler \"{Root.DirectoryPath}\" {Fixture.Console.Value.BinaryLogPath}");
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
    }

    [Theory]
    [MemberData(nameof(GetBasicAnalyzerKinds))]
    public void GeneratedBoth(BasicAnalyzerKind basicAnalyzerKind)
    {
        RunWithBoth(Fixture.Console.Value, logPath =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logPath));
            var dir = Root.NewDirectory("generated");
            var (exitCode, output) = RunCompLogEx($@"generated ""{logPath}"" -a {basicAnalyzerKind} -o {dir}");
            Assert.Equal(Constants.ExitSuccess, exitCode);
            Assert.Single(Directory.EnumerateFiles(dir, "RegexGenerator.g.cs", SearchOption.AllDirectories));
            Directory.Delete(dir, recursive: true);
        });
    }

    [Theory]
    [MemberData(nameof(GetCustomCompilerArgument))]
    public void GeneratedCustomCompilers(string customCompilerArgument, bool isOlderCompiler)
    {
        RunWithBoth(Fixture.Console.Value, logPath =>
        {
            var dir = Root.NewDirectory("generated");
            var (exitCode, output) = RunCompLogEx($@"generated ""{logPath}"" -o {dir} {customCompilerArgument}");
            if (exitCode != Constants.ExitSuccess)
            {
                Assert.True(isOlderCompiler);
            }
            else
            {
                Assert.Single(Directory.EnumerateFiles(dir, "RegexGenerator.g.cs", SearchOption.AllDirectories));
            }
            Directory.Delete(dir, recursive: true);
        });
    }

    /// <summary>
    /// Verify the behavior when an invalid project filter is specified
    /// </summary>
    [Fact]
    public void GeneratedBadFilter()
    {
        RunWithBoth(Fixture.Console.Value, logPath =>
        {
            AssertCompilerCallReader(void (ICompilerCallReader reader) => AssertCorrectReader(reader, logPath));
            var (exitCode, output) = RunCompLogEx($@"generated ""{logPath}"" -p console-does-not-exist.csproj");
            Assert.Equal(Constants.ExitSuccess, exitCode);
            Assert.Contains("No compilations found", output);
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
        Assert.NotEqual(Constants.ExitSuccess, exitCode);
        Assert.Contains(RoslynUtil.ErrorReadingGeneratedFilesDiagnosticDescriptor.Id, output);
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
        var (exitCode, output) = RunCompLogEx($@"print ""{Fixture.ClassLibMulti.Value.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains($"classlibmulti.csproj ({TestUtil.TestTargetFramework})", output);
        Assert.Contains($"classlibmulti.csproj ({TestUtil.TestTargetFramework})", output);
    }

    [Fact]
    public void PrintOne()
    {
        var logData = Fixture.ClassLibMulti.Value;
        var projectFileName = Path.GetFileName(logData.ProjectFilePath);
        var (exitCode, output) = RunCompLogEx($@"print ""{logData.BinaryLogPath}"" -f {TestUtil.TestTargetFramework}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain($"{projectFileName} (net6.0)", output);
        Assert.Contains($"{projectFileName} ({TestUtil.TestTargetFramework})", output);
    }

    [Fact]
    public void PrintCompilers()
    {
        var (exitCode, output) = RunCompLogEx($@"print ""{Fixture.Console.Value.BinaryLogPath}"" -c");
        Assert.Equal(Constants.ExitSuccess, exitCode);

        using var reader = CompilerLogReader.Create(ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath);
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
        var (exitCode, output) = RunCompLogEx($"print {ClassFixture.RemovedConsoleProject.Value.BinaryLogPath} -c");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.StartsWith("Projects", output);
    }

    /// <summary>
    /// Engage the code to find files in the specified directory
    /// </summary>
    [Fact]
    public void PrintDirectory()
    {
        var filePath = Root.CopyFile(Fixture.Console.Value.BinaryLogPath!);
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"print {filePath}"));
    }

    /// <summary>
    /// Engage the code to find files in the specified directory.Make sure that it works
    /// on builds that fail.
    /// </summary>
    [Fact]
    public void PrintDirectoryBadProject()
    {
        var dir = Path.GetDirectoryName(ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath);
        Assert.Equal(Constants.ExitSuccess, RunCompLog($"print {dir}"));
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
        Debug.Assert(Fixture.WpfApp is not null);
        var logData = Fixture.WpfApp.Value;

        var (exitCode, output) = RunCompLogEx($@"print --all ""{logData.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains("WpfTemporaryCompile", output);

        (exitCode, output) = RunCompLogEx($@"print ""{logData.BinaryLogPath}""");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.DoesNotContain("WpfTemporaryCompile", output);
    }

    [Fact]
    public void PrintFrameworks()
    {
        var (exitCode, output) = RunCompLogEx($@"print --all ""{Fixture.ClassLibMulti.Value.BinaryLogPath}"" --framework {TestUtil.TestTargetFramework}");
        Assert.Equal(Constants.ExitSuccess, exitCode);
        Assert.Contains($"({TestUtil.TestTargetFramework})", output);
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
            using var binlogStream = new FileStream(ClassFixture.ConsoleWithDiagnostics.Value.BinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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
        var exitCode = RunCompLog($@"create ""{Fixture.Console.Value.BinaryLogPath}"" -o build.complog", RootDirectory);
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
            zipArchive.CreateEntryFromFile(Fixture.Console.Value.BinaryLogPath!, "build.binlog");
        }
    }
}

#endif
