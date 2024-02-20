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

namespace Basic.CompilerLog.Util;

/// <summary>
/// Controls how analyzers (and generators) are loaded 
/// </summary>
public enum BasicAnalyzerKind
{
    /// <summary>
    /// Analyzers and generators from the original are not loaded at all. In the case 
    /// the original build had generated files they are just added directly to the
    /// compilation.
    /// </summary>
    /// <remarks>
    /// This option avoids loading third party analyzers and generators.
    /// </remarks>
    None = 0,

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
public abstract class BasicAnalyzerHost : IDisposable
{
    public static BasicAnalyzerKind DefaultKind
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
    private readonly ConcurrentQueue<Diagnostic> _diagnostics = new();

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

    protected BasicAnalyzerHost(BasicAnalyzerKind kind)
    {
        Kind = kind;
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
        return kind is BasicAnalyzerKind.OnDisk or BasicAnalyzerKind.None;
#endif
    }
}

