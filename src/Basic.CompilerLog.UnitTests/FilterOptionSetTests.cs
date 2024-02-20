#if NETCOREAPP
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class FilterOptionSetTest
{
    [Fact]
    public void CheckForAnalyzers()
    {
        var options = new FilterOptionSet(analyzers: false);
        Assert.Throws<InvalidOperationException>(() => options.IncludeAnalyzers);

        options = new FilterOptionSet(analyzers: true);
        Assert.False(options.IncludeAnalyzers);
    }
}
#endif