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

#if NET
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
    [InlineData("csc a.cs b.cs", "csc", "a.cs b.cs")]
    [InlineData("/path/to/csc a.cs b.cs", "/path/to/csc", "a.cs b.cs")]
    public void ParseCompilerAndArgumentsCsc(string inputArgs, string? expectedCompilerFilePath, string expectedArgs)
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "csc");
        Assert.Equal(ToArray(expectedArgs), actualArgs);
        Assert.Equal(expectedCompilerFilePath, actualCompilerFilePath);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData(@"  C:\Program Files\dotnet\dotnet.exe exec ""C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll"" a.cs", @"C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll", "a.cs")] 
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe exec ""C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll"" a.cs", @"C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll", "a.cs")] 
    [InlineData(@"""C:\Program Files\dotnet\dotnet.exe"" exec ""C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll"" a.cs", @"C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll", "a.cs")] 
    [InlineData(@"'C:\Program Files\dotnet\dotnet.exe' exec ""C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll"" a.cs", @"C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll", "a.cs")] 
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe a.cs b.cs", @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe", "a.cs b.cs")]
    [InlineData(@"""C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"" a.cs b.cs", @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe", "a.cs b.cs")]
    public void ParseCompilerAndArgumentsCscWindows(string inputArgs, string? expectedCompilerFilePath, string expectedArgs)
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "csc");
        Assert.Equal(ToArray(expectedArgs), actualArgs);
        Assert.Equal(expectedCompilerFilePath, actualCompilerFilePath);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData(@"/dotnet/dotnet exec /dotnet/sdk/bincore/csc.dll a.cs", "/dotnet/sdk/bincore/csc.dll", "a.cs")] 
    [InlineData(@"/dotnet/dotnet exec ""/dotnet/sdk/bincore/csc.dll"" a.cs", "/dotnet/sdk/bincore/csc.dll", "a.cs")] 
    public void ParseCompilerAndArgumentsCscUnix(string inputArgs, string? expectedCompilerFilePath, string expectedArgs)
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "csc");
        Assert.Equal(ToArray(expectedArgs), actualArgs);
        Assert.Equal(expectedCompilerFilePath, actualCompilerFilePath);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData("dotnet.exe exec vbc.dll a.cs", "vbc.dll", "a.cs")]
    [InlineData("dotnet-can-be-any-host-name exec vbc.dll a.vb", "vbc.dll", "a.vb")]
    [InlineData("vbc.exe a.cs b.cs", "vbc.exe", "a.cs b.cs")]
    [InlineData("vbc a.vb b.vb", "vbc", "a.vb b.vb")]
    [InlineData("/path/to/vbc a.vb b.vb", "/path/to/vbc", "a.vb b.vb")]
    public void ParseCompilerAndArgumentsVbc(string inputArgs, string? expectedCompilerFilePath, string expectedArgs)
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "vbc");
        Assert.Equal(ToArray(expectedArgs), actualArgs);
        Assert.Equal(expectedCompilerFilePath, actualCompilerFilePath);
        static string[] ToArray(string arg) => arg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData("dotnet not what we expect a.cs")]
    [InlineData("dotnet csc2 what we expect a.cs")]
    [InlineData("dotnet exec vbc.dll what we expect a.cs")]
    [InlineData("empty")]
    [InlineData("   ")]
    public void ParseCompilerAndArgumentsBad(string inputArgs)
    {
        Assert.Throws<InvalidOperationException>(() => BinaryLogUtil.ParseTaskForCompilerAndArguments(inputArgs, "csc"));
    }

    [Fact]
    public void ParseCompilerAndArgumentsNull()
    {
        var (actualCompilerFilePath, actualArgs) = BinaryLogUtil.ParseTaskForCompilerAndArguments(null, "csc");
        Assert.Null(actualCompilerFilePath);
        Assert.Empty(actualArgs);
    }
}

public sealed class MSBuildProjectDataTests
{
    [Fact]
    public void MSBuildProjectDataToString()
    {
        var evalData = new BinaryLogUtil.MSBuildProjectEvaluationData(@"example.csproj");
        var data = new BinaryLogUtil.MSBuildProjectContextData(@"example.csproj", 100, 1);
        Assert.NotEmpty(data.ToString());
    }
}

public sealed class CompilationTaskDataTests
{
    internal BinaryLogUtil.MSBuildProjectEvaluationData EvaluationData { get; }
    internal BinaryLogUtil.MSBuildProjectContextData ContextData { get; }

    public CompilationTaskDataTests()
    {
        EvaluationData = new BinaryLogUtil.MSBuildProjectEvaluationData(@"example.csproj");
        ContextData = new(@"example.csproj", 100, 1);
    }

    [Fact]
    public void TryCreateCompilerCallBadArguments()
    {
        var data = new BinaryLogUtil.CompilationTaskData(1, 1, true)
        {
            CommandLineArguments = "dotnet not a compiler call",
        };

        Assert.Throws<InvalidOperationException>(() => data.TryCreateCompilerCall(ContextData.ProjectFile, null, CompilerCallKind.Unknown, ownerState: null));
    }

    [Fact]
    public void TryCreateCompilerNoArguments()
    {
        var data = new BinaryLogUtil.CompilationTaskData(1, 1, true)
        {
            CommandLineArguments = null,
        };

        Assert.Null(data.TryCreateCompilerCall(ContextData.ProjectFile, null, CompilerCallKind.Unknown, null));
    }
}