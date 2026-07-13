using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class FixtureBuildCacheTests : IDisposable
{
    internal TempDir Root { get; } = new();

    internal FixtureBuildCache Cache { get; }

    public FixtureBuildCacheTests()
    {
        Cache = new FixtureBuildCache(Root.DirectoryPath, "test");
    }

    public void Dispose()
    {
        Root.Dispose();
    }

    private string GetBuildDirectory(string name = "entry") =>
        Path.Combine(Cache.CacheDirectory, name);

    /// <summary>
    /// A recipe in the shape the fixtures use: write some source files, then run a command that
    /// produces output files. The command is counted so tests can assert whether the process
    /// actually ran or the cache satisfied it.
    /// </summary>
    private sealed class FakeRecipe(FixtureBuildCache cache)
    {
        internal string SourceContent = "source";
        internal string Args = "build -bl";
        internal int RunCount;

        internal void Run(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "source.txt"), SourceContent);
            var result = cache.RunCommand(Args, directory, () =>
            {
                RunCount++;
                File.WriteAllText(Path.Combine(directory, "output.txt"), $"built from {SourceContent}");
                return new ProcessResult(exitCode: 0, standardOut: "", standardError: "");
            });
            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public void CommandRunsOnceThenReplaysAreVerified()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);

        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(1, recipe.RunCount);
        Assert.True(File.Exists(Path.Combine(buildDirectory, "output.txt")));

        Assert.True(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(1, recipe.RunCount);
    }

    [Fact]
    public void CommandOutsideBuildRunsUncached()
    {
        var directory = Root.NewDirectory();
        var runCount = 0;
        for (var i = 0; i < 2; i++)
        {
            var result = Cache.RunCommand("build", directory, () =>
            {
                runCount++;
                return new ProcessResult(exitCode: 0, standardOut: "", standardError: "");
            });
            Assert.True(result.Succeeded);
        }

        Assert.Equal(2, runCount);
    }

    [Fact]
    public void ChangedSourceContentRebuilds()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        recipe.SourceContent = "changed source";
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(2, recipe.RunCount);
        Assert.Equal("built from changed source", File.ReadAllText(Path.Combine(buildDirectory, "output.txt")));

        // The changed recipe becomes the cached state for later runs.
        Assert.True(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(2, recipe.RunCount);
    }

    [Fact]
    public void ChangedCommandLineRebuilds()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        recipe.Args = "build -bl -other";
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(2, recipe.RunCount);
    }

    /// <summary>
    /// Reverting to previously built inputs must not re-run the command: the store still has the
    /// outputs for that key and restores them by copy.
    /// </summary>
    [Fact]
    public void RevertedSourceContentRestoresFromStore()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        recipe.SourceContent = "changed source";
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(2, recipe.RunCount);

        recipe.SourceContent = "source";
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(2, recipe.RunCount);
        Assert.Equal("built from source", File.ReadAllText(Path.Combine(buildDirectory, "output.txt")));
    }

    /// <summary>
    /// A deleted output (say the user cleaned up bin directories by hand) is detected and healed
    /// from the store without re-running the command.
    /// </summary>
    [Fact]
    public void DeletedOutputIsRestoredWithoutRunning()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        File.Delete(Path.Combine(buildDirectory, "output.txt"));
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(1, recipe.RunCount);
        Assert.True(File.Exists(Path.Combine(buildDirectory, "output.txt")));
    }

    /// <summary>
    /// A missing sentinel means a previous run crashed part way through the build. The directory
    /// is rebuilt, but the store still satisfies the unchanged commands.
    /// </summary>
    [Fact]
    public void MissingSentinelRebuildsFromStore()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        File.Delete(buildDirectory + ".complete");
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(1, recipe.RunCount);
        Assert.True(File.Exists(Path.Combine(buildDirectory, "output.txt")));
    }

    /// <summary>
    /// The SDK version from global.json participates in the command key, so changing it re-runs
    /// the commands.
    /// </summary>
    [Fact]
    public void ChangedSdkVersionRebuilds()
    {
        var buildDirectory = GetBuildDirectory();
        var sdkVersion = "1.0.100";
        var recipe = new FakeRecipe(Cache);
        void Run(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "global.json"), $$"""{ "sdk": { "version": "{{sdkVersion}}" } }""");
            recipe.Run(directory);
        }

        Assert.False(Cache.RunBuild(buildDirectory, Run));
        Assert.True(Cache.RunBuild(buildDirectory, Run));
        Assert.Equal(1, recipe.RunCount);

        sdkVersion = "2.0.100";
        Assert.False(Cache.RunBuild(buildDirectory, Run));
        Assert.Equal(2, recipe.RunCount);
    }

    [Fact]
    public void TryReadSdkVersionWalksUp()
    {
        var directory = Root.NewDirectory();
        var nested = Path.Combine(directory, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(directory, "global.json"), """{ "sdk": { "version": "9.0.100", "rollForward": "minor" } }""");
        Assert.Equal("9.0.100", FixtureBuildCache.TryReadSdkVersion(nested));
    }

    /// <summary>
    /// A run that crashes while holding a <see cref="ReadOnlyDirectoryScope"/> leaves the cached
    /// files read-only on disk. The next run must restore write access before replaying.
    /// </summary>
    [Fact]
    public void ReadOnlyBuildDirectoryIsMadeWritable()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        // Simulate the crash by setting read-only and never disposing the scope.
        _ = new ReadOnlyDirectoryScope(buildDirectory, setReadOnly: true);

        Assert.True(Cache.RunBuild(buildDirectory, recipe.Run));
        Assert.Equal(1, recipe.RunCount);
        File.WriteAllText(Path.Combine(buildDirectory, "writable.txt"), "writable again");
    }

    [Fact]
    public void FailedCommandIsNotCached()
    {
        var buildDirectory = GetBuildDirectory();
        var runCount = 0;
        var fail = true;
        void Run(string directory)
        {
            var result = Cache.RunCommand("build", directory, () =>
            {
                runCount++;
                return new ProcessResult(exitCode: fail ? 1 : 0, standardOut: "", standardError: "");
            });

            // FixtureBase fails the fixture (throws) when a command does not succeed.
            Assert.True(result.Succeeded);
        }

        Assert.ThrowsAny<Exception>(() => Cache.RunBuild(buildDirectory, Run));
        fail = false;
        Assert.False(Cache.RunBuild(buildDirectory, Run));
        Assert.Equal(2, runCount);
        Assert.True(Cache.RunBuild(buildDirectory, Run));
        Assert.Equal(2, runCount);
    }

    [Fact]
    public void PruneDeletesStaleStoreEntriesAndKeepsRecent()
    {
        var buildDirectory = GetBuildDirectory();
        var recipe = new FakeRecipe(Cache);
        Assert.False(Cache.RunBuild(buildDirectory, recipe.Run));

        var storeDirectory = Path.Combine(Root.DirectoryPath, "store");
        var keyDirectory = Directory.GetDirectories(storeDirectory).Single();

        var staleDirectory = Path.Combine(storeDirectory, "stalekey");
        Directory.CreateDirectory(staleDirectory);
        File.WriteAllText(Path.Combine(staleDirectory, "last-used.txt"), DateTime.UtcNow.AddDays(-30).ToString("O"));

        Cache.PruneStaleEntries();
        Assert.False(Directory.Exists(staleDirectory));
        Assert.True(Directory.Exists(keyDirectory));
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
