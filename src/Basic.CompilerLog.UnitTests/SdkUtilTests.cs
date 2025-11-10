using Basic.CompilerLog.Util;
using NuGet.Versioning;
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

    [Fact]
    public void SdkVersions()
    {
        using var temp = new TempDir();
        var a = Path.GetFullPath(Path.Combine(temp.NewDirectory("sdk/9.0.100/Roslyn/bincore"), "../.."));
        var b = Path.GetFullPath(Path.Combine(temp.NewDirectory("sdk/10.0.100-rc.2.25502.107/Roslyn/bincore"), "../.."));
        temp.NewDirectory("sdk/invalid-version");
        var sdks = SdkUtil.GetSdkDirectoriesAndVersion(temp.DirectoryPath);
        Assert.Equal(
            [
                (a, new NuGetVersion(9, 0, 100)),
                (b, new NuGetVersion(10, 0, 100, "rc.2.25502.107")),
            ],
            sdks.OrderBy(t => t.SdkVersion));

        var latestSdk = SdkUtil.GetLatestSdkDirectories(temp.DirectoryPath);
        Assert.Equal((b, new NuGetVersion(10, 0, 100, "rc.2.25502.107")), latestSdk);
    }
}
