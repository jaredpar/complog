using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Basic.CompilerLog.Util;

namespace Basic.CompilerLog.UnitTests;

public class PolyfillTests
{
    [Fact]
    public void ReadExactlyTooMany()
    {
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3], 0, 3);

        byte[] buffer = new byte[10];
        Assert.Throws<EndOfStreamException>(() => stream.ReadExactly(buffer.AsSpan()));
    }

    [Fact]
    public void WriteSimple()
    {
        var writer = new StringWriter();
        ReadOnlySpan<char> span = "hello".AsSpan();
        writer.Write(span);
        Assert.Equal("hello", writer.ToString());
    }

    [Fact]
    public void WriteLineSimple()
    {
        var writer = new StringWriter();
        ReadOnlySpan<char> span = "hello".AsSpan();
        writer.WriteLine(span);
        Assert.Equal("hello" + Environment.NewLine, writer.ToString());
    }
}
