#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util;

// TODO: update this
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
/// 
/// This differs from <see cref="CompilerLogReaderOptions"/> in that it holds actual
/// state. Nothing here is really serializable between compilations. It must be 
/// created and managed by the caller.
/// </summary>
public sealed class CompilerLogState : IDisposable
{
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

    internal List<BasicAnalyzerHost> BasicAnalyzerHosts { get; } = new();

#if NETCOREAPP

    public AssemblyLoadContext CompilerLoadContext { get; }

    /// <summary>
    /// Create a new instance of the compiler log state
    /// </summary>
    /// <param name="baseDir">The base path that should be used to create <see cref="CryptoKeyFileDirectory"/>
    /// and <see cref="AnalyzerDirectory"/> paths</param>
    /// <param name="compilerLoadContext">The <see cref="AssemblyLoadContext"/> that should be used to load
    /// analyzers</param>
    public CompilerLogState(AssemblyLoadContext? compilerLoadContext, string? baseDir = null)
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
    public CompilerLogState(string? baseDir = null)
    {
        BaseDirectory = baseDir ?? Path.Combine(Path.GetTempPath(), "Basic.CompilerLog", Guid.NewGuid().ToString("N"));
        CryptoKeyFileDirectory = Path.Combine(BaseDirectory, "CryptoKeys");
        AnalyzerDirectory = Path.Combine(BaseDirectory, "Analyzers");
#if NETCOREAPP
        CompilerLoadContext = CommonUtil.GetAssemblyLoadContext(null);
#endif
    }

    public void Dispose()
    {
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
}
