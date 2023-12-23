using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
