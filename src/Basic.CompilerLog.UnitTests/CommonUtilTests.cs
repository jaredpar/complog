
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public sealed class CommonUtilTests
{
#if NETCOREAPP

    [Fact]
    public void GetAssemlbyLoadContext()
    {
        var alc = new AssemblyLoadContext("Custom", isCollectible: true);
        Assert.Same(alc, CommonUtil.GetAssemblyLoadContext(alc));
        alc.Unload();
    }

#endif
}
