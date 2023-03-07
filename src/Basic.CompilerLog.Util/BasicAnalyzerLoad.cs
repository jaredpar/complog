using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Controls how analyzers (and generators) are loaded 
/// </summary>
public enum BasicAnalyzersOptions
{
    /// <summary>
    /// Analyzers are loaded in memory and disk is not used. 
    /// </summary>
    InMemory = 0,

    /// <summary>
    /// Analyzers are written to disk and loaded from there. This will produce as a 
    /// side effect <see cref="AnalyzerFileReference"/> instances. 
    /// TODO: can we have a cached version of this?
    /// </summary>
    OnDisk = 1,
}

/// <summary>
/// The set of analyzers loaded for a given <see cref="Compilation"/>
/// </summary>
public abstract class BasicAnalyzers : IDisposable
{
    public BasicAnalyzersOptions Options { get; }
    public AssemblyLoadContext AssemblyLoadContext { get; }
    public ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }

    protected BasicAnalyzers(
        BasicAnalyzersOptions options,
        AssemblyLoadContext loadContext,
        ImmutableArray<AnalyzerReference> analyzerReferences)
    {
        Options = options;
        AssemblyLoadContext = loadContext;
        AnalyzerReferences = analyzerReferences;
    }

    public void Dispose()
    {
        AssemblyLoadContext.Unload();
        DisposeCore();
    }

    public abstract void DisposeCore();
}
