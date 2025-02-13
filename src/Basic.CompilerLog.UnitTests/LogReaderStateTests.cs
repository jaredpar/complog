
using Basic.CompilerLog.Util;
using Xunit;

#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public class LogReaderStateTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public LogReaderStateTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(LogReaderState))
    {
        Fixture = fixture;
    }

    [Fact]
    public void DisposeCleansUpDirectories()
    {
        var state = new Util.LogReaderState(baseDir: Root.NewDirectory());
        Directory.CreateDirectory(state.AnalyzerDirectory);
        state.Dispose();
        Assert.False(Directory.Exists(state.BaseDirectory));
    }

    /// <summary>
    /// Don't throw if state can't clean up the directories because they are locked.
    /// </summary>
    [Fact]
    public void DisposeDirectoryLocked()
    {
        var state = new Util.LogReaderState(baseDir: Root.NewDirectory());
        Directory.CreateDirectory(state.AnalyzerDirectory);
        var fileStream = new FileStream(Path.Combine(state.AnalyzerDirectory, "example.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        state.Dispose();
        fileStream.Dispose();
    }

    [Fact]
    public void CreateBasicAnalyzerHostBadKind()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        Assert.Throws<InvalidOperationException>(() => BasicAnalyzerHost.Create(
            reader,
            (BasicAnalyzerKind)42,
            reader.ReadCompilerCall(0),
            []));
    }

#if NET
    [Fact]
    public void CustomAssemblyLoadContext()
    {
        var alc = new AssemblyLoadContext("Custom", isCollectible: true);
        var options = new Util.LogReaderState(alc);
        Assert.Same(alc, options.CompilerLoadContext);
        alc.Unload();
    }
#endif
}