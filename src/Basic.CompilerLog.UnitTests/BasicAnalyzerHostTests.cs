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
using Xunit.Runner.Common;
using Microsoft.CodeAnalysis;
using System.Reflection.PortableExecutable;



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
        var generator = new BasicGeneratedFilesAnalyzerReference(generatedTexts);
        var compilation = CSharpCompilation.Create(
            "example",
            [],
            Basic.Reference.Assemblies.Net100.References.All);
        var driver = CSharpGeneratorDriver.Create([generator!]);
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
        var diagnostic = Diagnostic.Create(RoslynUtil.ErrorReadingGeneratedFilesDiagnosticDescriptor, Location.None, "message");
        var host = new BasicAnalyzerHostNone(diagnostic);
        var analyzerReferences = host.AnalyzerReferences.Single();
        var list = new List<Diagnostic>();
        var bar = analyzerReferences.AsBasicAnalyzerReference();
        _ = bar.GetGenerators(LanguageNames.CSharp, list);
        Assert.Equal([diagnostic], list);
        list.Clear();
        _ = bar.GetAnalyzers(LanguageNames.CSharp, list);
        Assert.Empty(list);
    }

    /// <summary>
    /// Verify that ReadyToRun analyzers stored in a compiler log are stripped to IL-only when
    /// <see cref="CompilerLogReader.ForceStripReadyToRun"/> is set. This exercises the stripping
    /// code path unconditionally, regardless of whether the stored R2R native code matches the
    /// current process architecture.
    /// </summary>
    [Fact]
    public void ReadyToRunAnalyzersAreStripped()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        // Force stripping so this test exercises the stripping path on every CI platform,
        // including those whose architecture matches the R2R native code in the log.
        reader.ForceStripReadyToRun = true;
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // Find at least one R2R analyzer in the stored log bytes
        var r2rData = analyzerDataList
            .Where(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)))
            .ToList();

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.NotEmpty(r2rData);

        // When retrieved through the data provider interface, they should be IL-only
        var provider = (IBasicAnalyzerHostDataProvider)reader;
        foreach (var analyzerData in r2rData)
        {
            var strippedBytes = provider.GetAssemblyBytes(analyzerData.AssemblyData);
            Assert.False(R2RUtil.IsReadyToRun(strippedBytes), $"{analyzerData.FileName} should be IL-only after stripping");

            // Verify that the CopyAssemblyBytes path also produces stripped bytes
            using var ms = new MemoryStream();
            provider.CopyAssemblyBytes(analyzerData.AssemblyData, ms);
            Assert.False(R2RUtil.IsReadyToRun(ms.ToArray()), $"{analyzerData.FileName} CopyAssemblyBytes should produce IL-only output");
        }
    }

    /// <summary>
    /// Verify that R2R analyzer assemblies whose machine type matches the current process
    /// architecture are NOT automatically stripped. Same-arch assemblies execute natively and
    /// must be left intact to preserve their strong-name identity and avoid unnecessary overhead.
    /// </summary>
    [Fact]
    public void ReadyToRunNotStrippedOnSameArchitecture()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // Find R2R assemblies that do NOT need stripping (i.e. same-arch as current process)
        var sameArchR2rData = analyzerDataList
            .Where(a =>
            {
                var bytes = reader.GetAssemblyBytes(a.Mvid);
                return R2RUtil.IsReadyToRun(bytes) && !R2RUtil.NeedsStripping(bytes);
            })
            .ToList();

        if (sameArchR2rData.Count == 0)
        {
            // No same-arch R2R assemblies present (e.g. running on a different architecture
            // than the build machine). Nothing to verify.
            return;
        }

        var provider = (IBasicAnalyzerHostDataProvider)reader;
        foreach (var analyzerData in sameArchR2rData)
        {
            var bytes = provider.GetAssemblyBytes(analyzerData.AssemblyData);
            Assert.True(R2RUtil.IsReadyToRun(bytes), $"{analyzerData.FileName} should not be stripped when architecture matches");
        }
    }

    /// <summary>
    /// Verify that R2R stripping produces a valid, loadable assembly that retains its analyzer
    /// functionality. Stripped analyzers must still execute correctly.
    /// </summary>
#if NET
    [Fact]
    public void ReadyToRunAnalyzersStillWork()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.InMemory);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.Contains(analyzerDataList, a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));

        // Load all analyzers through the host (which strips R2R automatically) and run them
        using var host = new BasicAnalyzerHostInMemory(reader, analyzerDataList);
        Assert.NotEmpty(host.AnalyzerReferences);

        var diagnostics = new List<Diagnostic>();
        foreach (var reference in host.AnalyzerReferences)
        {
            reference.AsBasicAnalyzerReference().GetAnalyzers(LanguageNames.CSharp, diagnostics);
        }

        // The .NET SDK analyzers contain real analyzers, so we expect non-empty results
        Assert.NotEmpty(host.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp)));
    }
#endif

    /// <summary>
    /// Verify that the stripped analyzer byte cache is populated when bytes are retrieved,
    /// and that subsequent calls return the cached (already-stripped) result.
    /// </summary>
    [Fact]
    public void ReadyToRunStrippedAnalyzerBytesAreCached()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        // Force stripping so the cache is exercised regardless of the current architecture.
        reader.ForceStripReadyToRun = true;
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rData = analyzerDataList
            .Where(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)))
            .ToList();

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.NotEmpty(r2rData);

        var provider = (IBasicAnalyzerHostDataProvider)reader;
        var analyzerData = r2rData[0];

        var bytes1 = provider.GetAssemblyBytes(analyzerData.AssemblyData);
        var bytes2 = provider.GetAssemblyBytes(analyzerData.AssemblyData);

        // Same reference should be returned from the cache
        Assert.Same(bytes1, bytes2);
    }

#if NET
    /// <summary>
    /// Verify that when <see cref="CompilerLogReader.ForceStripReadyToRun"/> is set, R2R analyzer
    /// assemblies are stripped and generators that actually execute during compilation still
    /// produce correct output. The Console fixture uses [GeneratedRegex] which requires the
    /// RegexGenerator source generator to run.
    /// </summary>
    [Fact]
    public void ForceStripReadyToRunGeneratorsExecute()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.InMemory);
        reader.ForceStripReadyToRun = true;

        var data = reader.ReadCompilationData(0);
        var compilation = data.GetCompilationAfterGenerators(out var diagnostics, CancellationToken);

        Assert.Empty(diagnostics);

        // The Console fixture uses [GeneratedRegex], so RegexGenerator must have run and
        // produced the REGEX_DEFAULT_MATCH_TIMEOUT field in the generated source.
        Assert.Contains(compilation.SyntaxTrees, t => t.ToString().Contains("REGEX_DEFAULT_MATCH_TIMEOUT"));

        data.BasicAnalyzerHost.Dispose();
    }
#endif

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

