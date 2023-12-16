
using Xunit;

namespace Basic.CompilerLog.UnitTests;

internal static class AssertEx
{
    internal static void HasData(MemoryStream? stream)
    {
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }
}