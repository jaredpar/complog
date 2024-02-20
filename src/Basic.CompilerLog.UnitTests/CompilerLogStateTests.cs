
using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Abstractions;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public class CompilerLogStateTests : TestBase
{
    public CompilerLogStateTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper, nameof(CompilerLogStateTests))
    {
    }

    [Fact]
    public void DisposeCleansUpDirectories()
    {
        var state = new CompilerLogState(baseDir: Root.NewDirectory());
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
        var state = new CompilerLogState(baseDir: Root.NewDirectory());
        Directory.CreateDirectory(state.AnalyzerDirectory);
        var fileStream = new FileStream(Path.Combine(state.AnalyzerDirectory, "example.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        state.Dispose();
        fileStream.Dispose();
    }

#if NETCOREAPP
    [Fact]
    public void CustomAssemblyLoadContext()
    {
        var alc = new AssemblyLoadContext("Custom", isCollectible: true);
        var options = new CompilerLogState(alc);
        Assert.Same(alc, options.CompilerLoadContext);
        alc.Unload();
    }
#endif
}