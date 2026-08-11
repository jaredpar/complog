
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public sealed class CommonUtilTests
{
#if NET
    [Fact]
    public void GetAssemblyLoadContext()
    {
        var alc = new AssemblyLoadContext("Custom", isCollectible: true);
        Assert.Same(alc, CommonUtil.GetAssemblyLoadContext(alc));
        alc.Unload();
    }
#endif

    [Fact]
    public void Defines()
    {
#if NET
        Assert.True(TestBase.IsNetCore);
        Assert.False(TestBase.IsNetFramework);
#else
        Assert.False(TestBase.IsNetCore);
        Assert.True(TestBase.IsNetFramework);
#endif
    }

    [Fact]
    public void GetCompilerLogTempDirectoryDefault()
    {
        var dir = CommonUtil.GetCompilerLogTempDirectory();
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Basic.CompilerLog"), dir);
    }

    [Fact]
    public void CleanupDeletesStaleDirectories()
    {
        var parentDir = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.Test.Cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDir);
        var dirName = Guid.NewGuid().ToString("N");
        var staleDir = Path.Combine(parentDir, dirName);
        Directory.CreateDirectory(staleDir);

        // Create an unheld lock file in the locks directory (simulates a crashed process that left
        // the lock file behind — the exclusive-open probe will acquire it and treat the dir stale).
        var locksDir = Path.Combine(parentDir, "locks");
        Directory.CreateDirectory(locksDir);
        var lockPath = Path.Combine(locksDir, dirName + ".lock");
        File.WriteAllText(lockPath, "");
        // Also create a file that would normally exist in a working directory
        File.WriteAllText(Path.Combine(staleDir, "analyzer.dll"), "fake");

        CommonUtil.CleanupStaleTempDirectories(parentDir);

        Assert.False(Directory.Exists(staleDir));
        // The stale lock file should also be removed
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void CleanupDeletesDirectoryWithNoLockFile()
    {
        var parentDir = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.Test.Cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDir);
        var staleDir = Path.Combine(parentDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "analyzer.dll"), "fake");

        CommonUtil.CleanupStaleTempDirectories(parentDir);

        Assert.False(Directory.Exists(staleDir));
    }

    [Fact]
    public void CleanupSkipsLockedDirectory()
    {
        var parentDir = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.Test.Cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDir);
        var dirName = Guid.NewGuid().ToString("N");
        var activeDir = Path.Combine(parentDir, dirName);
        Directory.CreateDirectory(activeDir);

        // Hold the lock file open in the locks directory to simulate an active process
        var locksDir = Path.Combine(parentDir, "locks");
        Directory.CreateDirectory(locksDir);
        using var lockStream = new FileStream(
            Path.Combine(locksDir, dirName + ".lock"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        CommonUtil.CleanupStaleTempDirectories(parentDir);

        Assert.True(Directory.Exists(activeDir));

        // Cleanup
        lockStream.Dispose();
        Directory.Delete(parentDir, recursive: true);
    }

    [Fact]
    public void CleanupNoOpWhenDirectoryDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.Test.Cleanup", Guid.NewGuid().ToString("N"));
        // Should not throw
        CommonUtil.CleanupStaleTempDirectories(nonExistent);
    }
}