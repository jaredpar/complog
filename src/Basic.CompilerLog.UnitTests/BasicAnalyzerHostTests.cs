using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class BasicAnalyzerHostTests
{
    [Fact]
    public void Supported()
    {
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.Default));
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.OnDisk));
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.None));
#if NETCOREAPP
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.InMemory));
#else
        Assert.False(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.InMemory));
#endif

        // To make sure this test is updated every time a new value is added
        Assert.Equal(4, Enum.GetValues(typeof(BasicAnalyzerKind)).Length);
    }

    [Fact]
    public void Dispose()
    {
        var host = new BasicAnalyzerHostNone(readGeneratedFiles: true, ImmutableArray<(SourceText, string)>.Empty, BasicAnalyzerHostOptions.None);
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = host.AnalyzerReferences; });
    }

    [Fact]
    public void Props()
    {
        var host = new BasicAnalyzerHostNone(readGeneratedFiles: true, ImmutableArray<(SourceText, string)>.Empty, BasicAnalyzerHostOptions.None);
        host.Dispose();
        Assert.Equal(BasicAnalyzerKind.None, host.Kind);
        Assert.Same(BasicAnalyzerHostOptions.None, host.Options);
    }

}
