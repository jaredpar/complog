
using Basic.CompilerLog.Util;
using Xunit;

#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

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
        Assert.True(Directory.Exists(state.BaseDirectory));
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

    [Fact]
    public void DisposeGuards()
    {
        var state = new Util.LogReaderState(baseDir: Root.NewDirectory());
        state.Dispose();
        Assert.True(state.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => state.GetOrCreateBasicAnalyzerHost(null!, BasicAnalyzerKind.InMemory, null!));
    }

    [Fact]
    public void CreatesLockFile()
    {
        // Lock files are only created when using the default temp directory (baseDir: null)
        using var state = new Util.LogReaderState();
        var lockPath = Path.Combine(state.BaseDirectory, ".lock");
        Assert.True(File.Exists(lockPath));

        // Lock should prevent external exclusive access
        Assert.Throws<IOException>(() => new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    [Fact]
    public void CleanupDoesNotDeleteActiveState()
    {
        // Lock files and cleanup only apply to the default temp directory
        using var state1 = new Util.LogReaderState();
        Assert.True(Directory.Exists(state1.BaseDirectory));

        // A second state in the same default parent should not delete the first
        using var state2 = new Util.LogReaderState();
        Assert.True(Directory.Exists(state1.BaseDirectory));
        Assert.True(Directory.Exists(state2.BaseDirectory));
    }

    [Fact]
    public void NoLockFileWithCustomBaseDir()
    {
        var state = new Util.LogReaderState(baseDir: Root.NewDirectory());
        var lockPath = Path.Combine(state.BaseDirectory, ".lock");
        Assert.False(File.Exists(lockPath));
        state.Dispose();
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