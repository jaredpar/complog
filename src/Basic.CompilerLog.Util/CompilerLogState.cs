using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
public sealed class CompilerLogState : IDisposable
{
    /// <summary>
    /// The compiler supports strong named keys that exist on disk. In order for compilation to succeed at the 
    /// Emit section, even for some binding purposes, that file must continue to exist on disk when the project
    /// is re-hydrated.
    /// </summary>
    public string CryptoKeyFileDirectory { get; }

    internal List<BasicAnalyzerHost> BasicAnalyzerHosts { get; } = new();

    public CompilerLogState(string? cryptoKeyFileDirectoryBase = null)
    {
        cryptoKeyFileDirectoryBase ??= Path.GetTempPath();
        CryptoKeyFileDirectory = Path.Combine(cryptoKeyFileDirectoryBase, "Basic.CompilerLog", Guid.NewGuid().ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(CryptoKeyFileDirectory))
        {
            Directory.Delete(CryptoKeyFileDirectory, recursive: true);
        }

        foreach (var host in BasicAnalyzerHosts)
        {
            host.Dispose();
        }
    }
}
