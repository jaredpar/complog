using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

public abstract class TestBase : IDisposable
{
    private static readonly object Guard = new();

    internal static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    internal ITestOutputHelper TestOutputHelper { get; }
    internal TempDir Root { get; }
    internal CompilerLogState State { get; }
    internal string RootDirectory => Root.DirectoryPath;

    protected TestBase(ITestOutputHelper testOutputHelper, string name)
    {
        TestOutputHelper = testOutputHelper;
        Root = new TempDir(name);
        State = new CompilerLogState(Root.NewDirectory("crypto-keys"));
    }

    public void Dispose()
    {
        TestOutputHelper.WriteLine("Deleting temp directory");
        Root.Dispose();
    }

    public CompilationData GetCompilationData(
        string complogFilePath,
        Func<CompilerCall, bool>? predicate = null,
        BasicAnalyzerHostOptions? options = null)
    {
        using var reader = CompilerLogReader.Create(complogFilePath, options, State);
        return reader.ReadAllCompilationData(predicate).Single();
    }

    protected void RunDotNet(string command, string? workingDirectory = null)
    {
        workingDirectory ??= RootDirectory;
        TestOutputHelper.WriteLine($"Working directory: {workingDirectory}");
        TestOutputHelper.WriteLine($"Executing: dotnet {command}");

        ProcessResult result;

        // There is a bug in the 7.0 SDK that causes an exception if multiple dotnet new commands
        // are run in parallel. This can happen with our tests. Temporarily guard against this 
        // with a lock
        // https://github.com/dotnet/sdk/pull/28677
        lock (Guard)
        {
            result = DotnetUtil.Command(command, workingDirectory);
        }

        TestOutputHelper.WriteLine(result.StandardOut);
        TestOutputHelper.WriteLine(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    protected void AddProjectProperty(string property, string? workingDirectory = null)
    {
        workingDirectory ??= RootDirectory;
        var projectFile = Directory.EnumerateFiles(workingDirectory, "*proj").Single();
        var lines = File.ReadAllLines(projectFile);
        using var writer = new StreamWriter(projectFile, append: false);
        foreach (var line in lines)
        {
            if (line.Contains("</PropertyGroup>"))
            {
                writer.WriteLine(property);
            }

            writer.WriteLine(line);
        }
    }

    protected string GetBinaryLogFullPath(string? workingDirectory = null) =>
        Path.Combine(workingDirectory ?? RootDirectory, "msbuild.binlog");

    protected CompilerLogReader GetReader(bool emptyDirectory = true )
    {
        var reader = CompilerLogReader.Create(GetBinaryLogFullPath());
        if (emptyDirectory)
        {
            Root.EmptyDirectory();
        }

        return reader;
    }

    /// <summary>
    /// Run the build.cmd / .sh generated from an export command
    /// </summary>
    internal static ProcessResult RunBuildCmd(string directory) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
         ? ProcessUtil.Run("cmd", args: "/c build.cmd", workingDirectory: directory)
         : ProcessUtil.Run(Path.Combine(directory, "build.sh"), args: "", workingDirectory: directory);
}
