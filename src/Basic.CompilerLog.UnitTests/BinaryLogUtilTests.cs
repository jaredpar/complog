using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public sealed class BinaryLogUtilTests
{
    [Theory]
    [InlineData("dotnet exec csc.dll a.cs", "a.cs")]
    [InlineData("dotnet not what we expect a.cs", "")]
    [InlineData("csc.exe a.cs b.cs", "a.cs b.cs")]
    public void SkipCompilerExecutableTests(string args, string expected)
    {
        var realArgs = BinaryLogUtil.SkipCompilerExecutable(ToArray(args), "csc.exe", "csc.dll");
        Assert.Equal(ToArray(expected), realArgs);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }
}

public sealed class MSBuildProjectDataTests
{
    [Fact]
    public void MSBuildProjectDataToString()
    {
        var data = new BinaryLogUtil.MSBuildProjectData(@"example.csproj");
        Assert.NotEmpty(data.ToString());
    }
}

public sealed class CompilationTaskDataTests
{
    internal BinaryLogUtil.MSBuildProjectData ProjectData { get; } = new BinaryLogUtil.MSBuildProjectData(@"example.csproj");

    [Fact]
    public void TryCreateCompilerCallBadArguments()
    {
        var data = new BinaryLogUtil.CompilationTaskData(ProjectData, 1)
        {
            CommandLineArguments = "dotnet not a compiler call",
        };

        var diagnostics = new List<string>();
        Assert.Null(data.TryCreateCompilerCall(diagnostics));
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void TryCreateCompilerNoArguments()
    {
        var data = new BinaryLogUtil.CompilationTaskData(ProjectData, 1)
        {
            CommandLineArguments = null,
        };

        var diagnostics = new List<string>();
        Assert.Null(data.TryCreateCompilerCall(diagnostics));

        // This is a normal non-compile case so no diagnostics are emitted
        Assert.Empty(diagnostics);
    }
}