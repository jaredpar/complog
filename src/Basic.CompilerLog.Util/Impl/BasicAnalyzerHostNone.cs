using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// This is a per-compilation analyzer assembly loader that can be used to produce 
/// <see cref="AnalyzerFileReference"/> instances
/// </summary>
internal sealed class BasicAnalyzerHostNone : BasicAnalyzerHost
{
    public BasicAnalyzerHostNone()
        : base(BasicAnalyzerKind.None, ImmutableArray<AnalyzerReference>.Empty)
    {

    }

    protected override void DisposeCore()
    {
        // Do nothing
    }
}