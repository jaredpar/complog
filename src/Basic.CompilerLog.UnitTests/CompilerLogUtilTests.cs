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
}