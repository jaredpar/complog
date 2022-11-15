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
    internal readonly static Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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
        var result = DotnetUtil.Command(command, workingDirectory);
        TestOutputHelper.WriteLine(result.StandardOut);
        TestOutputHelper.WriteLine(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }
}
