using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilerCallReaderUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilerCallReaderUtilTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void CreateBadExtension()
    {
        Assert.Throws<ArgumentException>(() => CompilerCallReaderUtil.Get("file.bad"));
    }

    [Fact]
    public void GetBadArguments()
    {
        var binlogPath = Fixture.Console.Value.BinaryLogPath!;
        Assert.Throws<ArgumentException>(() => CompilerCallReaderUtil.Get(binlogPath, BasicAnalyzerKind.None));
    }
}