
using Basic.CompilerLog.Util;
using Xunit;

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

    /// <summary>
    /// Use this over Assert.Equal for collections as the error messages are more actionable
    /// </summary>
    /// <param name="actual"></param>
    internal static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        using var e1 = expected.GetEnumerator();
        using var e2 = actual.GetEnumerator();

        while (true)
        {
            if (!e1.MoveNext())
            {
                Assert.False(e2.MoveNext());
                break;
            }

            Assert.True(e2.MoveNext());
            Assert.Equal(e1.Current, e2.Current);
        }
    }
}