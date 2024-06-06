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
    [InlineData("dotnet exec csc.dll a.cs", "csc.dll", "a.cs")]
    [InlineData("dotnet.exe exec csc.dll a.cs", "csc.dll", "a.cs")]
    [InlineData("dotnet-can-be-any-host-name exec csc.dll a.cs", "csc.dll", "a.cs")]
    [InlineData("csc.exe a.cs b.cs", "csc.exe", "a.cs b.cs")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe exec ""C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll"" a.cs", @"C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll", "a.cs")] 
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe a.cs b.cs", @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe", "a.cs b.cs")]
    public void ParseCompilerAndArgumentsCsc(string inputArgs, string? expectedCompilerFilePath, string expectedArgs)
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "csc.exe", "csc.dll");
        Assert.Equal(ToArray(expectedArgs), actualArgs);
        Assert.Equal(expectedCompilerFilePath, actualCompilerFilePath);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData("dotnet.exe exec vbc.dll a.cs", "vbc.dll", "a.cs")]
    [InlineData("dotnet-can-be-any-host-name exec vbc.dll a.vb", "vbc.dll", "a.vb")]
    [InlineData("vbc.exe a.cs b.cs", "vbc.exe", "a.cs b.cs")]
    public void ParseCompilerAndArgumentsVbc(string inputArgs, string? expectedCompilerFilePath, string expectedArgs)
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "vbc.exe", "vbc.dll");
        Assert.Equal(ToArray(expectedArgs), actualArgs);
        Assert.Equal(expectedCompilerFilePath, actualCompilerFilePath);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }


    [Theory]
    [InlineData("dotnet not what we expect a.cs")]
    [InlineData("dotnet csc2 what we expect a.cs")]
    [InlineData("dotnet exec vbc.dll what we expect a.cs")]
    public void ParseCompilerAndArgumentsBad(string inputArgs)
    {
        Assert.Throws<InvalidOperationException>(() => BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "csc.exe", "csc.dll"));
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

        Assert.Throws<InvalidOperationException>(() => data.TryCreateCompilerCall(ownerState: null));
    }

    [Fact]
    public void TryCreateCompilerNoArguments()
    {
        var data = new BinaryLogUtil.CompilationTaskData(ProjectData, 1)
        {
            CommandLineArguments = null,
        };

        Assert.Null(data.TryCreateCompilerCall(null));
    }
}