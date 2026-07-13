using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class FixtureBuildCacheTests : IDisposable
{
    internal TempDir Root { get; } = new();

    internal FixtureBuildCache Cache { get; }

    public FixtureBuildCacheTests()
    {
        Cache = new FixtureBuildCache(Root.DirectoryPath, "test-key");
    }

    public void Dispose()
    {
        Root.Dispose();
    }

    private string GetBuildDirectory(string name = "entry") =>
        Path.Combine(Cache.CacheDirectory, name);

    [Fact]
    public void BuildRunsOnceThenIsCached()
    {
        var buildDirectory = GetBuildDirectory();
        var buildCount = 0;
        void Build(string path)
        {
            buildCount++;
            File.WriteAllText(Path.Combine(path, "output.txt"), "built");
        }

        Assert.False(Cache.RunBuild(buildDirectory, Build));
        Assert.True(Cache.RunBuild(buildDirectory, Build));
        Assert.Equal(1, buildCount);
        Assert.Equal("built", File.ReadAllText(Path.Combine(buildDirectory, "output.txt")));
    }

    [Fact]
    public void CachedBuildSurvivesNewCacheInstance()
    {
        var buildDirectory = GetBuildDirectory();
        Assert.False(Cache.RunBuild(buildDirectory, path => File.WriteAllText(Path.Combine(path, "output.txt"), "built")));

        var otherCache = new FixtureBuildCache(Root.DirectoryPath, "test-key");
        Assert.True(otherCache.RunBuild(buildDirectory, _ => Assert.Fail("Should not rebuild")));
    }

    [Fact]
    public void DifferentKeysDoNotShareBuilds()
    {
        var buildCount = 0;
        void Build(string path) => buildCount++;

        Assert.False(Cache.RunBuild(GetBuildDirectory(), Build));
        var otherCache = new FixtureBuildCache(Root.DirectoryPath, "other-key");
        Assert.False(otherCache.RunBuild(Path.Combine(otherCache.CacheDirectory, "entry"), Build));
        Assert.Equal(2, buildCount);
    }

    /// <summary>
    /// A directory without a completion marker means a previous run crashed part way through the
    /// build. The partial content must be discarded.
    /// </summary>
    [Fact]
    public void PartialBuildIsRebuilt()
    {
        var buildDirectory = GetBuildDirectory();
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(buildDirectory, "stale.txt"), "partial");

        var cached = Cache.RunBuild(buildDirectory, path => File.WriteAllText(Path.Combine(path, "output.txt"), "built"));
        Assert.False(cached);
        Assert.False(File.Exists(Path.Combine(buildDirectory, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(buildDirectory, "output.txt")));
    }

    /// <summary>
    /// A marker without the directory (say the user deleted the directory by hand) must trigger a
    /// rebuild rather than handing out a missing path.
    /// </summary>
    [Fact]
    public void MarkerWithoutDirectoryIsRebuilt()
    {
        var buildDirectory = GetBuildDirectory();
        File.WriteAllText(buildDirectory + ".complete", "stale");

        var cached = Cache.RunBuild(buildDirectory, path => File.WriteAllText(Path.Combine(path, "output.txt"), "built"));
        Assert.False(cached);
        Assert.True(File.Exists(Path.Combine(buildDirectory, "output.txt")));
    }

    /// <summary>
    /// A run that crashes while holding a <see cref="ReadOnlyDirectoryScope"/> leaves the cached
    /// files read-only on disk. The next run must restore write access when handing out the
    /// cached directory.
    /// </summary>
    [Fact]
    public void ReadOnlyCachedBuildIsMadeWritable()
    {
        var buildDirectory = GetBuildDirectory();
        Assert.False(Cache.RunBuild(buildDirectory, path => File.WriteAllText(Path.Combine(path, "output.txt"), "built")));

        // Simulate the crash by setting read-only and never disposing the scope.
        _ = new ReadOnlyDirectoryScope(buildDirectory, setReadOnly: true);

        Assert.True(Cache.RunBuild(buildDirectory, _ => Assert.Fail("Should not rebuild")));
        File.WriteAllText(Path.Combine(buildDirectory, "writable.txt"), "writable again");
    }

    [Fact]
    public void FailedBuildIsNotCached()
    {
        var buildDirectory = GetBuildDirectory();
        Assert.Throws<InvalidOperationException>(() =>
            Cache.RunBuild(buildDirectory, _ => throw new InvalidOperationException("build failed")));

        var cached = Cache.RunBuild(buildDirectory, path => File.WriteAllText(Path.Combine(path, "output.txt"), "built"));
        Assert.False(cached);
    }

    [Fact]
    public void PruneDeletesStaleKeysAndKeepsCurrent()
    {
        var buildDirectory = GetBuildDirectory();
        _ = Cache.RunBuild(buildDirectory, path => File.WriteAllText(Path.Combine(path, "output.txt"), "built"));

        var staleCache = new FixtureBuildCache(Root.DirectoryPath, "stale-key");
        _ = staleCache.RunBuild(Path.Combine(staleCache.CacheDirectory, "entry"), path => File.WriteAllText(Path.Combine(path, "output.txt"), "built"));
        File.WriteAllText(
            Path.Combine(staleCache.CacheDirectory, "last-used.txt"),
            DateTime.UtcNow.AddDays(-30).ToString("O"));

        Cache.PruneStaleEntries();
        Assert.False(Directory.Exists(staleCache.CacheDirectory));
        Assert.True(Directory.Exists(Cache.CacheDirectory));
        Assert.True(File.Exists(Path.Combine(buildDirectory, "output.txt")));
    }

    [Fact]
    public void PruneKeepsRecentlyUsedKeys()
    {
        var otherCache = new FixtureBuildCache(Root.DirectoryPath, "other-key");
        _ = otherCache.RunBuild(Path.Combine(otherCache.CacheDirectory, "entry"), path => File.WriteAllText(Path.Combine(path, "output.txt"), "built"));

        Cache.PruneStaleEntries();
        Assert.True(Directory.Exists(otherCache.CacheDirectory));
    }

    /// <summary>
    /// The read-only protection that fixtures put on cached directories must be removable by a
    /// fresh process that never created the protection (crash recovery).
    /// </summary>
    [Fact]
    public void EnsureWritableClearsAbandonedReadOnlyScope()
    {
        var dir = Root.NewDirectory();
        var filePath = Path.Combine(dir, "test.txt");
        File.WriteAllText(filePath, "hello world");
        _ = new ReadOnlyDirectoryScope(dir, setReadOnly: true);

        ReadOnlyDirectoryScope.EnsureWritable(dir);
        File.WriteAllText(filePath, "modified");
        Assert.Equal("modified", File.ReadAllText(filePath));
    }

    [Fact]
    public void EnsureWritableOnMissingDirectoryIsNoOp()
    {
        ReadOnlyDirectoryScope.EnsureWritable(Path.Combine(Root.DirectoryPath, "does-not-exist"));
    }
}
