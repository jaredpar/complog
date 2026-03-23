#if NET
using Xunit;
using Basic.CompilerLog.App;
using Mono.Options;

namespace Basic.CompilerLog.UnitTests;

public sealed class FilterOptionSetTest
{
    [Fact]
    public void CheckForAnalyzers()
    {
        var options = new FilterOptionSet(analyzers: false);
        Assert.Throws<InvalidOperationException>(() => options.IncludeAnalyzers);

        options = new FilterOptionSet(analyzers: true);
        Assert.True(options.IncludeAnalyzers);
    }

    [Fact]
    public void StripReadyToRunDefaultIsNull()
    {
        var options = new FilterOptionSet(analyzers: true);
        Assert.Null(options.StripReadyToRun);
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData("always", true)]
    [InlineData("never", false)]
    public void StripReadyToRunParsed(string value, bool? expected)
    {
        var options = new FilterOptionSet(analyzers: true);
        options.Parse([$"--strip={value}"]);
        Assert.Equal(expected, options.StripReadyToRun);
    }

    [Fact]
    public void StripReadyToRunInvalidValue()
    {
        var options = new FilterOptionSet(analyzers: true);
        Assert.Throws<OptionException>(() => options.Parse(["--strip=bad"]));
    }

    [Fact]
    public void StripReadyToRunNotAvailableWithoutAnalyzers()
    {
        var options = new FilterOptionSet(analyzers: false);
        Assert.Throws<InvalidOperationException>(() => options.StripReadyToRun);
    }
}
#endif