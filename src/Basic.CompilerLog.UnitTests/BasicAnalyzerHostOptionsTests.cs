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

public sealed class BasicAnalyzerHostOptionsTests
{
#if NETCOREAPP
    [Fact]
    public void CustomAssemblyLoadContext()
    {
        var alc = new AssemblyLoadContext("Custom", isCollectible: true);
        var options = new BasicAnalyzerHostOptions(alc, BasicAnalyzerKind.Default, cacheable: true);
        Assert.Same(alc, options.CompilerLoadContext);
        alc.Unload();
    }
#endif
}
