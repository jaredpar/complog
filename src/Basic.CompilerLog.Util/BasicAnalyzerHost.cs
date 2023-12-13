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
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Concurrent;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util;

public sealed class BasicAnalyzerHostOptions
{
    public static BasicAnalyzerHostOptions Default { get; } = new BasicAnalyzerHostOptions(BasicAnalyzerKind.Default, cacheable: true);

    public static BasicAnalyzerHostOptions None { get; } = new BasicAnalyzerHostOptions(BasicAnalyzerKind.None, cacheable: true);

    public static BasicAnalyzerKind RuntimeDefaultKind
    {
        get
        {
#if NETCOREAPP
            return BasicAnalyzerKind.InMemory;
#else
            return BasicAnalyzerKind.OnDisk;
#endif
        }

    }

    public BasicAnalyzerKind Kind { get; }

    /// <summary>
    /// In the case analyzers are realized on disk for evaluation this is the base directory they should 
    /// be in.
    /// </summary>
    public string? AnalyzerDirectory { get; }

    /// <summary>
    /// When true requests for the exact same set of analyzers will return 
    /// the same <see cref="BasicAnalyzerHost"/> instance.
    /// </summary>
    public bool Cacheable { get; }

    internal BasicAnalyzerKind ResolvedKind => Kind switch
    {
        BasicAnalyzerKind.Default => RuntimeDefaultKind,
        _ => Kind
    };

#if NETCOREAPP

    public AssemblyLoadContext CompilerLoadContext { get; }

    public BasicAnalyzerHostOptions(
        AssemblyLoadContext compilerLoadContext,
        BasicAnalyzerKind kind,
        string? analyzerDirectory = null,
        bool cacheable = true)
    {
        Kind = kind;
        AnalyzerDirectory = analyzerDirectory;
        CompilerLoadContext = compilerLoadContext;
        Cacheable = cacheable;
    }

#endif

    public BasicAnalyzerHostOptions(
        BasicAnalyzerKind kind,
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
public enum BasicAnalyzerKind
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

    /// <summary>
    /// Analyzers and generators from the original are not loaded at all. In the case 
    /// the original build had generated files they will be added through an in 
    /// memory analyzer that just adds them directly.
    /// </summary>
    /// <remarks>
    /// This option avoids loading third party analyzers and generators.
    /// </remarks>
    None = 3,
}

/// <summary>
/// The set of analyzers loaded for a given <see cref="Compilation"/>
/// </summary>
public abstract class BasicAnalyzerHost : IDisposable
{
    private readonly ConcurrentQueue<Diagnostic> _diagnostics = new();

    public BasicAnalyzerHostOptions Options { get; }
    public BasicAnalyzerKind Kind { get; }
    public ImmutableArray<AnalyzerReference> AnalyzerReferences
    {
        get
        {
            CheckDisposed();
            return AnalyzerReferencesCore;
        }
    }

    protected abstract ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    public bool IsDisposed { get; private set; }

    protected BasicAnalyzerHost(BasicAnalyzerKind kind, BasicAnalyzerHostOptions options)
    {
        Kind = kind;
        Options = options;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            DisposeCore();
        }
        finally
        {
            IsDisposed = true;
        }
    }

    protected abstract void DisposeCore();

    protected void CheckDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(BasicAnalyzerHost));
        }
    }

    protected void AddDiagnostic(Diagnostic diagnostic) => _diagnostics.Enqueue(diagnostic);

    /// <summary>
    /// Get the current set of diagnostics. This can change as analyzers can add them during 
    /// execution which can happen in parallel to analysis.
    /// </summary>
    public List<Diagnostic> GetDiagnostics() => _diagnostics.ToList();

    public static bool IsSupported(BasicAnalyzerKind kind)
    {
#if NETCOREAPP
        return true;
#else
        return kind is BasicAnalyzerKind.OnDisk or BasicAnalyzerKind.Default or BasicAnalyzerKind.None;
#endif
    }
}

