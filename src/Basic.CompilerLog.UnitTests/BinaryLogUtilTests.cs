using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Build.Framework;
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

public sealed class BinaryLogUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public BinaryLogUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture compilerLogFixture)
        : base(testOutputHelper, testContextAccessor, nameof(BinaryLogUtilTests))
    {
        Fixture = compilerLogFixture;
    }

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

    [WindowsTheory]
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

    [Fact]
    public void ReadAllCompilerCallsSingle()
    {
        using var stream = File.OpenRead(Fixture.Console.Value.BinaryLogPath!);
        Assert.Single(BinaryLogUtil.ReadAllCompilerCalls(stream));
    }

    [Theory]
    [InlineData(MessageImportance.Low, false)]
    [InlineData(MessageImportance.Normal, false)]
    [InlineData(MessageImportance.High, false)]
    [InlineData(MessageImportance.Low, true)]
    [InlineData(MessageImportance.Normal, true)]
    [InlineData(MessageImportance.High, true)]
    public void ReadMSBuildData(MessageImportance importance, bool quoted)
    {
        var quote = quoted ? "\"" : "";
        var data = ReadMSBuildDataFromEvents(
            Message("process = ignored"),
            Message(" Process = ignored"),
            Message("Unrelated message"),
            Message($"Process = {quote}dotnet host{quote}"),
            Message($"MSBuild executable path = {quote}MSBuild.dll{quote}"),
            Message($"Command line arguments = {quote}dotnet build \"project file.csproj\"{quote}"),
            Message($"MSBuild version = {quote}18.0.0{quote}"),
            Message("Process = replacement"),
            Message("MSBuild executable path = replacement"),
            Message("Command line arguments = replacement"),
            Message("MSBuild version = replacement"));

        Assert.NotNull(data);
        Assert.Equal("dotnet host", data.ProcessPath);
        Assert.Equal("MSBuild.dll", data.MSBuildPath);
        Assert.Equal("dotnet build \"project file.csproj\"", data.CommandLine);
        Assert.Equal("18.0.0", data.MSBuildVersion);

        BuildMessageEventArgs Message(string text) => new(text, null, "MSBuild", importance);
    }

    [Fact]
    public void ReadMSBuildDataStopsAtProject()
    {
        var data = ReadMSBuildDataFromEvents(
            new BuildMessageEventArgs("Process = dotnet", null, "MSBuild", MessageImportance.Low),
            new ProjectStartedEventArgs("", "", "example.csproj", "", Array.Empty<object>(), Array.Empty<object>())
            {
                BuildEventContext = new BuildEventContext(1, 1, 1, 1)
            },
            new BuildMessageEventArgs("Process = replacement", null, "MSBuild", MessageImportance.Low),
            new BuildMessageEventArgs("MSBuild executable path = late", null, "MSBuild", MessageImportance.Low),
            new BuildMessageEventArgs("Command line arguments = late", null, "MSBuild", MessageImportance.Low),
            new BuildMessageEventArgs("MSBuild version = late", null, "MSBuild", MessageImportance.Low));

        Assert.NotNull(data);
        Assert.Equal("dotnet", data.ProcessPath);
        Assert.Null(data.MSBuildPath);
        Assert.Null(data.CommandLine);
        Assert.Null(data.MSBuildVersion);
    }

    [Fact]
    public void ReadMSBuildDataMissing()
    {
        Assert.Null(ReadMSBuildDataFromEvents(
            new BuildMessageEventArgs(null, null, "MSBuild", MessageImportance.Low),
            new BuildMessageEventArgs("Unrelated message", null, "MSBuild", MessageImportance.High)));
    }

    /// <summary>
    /// Exercise the metadata parser through the binlog wire format, including omitted importance.
    /// </summary>
    private MSBuildData? ReadMSBuildDataFromEvents(params BuildEventArgs[] events)
    {
        var filePath = Path.Combine(RootDirectory, "metadata.binlog");
        var source = new EventArgsDispatcher();
        var logger = new BinaryLogger { Parameters = $"{filePath};ProjectImports=None" };
        logger.Initialize(source);
        try
        {
            foreach (var e in events)
            {
                source.Dispatch(e);
            }
        }
        finally
        {
            logger.Shutdown();
        }

        using var stream = File.OpenRead(filePath);
        return BinaryLogUtil.ReadAllData(stream).MSBuildData;
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
        var data = new BinaryLogUtil.MSBuildCompilerTaskData(1, 1, true)
        {
            CommandLineArguments = "dotnet not a compiler call",
        };

        Assert.Throws<InvalidOperationException>(() => data.TryCreateCompilerTaskData(ContextData.ProjectFile, null, CompilerCallKind.Unknown, ownerState: null));
    }

    [Fact]
    public void TryCreateCompilerNoArguments()
    {
        var data = new BinaryLogUtil.MSBuildCompilerTaskData(1, 1, true)
        {
            CommandLineArguments = null,
        };

        Assert.Null(data.TryCreateCompilerTaskData(ContextData.ProjectFile, null, CompilerCallKind.Unknown, null));
    }
}
