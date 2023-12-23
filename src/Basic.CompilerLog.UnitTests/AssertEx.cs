
using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

internal static class AssertEx
{
    internal static void HasData(MemoryStream? stream)
    {
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }

    internal static void Success<T>(ITestOutputHelper testOutputHelper, T emitResult)
        where T : struct, IEmitResult
    {
        if (!emitResult.Success)
        {
            foreach (var diagnostic in emitResult.Diagnostics)
            {
                testOutputHelper.WriteLine(diagnostic.ToString());
            }
        }

        Assert.True(emitResult.Success);
    }
}