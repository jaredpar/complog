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

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public sealed class BasicAnalyzerHostTests
{
    [Fact]
    public void Supported()
    {
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.OnDisk));
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.None));
#if NETCOREAPP
        Assert.True(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.InMemory));
#else
        Assert.False(BasicAnalyzerHost.IsSupported(BasicAnalyzerKind.InMemory));
#endif

        // To make sure this test is updated every time a new value is added
        Assert.Equal(3, Enum.GetValues(typeof(BasicAnalyzerKind)).Length);
    }

    [Fact]
    public void NoneDispose()
    {
        var host = new BasicAnalyzerHostNone(ImmutableArray<(SourceText, string)>.Empty);
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = host.AnalyzerReferences; });
    }

    [Fact]
    public void NoneProps()
    {
        var host = new BasicAnalyzerHostNone(ImmutableArray<(SourceText, string)>.Empty);
        host.Dispose();
        Assert.Equal(BasicAnalyzerKind.None, host.Kind);
        Assert.Empty(host.GeneratedSourceTexts);
    }

    [Fact]
    public void Error()
    {
        var message = "my error message";
        var host = new BasicAnalyzerHostNone(message);
        var diagnostic = host.GetDiagnostics().Single();
        Assert.Contains(message, diagnostic.GetMessage());
    }
}
