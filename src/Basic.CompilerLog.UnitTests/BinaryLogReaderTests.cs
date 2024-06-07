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

    /// <summary>
    /// Make sure the underlying stream is managed properly so we can read the compiler calls twice.
    /// </summary>
    [Fact]
    public void ReadAllCompilerCallsTwice()
    {   
        using var state = new LogReaderState();
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!, BasicAnalyzerKind.OnDisk, state);
        Assert.Single(reader.ReadAllCompilerCalls());
        Assert.Single(reader.ReadAllCompilerCalls());
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

    [Theory]
    [CombinatorialData]
    public void GetCompilationSimple(BasicAnalyzerKind basicAnalyzerKind)
    {
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!, basicAnalyzerKind);
        var compilerCall = reader.ReadAllCompilerCalls().First();
        var compilationData = reader.ReadCompilationData(compilerCall);
        Assert.NotNull(compilationData);
        var emitResult = compilationData.EmitToMemory();
        Assert.True(emitResult.Success);
    }

    [Fact]
    public void ReadDeletedPdb()
    {
        var dir = Root.NewDirectory();
        RunDotNet($"new console --name example --output .", dir);
        RunDotNet("build -bl -nr:false", dir);

        // Delete the PDB
        Directory.EnumerateFiles(dir, "*.pdb", SearchOption.AllDirectories).ForEach(File.Delete);

        using var reader = BinaryLogReader.Create(Path.Combine(dir, "msbuild.binlog"), BasicAnalyzerKind.None);
        var data = reader.ReadAllCompilationData().Single();
        var diagnostic = data.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Single();
        Assert.Contains("Can't find portable pdb file for", diagnostic.GetMessage());

        Assert.Throws<InvalidOperationException>(() => reader.ReadAllGeneratedFiles(data.CompilerCall));
    }

    [Fact]
    public void ReadGeneratedFiles()
    {
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!, BasicAnalyzerKind.None);
        var compilerCall = reader.ReadAllCompilerCalls().Single();
        var generatedFiles = reader.ReadAllGeneratedFiles(compilerCall);
        Assert.Single(generatedFiles);
        var tuple = generatedFiles.Single();
        Assert.True(tuple.Stream.TryGetBuffer(out var _));
    }
}
