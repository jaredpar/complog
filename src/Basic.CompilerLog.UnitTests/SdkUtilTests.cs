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

    [Fact]
    public void GetSdkDirectories_ExcludesEmptyBincoreDirectory()
    {
        using var temp = new TempDir();
        // Create a valid SDK directory with csc.dll
        var validSdkDir = temp.NewDirectory("sdk/9.0.100/Roslyn/bincore");
        var realSdkDirectory = SdkUtil.GetLatestSdkDirectory().SdkDirectory;
        var roslynDir = Path.Combine(realSdkDirectory, "Roslyn", "bincore");
        var cscPath = Path.Combine(roslynDir, "csc.dll");
        File.Copy(cscPath, Path.Combine(validSdkDir, "csc.dll"));
        
        // Create an empty bincore directory (simulating incomplete Windows deletion)
        temp.NewDirectory("sdk/10.0.100/Roslyn/bincore");
        
        var sdks = SdkUtil.GetSdkDirectories(temp.DirectoryPath);
        
        // Should only include the valid SDK
        Assert.Single(sdks);
        Assert.Equal(new SdkVersion(9, 0, 100), sdks[0].SdkVersion);
    }

    [Fact]
    public void GetSdkDirectories_ExcludesBincoreWithoutCompiler()
    {
        using var temp = new TempDir();
        // Create SDK directory with bincore but no compiler files
        var bincoreDir = temp.NewDirectory("sdk/9.0.100/Roslyn/bincore");
        File.WriteAllText(Path.Combine(bincoreDir, "SomeOtherFile.txt"), "content");
        
        var sdks = SdkUtil.GetSdkDirectories(temp.DirectoryPath);
        
        // Should exclude this invalid SDK
        Assert.Empty(sdks);
    }

    [Fact]
    public void GetSdkDirectories_IncludesValidBincoreWithCscDll()
    {
        using var temp = new TempDir();
        var validSdkDir = temp.NewDirectory("sdk/9.0.100/Roslyn/bincore");
        
        // Copy the real csc.dll from the actual SDK
        var realSdkDirectory = SdkUtil.GetLatestSdkDirectory().SdkDirectory;
        var roslynDir = Path.Combine(realSdkDirectory, "Roslyn", "bincore");
        var cscPath = Path.Combine(roslynDir, "csc.dll");
        File.Copy(cscPath, Path.Combine(validSdkDir, "csc.dll"));
        
        var sdks = SdkUtil.GetSdkDirectories(temp.DirectoryPath);
        
        // Should include this valid SDK
        Assert.Single(sdks);
        Assert.Equal(new SdkVersion(9, 0, 100), sdks[0].SdkVersion);
    }

    [Fact]
    public void GetSdkDirectories_IncludesValidBincoreWithCscExe()
    {
        using var temp = new TempDir();
        var validSdkDir = temp.NewDirectory("sdk/9.0.100/Roslyn/bincore");
        
        // Create a fake apphost file using the platform-appropriate name
        var appHostName = RoslynUtil.GetCompilerAppFileName(isCSharp: true);
        File.WriteAllText(Path.Combine(validSdkDir, appHostName), "fake apphost");
        
        var sdks = SdkUtil.GetSdkDirectories(temp.DirectoryPath);
        
        // Should include this valid SDK
        Assert.Single(sdks);
        Assert.Equal(new SdkVersion(9, 0, 100), sdks[0].SdkVersion);
    }
}
