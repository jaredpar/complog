using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
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

public interface IBasicAnalyzerReference
{
    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language, List<Diagnostic> diagnostics);
    public ImmutableArray<ISourceGenerator> GetGenerators(string language, List<Diagnostic> diagnostics);
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
#if NET
            return BasicAnalyzerKind.InMemory;
#else
            return BasicAnalyzerKind.OnDisk;
#endif
        }
    }

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

    public static bool IsSupported(BasicAnalyzerKind kind)
    {
#if NET
        return true;
#else
        return kind is BasicAnalyzerKind.OnDisk or BasicAnalyzerKind.None;
#endif
    }

    internal static BasicAnalyzerHost Create(
        IBasicAnalyzerHostDataProvider dataProvider,
        BasicAnalyzerKind kind,
        CompilerCall compilerCall,
        List<AnalyzerData> analyzers)
    {
        return kind switch
        {
            BasicAnalyzerKind.OnDisk => new BasicAnalyzerHostOnDisk(dataProvider, analyzers),
            BasicAnalyzerKind.InMemory => new BasicAnalyzerHostInMemory(dataProvider, analyzers),
            BasicAnalyzerKind.None => CreateNone(analyzers),
            _ => throw new InvalidOperationException()
        };

        BasicAnalyzerHostNone CreateNone(List<AnalyzerData> analyzers)
        {
            if (analyzers.Count == 0)
            {
                return new BasicAnalyzerHostNone();
            }

            if (!dataProvider.HasAllGeneratedFileContent(compilerCall))
            {
                return new(CreateDiagnostic("Generated files not available in the PDB"));
            }

            try
            {
                var generatedSourceTexts = dataProvider.ReadAllGeneratedSourceTexts(compilerCall);
                return new BasicAnalyzerHostNone(generatedSourceTexts);
            }
            catch (Exception ex)
            {
                return new(CreateDiagnostic(ex.Message));
            }
        }

        static Diagnostic CreateDiagnostic(string message) =>
            Diagnostic.Create(
                RoslynUtil.ErrorReadingGeneratedFilesDiagnosticDescriptor,
                Location.None,
                message);
    }
}

