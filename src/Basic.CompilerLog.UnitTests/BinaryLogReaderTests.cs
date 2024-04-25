using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
#if NETCOREAPP
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class BinaryLogReaderTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public BinaryLogReaderTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void ReadCommandLineArgumentsOwnership()
    {
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!);
        var compilerCall = reader.ReadAllCompilerCalls().First();
        Assert.NotNull(reader.ReadCommandLineArguments(compilerCall));

        compilerCall = compilerCall.ChangeOwner(null);
        Assert.Throws<ArgumentException>(() => reader.ReadCommandLineArguments(compilerCall));
    }

    [Fact]
    public void DisposeDouble()
    {
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!);
        reader.Dispose();
        reader.Dispose();
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory)]
    [InlineData(BasicAnalyzerKind.OnDisk)]
    public void CreateFilePath(BasicAnalyzerKind basicAnalyzerKind)
    {
        var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!, basicAnalyzerKind);
        Assert.Equal(basicAnalyzerKind, reader.BasicAnalyzerKind);
        Assert.True(reader.OwnsLogReaderState);
        Assert.False(reader.LogReaderState.IsDisposed);
        reader.Dispose();
        Assert.True(reader.LogReaderState.IsDisposed);
        Assert.True(reader.IsDisposed);
    }

    [Fact]
    public void CreateFilePathLogReaderState()
    {
        var state = new LogReaderState();
        var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!, BasicAnalyzerKind.OnDisk, state);
        Assert.False(reader.OwnsLogReaderState);
        Assert.False(reader.LogReaderState.IsDisposed);
        reader.Dispose();
        Assert.False(reader.LogReaderState.IsDisposed);
        Assert.True(reader.IsDisposed);
        state.Dispose();
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory, true)]
    [InlineData(BasicAnalyzerKind.OnDisk, true)]
    [InlineData(BasicAnalyzerKind.InMemory, false)]
    public void CreateStream1(BasicAnalyzerKind basicAnalyzerKind, bool leaveOpen)
    {
        var stream = new FileStream(Fixture.Console.Value.BinaryLogPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = BinaryLogReader.Create(stream, basicAnalyzerKind, leaveOpen);
        Assert.Equal(basicAnalyzerKind, reader.BasicAnalyzerKind);
        reader.Dispose();
        // CanRead is the best approximation we have for checking if the stream is disposed
        Assert.Equal(leaveOpen, stream.CanRead);
        stream.Dispose();
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory, true)]
    [InlineData(BasicAnalyzerKind.OnDisk, true)]
    [InlineData(BasicAnalyzerKind.InMemory, false)]
    public void CreateStream2(BasicAnalyzerKind basicAnalyzerKind, bool leaveOpen)
    {
        var state = new LogReaderState();
        var stream = new FileStream(Fixture.Console.Value.BinaryLogPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = BinaryLogReader.Create(stream, basicAnalyzerKind, leaveOpen);
        Assert.Equal(basicAnalyzerKind, reader.BasicAnalyzerKind);
        reader.Dispose();
        // CanRead is the best approximation we have for checking if the stream is disposed
        Assert.Equal(leaveOpen, stream.CanRead);
        stream.Dispose();
        Assert.False(state.IsDisposed);
        state.Dispose();
    }

    [Theory]
    [InlineData(BasicAnalyzerKind.InMemory)]
    [InlineData(BasicAnalyzerKind.OnDisk)]
    public void VerifyBasicAnalyzerKind(BasicAnalyzerKind basicAnalyzerKind)
    {
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!, basicAnalyzerKind);
        var compilerCall = reader.ReadAllCompilerCalls().First();
        var compilationData = reader.ReadCompilationData(compilerCall);
        Assert.Equal(basicAnalyzerKind, compilationData.BasicAnalyzerHost.Kind);
    }
}
