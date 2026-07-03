
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

        // Create an unlocked lock file in the locks directory (simulates a dead process)
        var locksDir = Path.Combine(parentDir, "locks");
        Directory.CreateDirectory(locksDir);
        File.WriteAllText(Path.Combine(locksDir, dirName + ".lock"), "");
        // Also create a file that would normally exist in a working directory
        File.WriteAllText(Path.Combine(staleDir, "analyzer.dll"), "fake");

        CommonUtil.CleanupStaleTempDirectories(parentDir);

        Assert.False(Directory.Exists(staleDir));
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
    public void CleanupDeletesLegacyLockedDirectory()
    {
        // Tests backward compat: a directory with an in-directory .lock file (old format)
        // that is not held should be cleaned up
        var parentDir = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.Test.Cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDir);
        var staleDir = Path.Combine(parentDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, ".lock"), "");

        CommonUtil.CleanupStaleTempDirectories(parentDir);

        Assert.False(Directory.Exists(staleDir));
    }

    [Fact]
    public void CleanupSkipsLegacyLockedDirectory()
    {
        // Tests backward compat: a directory with a held in-directory .lock file (old format)
        // should be skipped
        var parentDir = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.Test.Cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDir);
        var activeDir = Path.Combine(parentDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(activeDir);

        using var lockStream = new FileStream(
            Path.Combine(activeDir, ".lock"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        CommonUtil.CleanupStaleTempDirectories(parentDir);

        Assert.True(Directory.Exists(activeDir));

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