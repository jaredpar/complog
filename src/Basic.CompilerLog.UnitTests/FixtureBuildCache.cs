using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// Content keyed cache for the expensive <c>dotnet new</c> / <c>dotnet build</c> invocations that
/// the test fixtures use to produce their scratch directories. The outputs of those commands are
/// deterministic for a given fixture recipe and SDK, so once a machine has produced them they can
/// be reused by every subsequent test run.
/// </summary>
/// <remarks>
/// Only the process outputs (project sources, bin/obj and the binary log) are cached. Anything
/// derived from product code, like the binlog to complog conversion, still runs on every test run
/// so product changes are always exercised.
///
/// The cache lives at a stable machine level path so that runs can find each other's outputs:
///
///   %TMP%/Basic.CompilerLog.UnitTests/build-cache/
///     locks/                     lock files, kept outside the directories they guard
///     {netcore|netfx}-{key}/     cache root for one content key (<see cref="CacheDirectory"/>)
///       last-used.txt            timestamp used for pruning stale keys
///       {fixture dirs...}        one subdirectory per fixture build plus a sibling
///       {entry}.complete         marker written only after the build succeeded
///
/// The key hashes the fixture source files (embedded as resources), the SDK constants and the
/// resolved SDK version, so editing a fixture recipe or changing SDK invalidates the cache while
/// product code edits do not. The cache root is keyed by the test runtime (netcore/netfx) so the
/// two test processes spawned by a multi targeted <c>dotnet test</c> never share directories.
///
/// Set the <c>COMPLOG_TEST_BUILD_CACHE</c> environment variable to <c>0</c> to disable caching, in
/// which case the fixtures fall back to building in their per run temp directories. In GitHub
/// Actions the cache is disabled by default (the machines are fresh so it would never hit); set
/// the variable to <c>1</c> to opt in there.
/// </remarks>
internal sealed class FixtureBuildCache
{
    internal const string EnvironmentVariableName = "COMPLOG_TEST_BUILD_CACHE";

    /// <summary>
    /// Cache roots for other keys (old fixture recipes, old SDKs) that have not been used for this
    /// long are deleted on startup.
    /// </summary>
    private static readonly TimeSpan s_pruneAge = TimeSpan.FromDays(7);

    private static readonly TimeSpan s_buildLockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The inputs that determine the contents of the cached build outputs. These are embedded into
    /// the test assembly so any edit to a fixture recipe changes the cache key.
    /// </summary>
    private static readonly string[] s_contentInputResourceNames =
    [
        "BuildCacheInput.CompilerLogFixture.cs",
        "BuildCacheInput.SolutionFixture.cs",
        "BuildCacheInput.FixtureBase.cs",
        "BuildCacheInput.FixtureBuildCache.cs",
        "BuildCacheInput.DotnetUtil.cs",
        "BuildCacheInput.ProcessUtil.cs",
        "Key.snk",
    ];

    private static readonly Lazy<(FixtureBuildCache? Instance, string? DisabledReason)> s_lazyInstance = new(CreateInstance);

    /// <summary>
    /// The shared cache for this test process, or null when caching is disabled (see
    /// <see cref="DisabledReason"/>).
    /// </summary>
    internal static FixtureBuildCache? Instance => s_lazyInstance.Value.Instance;

    internal static string? DisabledReason => s_lazyInstance.Value.DisabledReason;

    /// <summary>
    /// The stable directory holding every cached build for the current content key. Fixtures place
    /// their scratch directories under this path.
    /// </summary>
    internal string CacheDirectory { get; }

    private readonly string _locksDirectory;

    internal FixtureBuildCache(string baseDirectory, string cacheKey)
    {
        CacheDirectory = Path.Combine(baseDirectory, cacheKey);
        _locksDirectory = Path.Combine(baseDirectory, "locks");
        _ = Directory.CreateDirectory(CacheDirectory);
        _ = Directory.CreateDirectory(_locksDirectory);
    }

    private static (FixtureBuildCache?, string?) CreateInstance()
    {
        try
        {
            var envValue = Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim();
            if (envValue is "0" or "false" or "off" or "disable" or "disabled")
            {
                return (null, $"disabled via {EnvironmentVariableName}={envValue}");
            }

            // CI machines are fresh so the cache would never hit there. Opt-in only so CI keeps
            // using the per run temp directories.
            if (TestUtil.InGitHubActions && envValue is not ("1" or "true" or "on"))
            {
                return (null, $"disabled by default in GitHub Actions, set {EnvironmentVariableName}=1 to enable");
            }

            if (GetResolvedSdkVersion() is not { } resolvedSdkVersion)
            {
                return (null, $"could not resolve the .NET SDK version for {TestUtil.SdkVersion}");
            }

            var runtime = TestUtil.IsNetCore ? "netcore" : "netfx";
            var cacheKey = $"{runtime}-{ComputeContentHash(resolvedSdkVersion)}";
            var baseDirectory = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.UnitTests", "build-cache");
            var cache = new FixtureBuildCache(baseDirectory, cacheKey);
            cache.PruneStaleEntries();
            return (cache, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// The SDK that <c>dotnet</c> resolves for our pinned global.json. Installing a newer SDK on
    /// the machine can change what "roll forward minor" resolves to, which changes the build
    /// outputs, so it has to be part of the cache key.
    /// </summary>
    private static string? GetResolvedSdkVersion()
    {
        var probeDirectory = Path.Combine(TestUtil.TestTempRoot, "sdk-probe");
        _ = Directory.CreateDirectory(probeDirectory);
        TestUtil.WriteGlobalJson(probeDirectory);
        var result = DotnetUtil.Command("--version", probeDirectory);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOut))
        {
            return null;
        }

        return result.StandardOut.Trim();
    }

    private static string ComputeContentHash(string resolvedSdkVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
        {
            writer.WriteLine($"SdkVersion={TestUtil.SdkVersion}");
            writer.WriteLine($"TestTargetFramework={TestUtil.TestTargetFramework}");
            writer.WriteLine($"ResolvedSdkVersion={resolvedSdkVersion}");
            writer.WriteLine($"OS={GetOSName()}");
            writer.WriteLine($"Architecture={RuntimeInformation.OSArchitecture}");
        }

        foreach (var resourceName in s_contentInputResourceNames)
        {
            var bytes = ResourceLoader.GetResourceBlob(resourceName);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.Position = 0;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        var builder = new StringBuilder();
        foreach (var b in hash)
        {
            _ = builder.Append(b.ToString("x2"));
        }

        return builder.ToString(0, 16);

        static string GetOSName() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
            "other";
    }

    /// <summary>
    /// Runs <paramref name="build"/> against <paramref name="buildDirectory"/> unless a previous
    /// run already completed it, in which case the existing directory is used as is. Returns true
    /// when the cached output was used. <paramref name="buildDirectory"/> must be under
    /// <see cref="CacheDirectory"/>.
    /// </summary>
    internal bool RunBuild(string buildDirectory, Action<string> build)
    {
        Debug.Assert(buildDirectory.StartsWith(CacheDirectory, StringComparison.Ordinal));
        var markerFilePath = buildDirectory + ".complete";
        if (IsBuildComplete())
        {
            OnCacheHit();
            return true;
        }

        // The lock guards against another test process (or a parallel run of the other test
        // framework flavor) building the same entry at the same time.
        using (AcquireBuildLock(buildDirectory))
        {
            if (IsBuildComplete())
            {
                OnCacheHit();
                return true;
            }

            // No marker means a previous run crashed part way through this build. Clear out any
            // partial state, including read-only protections it may have left behind.
            if (File.Exists(markerFilePath))
            {
                File.Delete(markerFilePath);
            }

            if (Directory.Exists(buildDirectory))
            {
                ReadOnlyDirectoryScope.EnsureWritable(buildDirectory);
                Directory.Delete(buildDirectory, recursive: true);
            }

            _ = Directory.CreateDirectory(buildDirectory);
            build(buildDirectory);
            File.WriteAllText(markerFilePath, DateTime.UtcNow.ToString("O"));
        }

        Touch();
        return false;

        bool IsBuildComplete() => File.Exists(markerFilePath) && Directory.Exists(buildDirectory);

        void OnCacheHit()
        {
            // A run that crashed while the fixture held its ReadOnlyDirectoryScope leaves the
            // cached files read-only on disk. Restore write access here; the fixture re-applies
            // the scope for the current run.
            ReadOnlyDirectoryScope.EnsureWritable(buildDirectory);
            Touch();
        }
    }

    private IDisposable AcquireBuildLock(string buildDirectory)
    {
        var relativeName = buildDirectory
            .Substring(CacheDirectory.Length)
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_')
            .Replace(' ', '_')
            .Trim('_');
        var lockFilePath = Path.Combine(_locksDirectory, $"{Path.GetFileName(CacheDirectory)}.{relativeName}.lock");
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                // Update the write time so pruning can tell this lock file is still in use.
                stream.WriteByte(0);
                stream.Flush();
                return stream;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (stopwatch.Elapsed > s_buildLockTimeout)
                {
                    throw new TimeoutException($"Could not acquire build cache lock {lockFilePath}", ex);
                }

                Thread.Sleep(millisecondsTimeout: 500);
            }
        }
    }

    /// <summary>
    /// Records that this cache root was used so <see cref="PruneStaleEntries"/> keeps it alive.
    /// </summary>
    private void Touch()
    {
        try
        {
            File.WriteAllText(Path.Combine(CacheDirectory, "last-used.txt"), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Best effort: a concurrent process may be writing the same file.
        }
    }

    /// <summary>
    /// Best effort deletion of cache roots for other keys (and their lock files) that have not
    /// been used recently. These accumulate as fixture recipes and SDKs change over time.
    /// </summary>
    internal void PruneStaleEntries()
    {
        var baseDirectory = Path.GetDirectoryName(CacheDirectory)!;
        foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
        {
            if (string.Equals(directory, CacheDirectory, StringComparison.Ordinal) ||
                string.Equals(directory, _locksDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (DateTime.UtcNow - GetLastUsedTime(directory) > s_pruneAge)
                {
                    ReadOnlyDirectoryScope.EnsureWritable(directory);
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best effort: the directory may be in use by another test process.
            }
        }

        foreach (var lockFilePath in Directory.EnumerateFiles(_locksDirectory))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFilePath) > s_pruneAge)
                {
                    File.Delete(lockFilePath);
                }
            }
            catch
            {
                // Best effort: the lock may be held by another test process.
            }
        }

        static DateTime GetLastUsedTime(string directory)
        {
            var lastUsedFilePath = Path.Combine(directory, "last-used.txt");
            if (File.Exists(lastUsedFilePath) &&
                DateTime.TryParse(File.ReadAllText(lastUsedFilePath), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastUsed))
            {
                return lastUsed;
            }

            return Directory.GetCreationTimeUtc(directory);
        }
    }
}
