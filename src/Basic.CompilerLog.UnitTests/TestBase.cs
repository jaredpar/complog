using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Linq;
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
    internal string RootDirectory => Root.DirectoryPath;

    protected TestBase(ITestOutputHelper testOutputHelper, string name)
    {
        TestOutputHelper = testOutputHelper;
        Root = new TempDir(name);
    }

    public void Dispose()
    {
        Root.Dispose();
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

    protected CompilerLogReader GetReader(bool emptyDirectory = true, string? cryptoKeyFileDirectory = null)
    {
        var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"), cryptoKeyFileDirectory);
        if (emptyDirectory)
        {
            Root.EmptyDirectory();
        }

        return reader;
    }
}
