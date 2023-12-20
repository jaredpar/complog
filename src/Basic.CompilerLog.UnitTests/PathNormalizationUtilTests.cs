
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class PathNormalizationUtilTests
{
    [Theory]
    [InlineData(@"c:\", "/code/")]
    [InlineData(@"c:\\", "/code/")]
    [InlineData(@"c:\src\blah.cs", "/code/src/blah.cs")]
    [InlineData(@"c:\src\..\blah.cs", "/code/src/../blah.cs")]
    public void WindowsToUnixNormalize(string path, string expected)
    {
        var actual = PathNormalizationUtil.WindowsToUnix.NormalizePath(path);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/", @"c:\code\")]
    [InlineData("/example", @"c:\code\example")]
    [InlineData("/example/", @"c:\code\example\")]
    [InlineData("/example/blah.cs", @"c:\code\example\blah.cs")]
    [InlineData("/example/../blah.cs", @"c:\code\example\..\blah.cs")]
    public void UnixToWindowsNormalize(string path, string expected)
    {
        var actual = PathNormalizationUtil.UnixToWindows.NormalizePath(path);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"c:\", true)]
    [InlineData(@"c:", true)]
    [InlineData(@"c:\\\", true)]
    [InlineData(@"c:\..\", true)]
    [InlineData(@"\..\", false)]
    [InlineData(@"example\blah.cs", false)]
    public void WindowsIsRooted(string path, bool expected)
    {
        Assert.Equal(expected, PathNormalizationUtil.WindowsToUnix.IsPathRooted(path));
    }

    [Theory]
    [InlineData(@"/", true)]
    [InlineData(@"/blah", true)]
    [InlineData(@"/code/blah.cs", true)]
    [InlineData(@"../", false)]
    [InlineData(@"example/blah.cs", false)]
    public void UnixIsRooted(string path, bool expected)
    {
        Assert.Equal(expected, PathNormalizationUtil.UnixToWindows.IsPathRooted(path));
    }
}