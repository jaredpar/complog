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

public readonly struct BasicAnalyzersOptions
{
    public static BasicAnalyzersOptions Default { get; } = new BasicAnalyzersOptions(BasicAnalyzersKind.InMemory, cacheable: true);

    public BasicAnalyzersKind Kind { get; }

    public AssemblyLoadContext CompilerLoadContext { get; }

    /// <summary>
    /// When true requests for the exact same set of analyzers will return 
    /// the same <see cref="BasicAnalyzers"/> instance.
    /// </summary>
    public bool Cachable { get; }

    public BasicAnalyzersOptions(
        BasicAnalyzersKind kind,
        bool cacheable = true,
        AssemblyLoadContext? compilerLoadContext = null)
    {
        Kind = kind;
        CompilerLoadContext = CommonUtil.GetAssemblyLoadContext(compilerLoadContext);
        Cachable = cacheable;
    }
}

/// <summary>
/// Controls how analyzers (and generators) are loaded 
/// </summary>
public enum BasicAnalyzersKind
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
    private int _refCount;

    public BasicAnalyzersKind Kind { get; }
    public AssemblyLoadContext AssemblyLoadContext { get; }
    public ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }

    public bool IsDisposed => _refCount <= 0;

    protected BasicAnalyzers(
        BasicAnalyzersKind kind,
        AssemblyLoadContext loadContext,
        ImmutableArray<AnalyzerReference> analyzerReferences)
    {
        Kind = kind;
        AssemblyLoadContext = loadContext;
        AnalyzerReferences = analyzerReferences;
        _refCount = 1;
    }

    internal void Increment()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(BasicAnalyzers));
        }

        _refCount++;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        _refCount--;
        if (_refCount == 0)
        {
            AssemblyLoadContext.Unload();
            DisposeCore();
        }
    }

    public abstract void DisposeCore();
}
