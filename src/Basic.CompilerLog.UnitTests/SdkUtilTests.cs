using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Sdk;
using static Basic.CompilerLog.Util.RoslynUtil;

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
        var a = MakeSdk("9.0.100");
        var b = MakeSdk("10.0.100-rc.2.25502.107");
        var c = temp.NewDirectory("sdk/invalid-version");
        var sdks = SdkUtil.GetSdkDirectories(temp.DirectoryPath);
        Assert.Equal(
            [
                (a, new SdkVersion(9, 0, 100)),
                (b, new SdkVersion(10, 0, 100, "rc.2.25502.107")),
            ],
            sdks.OrderBy(t => t.SdkVersion));

        var latestSdk = SdkUtil.GetLatestSdkDirectory(temp.DirectoryPath);
        Assert.Equal((b, new SdkVersion(10, 0, 100, "rc.2.25502.107")), latestSdk);

        string MakeSdk(string version)
        {
            var sdkPath = Path.Combine(temp.DirectoryPath, "sdk", version);
            var compilerPath = Path.Combine(sdkPath, "Roslyn", "bincore");
            _ = Directory.CreateDirectory(compilerPath);
            File.WriteAllLines(Path.Combine(compilerPath, GetCompilerAppFileName(isCSharp: true)), []);
            File.WriteAllLines(Path.Combine(compilerPath, GetCompilerAppFileName(isCSharp: false)), []);
            return sdkPath;
        }
    }
}
