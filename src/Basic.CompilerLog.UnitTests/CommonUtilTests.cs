
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

public sealed class CommonUtilTests
{
    [Theory]
    [InlineData(@"/target:exe", "app.exe")]
    [InlineData(@"/target:winexe", "app.exe")]
    [InlineData(@"/target:module", "app.netmodule")]
    [InlineData(@"/target:library", "app.dll")]
    [InlineData(@"/target:module other.cs", "other.netmodule")]
    [InlineData(@"/target:library other.cs", "other.dll")]
    public void GetAssemblyFileName(string commandLine, string expectedFileName)
    {
        var args = CSharpCommandLineParser.Default.Parse(
            commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            baseDirectory: Path.Combine(ResilientDirectoryTests.RootPath, "code"),
            sdkDirectory: null,
            additionalReferenceDirectories: null);
        var actualFileName = CommonUtil.GetAssemblyFileName(args);
        Assert.Equal(expectedFileName, actualFileName);
    }

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
