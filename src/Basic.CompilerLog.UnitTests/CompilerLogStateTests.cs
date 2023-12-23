
using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Abstractions;

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

}