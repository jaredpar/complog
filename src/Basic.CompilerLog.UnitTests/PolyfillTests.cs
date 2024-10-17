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

    [Fact]
    public void ContainsCharSimple()
    {
        var span = "test".AsSpan();
        Assert.True(span.Contains('e'));
        Assert.False(span.Contains('f'));
    }

    [Fact]
    public void GetByteCountEmpty()
    {
        Assert.Equal(0, TestBase.DefaultEncoding.GetByteCount(ReadOnlySpan<char>.Empty));
        Assert.Equal(0, TestBase.DefaultEncoding.GetByteCount((ReadOnlySpan<char>)default));
    }

    [Fact]
    public void GetBytesCountEmpty()
    {
        var buffer = new byte[10];
        Assert.Equal(0, TestBase.DefaultEncoding.GetBytes(ReadOnlySpan<char>.Empty, buffer.AsSpan()));
        Assert.Throws<ArgumentException>(() => TestBase.DefaultEncoding.GetBytes("hello".AsSpan(), buffer.AsSpan(0, 0)));
    }
}
