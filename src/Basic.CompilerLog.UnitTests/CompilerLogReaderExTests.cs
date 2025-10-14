using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;
using Xunit;

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

    public CompilerLogReaderExTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, SolutionFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Convert the console binary log and return a reader over it
    /// </summary>
    private CompilerLogReader ConvertConsole(Func<CompilerCall, CompilerCall> func, BasicAnalyzerKind? basicAnalyzerKind = null, List<string>? diagnostics = null) =>
        ChangeCompilerCall(
            Fixture.SolutionBinaryLogPath,
            x => x.ProjectFileName == "console.csproj",
            func,
            basicAnalyzerKind,
            diagnostics);

    private CompilerLogReader ConvertConsoleArgs(Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>> func, BasicAnalyzerKind? basicAnalyzerKind = null) =>
        ConvertConsole(x =>
        {
            var args = func(x.GetArguments());
            return x.WithArguments(args);
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

    [Theory]
    [MemberData(nameof(GetMissingFileArguments))]
    public async Task MissingFiles(string? option, string fileName, bool hasDiagnostics)
    {
        var diagnostics = new List<string>();
        var filePath = Path.Combine(RootDirectory, fileName);
        var prefix = option is null ? "" : $"/{option}:";
        using var reader = ConvertConsole(x => x.WithAdditionalArguments([$"{prefix}{filePath}"]), BasicAnalyzerKind.None, diagnostics);
        Assert.Equal([RoslynUtil.GetDiagnosticMissingFile(filePath)], diagnostics);
        var compilationData = reader.ReadAllCompilationData().Single();
        if (hasDiagnostics)
        {
            Assert.Equal([RoslynUtil.CannotReadFileDiagnosticDescriptor], compilationData.CreationDiagnostics.Select(x => x.Descriptor));

            _ = compilationData.GetCompilationAfterGenerators(out var diagnostics2, CancellationToken);
            Assert.Contains(RoslynUtil.CannotReadFileDiagnosticDescriptor, diagnostics2.Select(x => x.Descriptor));

            diagnostics2 = await compilationData.GetAllDiagnosticsAsync(CancellationToken);
            Assert.Contains(RoslynUtil.CannotReadFileDiagnosticDescriptor, diagnostics2.Select(x => x.Descriptor));
        }
        else
        {
            Assert.Empty(compilationData.CreationDiagnostics);
        }
    }
}
