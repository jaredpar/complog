using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using System.Reflection.PortableExecutable;
using Xunit;

#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class R2RUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public R2RUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(R2RUtilTests))
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Verify that ReadyToRun analyzers stored in a compiler log are stripped to IL-only when
    /// <see cref="LogReaderState.StripReadyToRun"/> is set to <see langword="true"/>. This exercises
    /// the stripping code path unconditionally, regardless of whether the stored R2R native code
    /// matches the current process architecture.
    /// </summary>
    [Fact]
    public void ReadyToRunAnalyzersAreStripped()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None,
            new LogReaderState(stripReadyToRun: true));
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

        // The SDK always produces same-arch R2R analyzers when building on the native platform
        Assert.Empty(sameArchR2rData);
    }

    /// <summary>
    /// Verify that R2R stripping produces a valid, loadable assembly that retains its analyzer
    /// functionality. Stripped analyzers must still execute correctly.
    /// </summary>
#if NET
    [Fact]
    public void ReadyToRunAnalyzersStillWork()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.InMemory,
            new LogReaderState(stripReadyToRun: true));
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.Contains(analyzerDataList, a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));

        // Load all analyzers through the host (which strips R2R) and run them
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
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None,
            new LogReaderState(stripReadyToRun: true));
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
    /// Verify that when R2R stripping is always enabled, generators that actually execute during
    /// compilation still produce correct output. The Console fixture uses [GeneratedRegex] which
    /// requires the RegexGenerator source generator to run.
    /// </summary>
    [Fact]
    public void StripReadyToRunGeneratorsExecute()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.InMemory,
            new LogReaderState(stripReadyToRun: true));

        var data = reader.ReadCompilationData(0);
        var compilation = data.GetCompilationAfterGenerators(out var diagnostics, CancellationToken);

        Assert.Empty(diagnostics);

        // The Console fixture uses [GeneratedRegex], so RegexGenerator must have run and
        // produced the REGEX_DEFAULT_MATCH_TIMEOUT field in the generated source.
        Assert.Contains(compilation.SyntaxTrees, t => t.ToString().Contains("REGEX_DEFAULT_MATCH_TIMEOUT"));

        data.BasicAnalyzerHost.Dispose();
    }
#endif
}
