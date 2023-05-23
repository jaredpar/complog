using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util;

public readonly struct BasicAnalyzersOptions
{
    public static BasicAnalyzersOptions Default { get; } = new BasicAnalyzersOptions(BasicAnalyzersKind.Default, cacheable: true);

    public static BasicAnalyzersKind RuntimeDefaultKind
    {
        get
        {
#if NETCOREAPP
            return BasicAnalyzersKind.InMemory;
#else
            return BasicAnalyzersKind.OnDisk;
#endif
        }

    }


    public BasicAnalyzersKind Kind { get; }

    /// <summary>
    /// In the case analyzers are realized on disk for evaluation this is the base directory they should 
    /// be in.
    /// </summary>
    public string? AnalyzerDirectory { get; }

    /// <summary>
    /// When true requests for the exact same set of analyzers will return 
    /// the same <see cref="BasicAnalyzers"/> instance.
    /// </summary>
    public bool Cacheable { get; }

    internal BasicAnalyzersKind ResolvedKind => Kind switch
    {
        BasicAnalyzersKind.Default => RuntimeDefaultKind,
        _ => Kind
    };

#if NETCOREAPP

    public AssemblyLoadContext CompilerLoadContext { get; }

    public BasicAnalyzersOptions(
        AssemblyLoadContext compilerLoadContext,
        BasicAnalyzersKind kind,
        string? analyzerDirectory = null,
        bool cacheable = true)
    {
        Kind = kind;
        AnalyzerDirectory = analyzerDirectory;
        CompilerLoadContext = compilerLoadContext;
        Cacheable = cacheable;
    }

#endif

    public BasicAnalyzersOptions(
        BasicAnalyzersKind kind,
        string? analyzerDirectory = null,
        bool cacheable = true)
    {
        Kind = kind;
        AnalyzerDirectory = analyzerDirectory;
        Cacheable = cacheable;

#if NETCOREAPP
        CompilerLoadContext = CommonUtil.GetAssemblyLoadContext(null);
#endif
    }

    public string GetAnalyzerDirectory(string name)
    {
        var basePath = AnalyzerDirectory ?? Path.Combine(Path.GetTempPath(), "Basic.CompilerLog");
        return Path.Combine(basePath, name);
    }
}

/// <summary>
/// Controls how analyzers (and generators) are loaded 
/// </summary>
public enum BasicAnalyzersKind
{
    /// <summary>
    /// Default for the current runtime
    /// </summary>
    Default = 0,

    /// <summary>
    /// Analyzers are loaded in memory and disk is not used. 
    /// </summary>
    InMemory = 1,

    /// <summary>
    /// Analyzers are written to disk and loaded from there. This will produce as a 
    /// side effect <see cref="AnalyzerFileReference"/> instances. 
    /// </summary>
    OnDisk = 2,
}

/// <summary>
/// The set of analyzers loaded for a given <see cref="Compilation"/>
/// </summary>
public abstract class BasicAnalyzers : IDisposable
{
    private int _refCount;

    public BasicAnalyzersKind Kind { get; }
    public ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }

    public bool IsDisposed => _refCount <= 0;

    protected BasicAnalyzers(
        BasicAnalyzersKind kind,
        ImmutableArray<AnalyzerReference> analyzerReferences)
    {
        Kind = kind;
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
            DisposeCore();
        }
    }

    public abstract void DisposeCore();

    public static bool IsSupported(BasicAnalyzersKind kind)
    {
#if NETCOREAPP
        return true;
#else
        return kind is BasicAnalyzersKind.OnDisk or BasicAnalyzersKind.Default;
#endif
    }
}

