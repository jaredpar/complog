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

public sealed class CompilerLogReaderOptions
{
    public static CompilerLogReaderOptions Default { get; } = new CompilerLogReaderOptions(BasicAnalyzerHost.DefaultKind, cacheAnalyzers: true);

    public static CompilerLogReaderOptions None { get; } = new CompilerLogReaderOptions(BasicAnalyzerKind.None, cacheAnalyzers: true);

    public BasicAnalyzerKind BasicAnalyzerKind { get; }

    /// <summary>
    /// When true requests for the exact same set of analyzers will return 
    /// the same <see cref="BasicAnalyzerHost"/> instance.
    /// </summary>
    public bool CacheAnalyzers { get; }

    public CompilerLogReaderOptions(
        BasicAnalyzerKind basicAnalyzerKind,
        bool cacheAnalyzers = true)
    {
        BasicAnalyzerKind = basicAnalyzerKind;
        CacheAnalyzers = cacheAnalyzers;
    }
}
