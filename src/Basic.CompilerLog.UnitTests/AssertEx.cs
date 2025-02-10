
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
                var b = e2.MoveNext();
                if (b)
                {

                }
                Assert.False(b);
                break;
            }

            Assert.True(e2.MoveNext());
            if (!e1.Current.Equals(e2.Current))
            {

            }

            Assert.Equal(e1.Current, e2.Current);
        }
    }
}