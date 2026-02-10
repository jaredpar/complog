using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilerLogUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilerLogUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void CreateBadExtension()
    {
        Assert.Throws<ArgumentException>(() => CompilerCallReaderUtil.Create("file.bad"));
    }

    [Fact]
    public void TryConvertBuildFileBadExtension()
    {
        Assert.Throws<ArgumentException>(() => CompilerLogUtil.TryConvertBuildFile("file.bad", "output.complog"));
    }

    [Fact]
    public void TryConvertResponseFileMissingFile()
    {
        using var tempDir = new TempDir();
        var rspPath = Path.Combine(tempDir.DirectoryPath, "missing.rsp");
        var complogPath = Path.Combine(tempDir.DirectoryPath, "missing.complog");

        var result = CompilerLogUtil.TryConvertResponseFile(rspPath, complogPath);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Error reading response file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryConvertResponseFileEmpty()
    {
        using var tempDir = new TempDir();
        var rspPath = Path.Combine(tempDir.DirectoryPath, "empty.rsp");
        File.WriteAllText(rspPath, string.Empty);
        var complogPath = Path.Combine(tempDir.DirectoryPath, "empty.complog");

        var result = CompilerLogUtil.TryConvertResponseFile(rspPath, complogPath);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Response file contains no arguments", StringComparison.OrdinalIgnoreCase));
    }
}
