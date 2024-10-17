
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public sealed class CommonUtilTests
{
#if NET
    [Fact]
    public void GetAssemblyLoadContext()
    {
        var alc = new AssemblyLoadContext("Custom", isCollectible: true);
        Assert.Same(alc, CommonUtil.GetAssemblyLoadContext(alc));
        alc.Unload();
    }
#endif

    [Fact]
    public void Defines()
    {
#if NET
        Assert.True(DotnetUtil.IsNetCore);
        Assert.False(DotnetUtil.IsNetFramework);
#elif NETFRAMEWORK
        Assert.False(DotnetUtil.IsNetCore);
        Assert.True(DotnetUtil.IsNetFramework);
#endif
    }
}

