#if NET
using System.Runtime.Loader;
#endif

using System.Diagnostics;
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

    /// <summary>
    /// Should instances of <see cref="BasicAnalyzerHost" /> be cached and re-used
    /// </summary>
    internal bool CacheAnalyzers => _analyzersMap is not null;

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
    public LogReaderState(AssemblyLoadContext? compilerLoadContext, string? baseDir = null, bool cacheAnalyzers = true)
        : this(baseDir)
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
    public LogReaderState(string? baseDir = null, bool cacheAnalyzers = true)
    {
        BaseDirectory = baseDir ?? Path.Combine(Path.GetTempPath(), "Basic.CompilerLog", Guid.NewGuid().ToString("N"));
        CryptoKeyFileDirectory = Path.Combine(BaseDirectory, "CryptoKeys");
        AnalyzerDirectory = Path.Combine(BaseDirectory, "Analyzers");
#if NET
        CompilerLoadContext = CommonUtil.GetAssemblyLoadContext(null);
#endif
        if (cacheAnalyzers)
        {
            _analyzersMap = new();
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

        try
        {
            if (Directory.Exists(BaseDirectory))
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Nothing to do if we can't delete the directories
        }
    }

    internal BasicAnalyzerHost GetOrCreateBasicAnalyzerHost(
        IBasicAnalyzerHostDataProvider dataProvider,
        BasicAnalyzerKind kind,
        CompilerCall compilerCall)
    {
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
