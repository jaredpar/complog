using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp;

#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class BasicAnalyzerHostTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public BasicAnalyzerHostTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void Supported()
    {
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.OnDisk));
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.None));
#if NET
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.InMemory));
#else
        Assert.False(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.InMemory));
#endif

        // To make sure this test is updated every time a new value is added
        Assert.Equal(3, Enum.GetValues(typeof(BasicAnalyzerKind)).Length);
    }

    [Fact]
    public void NoneDispose()
    {
        var host = new BasicAnalyzerHostNone([]);
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = host.AnalyzerReferences; });
    }

    [Fact]
    public void NoneProps()
    {
        var host = new BasicAnalyzerHostNone([]);
        host.Dispose();
        Assert.Equal(BasicAnalyzerKind.None, host.Kind);
        Assert.Empty(host.GeneratedSourceTexts);
    }

    /// <summary>
    /// What happens when two separate generators produce files with the same name?
    /// </summary>
    [Fact]
    public void NoneConflictingFileNames()
    {
        var root = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? @"c:\code"
            : "/code";
        var sourceText1 = SourceText.From("// file 1", CommonUtil.ContentEncoding);
        var sourceText2 = SourceText.From("// file 2", CommonUtil.ContentEncoding);
        List<(SourceText SourceText, string FilePath)> generatedTexts = 
        [
            (sourceText1, Path.Combine(root, "file.cs")),
            (sourceText2, Path.Combine(root, "file.cs")),
        ];
        var host = new BasicAnalyzerHostNone(generatedTexts);
        var compilation = CSharpCompilation.Create(
            "example",
            [],
            Basic.Reference.Assemblies.Net80.References.All);
        var driver = CSharpGeneratorDriver.Create([host.Generator!]);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var compilation2, out var diagnostics, CancellationToken);
        Assert.Empty(diagnostics);
        var syntaxTrees = compilation2.SyntaxTrees.ToList();
        Assert.Equal(2, syntaxTrees.Count);
        Assert.Equal("// file 1", syntaxTrees[0].ToString());
        Assert.EndsWith("file.cs", syntaxTrees[0].FilePath);
        Assert.Equal("// file 2", syntaxTrees[1].ToString());
        Assert.EndsWith("file.cs", syntaxTrees[1].FilePath);
        Assert.NotEqual(syntaxTrees[0].FilePath, syntaxTrees[1].FilePath);
    }

    [Fact]
    public void Error()
    {
        var message = "my error message";
        var host = new BasicAnalyzerHostNone(message);
        var diagnostic = host.GetDiagnostics().Single();
        Assert.Contains(message, diagnostic.GetMessage());
    }

#if NETFRAMEWORK
    [Fact]
    public void InMemory()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.InMemory);
        var data = reader.ReadCompilationData(0);
        var host = (BasicAnalyzerHostInMemory)data.BasicAnalyzerHost;
        Assert.NotEmpty(host.AnalyzerReferences);
        Assert.Throws<PlatformNotSupportedException>(() => host.Loader.LoadFromAssemblyName(typeof(BasicAnalyzerHost).Assembly.GetName()));
    }
#endif
}
