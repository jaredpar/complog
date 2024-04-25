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

/// <summary>
/// Similar to <see cref="CompilerLogReaderTests"/> but using the <see cref="SolutionFixture"/> 
/// instead. This allows for a lot of modding of the compiler log that lets us test corner 
/// cases.
/// </summary>
[Collection(SolutionFixtureCollection.Name)]
public sealed class CompilerLogReaderExTests : TestBase
{
    public SolutionFixture Fixture { get; }

    public CompilerLogReaderExTests(ITestOutputHelper testOutputHelper, SolutionFixture fixture)
        : base(testOutputHelper, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Convert the console binary log and return a reader over it
    /// </summary>
    private CompilerLogReader ConvertConsole(Func<CompilerCall, CompilerCall> func, BasicAnalyzerKind? basicAnalyzerKind = null)
    {
        var diagnostics = new List<string>();
        
        using var binlogStream = new FileStream(Fixture.SolutionBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var compilerCall = BinaryLogUtil.ReadAllCompilerCalls(
            binlogStream,
            diagnostics,
            static x => x.ProjectFileName == "console.csproj").Single();

        Assert.Empty(diagnostics);
        compilerCall = func(compilerCall);
        
        var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, diagnostics);
        builder.Add(compilerCall, BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall));
        builder.Close();
        stream.Position = 0;
        return CompilerLogReader.Create(stream, basicAnalyzerKind, State, leaveOpen: false);
    }

    private CompilerLogReader ConvertConsoleArgs(Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>> func, BasicAnalyzerKind? basicAnalyzerKind = null) => 
        ConvertConsole(x =>
        {
            var args = func(x.GetArguments());
            return x.ChangeArguments(args);
        }, basicAnalyzerKind);

    [Fact]
    public void AnalyzerConfigNone()
    {
        var reader = ConvertConsoleArgs(args => 
            args
                .Where(x => !x.StartsWith("/analyzerconfig:", StringComparison.Ordinal))
                .ToArray());
        var data = reader.ReadAllCompilationData().Single();
        var optionsProvider = (BasicAnalyzerConfigOptionsProvider)data.AnalyzerOptions.AnalyzerConfigOptionsProvider;
        Assert.True(optionsProvider.IsEmpty);

        var syntaxProvider = (BasicSyntaxTreeOptionsProvider?)data.Compilation.Options.SyntaxTreeOptionsProvider;
        Assert.NotNull(syntaxProvider);
        Assert.False(syntaxProvider.IsEmpty);
    }

    [Theory]
    [InlineData("true", GeneratedKind.MarkedGenerated)]
    [InlineData("false", GeneratedKind.NotGenerated)]
    [InlineData("0", GeneratedKind.Unknown)]
    public void AnalyzerConfigGeneratedCode(string value, GeneratedKind expectedKind)
    {
        var text = $"""
            # C# files
            is_global = true
            generated_code = {value}
            """;

        var globalConfigFilePath = Root.NewFile(".editorconfig", text);
        var reader = ConvertConsoleArgs(args => 
            args
                .Where(x => !x.StartsWith("/analyzerconfig:", StringComparison.Ordinal))
                .Append($"/analyzerconfig:{globalConfigFilePath}")
                .ToArray());

        var data = reader.ReadAllCompilationData().Single();
        var syntaxProvider = (BasicSyntaxTreeOptionsProvider?)data.Compilation.Options.SyntaxTreeOptionsProvider;
        Assert.NotNull(syntaxProvider);

        var syntaxTree = data.Compilation.SyntaxTrees.First();
        Assert.Equal(expectedKind, syntaxProvider.IsGenerated(syntaxTree, CancellationToken.None));
    }
}