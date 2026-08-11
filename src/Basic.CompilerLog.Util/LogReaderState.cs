#if NET
using System.Runtime.Loader;
#endif

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Basic.CompilerLog.Util.Impl;

namespace Basic.CompilerLog.Util;

/// <summary>
/// The <see cref="CompilationData"/> have underlying state associated with them: 
///     - File system entries to hold crypto key files
///     - <see cref="BasicAnalyzerHost"/> which control loaded analyzers
///
/// Rather than have each <see cref="CompilationData"/> maintain it's own and state
/// and be disposable, all of it is stored here. Generally this is implicitly tied
/// to the lifetime of a <see cref="CompilerLogReader"/> but this can be explicitly
/// managed in cases where <see cref="CompilationData"/> live longer than the 
/// underlying reader.
/// </summary>
public sealed class LogReaderState : IDisposable
{
    private readonly Dictionary<string, BasicAnalyzerHost>? _analyzersMap;
    private FileStream? _lockFileStream;

    /// <summary>
    /// Should instances of <see cref="BasicAnalyzerHost" /> be cached and re-used
    /// </summary>
    internal bool CacheAnalyzers => _analyzersMap is not null;

    /// <summary>
    /// This is the base directory that is used for any on disk assets that need to be created
    /// by compiler logs. This is typically used for crypto key files and analyzers.
    /// </summary>
    internal string BaseDirectory { get; }

    /// <summary>
    /// The compiler supports strong named keys that exist on disk. In order for compilation to succeed at the 
    /// Emit section, even for some binding purposes, that file must continue to exist on disk when the project
    /// is re-hydrated.
    /// </summary>
    public string CryptoKeyFileDirectory { get; }

    /// <summary>
    /// In the case analyzers are realized on disk for evaluation this is the base directory they should 
    /// be in.
    /// </summary>
    public string AnalyzerDirectory { get; }

    public bool IsDisposed { get; private set;}

    /// <summary>
    /// Controls whether ReadyToRun (R2R) native code is stripped from analyzer assemblies before use.
    /// <list type="bullet">
    ///   <item><description><see langword="null"/> (default): strip only when the assembly targets a
    ///     different architecture than the current process.</description></item>
    ///   <item><description><see langword="true"/>: always strip R2R native code.</description></item>
    ///   <item><description><see langword="false"/>: never strip; load assemblies as stored in the log.</description></item>
    /// </list>
    /// </summary>
    public bool? StripReadyToRun { get; set; }

    internal List<BasicAnalyzerHost> BasicAnalyzerHosts { get; } = new();

#if NET

    public AssemblyLoadContext CompilerLoadContext { get; }

    /// <summary>
    /// Create a new instance of the compiler log state
    /// </summary>
    /// <param name="baseDir">The base path that should be used to create <see cref="CryptoKeyFileDirectory"/>
    /// and <see cref="AnalyzerDirectory"/> paths</param>
    /// <param name="compilerLoadContext">The <see cref="AssemblyLoadContext"/> that should be used to load
    /// <param name="cacheAnalyzers">Should analyzers be cached</param>
    /// analyzers</param>
    /// <param name="stripReadyToRun">See <see cref="StripReadyToRun"/></param>
    public LogReaderState(AssemblyLoadContext? compilerLoadContext, string? baseDir = null, bool cacheAnalyzers = true, bool? stripReadyToRun = null)
        : this(baseDir, cacheAnalyzers, stripReadyToRun)
    {
        CompilerLoadContext = CommonUtil.GetAssemblyLoadContext(compilerLoadContext);
    }

#endif

    /// <summary>
    /// Create a new instance of the compiler log state
    /// </summary>
    /// <param name="baseDir">The base path that should be used to create <see cref="CryptoKeyFileDirectory"/>
    /// and <see cref="AnalyzerDirectory"/> paths</param>
    /// <param name="cacheAnalyzers">Should analyzers be cached</param>
    /// <param name="stripReadyToRun">See <see cref="StripReadyToRun"/></param>
    public LogReaderState(string? baseDir = null, bool cacheAnalyzers = true, bool? stripReadyToRun = null)
    {
        var dirName = Guid.NewGuid().ToString("N");
        BaseDirectory = baseDir ?? Path.Combine(CommonUtil.GetCompilerLogTempDirectory(), dirName);
        CryptoKeyFileDirectory = Path.Combine(BaseDirectory, "CryptoKeys");
        AnalyzerDirectory = Path.Combine(BaseDirectory, "Analyzers");
        StripReadyToRun = stripReadyToRun;
#if NET
        CompilerLoadContext = CommonUtil.GetAssemblyLoadContext(null);
#endif
        if (cacheAnalyzers)
        {
            _analyzersMap = new();
        }

        // Only create a lock file and run cleanup when using the default shared temp directory.
        // Custom base directories are managed by the caller and don't participate in the
        // lock-based cleanup protocol.
        if (baseDir is null)
        {
            // Create the lock file FIRST in the shared locks directory. This ensures that
            // cleanup cannot race with directory creation — the lock is held before the
            // working directory exists.
            var locksDir = CommonUtil.GetLocksDirectory();
            Directory.CreateDirectory(locksDir);

            // Hold the lock file open with FileShare.None for the lifetime of this instance so the
            // cleanup probe (see CommonUtil.CleanupStaleTempDirectories) can use an exclusive open
            // as a liveness test.
            //
            // On Windows we also pass FileOptions.DeleteOnClose so the file is removed atomically
            // when the handle closes — including on a hard crash, where the kernel enforces it. That
            // removes the release/delete window that previously let cleanup race with disposal.
            //
            // On Unix we must NOT use DeleteOnClose here: combining it with FileShare.None disables
            // share enforcement (dotnet/runtime#59995), which would let the probe open a live owner's
            // lock and wrongly delete an active directory. Instead the file is deleted manually in
            // Dispose. Unix has no delete-pending semantics, so the manual delete is race-free, and a
            // crash leaves the file behind for the probe to reclaim (the OS releases its advisory lock
            // on process death).
            var lockFileOptions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? FileOptions.DeleteOnClose
                : FileOptions.None;
            _lockFileStream = new FileStream(
                Path.Combine(locksDir, dirName + ".lock"),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                lockFileOptions);

            // Now create the working directory
            Directory.CreateDirectory(BaseDirectory);

            // Clean up stale sibling directories from previous invocations that didn't
            // get a chance to clean up (e.g. process crash).
            var parentDir = Path.GetDirectoryName(BaseDirectory);
            if (parentDir is not null)
            {
                CommonUtil.CleanupStaleTempDirectories(parentDir);
            }
        }
        else
        {
            Directory.CreateDirectory(BaseDirectory);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        foreach (var host in BasicAnalyzerHosts)
        {
            host.Dispose();
        }

        // It's important to clear out this map as the BasicAnalyzerHost can maintain 
        // a reference to the AssemblyLoadContext which could prevent it from fully
        // unloading.
        BasicAnalyzerHosts.Clear();

        // Similarly need to drop references to Analyzers which could be holding onto
        // an AssemblyLoadContext
        _analyzersMap?.Clear();

        try
        {
            if (Directory.Exists(CryptoKeyFileDirectory))
            {
                Directory.Delete(CryptoKeyFileDirectory, recursive: true);
            }

            // It's expected that some hosts will clean up their directories asynchronously. Both
            // this type and the hosts need to attempt to clean up the base directory.
            CommonUtil.DeleteDirectoryIfEmpty(BaseDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            // Parent directory was already deleted (e.g. by test cleanup). Expected.
        }
        catch (Exception)
        {
            // Nothing to do if we can't delete the directories. This is best-effort cleanup and
            // concurrent instances may already be removing these directories.
        }

        // Release the lock file AFTER cleaning up the base directory. The lock must be held until
        // we're done with the directory so cleanup won't race with us.
        //
        // On Windows the stream was opened with FileOptions.DeleteOnClose, so disposing it removes
        // the lock file atomically — no separate File.Delete (and its associated sharing race) is
        // needed. On Unix DeleteOnClose is not used (see the ctor), so delete the file manually.
        if (_lockFileStream is not null)
        {
            var lockFilePath = _lockFileStream.Name;
            _lockFileStream.Dispose();
            _lockFileStream = null;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    File.Delete(lockFilePath);
                }
                catch (Exception)
                {
                    // Best effort. A concurrent cleanup pass may have already reclaimed it.
                }
            }
        }
    }

    internal BasicAnalyzerHost GetOrCreateBasicAnalyzerHost(
        IBasicAnalyzerHostDataProvider dataProvider,
        BasicAnalyzerKind kind,
        CompilerCall compilerCall)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(LogReaderState));
        }

        BasicAnalyzerHost? basicAnalyzerHost;
        string? key = null;
        var analyzers = dataProvider.ReadAllAnalyzerData(compilerCall);

        // The None kind is not cached because there is no real advantage to it. Caching is only
        // useful to stop lots of 3rd party assemblies from loading over and over again. The 
        // none host has a very simple in memory analyzer that doesn't need to be cached.
        if (CacheAnalyzers && (kind == BasicAnalyzerKind.InMemory || kind == BasicAnalyzerKind.OnDisk))
        {
            key = GetKey(analyzers);
            if (_analyzersMap!.TryGetValue(key, out basicAnalyzerHost))
            {
                return basicAnalyzerHost;
            }
        }

        basicAnalyzerHost = BasicAnalyzerHost.Create(dataProvider, kind, compilerCall, analyzers);
        BasicAnalyzerHosts.Add(basicAnalyzerHost);

        if (key is not null)
        {
            Debug.Assert(_analyzersMap is not null);
            _analyzersMap![key] = basicAnalyzerHost;
        }

        return basicAnalyzerHost;

        static string GetKey(List<AnalyzerData> analyzers)
        {
            var builder = new StringBuilder();
            foreach (var analyzer in analyzers.OrderBy(x => x.Mvid))
            {
                _ = builder.AppendLine($"{analyzer.Mvid}");
            }
            return builder.ToString();
        }
    }
}
