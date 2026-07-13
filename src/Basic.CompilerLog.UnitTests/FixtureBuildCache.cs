using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// Caches the output of the individual <c>dotnet</c> commands that the test fixtures run to
/// produce their scratch directories. The output of these commands is deterministic for a given
/// SDK and set of inputs, so once a machine has run them they can be reused by every subsequent
/// test run.
/// </summary>
/// <remarks>
/// Every fixture command is cached separately:
///
///   key   = SDK version (from the nearest global.json) + command line + working directory +
///           checksums of the files in the working directory (excluding bin / obj / *.binlog
///           which are outputs, not inputs)
///   value = the files the command added or changed, captured by diffing the directory around
///           the command
///
/// For a `dotnet new` in a fresh directory the key degenerates to just the SDK version and the
/// command line. For a `dotnet build` the key is the SDK version plus the content of the
/// directory being built. Because keys are derived from the actual inputs of each command,
/// editing one fixture recipe only re-runs the commands whose inputs changed, and editing
/// unrelated test code invalidates nothing.
///
/// Fixture directories live at a stable machine level path because the generated binlogs and
/// project files bake in absolute paths:
///
///   %TMP%/Basic.CompilerLog.UnitTests/build-cache/
///     locks/            lock files, kept outside the directories they guard
///     store/{key}/      cached command output (manifest.txt + files/ tree)
///     netcore/, netfx/  fixture directories per test runtime (<see cref="CacheDirectory"/>)
///
/// A fixture build replays its recipe on every run via <see cref="RunBuild"/>. When a sentinel
/// from a previous run records the same command sequence and the directory content matches, the
/// replay is a cheap verification (no processes, no copies). Otherwise the directory is rebuilt,
/// with each command either restored from the store or actually executed.
///
/// The cache root is held under an exclusive lock for the lifetime of the test process. A second
/// concurrent test process of the same runtime simply runs uncached in its per run temp
/// directory. Set the <c>COMPLOG_TEST_BUILD_CACHE</c> environment variable to <c>0</c> to disable
/// caching entirely. In GitHub Actions the cache is disabled by default (the machines are fresh
/// so it would never hit); set the variable to <c>1</c> to opt in there.
/// </remarks>
internal sealed class FixtureBuildCache
{
    internal const string EnvironmentVariableName = "COMPLOG_TEST_BUILD_CACHE";

    /// <summary>
    /// Store entries (and stale cache directories from older layouts) that have not been used for
    /// this long are deleted on startup.
    /// </summary>
    private static readonly TimeSpan s_pruneAge = TimeSpan.FromDays(7);

    private static readonly Lazy<(FixtureBuildCache? Instance, string? DisabledReason)> s_lazyInstance = new(CreateInstance);

    /// <summary>
    /// Ambient state for the fixture build that is currently replaying on this thread / async
    /// flow. Set by <see cref="RunBuild"/> and consumed by <see cref="RunCommand"/>.
    /// </summary>
    private static readonly AsyncLocal<EntryContext?> s_entryContext = new();

    /// <summary>
    /// Lock that gives this process exclusive use of the cache root for its runtime. Held until
    /// the process exits.
    /// </summary>
    private static FileStream? s_cacheRootLock;

    /// <summary>
    /// The shared cache for this test process, or null when caching is disabled (see
    /// <see cref="DisabledReason"/>).
    /// </summary>
    internal static FixtureBuildCache? Instance => s_lazyInstance.Value.Instance;

    internal static string? DisabledReason => s_lazyInstance.Value.DisabledReason;

    /// <summary>
    /// The stable directory holding the fixture directories for this test runtime. Fixtures place
    /// their scratch directories under this path.
    /// </summary>
    internal string CacheDirectory { get; }

    private readonly string _storeDirectory;

    private enum ReplayMode
    {
        /// <summary>
        /// The directory was materialized by a previous run. Commands are recorded but not
        /// executed; afterwards the recorded state is compared against the sentinel.
        /// </summary>
        Verify,

        /// <summary>
        /// The directory is being built. Commands are restored from the store when their key
        /// hits, executed otherwise.
        /// </summary>
        Build,
    }

    private sealed class EntryContext(ReplayMode mode)
    {
        internal ReplayMode Mode { get; } = mode;
        internal List<(string WorkingDirectory, string Args)> Commands { get; } = [];
        internal List<string> CommandKeys { get; } = [];
        internal List<string> OutputFilePaths { get; } = [];
    }

    private sealed class SentinelData
    {
        internal string SdkVersion = "";
        internal string FinalHash = "";
        internal List<(string WorkingDirectory, string Args)> Commands { get; } = [];
        internal List<string> CommandKeys { get; } = [];
        internal List<string> OutputFilePaths { get; } = [];
    }

    internal FixtureBuildCache(string baseDirectory, string name)
    {
        CacheDirectory = Path.Combine(baseDirectory, name);
        _storeDirectory = Path.Combine(baseDirectory, "store");
        _ = Directory.CreateDirectory(CacheDirectory);
        _ = Directory.CreateDirectory(_storeDirectory);
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

            var runtime = TestUtil.IsNetCore ? "netcore" : "netfx";
            var baseDirectory = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.UnitTests", "build-cache");
            var locksDirectory = Path.Combine(baseDirectory, "locks");
            _ = Directory.CreateDirectory(locksDirectory);

            // The fixture directories are written to on every run (the recipes replay in place)
            // so they cannot be shared between concurrent test processes. Take the root lock for
            // the lifetime of this process; a concurrent process falls back to uncached builds.
            var lockFilePath = Path.Combine(locksDirectory, $"{runtime}.lock");
            try
            {
                s_cacheRootLock = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                // Update the write time so pruning can tell this lock file is still in use.
                s_cacheRootLock.WriteByte(0);
                s_cacheRootLock.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, "another test process is currently using the build cache");
            }

            var cache = new FixtureBuildCache(baseDirectory, runtime);
            cache.PruneStaleEntries();
            return (cache, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Replays the fixture recipe in <paramref name="action"/> against <paramref name="buildDirectory"/>.
    /// When a previous run materialized the directory with the same recipe this only verifies the
    /// state (no processes are spawned, nothing is copied) and returns true. Otherwise the
    /// directory is rebuilt with each command restored from the store or executed.
    /// <paramref name="buildDirectory"/> must be under <see cref="CacheDirectory"/>.
    /// </summary>
    internal bool RunBuild(string buildDirectory, Action<string> action)
    {
        Debug.Assert(buildDirectory.StartsWith(CacheDirectory, StringComparison.Ordinal));
        var sentinelFilePath = buildDirectory + ".complete";

        // A run that crashed while the fixture held its ReadOnlyDirectoryScope leaves the files
        // read-only on disk which would break the replay writes below.
        ReadOnlyDirectoryScope.EnsureWritable(buildDirectory);

        if (Directory.Exists(buildDirectory) && TryReadSentinel(sentinelFilePath) is { } sentinel)
        {
            var verifyContext = new EntryContext(ReplayMode.Verify);
            bool verified;
            try
            {
                RunWithContext(verifyContext, action, buildDirectory);
                verified = VerifySentinel(sentinel, verifyContext, buildDirectory);
            }
            catch
            {
                // The recipe no longer replays cleanly against the existing directory (for
                // example it reads a file a previous recipe version created). Rebuild from
                // scratch below; a genuine recipe bug will throw again there and propagate.
                verified = false;
            }

            if (verified)
            {
                foreach (var key in sentinel.CommandKeys)
                {
                    Touch(Path.Combine(_storeDirectory, key));
                }

                return true;
            }
        }

        if (File.Exists(sentinelFilePath))
        {
            File.Delete(sentinelFilePath);
        }

        if (Directory.Exists(buildDirectory))
        {
            ReadOnlyDirectoryScope.EnsureWritable(buildDirectory);
            Directory.Delete(buildDirectory, recursive: true);
        }

        _ = Directory.CreateDirectory(buildDirectory);
        var buildContext = new EntryContext(ReplayMode.Build);
        RunWithContext(buildContext, action, buildDirectory);
        WriteSentinel(sentinelFilePath, buildContext, buildDirectory);
        return false;

        static void RunWithContext(EntryContext context, Action<string> action, string buildDirectory)
        {
            var saved = s_entryContext.Value;
            s_entryContext.Value = context;
            try
            {
                action(buildDirectory);
            }
            finally
            {
                s_entryContext.Value = saved;
            }
        }
    }

    /// <summary>
    /// Runs a single dotnet command with caching. Outside of a <see cref="RunBuild"/> replay this
    /// just invokes <paramref name="runProcess"/>. During a replay the command is verified,
    /// restored from the store, or executed and recorded.
    /// </summary>
    internal ProcessResult RunCommand(string args, string workingDirectory, Func<ProcessResult> runProcess)
    {
        if (s_entryContext.Value is not { } context)
        {
            return runProcess();
        }

        context.Commands.Add((workingDirectory, args));
        if (context.Mode == ReplayMode.Verify)
        {
            // The directory already holds the output of this command from a previous run. Whether
            // that state is still valid is decided at the end of the replay by comparing the
            // command sequence and directory content against the sentinel.
            return new ProcessResult(exitCode: 0, standardOut: "(replayed from build cache)", standardError: "");
        }

        var key = ComputeCommandKey(args, workingDirectory);
        context.CommandKeys.Add(key);
        var keyDirectory = Path.Combine(_storeDirectory, key);
        if (TryRestoreCommandOutputs(keyDirectory, workingDirectory, context.OutputFilePaths))
        {
            Touch(keyDirectory);
            return new ProcessResult(exitCode: 0, standardOut: $"(restored from build cache {key})", standardError: "");
        }

        var snapshot = SnapshotDirectory(workingDirectory);
        var result = runProcess();
        if (result.Succeeded)
        {
            StoreCommandOutputs(keyDirectory, args, workingDirectory, snapshot, context.OutputFilePaths);
            Touch(keyDirectory);
        }

        return result;
    }

    /// <summary>
    /// The key that identifies what a command produces: the SDK it runs on, the command line and
    /// the input files it can observe. Build outputs (bin, obj, binlogs) are excluded because
    /// they are products of the cached commands themselves.
    /// </summary>
    private static string ComputeCommandKey(string args, string workingDirectory)
    {
        var sdkVersion = TryReadSdkVersion(workingDirectory) ?? "none";
        var builder = new StringBuilder();
        builder.AppendLine($"sdk={sdkVersion}");
        builder.AppendLine($"args={args}");
        builder.AppendLine($"workingDirectory={workingDirectory}");
        foreach (var (relativePath, hash) in HashInputFiles(workingDirectory))
        {
            builder.AppendLine($"{hash} {relativePath}");
        }

        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return ToHexString(hashBytes, length: 32);
    }

    /// <summary>
    /// Reads the pinned SDK version from the nearest global.json at or above
    /// <paramref name="directory"/>, the same way the dotnet CLI resolves it.
    /// </summary>
    internal static string? TryReadSdkVersion(string directory)
    {
        var current = directory;
        while (current is not null)
        {
            var globalJsonPath = Path.Combine(current, "global.json");
            if (File.Exists(globalJsonPath))
            {
                var match = Regex.Match(File.ReadAllText(globalJsonPath), @"""version""\s*:\s*""([^""]+)""");
                return match.Success ? match.Groups[1].Value : null;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// The content of the input files under <paramref name="directory"/>, sorted so the result is
    /// stable. Excludes build outputs.
    /// </summary>
    private static List<(string RelativePath, string Hash)> HashInputFiles(string directory)
    {
        var list = new List<(string RelativePath, string Hash)>();
        using var sha = SHA256.Create();
        foreach (var relativePath in EnumerateRelativeFiles(directory))
        {
            if (IsBuildOutput(relativePath))
            {
                continue;
            }

            using var stream = File.OpenRead(Path.Combine(directory, relativePath));
            list.Add((relativePath, ToHexString(sha.ComputeHash(stream), length: 64)));
        }

        list.Sort((x, y) => string.CompareOrdinal(x.RelativePath, y.RelativePath));
        return list;
    }

    private static bool IsBuildOutput(string relativePath)
    {
        if (Path.GetExtension(relativePath) is ".binlog")
        {
            return true;
        }

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateRelativeFiles(string directory)
    {
        var prefixLength = directory.Length + 1;
        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            yield return filePath.Substring(prefixLength);
        }
    }

    /// <summary>
    /// Hash of the non-output content of the build directory. Used to detect that a replay of the
    /// recipe produced (or would produce) a different state than the last materialization.
    /// </summary>
    private static string ComputeContentHash(string directory)
    {
        var builder = new StringBuilder();
        foreach (var (relativePath, hash) in HashInputFiles(directory))
        {
            builder.AppendLine($"{hash} {relativePath}");
        }

        using var sha = SHA256.Create();
        return ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())), length: 32);
    }

    private static string ToHexString(byte[] bytes, int length)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            _ = builder.Append(b.ToString("x2"));
        }

        return builder.ToString(0, Math.Min(length, builder.Length));
    }

    private static Dictionary<string, (long Length, DateTime LastWriteTimeUtc)> SnapshotDirectory(string directory)
    {
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        foreach (var relativePath in EnumerateRelativeFiles(directory))
        {
            var info = new FileInfo(Path.Combine(directory, relativePath));
            map[relativePath] = (info.Length, info.LastWriteTimeUtc);
        }

        return map;
    }

    /// <summary>
    /// Records the files that a command added or changed into the store. The layout is a
    /// manifest.txt with one "{length}\t{relativePath}" line per file plus the file content under
    /// files/.
    /// </summary>
    private void StoreCommandOutputs(
        string keyDirectory,
        string args,
        string workingDirectory,
        Dictionary<string, (long Length, DateTime LastWriteTimeUtc)> snapshot,
        List<string> outputFilePaths)
    {
        var tempDirectory = keyDirectory + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            var filesDirectory = Path.Combine(tempDirectory, "files");
            _ = Directory.CreateDirectory(filesDirectory);
            var manifest = new StringBuilder();
            foreach (var relativePath in EnumerateRelativeFiles(workingDirectory))
            {
                var filePath = Path.Combine(workingDirectory, relativePath);
                var info = new FileInfo(filePath);
                if (snapshot.TryGetValue(relativePath, out var before) &&
                    before.Length == info.Length &&
                    before.LastWriteTimeUtc == info.LastWriteTimeUtc)
                {
                    continue;
                }

                var blobPath = Path.Combine(filesDirectory, relativePath);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                File.Copy(filePath, blobPath);
                _ = manifest.AppendLine($"{info.Length}\t{relativePath}");
                outputFilePaths.Add(filePath);
            }

            File.WriteAllText(Path.Combine(tempDirectory, "manifest.txt"), manifest.ToString());
            File.WriteAllText(
                Path.Combine(tempDirectory, "command.txt"),
                $"args={args}{Environment.NewLine}workingDirectory={workingDirectory}{Environment.NewLine}");

            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }

            Directory.Move(tempDirectory, keyDirectory);
        }
        catch
        {
            // Best effort: losing a store entry only costs a rebuild on a later run.
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Copies the recorded output of a command into <paramref name="workingDirectory"/>. Returns
    /// false when there is no (complete) store entry for the key.
    /// </summary>
    private static bool TryRestoreCommandOutputs(string keyDirectory, string workingDirectory, List<string> outputFilePaths)
    {
        var manifestPath = Path.Combine(keyDirectory, "manifest.txt");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var entries = new List<string>();
        foreach (var line in File.ReadAllLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tabIndex = line.IndexOf('\t');
            var relativePath = line.Substring(tabIndex + 1);
            if (!File.Exists(Path.Combine(keyDirectory, "files", relativePath)))
            {
                return false;
            }

            entries.Add(relativePath);
        }

        foreach (var relativePath in entries)
        {
            var targetPath = Path.Combine(workingDirectory, relativePath);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(targetPath))
            {
                // Clear stale read-only attributes so the copy below can overwrite.
                File.SetAttributes(targetPath, FileAttributes.Normal);
            }

            File.Copy(Path.Combine(keyDirectory, "files", relativePath), targetPath, overwrite: true);
            outputFilePaths.Add(targetPath);
        }

        return true;
    }

    private const string SentinelVersion = "2";

    private static void WriteSentinel(string sentinelFilePath, EntryContext context, string buildDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"version={SentinelVersion}");
        builder.AppendLine($"sdk={TryReadSdkVersion(buildDirectory) ?? "none"}");
        builder.AppendLine($"finalHash={ComputeContentHash(buildDirectory)}");
        foreach (var (workingDirectory, args) in context.Commands)
        {
            builder.AppendLine($"command={workingDirectory}\t{args}");
        }

        foreach (var key in context.CommandKeys)
        {
            builder.AppendLine($"key={key}");
        }

        foreach (var outputFilePath in context.OutputFilePaths)
        {
            // A later command in the recipe may have replaced or removed an earlier command's
            // output; only record the files that survived so verification checks real state.
            if (File.Exists(outputFilePath))
            {
                builder.AppendLine($"output={outputFilePath}");
            }
        }

        File.WriteAllText(sentinelFilePath, builder.ToString());
    }

    private static SentinelData? TryReadSentinel(string sentinelFilePath)
    {
        if (!File.Exists(sentinelFilePath))
        {
            return null;
        }

        var data = new SentinelData();
        var sawVersion = false;
        foreach (var line in File.ReadAllLines(sentinelFilePath))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var name = line.Substring(0, separatorIndex);
            var value = line.Substring(separatorIndex + 1);
            switch (name)
            {
                case "version":
                    sawVersion = value == SentinelVersion;
                    break;
                case "sdk":
                    data.SdkVersion = value;
                    break;
                case "finalHash":
                    data.FinalHash = value;
                    break;
                case "command":
                    var tabIndex = value.IndexOf('\t');
                    if (tabIndex < 0)
                    {
                        return null;
                    }

                    data.Commands.Add((value.Substring(0, tabIndex), value.Substring(tabIndex + 1)));
                    break;
                case "key":
                    data.CommandKeys.Add(value);
                    break;
                case "output":
                    data.OutputFilePaths.Add(value);
                    break;
            }
        }

        return sawVersion ? data : null;
    }

    /// <summary>
    /// True when the verify replay matched the recorded materialization: same SDK, same command
    /// sequence, same directory content, and every recorded output file still present.
    /// </summary>
    private static bool VerifySentinel(SentinelData sentinel, EntryContext context, string buildDirectory)
    {
        if (sentinel.SdkVersion != (TryReadSdkVersion(buildDirectory) ?? "none"))
        {
            return false;
        }

        if (sentinel.Commands.Count != context.Commands.Count)
        {
            return false;
        }

        for (var i = 0; i < sentinel.Commands.Count; i++)
        {
            if (sentinel.Commands[i] != context.Commands[i])
            {
                return false;
            }
        }

        // The replay has already re-applied every file write, so a changed recipe shows up as a
        // different content hash even when the command lines are unchanged.
        if (sentinel.FinalHash != ComputeContentHash(buildDirectory))
        {
            return false;
        }

        foreach (var outputFilePath in sentinel.OutputFilePaths)
        {
            if (!File.Exists(outputFilePath))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Records that a store entry was used so <see cref="PruneStaleEntries"/> keeps it alive.
    /// </summary>
    private static void Touch(string keyDirectory)
    {
        try
        {
            if (Directory.Exists(keyDirectory))
            {
                File.WriteAllText(Path.Combine(keyDirectory, "last-used.txt"), DateTime.UtcNow.ToString("O"));
            }
        }
        catch
        {
            // Best effort: a concurrent process may be writing the same file.
        }
    }

    /// <summary>
    /// Best effort deletion of store entries, lock files and cache directories from older layouts
    /// that have not been used recently.
    /// </summary>
    internal void PruneStaleEntries()
    {
        var baseDirectory = Path.GetDirectoryName(CacheDirectory)!;
        var locksDirectory = Path.Combine(baseDirectory, "locks");
        foreach (var directory in Directory.EnumerateDirectories(_storeDirectory))
        {
            PruneDirectory(directory);
        }

        // Directories other than the per runtime fixture roots, the store and the locks are
        // left over from older cache layouts.
        foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name is "netcore" or "netfx" or "store" or "locks")
            {
                continue;
            }

            PruneDirectory(directory);
        }

        if (Directory.Exists(locksDirectory))
        {
            foreach (var lockFilePath in Directory.EnumerateFiles(locksDirectory))
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
        }

        static void PruneDirectory(string directory)
        {
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
