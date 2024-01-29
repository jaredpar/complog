
using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

public sealed class SdkUtilTests
{
    [Fact]
    public void GetDotnetDirectoryBadPath()
    {
        Assert.Throws<Exception>(() => SdkUtil.GetDotnetDirectory(@"C:\"));
    }
}